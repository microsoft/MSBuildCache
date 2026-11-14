// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;

using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;
using MSBuildCacheFingerprint = Microsoft.MSBuildCache.Fingerprinting.Fingerprint;

namespace Microsoft.MSBuildCache.S3.Tests;

// Creates an S3CacheClient over fakes and calls its protected members through reflection.
internal sealed class S3CacheClientHarness : IAsyncDisposable
{
    public const string BucketName = "msbuildcache-tests";
    public const string KeyPrefix = "msbuildcache";
    public const string Universe = "universe";

    public const long MinimumPartSizeBytes = 5 * 1024 * 1024;

    public const long DefaultMultipartThresholdBytes = 1024 * 1024;

    private static readonly MethodInfo AddNodeAsyncMethod = GetInstanceMethod(
        "AddNodeAsync",
        typeof(Context),
        typeof(StrongFingerprint),
        typeof(IReadOnlyDictionary<string, ContentHash>),
        typeof((ContentHash, byte[])),
        typeof((ContentHash, byte[])?),
        typeof(CancellationToken));

    private static readonly MethodInfo GetCacheEntryAsyncMethod = GetInstanceMethod(
        "GetCacheEntryAsync",
        typeof(Context),
        typeof(StrongFingerprint),
        typeof(CancellationToken));

    private static readonly MethodInfo GetSelectorsMethod = GetInstanceMethod(
        "GetSelectors",
        typeof(Context),
        typeof(Fingerprint),
        typeof(CancellationToken));

    private static readonly MethodInfo OpenStreamAsyncMethod = GetInstanceMethod(
        "OpenStreamAsync",
        typeof(Context),
        typeof(ContentHash),
        typeof(CancellationToken));

    private static readonly MethodInfo PlaceContentAsyncMethod = GetInstanceMethod(
        "PlaceContentAsync",
        typeof(Context),
        typeof(ContentHash),
        typeof(string),
        typeof(CancellationToken));

    private readonly string _rootDirectory;

#pragma warning disable CA2213 // Ownership is transferred to the cache client, which disposes the hasher it was given.
    private readonly IContentHasher _hasher;
#pragma warning restore CA2213

    public S3CacheClientHarness(
        long multipartThresholdBytes = DefaultMultipartThresholdBytes,
        long multipartPartSizeBytes = 0,
        int maxConcurrentPartsPerObject = 8,
        int maxConcurrentCacheContentOperations = 32,
        bool remoteCacheIsReadOnly = false,
        double contentRenewalAgeDays = 0)
    {
        _rootDirectory = Path.Combine(Path.GetTempPath(), "MSBuildCacheS3Tests", Guid.NewGuid().ToString("N"));
        RepoRoot = Path.Combine(_rootDirectory, "repo");
        string nugetPackageRoot = Path.Combine(_rootDirectory, "packages");
        Directory.CreateDirectory(RepoRoot);
        Directory.CreateDirectory(nugetPackageRoot);

        _hasher = HashInfoLookup.Find(HashType.Vso0).CreateContentHasher();

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by the cache client.
        Context = new Context(new Logger());
        S3 = new FakeAmazonS3Client();
        LocalCas = new FakeContentSession(_hasher);
        Keys = new S3ObjectKeys(KeyPrefix, HashType.Vso0, Universe);

        Client = new S3CacheClient(
            Context,
            new UnusedFingerprintFactory(),
            _hasher,
            new FakeCache(),
            LocalCas,
            S3,
            BucketName,
            KeyPrefix,
            Universe,
            RepoRoot,
            nugetPackageRoot,
            _ => FileRealizationMode.Copy,
            maxConcurrentCacheContentOperations,
            remoteCacheIsReadOnly,
            enableAsyncPublishing: false,
            enableAsyncMaterialization: false,
            skipUnchangedOutputFiles: false,
            touchOutputFiles: false,
            multipartThresholdBytes,
            multipartPartSizeBytes,
            maxConcurrentPartsPerObject,
            contentRenewalAgeDays);
#pragma warning restore CA2000
    }

