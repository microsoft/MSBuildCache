// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core.Tasks;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Content.Common;
using AbsolutePath = BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath;
using CompressionLevel = System.IO.Compression.CompressionLevel;
using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Microsoft.MSBuildCache.GitHubActions;

internal sealed class GithubActionsCacheClient : CacheClient
{
    private static readonly HttpClient s_httpClient = new HttpClient();
    private const long PutBlockSize = 8 * 1024 * 1024;

    private readonly string _urlBase;
    private readonly string _universe;
    private readonly CompressionLevel _compressionLevel;
    private readonly ILogger _logger;
    private readonly bool _remoteCacheIsReadOnly;

    public GithubActionsCacheClient(
        Context rootContext,
        IFingerprintFactory fingerprintFactory,
        HashType hashType,
        IContentSession localCAS,
        ILogger logger,
        string universe,
        AbsolutePath repoRoot,
        INodeContextRepository nodeContextRepository,
        Func<string, FileRealizationMode> getFileRealizationMode,
        int maxConcurrentCacheContentOperations,
        bool remoteCacheIsReadOnly,
        bool enableAsyncPublishing,
        bool enableAsyncMaterialization)
    : base(rootContext, fingerprintFactory, hashType, repoRoot, nodeContextRepository, getFileRealizationMode, localCAS, maxConcurrentCacheContentOperations, enableAsyncPublishing, enableAsyncMaterialization)
    {
        _compressionLevel = CompressionLevel.Optimal;
        _logger = logger;
        _remoteCacheIsReadOnly = remoteCacheIsReadOnly;

        if (!GithubActionsHelpers.IsGithubActionsEnvironment())
        {
            throw new InvalidOperationException("Not running in a Github Actions environment");
        }

        _urlBase = $"{GithubActionsHelpers.GetActionsCacheUrl().TrimEnd('/')}/_apis/artifactcache";
        _universe = (string.IsNullOrEmpty(universe) ? "DEFAULT" : universe)!;
    }

    private const string NodeBuildResultEntryName = "NODE_BUILD_RESULT";

