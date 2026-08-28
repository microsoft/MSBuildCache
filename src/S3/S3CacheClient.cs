// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Fingerprinting;
using FileInfo = System.IO.FileInfo;
using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.S3;

/// <summary>
/// Stores the cache in an S3 bucket, or in an S3-compatible store such as MinIO.
/// </summary>
/// <remarks>
/// Unlike <see cref="CasCacheClient"/> there is no BuildXL remote cache to put under a two-level cache, as BuildXL has
/// no S3 implementation. The local cache is only used as a content store and every fingerprint lookup goes to S3. See
/// <see cref="S3ObjectKeys"/> for the object layout.
/// </remarks>
internal sealed class S3CacheClient : CacheClient
{
    private const int CopyBufferSize = 128 * 1024;

    // S3 rejects parts smaller than this, except for the last part of a multipart upload.
    private const long MinimumPartSizeBytes = 5 * 1024 * 1024;

    private readonly IAmazonS3 _s3;
    private readonly IContentHasher _hasher;
    private readonly TransferUtility _transferUtility;
    private readonly S3ObjectKeys _keys;
    private readonly string _bucketName;
    private readonly bool _remoteCacheIsReadOnly;
    private readonly long _multipartThresholdBytes;
    private readonly long _partSizeBytes;
    private readonly int _maxConcurrentPartsPerObject;

    // A file copied by many projects is published by each of them, so avoid uploading the same content repeatedly.
    private readonly ConcurrentDictionary<ContentHash, Task> _remoteUploads = new();

    // Bounds in-flight part requests across all objects. Without this, each object being downloaded could fan out
    // into parts, multiplying the request count by MaxConcurrentCacheContentOperations.
    private readonly SemaphoreSlim _partGate;

    public S3CacheClient(
        Context rootContext,
        IFingerprintFactory fingerprintFactory,
        IContentHasher hasher,
        ICache localCache,
        IContentSession localCas,
        IAmazonS3 s3,
        string bucketName,
        string keyPrefix,
        string universe,
        string repoRoot,
        string nugetPackageRoot,
        Func<string, FileRealizationMode> getFileRealizationMode,
        int maxConcurrentCacheContentOperations,
        bool remoteCacheIsReadOnly,
        bool enableAsyncPublishing,
        bool enableAsyncMaterialization,
        bool skipUnchangedOutputFiles,
        bool touchOutputFiles,
        long multipartThresholdBytes,
        long multipartPartSizeBytes,
        int maxConcurrentPartsPerObject)
        : base(rootContext, fingerprintFactory, hasher, repoRoot, nugetPackageRoot, getFileRealizationMode, localCache, localCas, maxConcurrentCacheContentOperations, enableAsyncPublishing, enableAsyncMaterialization, skipUnchangedOutputFiles, touchOutputFiles)
    {
        _s3 = s3;
        _hasher = hasher;
        _bucketName = bucketName;
        _remoteCacheIsReadOnly = remoteCacheIsReadOnly;
        _multipartThresholdBytes = Math.Max(1, multipartThresholdBytes);
        _partSizeBytes = Math.Max(MinimumPartSizeBytes, multipartPartSizeBytes);
        _maxConcurrentPartsPerObject = Math.Max(1, maxConcurrentPartsPerObject);
        _partGate = new SemaphoreSlim(Math.Max(1, maxConcurrentCacheContentOperations));
        _keys = new S3ObjectKeys(keyPrefix, hasher.Info.HashType, universe);

        _transferUtility = new TransferUtility(
            s3,
            new TransferUtilityConfig
            {
                ConcurrentServiceRequests = _maxConcurrentPartsPerObject,
                MinSizeBeforePartUpload = _multipartThresholdBytes,
            });
    }