    public string RepoRoot { get; }

    public Context Context { get; }

    public FakeAmazonS3Client S3 { get; }

    public FakeContentSession LocalCas { get; }

    public S3ObjectKeys Keys { get; }

    public S3CacheClient Client { get; }

    // Doesn't repeat at part boundaries, so a part at the wrong offset is detectable.
    public static byte[] CreateContent(int length)
    {
        byte[] bytes = new byte[length];
        uint state = 0x9E3779B9;
        for (int i = 0; i < length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }

        return bytes;
    }

    public ContentHash HashOf(byte[] bytes) => _hasher.GetContentHash(bytes);

    public ContentHash PutContentInBucket(byte[] bytes)
    {
        ContentHash contentHash = HashOf(bytes);
        S3.Put(Keys.GetCas(contentHash), bytes);
        return contentHash;
    }

    public string PathUnderRepoRoot(string relativePath) => Path.Combine(RepoRoot, relativePath);

    public Task PlaceContentAsync(ContentHash contentHash, string absolutePath)
        => (Task)PlaceContentAsyncMethod.Invoke(Client, [Context, contentHash, absolutePath, CancellationToken.None])!;

    public Task<OpenStreamResult> OpenStreamAsync(ContentHash contentHash)
        => (Task<OpenStreamResult>)OpenStreamAsyncMethod.Invoke(Client, [Context, contentHash, CancellationToken.None])!;

    public IAsyncEnumerable<Selector> GetSelectors(Fingerprint weakFingerprint)
        => (IAsyncEnumerable<Selector>)GetSelectorsMethod.Invoke(Client, [Context, weakFingerprint, CancellationToken.None])!;

    public Task<AddNodeResult> AddNodeAsync(
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<string, ContentHash> outputs,
        (ContentHash Hash, byte[] Bytes) nodeBuildResult,
        (ContentHash Hash, byte[] Bytes)? pathSet = null)
        => (Task<AddNodeResult>)AddNodeAsyncMethod.Invoke(
            Client,
            [Context, fingerprint, outputs, nodeBuildResult, pathSet, CancellationToken.None])!;

    public async Task<object?> GetCacheEntryAsync(StrongFingerprint fingerprint)
    {
        Task task = (Task)GetCacheEntryAsyncMethod.Invoke(Client, [Context, fingerprint, CancellationToken.None])!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    public Task PlaceFilesAsync(object cacheEntry, IReadOnlyDictionary<string, ContentHash> files)
        => (Task)cacheEntry.GetType().GetMethod("PlaceFilesAsync")!.Invoke(cacheEntry, [Context, files, CancellationToken.None])!;

    public async Task<byte[]> GetNodeBuildResultAsync(object cacheEntry)
    {
        Task task = (Task)cacheEntry.GetType().GetMethod("GetNodeBuildResultAsync")!.Invoke(cacheEntry, [Context, CancellationToken.None])!;
        await task;

        using Stream stream = (Stream)task.GetType().GetProperty("Result")!.GetValue(task)!;
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();

        try
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static MethodInfo GetInstanceMethod(string name, params Type[] parameterTypes)
        => typeof(S3CacheClient).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, binder: null, parameterTypes, modifiers: null)
           ?? throw new MissingMethodException(nameof(S3CacheClient), name);

    private sealed class UnusedFingerprintFactory : IFingerprintFactory
    {
        public Task<MSBuildCacheFingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext) => throw new NotSupportedException();

        public PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations) => throw new NotSupportedException();

        public Task<MSBuildCacheFingerprint?> GetStrongFingerprintAsync(PathSet? pathSet) => throw new NotSupportedException();

        public bool MatchesCurrentState(PathSet? cachedPathSet) => throw new NotSupportedException();
    }
}