    protected override async Task AddNodeAsync(
        Context context,
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<AbsolutePath, ContentHash> outputs,
        (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
        (ContentHash hash, byte[] bytes)? pathSetBytes,
        CancellationToken cancellationToken)
    {
        if (_remoteCacheIsReadOnly)
        {
            return;
        }

        // If we are async publishing, then we need to grab content from the L1 and remap it.
        // If we are sync publishing, then we can point directly to it.
        var zipStream = new MemoryStream();
        using (var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            _logger.Debug($"Adding NodeBuildResult {nodeBuildResultBytes.hash.Serialize()} to zip archive");
            using (Stream entryStream = zipArchive.CreateEntry(NodeBuildResultEntryName, _compressionLevel).Open())
            {
#if NETFRAMEWORK
                await entryStream.WriteAsync(nodeBuildResultBytes.bytes, 0, nodeBuildResultBytes.bytes.Length, cancellationToken);
#else
                await entryStream.WriteAsync(nodeBuildResultBytes.bytes, cancellationToken);
#endif
            }

            if (EnableAsyncPublishing)
            {
                HashSet<ContentHash> uniqueHashes = outputs.ToHashSet(kvp => kvp.Value);
                foreach (ContentHash hash in uniqueHashes)
                {
                    using Stream entryStream = zipArchive.CreateEntry(hash.Serialize(), _compressionLevel).Open();
                    var contentStreamResult = await LocalCacheSession.OpenStreamAsync(context, hash, cancellationToken);
                    await contentStreamResult.Stream!.CopyToAsync(entryStream, 81920, cancellationToken);
                }
            }
            else
            {
                Dictionary<ContentHash, AbsolutePath> uniqueFiles = outputs.ToDictionaryAnyWins(kvp => kvp.Value, kvp => kvp.Key);
                foreach (KeyValuePair<ContentHash, AbsolutePath> kvp in uniqueFiles)
                {
                    zipArchive.CreateEntryFromFile(
                        kvp.Value.Path,
                        kvp.Key.Serialize(),
                        _compressionLevel);
                }
            }
        }

        string sfpKey = ForSession(ComputeKey(fingerprint));
        zipStream.Position = 0;
        await PostStreamAsync(sfpKey, zipStream, cancellationToken);

        if (pathSetBytes != null)
        {
            string pathSetKey = ForSession(ComputeKey(pathSetBytes.Value.hash));
            using MemoryStream pathSet = new(pathSetBytes.Value.bytes);
            await PostStreamAsync(pathSetKey, pathSet, cancellationToken);
        }

        List<Selector> existingSelectors = await GetSelectors(context, fingerprint.WeakFingerprint, cancellationToken).ToListAsync(cancellationToken);
        if (existingSelectors.Contains(fingerprint.Selector))
        {
            return;
        }

        while (existingSelectors.Count > SelectorsBag.MaxSelectors)
        {
            existingSelectors.RemoveAt(0);
        }

        existingSelectors.Add(fingerprint.Selector);
        var serialized = existingSelectors.Select(s => new SelectorSerialized(s)).ToArray();
        var selectorBag = new SelectorsBag()
        {
            selectors = serialized,
        };

        using var bagStream = new MemoryStream();
        JsonSerializer.Serialize(bagStream, selectorBag);
        string selectorsKey = ForSession(ComputeSelectorsKey(fingerprint.WeakFingerprint));
        bagStream.Position = 0;
        await PostStreamAsync(selectorsKey, bagStream, cancellationToken);
    }

    protected override async Task<ICacheEntry?> GetCacheEntryAsync(Context context, StrongFingerprint fingerprint, CancellationToken cancellationToken)
    {
        var response = await QueryCacheAsync(AnySession(ComputeKey(fingerprint)), cancellationToken);
        if (response?.archiveLocation == null)
        {
            return null;
        }

        using var zipStream = await response.GetStreamAsync(cancellationToken);
        return new CacheEntry(this, new ZipArchive(zipStream, ZipArchiveMode.Read));
    }

    private sealed class CacheEntry : ICacheEntry
    {
        private readonly GithubActionsCacheClient _client;
        private readonly ZipArchive _zipArchive;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public CacheEntry(GithubActionsCacheClient client, ZipArchive zipArchive)
        {
            _client = client;
            _zipArchive = zipArchive;
        }

        public async Task<Stream?> GetNodeBuildResultAsync(Context context, CancellationToken cancellationToken)
        {
            using var _semaphoreReleaser = await _semaphore.AcquireAsync(cancellationToken);
            ZipArchiveEntry? entry = _zipArchive.GetEntry(NodeBuildResultEntryName);
            using var stream = entry!.Open();
            MemoryStream copy = new();
            await stream.CopyToAsync(copy, 81920, cancellationToken);
            copy.Position = 0;
            return copy;
        }

        public async Task PlaceFilesAsync(Context context, IReadOnlyDictionary<AbsolutePath, ContentHash> files, CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<AbsolutePath, ContentHash> file in files)
            {
                PlaceFileResult placeResult = await _client.LocalCacheSession.PlaceFileAsync(context, file.Value, file.Key,
                    FileAccessMode.ReadOnly, FileReplacementMode.ReplaceExisting, FileRealizationMode.Any, cancellationToken);
                if (placeResult.Succeeded)
                {
                    continue;
                }

                using (await _semaphore.AcquireAsync(cancellationToken))
                {
                    ZipArchiveEntry content = _zipArchive.GetEntry(file.Value.Serialize())!;
                    using var stream = content!.Open();
                    await _client.LocalCacheSession.PutStreamAsync(context, file.Value, stream, cancellationToken);
                }

                placeResult = await _client.LocalCacheSession.PlaceFileAsync(context, file.Value, file.Key,
                    FileAccessMode.ReadOnly, FileReplacementMode.ReplaceExisting, FileRealizationMode.Any, cancellationToken);
                placeResult.ThrowIfFailure();
            }
        }

        public void Dispose()
        {
            _zipArchive.Dispose();
            _semaphore.Dispose();
        }
    }

    protected override async IAsyncEnumerable<Selector> GetSelectors(Context context, Fingerprint fingerprint, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        SelectorSerialized[]? selectors = null;

        var response = await QueryCacheAsync(AnySession(ComputeSelectorsKey(fingerprint)), cancellationToken);
        if (response?.archiveLocation != null)
        {
            using var bucketStream = await response.GetStreamAsync(cancellationToken);
            SelectorsBag? bucket = await bucketStream.Stream.DeserializeAsync<SelectorsBag>(cancellationToken: cancellationToken);
            selectors = bucket!.selectors!;
        }

        foreach (var selector in selectors!)
        {
            yield return selector.Deserialize();
        }
    }