    protected override async Task<AddNodeResult> AddNodeAsync(
        Context context,
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<string, ContentHash> outputs,
        (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
        (ContentHash hash, byte[] bytes)? pathSetBytes,
        CancellationToken cancellationToken)
    {
        if (_remoteCacheIsReadOnly)
        {
            // S3 is the only fingerprint store, so nothing can be published at all when it's read-only.
            return AddNodeResult.Skipped;
        }

        string entryKey = _keys.GetEntry(fingerprint);
        bool alreadyExists = await ObjectExistsAsync(entryKey, cancellationToken);

        Dictionary<ContentHash, string> contentAbsolutePaths = new(outputs.Count);
        foreach (KeyValuePair<string, ContentHash> kvp in outputs)
        {
            contentAbsolutePaths[kvp.Value] = kvp.Key;
        }

        List<Task> uploadTasks = new(contentAbsolutePaths.Count + 1);
        foreach (KeyValuePair<ContentHash, string> kvp in contentAbsolutePaths)
        {
            uploadTasks.Add(EnsureContentInRemoteAsync(context, kvp.Key, kvp.Value, cancellationToken));
        }

        // The PathSet is fetched by hash while evaluating selectors, so it needs to be in the CAS. The
        // NodeBuildResult does not, as it's stored inline in the entry below.
        if (pathSetBytes is not null)
        {
            uploadTasks.Add(EnsureBytesInRemoteAsync(context, pathSetBytes.Value.hash, pathSetBytes.Value.bytes, cancellationToken));
        }

        await Task.WhenAll(uploadTasks);

        if (!alreadyExists)
        {
            await PutObjectAsync(entryKey, nodeBuildResultBytes.bytes, cancellationToken);
            Tracer.Debug(context, $"Stored S3 cache entry `{entryKey}` for {fingerprint}");
        }

        // Write the selector last so a listed selector always has a readable entry behind it. This is done even when
        // the entry already existed, to repair an entry whose selector never landed.
        string selectorKey = _keys.GetSelector(fingerprint);
        await PutObjectAsync(selectorKey, Array.Empty<byte>(), cancellationToken);
        Tracer.Debug(context, $"Stored S3 selector `{selectorKey}`");

        return alreadyExists ? AddNodeResult.AlreadyExists : AddNodeResult.Added;
    }

    protected override async Task<ICacheEntry?> GetCacheEntryAsync(
        Context context,
        StrongFingerprint cacheStrongFingerprint,
        CancellationToken cancellationToken)
    {
        string entryKey = _keys.GetEntry(cacheStrongFingerprint);
        byte[]? nodeBuildResultBytes = await TryGetObjectBytesAsync(context, entryKey, cancellationToken);
        if (nodeBuildResultBytes is null)
        {
            Tracer.Debug(context, $"S3 cache entry not found for {cacheStrongFingerprint} (`{entryKey}`)");
            return null;
        }

        return new CacheEntry(this, nodeBuildResultBytes);
    }

    protected override async IAsyncEnumerable<Selector> GetSelectors(
        Context context,
        Fingerprint fingerprint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string prefix = _keys.GetSelectorPrefix(fingerprint);
        Tracer.Debug(context, $"Listing S3 selectors under `{prefix}`");

        string? continuationToken = null;
        do
        {
            ListObjectsV2Response response;
            try
            {
                response = await _s3.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _bucketName,
                        Prefix = prefix,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Treat as a weak fingerprint miss; the build still succeeds, it just can't use the cache.
                Tracer.Warning(context, $"Failed to list S3 selectors under `{prefix}`: {ex}");
                yield break;
            }

            foreach (S3Object s3Object in response.S3Objects)
            {
                if (_keys.TryParseSelector(s3Object.Key, fingerprint, out Selector selector))
                {
                    yield return selector;
                }
                else
                {
                    Tracer.Debug(context, $"Skipping unrecognized S3 selector key `{s3Object.Key}`");
                }
            }

            continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }

    /// <summary>
    /// Only used for PathSets, which the base class fetches while evaluating selectors, so the object is small enough
    /// to buffer. Output files are materialized in <see cref="CacheEntry.PlaceFilesAsync"/>.
    /// </summary>
    protected override async Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken)
    {
        OpenStreamResult localResult = await LocalCacheSession.OpenStreamAsync(context, contentHash, cancellationToken);
        if (localResult.Succeeded)
        {
            return localResult;
        }

        byte[]? bytes = await TryGetObjectBytesAsync(context, _keys.GetCas(contentHash), cancellationToken);
        if (bytes is null)
        {
            return new OpenStreamResult(localResult);
        }

        await PutLocalBytesAsync(context, contentHash, bytes, cancellationToken);
        return new OpenStreamResult(new MemoryStream(bytes, writable: false));
    }

    public override async ValueTask DisposeAsync()
    {
        _transferUtility.Dispose();
        _partGate.Dispose();
        _s3.Dispose();

        await base.DisposeAsync();
    }

    private Task EnsureContentInRemoteAsync(
        Context context,
        ContentHash contentHash,
        string absolutePath,
        CancellationToken cancellationToken)
        => _remoteUploads.GetOrAdd(
            contentHash,
            _ => PutOrPlaceFileGate.GatedOperationAsync(
                async (_, _) =>
                {
                    string casKey = _keys.GetCas(contentHash);
                    if (await ObjectExistsAsync(casKey, cancellationToken))
                    {
                        return 0;
                    }

                    // With async publishing the content is already in the local cache, so stream from there instead of
                    // contending with build operations which may still be touching the output.
                    OpenStreamResult localStream = await LocalCacheSession.OpenStreamAsync(context, contentHash, cancellationToken);
                    if (localStream.Succeeded && localStream.Stream is not null)
                    {
                        using (localStream.Stream)
                        {
                            await UploadContentAsync(casKey, localStream.Stream, cancellationToken);
                        }

                        return 0;
                    }

                    if (!File.Exists(absolutePath))
                    {
                        throw new CacheException($"Cannot publish content {contentHash.ToShortString()}: local cache miss and file '{absolutePath}' does not exist.");
                    }

                    using FileStream fileStream = File.OpenRead(absolutePath);
                    await UploadContentAsync(casKey, fileStream, cancellationToken);
                    return 0;
                },
                cancellationToken));

    private Task EnsureBytesInRemoteAsync(
        Context context,
        ContentHash contentHash,
        byte[] bytes,
        CancellationToken cancellationToken)
        => _remoteUploads.GetOrAdd(
            contentHash,
            _ => PutOrPlaceFileGate.GatedOperationAsync(
                async (_, _) =>
                {
                    // Also put it locally so OpenStreamAsync can serve it without a round trip.
                    await PutLocalBytesAsync(context, contentHash, bytes, cancellationToken);

                    string casKey = _keys.GetCas(contentHash);
                    if (!await ObjectExistsAsync(casKey, cancellationToken))
                    {
                        await PutObjectAsync(casKey, bytes, cancellationToken);
                    }

                    return 0;
                },
                cancellationToken));

    private async Task PlaceContentAsync(Context context, ContentHash contentHash, string absolutePath, CancellationToken cancellationToken)
    {
        CreateParentDirectory(absolutePath);

        PlaceFileResult placeResult = await PlaceFromLocalCacheAsync(context, contentHash, absolutePath, cancellationToken);
        if (placeResult.Succeeded)
        {
            PutLocalTaskCache.TryAdd(contentHash, Task.FromResult(new PutFileOperation(contentHash, BoolResult.Success)));
            return;
        }

        // Download beside the destination, hand the file to the local cache, then place it from there so the
        // realization and access modes end up the same as on a local cache hit.
        string tempPath = $"{absolutePath}.msbuildcache-{Guid.NewGuid():N}.tmp";
        try
        {
            if (!await TryDownloadToFileAsync(context, _keys.GetCas(contentHash), tempPath, contentHash, cancellationToken))
            {
                throw new CacheException($"Failed to materialize content {contentHash.ToShortString()} to '{absolutePath}': not found in local or remote cache.");
            }

            PutResult putResult = await LocalCacheSession.PutFileAsync(
                context,
                contentHash,
                new AbsolutePath(tempPath),
                FileRealizationMode.Move,
                cancellationToken);
            putResult.ThrowIfFailure();
            PutLocalTaskCache.TryAdd(contentHash, Task.FromResult(new PutFileOperation(contentHash, putResult)));
        }
        finally
        {
            // Move consumes the file on success; this covers the failure paths.
            TryDeleteFile(tempPath);
        }

        (await PlaceFromLocalCacheAsync(context, contentHash, absolutePath, cancellationToken)).ThrowIfFailure();
    }

    private Task<PlaceFileResult> PlaceFromLocalCacheAsync(Context context, ContentHash contentHash, string absolutePath, CancellationToken cancellationToken)
    {
        FileRealizationMode realizationMode = GetFileRealizationMode(absolutePath);
        return LocalCacheSession.PlaceFileAsync(
            context,
            contentHash,
            new AbsolutePath(absolutePath),
            realizationMode == FileRealizationMode.CopyNoVerify ? FileAccessMode.Write : FileAccessMode.ReadOnly,
            FileReplacementMode.ReplaceExisting,
            realizationMode,
            cancellationToken);
    }

    /// <summary>
    /// Downloads an object to disk without buffering it in memory, splitting it into concurrent ranged requests once
    /// it's large enough to be worth the extra round trips. Returns false if the object does not exist.
    /// </summary>
    private async Task<bool> TryDownloadToFileAsync(
        Context context,
        string key,
        string absolutePath,
        ContentHash expectedContentHash,
        CancellationToken cancellationToken)
    {
        long? length = await TryGetObjectLengthAsync(key, cancellationToken);
        if (length is null)
        {
            return false;
        }

        if (length.Value < _multipartThresholdBytes)
        {
            try
            {
                using GetObjectResponse response = await _s3.GetObjectAsync(_bucketName, key, cancellationToken);
                using FileStream destination = OpenForWrite(absolutePath, FileMode.Create, FileShare.None);
                await CopyExactlyAsync(response.ResponseStream, destination, length.Value, key, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                return false;
            }
        }
        else
        {
            // Establish the full length up front so parts can be written at their own offsets concurrently.
            using (FileStream placeholder = OpenForWrite(absolutePath, FileMode.Create, FileShare.ReadWrite))
            {
                placeholder.SetLength(length.Value);
            }

            // The Task.WhenAll below ensures no part is still holding this when it's disposed.
            using SemaphoreSlim objectGate = new(_maxConcurrentPartsPerObject);

            List<Task> partTasks = new();
            for (long offset = 0; offset < length.Value; offset += _partSizeBytes)
            {
                partTasks.Add(DownloadPartAsync(key, absolutePath, offset, Math.Min(offset + _partSizeBytes, length.Value) - 1, objectGate, cancellationToken));
            }

            try
            {
                await Task.WhenAll(partTasks);
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                // The object was removed between the metadata call and the ranged reads.
                return false;
            }

            Tracer.Debug(context, $"Downloaded `{key}` ({length.Value} bytes) as {partTasks.Count} parts");
        }

        long actualLength = new FileInfo(absolutePath).Length;
        if (actualLength != length.Value)
        {
            throw new CacheException($"Downloaded '{key}' to '{absolutePath}' with {actualLength} bytes, expected {length.Value}.");
        }

        await VerifyContentHashAsync(key, absolutePath, expectedContentHash);
        return true;
    }

    /// <summary>
    /// Parts are bounded twice: <paramref name="objectGate"/> caps how much of a single object is in flight, and a
    /// client-wide gate caps ranged requests across all objects being downloaded.
    /// </summary>
    private async Task DownloadPartAsync(string key, string absolutePath, long start, long end, SemaphoreSlim objectGate, CancellationToken cancellationToken)
    {
        await objectGate.WaitAsync(cancellationToken);
        try
        {
            await _partGate.WaitAsync(cancellationToken);
            try
            {
                using GetObjectResponse response = await _s3.GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = _bucketName,
                        Key = key,
                        ByteRange = new ByteRange(start, end),
                    },
                    cancellationToken);

                using FileStream destination = OpenForWrite(absolutePath, FileMode.Open, FileShare.ReadWrite);
                destination.Seek(start, SeekOrigin.Begin);
                await CopyExactlyAsync(response.ResponseStream, destination, end - start + 1, key, cancellationToken);
            }
            finally
            {
                _partGate.Release();
            }
        }
        finally
        {
            objectGate.Release();
        }
    }