    protected override async Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken)
    {
        if (contentHash.IsEmptyHash())
        {
            return new OpenStreamResult(new MemoryStream());
        }

        var response = await QueryCacheAsync(ComputeKey(contentHash), cancellationToken);
        if (response?.archiveLocation == null)
        {
            return new OpenStreamResult(OpenStreamResult.ResultCode.ContentNotFound, null);
        }

        return new OpenStreamResult(await response.GetStreamAsync(cancellationToken));
    }

    private static string ComputeKey(ContentHash contentHash) => $"content-{contentHash.HashType}-{contentHash.Serialize()}";
    private static string ComputeSelectorsKey(Fingerprint wfp) => $"selector-{wfp.Serialize()}";
    private static string ComputeKey(StrongFingerprint sfp) => $"contenthashlist-{sfp.WeakFingerprint.Serialize()}-{sfp.Selector.ContentHash.Serialize()}";

    private static string ForSession(string key) => $"{key}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    private static string AnySession(string key) => $"{key}-";

    private async Task<QueryCacheResponse?> QueryCacheAsync(string key, CancellationToken token)
    {
        var builder = new UriBuilder($"{_urlBase}/cache");
        builder.AppendQueryEscapeUriString("keys", key);
        builder.AppendQueryEscapeUriString("version", _universe);
        using var request = CreateRequest(HttpMethod.Get, builder.Uri);
        var response = await s_httpClient.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        Stream stream = await response.Content.ReadAsStreamAsync(token);
        return await stream.DeserializeAsync<QueryCacheResponse>(cancellationToken: token);
    }

    private async Task<string> PostStreamAsync(string key, Stream stream, CancellationToken cancellationToken)
    {
        ulong cacheId;
        {
            var builder = new UriBuilder($"{_urlBase}/caches");
            using var request = CreateRequest(HttpMethod.Post, builder.Uri);

            var jsonRequest = new ReserveCacheEntryRequest
            {
                key = key,
                version = _universe,
            };

            request.Content = new StringContent(JsonSerializer.Serialize(jsonRequest), Encoding.UTF8, "application/json");
            var response = await s_httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            ReserveCacheEntryResponse? cacheResponse = await responseStream.DeserializeAsync<ReserveCacheEntryResponse>(cancellationToken: cancellationToken);
            if (cacheResponse == null)
            {
                throw new InvalidOperationException("Deserialized null value");
            }

            if (cacheResponse.cacheId == null)
            {
                throw new InvalidOperationException($"{nameof(cacheResponse.cacheId)} is null");
            }

            cacheId = cacheResponse.cacheId.Value;
        }

        int blocks = (int)((stream.Length + PutBlockSize - 1) / PutBlockSize);
        for (int i = 0; i < blocks; i++)
        {
            long startOffsetInclusive = PutBlockSize * i;
            long endOffsetExclusive = Math.Min(startOffsetInclusive + PutBlockSize, stream.Length);
            int blockLength = (int)(endOffsetExclusive - startOffsetInclusive);

            var buffer = new byte[blockLength];
            await stream.ReadRequiredRangeAsync(buffer, 0, blockLength);

            var builder = new UriBuilder($"{_urlBase}/caches/{cacheId}");
            using var request = CreateRequest(new HttpMethod("PATCH"), builder.Uri);
            request.Content = new ByteArrayContent(buffer);
            // inclusive https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Content-Range
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(startOffsetInclusive, endOffsetExclusive - 1, stream.Length);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var response = await s_httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        {
            var builder = new UriBuilder($"{_urlBase}/caches/{cacheId}");
            using var request = CreateRequest(HttpMethod.Post, builder.Uri);
            var jsonRequest = new SealCacheEntryRequest
            {
                size = stream.Length,
            };

            request.Content = new StringContent(JsonSerializer.Serialize(jsonRequest), Encoding.UTF8, "application/json");
            var response = await s_httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        return key;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GithubActionsHelpers.GetToken());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.RequestUri = request.RequestUri.AppendQuery("api-version", "6.0-preview.1");
        return request;
    }

#pragma warning disable CA1812 // These classes are instantiated by JSON deserialization
    private sealed class QueryCacheRequest
    {
        public string? keys { get; set; }
        public string? version { get; set; }
    }

    private sealed class QueryCacheResponse
    {
        public string? cacheKey { get; set; }
        public string? scope { get; set; }
        public string? archiveLocation { get; set; }

        public async Task<StreamWithLength> GetStreamAsync(CancellationToken cancellationToken)
        {
            Stream stream = await s_httpClient.GetStreamAsync(archiveLocation, cancellationToken);
            return stream.WithLength(stream.Length);
        }
    }

    private sealed class ReserveCacheEntryRequest
    {
        public string? key { get; set; }
        public string? version { get; set; }
    }

    private sealed class ReserveCacheEntryResponse
    {
        public ulong? cacheId { get; set; }
    }

    private sealed class SealCacheEntryRequest
    {
        public long size { get; set; }
    }
#pragma warning restore CA1812

    private sealed class SelectorSerialized
    {
        [JsonConstructor]
        public SelectorSerialized() { }

        public SelectorSerialized(Selector s)
        {
            if (s.ContentHash.HashType == HashType.Unknown || s.ContentHash.ByteLength == 0)
            {
                throw new ArgumentException($"Bad contentHash: {s.ContentHash.Serialize()}");
            }
            contentHash = s.ContentHash.Serialize();
            output = s.Output.ToHex();
        }

        public string? contentHash { get; set; }
        public string? output { get; set; }

        public Selector Deserialize()
        {
            if (contentHash == null)
            {
                throw new InvalidOperationException();
            }

            return new Selector(
                new ContentHash(contentHash),
                HexUtilities.HexToBytes(output));
        }
    }

    private sealed class SelectorsBag
    {
        public const int MaxSelectors = 100;
        public SelectorSerialized[]? selectors { get; set; }
    }
}