    // FileAccess stays qualified as unqualified it binds to the Microsoft.MSBuildCache.FileAccess namespace.
    private static FileStream OpenForWrite(string absolutePath, FileMode mode, FileShare share)
        => new(absolutePath, mode, System.IO.FileAccess.Write, share, CopyBufferSize, useAsync: true);

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        string key,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
#if NETFRAMEWORK
            int read = await source.ReadAsync(buffer, 0, count, cancellationToken);
#else
            int read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
#endif
            if (read == 0)
            {
                throw new CacheException($"S3 object '{key}' ended with {remaining} expected bytes remaining.");
            }

#if NETFRAMEWORK
            await destination.WriteAsync(buffer, 0, read, cancellationToken);
#else
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
#endif
            remaining -= read;
        }
    }

    /// <summary>
    /// Guards against a truncated or misassembled transfer. A length check alone doesn't catch this, as the file is
    /// created at its full length before any part is written.
    /// </summary>
    private async Task VerifyContentHashAsync(string key, string absolutePath, ContentHash expectedContentHash)
    {
        using FileStream stream = new(
            absolutePath,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        ContentHash actualContentHash = await _hasher.GetContentHashAsync(stream);
        if (actualContentHash != expectedContentHash)
        {
            throw new CacheException($"S3 object '{key}' has content hash {actualContentHash}, expected {expectedContentHash}.");
        }
    }

    private async Task PutLocalBytesAsync(Context context, ContentHash contentHash, byte[] bytes, CancellationToken cancellationToken)
    {
        if (PutLocalTaskCache.ContainsKey(contentHash))
        {
            return;
        }

        using MemoryStream stream = new(bytes, writable: false);
        PutResult putResult = await LocalCacheSession.PutStreamAsync(context, contentHash.HashType, stream, cancellationToken);
        if (!putResult.Succeeded)
        {
            Tracer.Debug(context, $"Failed to put content {contentHash.ToShortString()} into the local cache: {putResult}");
            return;
        }

        if (putResult.ContentHash != contentHash)
        {
            throw new CacheException($"Content for {contentHash} hashed as {putResult.ContentHash}.");
        }

        PutLocalTaskCache.TryAdd(contentHash, Task.FromResult(new PutFileOperation(contentHash, putResult)));
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task<byte[]?> TryGetObjectBytesAsync(Context context, string key, CancellationToken cancellationToken)
    {
        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(_bucketName, key, cancellationToken);
            using MemoryStream buffer = new();
            await response.ResponseStream.CopyToAsync(buffer
#if !NETFRAMEWORK
                , cancellationToken
#endif
                );
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Treat as a cache miss; the build still succeeds, it just can't use the cache.
            Tracer.Warning(context, $"Failed to get S3 object `{key}`: {ex}");
            return null;
        }
    }

    private async Task<long?> TryGetObjectLengthAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            GetObjectMetadataResponse metadata = await _s3.GetObjectMetadataAsync(_bucketName, key, cancellationToken);
            return metadata.ContentLength;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private async Task<bool> ObjectExistsAsync(string key, CancellationToken cancellationToken)
        => await TryGetObjectLengthAsync(key, cancellationToken) is not null;

    /// <summary>
    /// Uploads cache content, splitting large objects into parts uploaded in parallel. This also caps how much work a
    /// failed request throws away, which matters for the very large static libraries some builds produce.
    /// </summary>
    private async Task UploadContentAsync(string key, Stream stream, CancellationToken cancellationToken)
    {
        // TransferUtility can only split a stream it can seek; a non-seekable one falls back to a single request.
        if (!stream.CanSeek || stream.Length < _multipartThresholdBytes)
        {
            await PutObjectAsync(key, stream, cancellationToken);
            return;
        }

        await _transferUtility.UploadAsync(
            new TransferUtilityUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = stream,
                PartSize = _partSizeBytes,
                AutoCloseStream = false,
            },
            cancellationToken);
    }

    // Uploads in a single request. The stream stays owned by the caller.
    private async Task PutObjectAsync(string key, Stream stream, CancellationToken cancellationToken)
    {
        _ = await _s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = stream,
                AutoCloseStream = false,
            },
            cancellationToken);
    }

    private async Task PutObjectAsync(string key, byte[] bytes, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new(bytes, writable: false);
        await PutObjectAsync(key, stream, cancellationToken);
    }

    internal static bool IsNotFound(AmazonS3Exception ex)
        => ex.StatusCode == HttpStatusCode.NotFound
           || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ex.ErrorCode, "NoSuchBucket", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

    private sealed class CacheEntry : ICacheEntry
    {
        private readonly S3CacheClient _client;
        private readonly byte[] _nodeBuildResultBytes;

        public CacheEntry(S3CacheClient client, byte[] nodeBuildResultBytes)
        {
            _client = client;
            _nodeBuildResultBytes = nodeBuildResultBytes;
        }

        public void Dispose()
        {
        }

        public Task<Stream?> GetNodeBuildResultAsync(Context context, CancellationToken cancellationToken)
            => Task.FromResult<Stream?>(new MemoryStream(_nodeBuildResultBytes, writable: false));

        public async Task PlaceFilesAsync(Context context, IReadOnlyDictionary<string, ContentHash> files, CancellationToken cancellationToken)
        {
            List<Task> tasks = new(files.Count);
            foreach (KeyValuePair<string, ContentHash> kvp in files)
            {
                string path = kvp.Key;
                ContentHash hash = kvp.Value;
                tasks.Add(_client.PutOrPlaceFileGate.GatedOperationAsync(
                    async (_, _) =>
                    {
                        await _client.PlaceContentAsync(context, hash, path, cancellationToken);
                        return 0;
                    },
                    cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
    }
}
