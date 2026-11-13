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

// BuildXL has no S3 cache, so unlike CasCacheClient there is no two-level cache. The local cache is only used as a
// content store and every fingerprint lookup goes to S3.
internal sealed class S3CacheClient : CacheClient
{
    private const int CopyBufferSize = 128 * 1024;

    // S3 rejects parts smaller than this, except for the last part of a multipart upload.
    private const long MinimumPartSizeBytes = 5 * 1024 * 1024;

    // A single CopyObject request cannot renew anything larger than this.
    private const long MaximumSingleRequestCopyBytes = 5L * 1024 * 1024 * 1024;

    private readonly IAmazonS3 _s3;
    private readonly IContentHasher _hasher;
    private readonly TransferUtility _transferUtility;
    private readonly S3ObjectKeys _keys;
    private readonly string _bucketName;
    private readonly bool _remoteCacheIsReadOnly;
    private readonly long _multipartThresholdBytes;
    private readonly long _partSizeBytes;
    private readonly int _maxConcurrentPartsPerObject;
    private readonly TimeSpan _contentRenewalAge;

    // The same content is often published by many projects.
    private readonly ConcurrentDictionary<ContentHash, Task> _remoteUploads = new();

    // Bounds ranged requests across all objects, since each object being downloaded fans out into parts.
    private readonly SemaphoreSlim _partGate;

    private readonly S3ReadRetry _readRetry = new();

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
        int maxConcurrentPartsPerObject,
        double contentRenewalAgeDays)
        : base(rootContext, fingerprintFactory, hasher, repoRoot, nugetPackageRoot, getFileRealizationMode, localCache, localCas, maxConcurrentCacheContentOperations, enableAsyncPublishing, enableAsyncMaterialization, skipUnchangedOutputFiles, touchOutputFiles)
    {
        _s3 = s3;
        _hasher = hasher;
        _bucketName = bucketName;
        _remoteCacheIsReadOnly = remoteCacheIsReadOnly;
        _multipartThresholdBytes = Math.Max(1, multipartThresholdBytes);
        _partSizeBytes = Math.Max(MinimumPartSizeBytes, multipartPartSizeBytes);
        _maxConcurrentPartsPerObject = Math.Max(1, maxConcurrentPartsPerObject);
        _contentRenewalAge = double.IsNaN(contentRenewalAgeDays) || double.IsInfinity(contentRenewalAgeDays)
            ? TimeSpan.Zero
            : TimeSpan.FromDays(Math.Min(365, Math.Max(0, contentRenewalAgeDays)));
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
            return AddNodeResult.Skipped;
        }

        string entryKey = _keys.GetEntry(fingerprint);
        bool alreadyExists = await TryGetObjectMetadataAsync(context, entryKey, cancellationToken) is not null;

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

        // The NodeBuildResult is stored inline in the entry, but the PathSet is fetched by hash.
        if (pathSetBytes is not null)
        {
            uploadTasks.Add(EnsureBytesInRemoteAsync(context, pathSetBytes.Value.hash, pathSetBytes.Value.bytes, cancellationToken));
        }

        await Task.WhenAll(uploadTasks);

        // Also rewrite an existing entry. Its selector is always rewritten, and a lifecycle rule counts from the last write.
        await PutObjectAsync(entryKey, nodeBuildResultBytes.bytes, cancellationToken);
        Tracer.Debug(context, $"Stored S3 cache entry `{entryKey}` for {fingerprint}");

        // Write the selector last so that a listed selector always has an entry. Also write it when the entry already
        // exists, in case an earlier publish failed after writing the entry.
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

    // Only used for PathSets, which are small enough to buffer.
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

    // A failed upload is forgotten so that a later node can retry it.
    private async Task UploadOnceAsync(ContentHash contentHash, Func<Task> uploadAsync)
    {
        while (true)
        {
            Task? started = null;
            Task upload = _remoteUploads.GetOrAdd(contentHash, _ => started = uploadAsync());

            if (ReferenceEquals(upload, started))
            {
                await upload;
                return;
            }

            try
            {
                await upload;
                return;
            }
            catch
            {
                _remoteUploads.TryRemove(contentHash, out _);
            }
        }
    }

    private Task EnsureContentInRemoteAsync(
        Context context,
        ContentHash contentHash,
        string absolutePath,
        CancellationToken cancellationToken)
        => UploadOnceAsync(
            contentHash,
            () => PutOrPlaceFileGate.GatedOperationAsync(
                async (_, _) =>
                {
                    string casKey = _keys.GetCas(contentHash);
                    GetObjectMetadataResponse? existing = await TryGetObjectMetadataAsync(context, casKey, cancellationToken);
                    if (existing is not null && await RenewObjectAsync(context, casKey, existing, cancellationToken))
                    {
                        return 0;
                    }

                    // With synchronous publishing the output isn't in the local cache yet. Put it there rather than
                    // uploading the file directly, which could have changed since it was hashed.
                    OpenStreamResult localStream = await LocalCacheSession.OpenStreamAsync(context, contentHash, cancellationToken);
                    if (!localStream.Succeeded || localStream.Stream is null)
                    {
                        if (!File.Exists(absolutePath))
                        {
                            throw new CacheException($"Cannot publish content {contentHash.ToShortString()}: local cache miss and file '{absolutePath}' does not exist.");
                        }

                        PutResult putResult = await LocalCacheSession.PutFileAsync(
                            context,
                            _hasher.Info.HashType,
                            new AbsolutePath(absolutePath),
                            GetFileRealizationMode(absolutePath),
                            cancellationToken);
                        putResult.ThrowIfFailure();

                        if (putResult.ContentHash != contentHash)
                        {
                            throw new CacheException($"Cannot publish content {contentHash.ToShortString()}: file '{absolutePath}' changed and now hashes as {putResult.ContentHash}.");
                        }

                        PutLocalTaskCache.TryAdd(contentHash, Task.FromResult(new PutFileOperation(contentHash, putResult)));
                        localStream = await LocalCacheSession.OpenStreamAsync(context, contentHash, cancellationToken);
                        localStream.ThrowIfFailure();
                    }

                    using Stream stream = localStream.Stream
                        ?? throw new CacheException($"The local cache returned no stream for content {contentHash.ToShortString()}.");
                    await UploadContentAsync(casKey, stream, cancellationToken);
                    return 0;
                },
                cancellationToken));

    private Task EnsureBytesInRemoteAsync(
        Context context,
        ContentHash contentHash,
        byte[] bytes,
        CancellationToken cancellationToken)
        => UploadOnceAsync(
            contentHash,
            () => PutOrPlaceFileGate.GatedOperationAsync(
                async (_, _) =>
                {
                    await PutLocalBytesAsync(context, contentHash, bytes, cancellationToken);

                    string casKey = _keys.GetCas(contentHash);
                    GetObjectMetadataResponse? existing = await TryGetObjectMetadataAsync(context, casKey, cancellationToken);
                    if (existing is null || !await RenewObjectAsync(context, casKey, existing, cancellationToken))
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

        // Download into the local cache and place from there, so the file ends up the same as on a local cache hit.
        string tempPath = $"{absolutePath}.msbuildcache-{Guid.NewGuid():N}.tmp";
        string casKey = _keys.GetCas(contentHash);
        try
        {
            if (!await TryDownloadToFileAsync(context, casKey, tempPath, contentHash, cancellationToken))
            {
                // MSBuild was already told this was a cache hit, so nothing else will produce the output.
                throw new CacheException($"Failed to materialize content {contentHash.ToShortString()} to '{absolutePath}' from `{GetObjectUrl(casKey)}`.");
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

    private async Task<bool> TryDownloadToFileAsync(
        Context context,
        string key,
        string absolutePath,
        ContentHash expectedContentHash,
        CancellationToken cancellationToken)
    {
        long? length = await TryGetObjectLengthAsync(context, key, cancellationToken);
        if (length is null)
        {
            return false;
        }

        if (length.Value < _multipartThresholdBytes)
        {
            try
            {
                bool downloaded = await _readRetry.TryRunAsync(
                    $"Download of `{GetObjectUrl(key)}` ({length.Value} bytes)",
                    async () =>
                    {
                        using GetObjectResponse response = await _s3.GetObjectAsync(_bucketName, key, cancellationToken);

                        using FileStream destination = OpenForWrite(absolutePath, FileMode.Create, FileShare.None);
                        await CopyExactlyAsync(response.ResponseStream, destination, length.Value, key, cancellationToken);
                    },
                    message => Tracer.Debug(context, message),
                    message => Tracer.Warning(context, message),
                    cancellationToken);

                if (!downloaded)
                {
                    return false;
                }
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                return false;
            }
        }
        else
        {
            using (FileStream placeholder = OpenForWrite(absolutePath, FileMode.Create, FileShare.ReadWrite))
            {
                placeholder.SetLength(length.Value);
            }

            using SemaphoreSlim objectGate = new(_maxConcurrentPartsPerObject);

            List<Task<bool>> partTasks = new();
            for (long offset = 0; offset < length.Value; offset += _partSizeBytes)
            {
                partTasks.Add(DownloadPartAsync(context, key, absolutePath, offset, Math.Min(offset + _partSizeBytes, length.Value) - 1, objectGate, cancellationToken));
            }

            bool[] parts;
            try
            {
                parts = await Task.WhenAll(partTasks);
            }
            catch (AmazonS3Exception ex) when (IsNotFound(ex))
            {
                return false;
            }

            foreach (bool part in parts)
            {
                if (!part)
                {
                    return false;
                }
            }

            Tracer.Debug(context, $"Downloaded `{key}` ({length.Value} bytes) as {partTasks.Count} parts");
        }

        long actualLength = new FileInfo(absolutePath).Length;
        if (actualLength != length.Value)
        {
            throw new CacheException($"Downloaded '{key}' to '{absolutePath}' with {actualLength} bytes, expected {length.Value}.");
        }

        await VerifyContentHashAsync(key, absolutePath, expectedContentHash, cancellationToken);
        return true;
    }

    private async Task<bool> DownloadPartAsync(Context context, string key, string absolutePath, long start, long end, SemaphoreSlim objectGate, CancellationToken cancellationToken)
    {
        await objectGate.WaitAsync(cancellationToken);
        try
        {
            await _partGate.WaitAsync(cancellationToken);
            try
            {
                return await _readRetry.TryRunAsync(
                    $"Download of `{GetObjectUrl(key)}` bytes {start}-{end}",
                    async () =>
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
                    },
                    message => Tracer.Debug(context, message),
                    message => Tracer.Warning(context, message),
                    cancellationToken);
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
                throw new IOException($"S3 object '{key}' ended with {remaining} expected bytes remaining.");
            }

#if NETFRAMEWORK
            await destination.WriteAsync(buffer, 0, read, cancellationToken);
#else
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
#endif
            remaining -= read;
        }
    }

    // Multipart downloads are written into a file of the full length, so a length check can't catch a misassembled one.
    private async Task VerifyContentHashAsync(
        string key,
        string absolutePath,
        ContentHash expectedContentHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using FileStream stream = new(
            absolutePath,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        ContentHash actualContentHash = await _hasher.GetContentHashAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
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

    private string GetObjectUrl(string key)
    {
        string serviceUrl = _s3.Config.ServiceURL ?? string.Empty;
        return serviceUrl.Length == 0
            ? $"s3://{_bucketName}/{key}"
            : $"{serviceUrl.TrimEnd('/')}/{_bucketName}/{key}";
    }

    private async Task<byte[]?> TryGetObjectBytesAsync(Context context, string key, CancellationToken cancellationToken)
    {
        try
        {
            (bool succeeded, byte[]? bytes) = await _readRetry.TryRunAsync(
                $"Read of `{GetObjectUrl(key)}`",
                async () =>
                {
                    using GetObjectResponse response = await _s3.GetObjectAsync(_bucketName, key, cancellationToken);
                    using MemoryStream buffer = new();
                    await CopyToAsync(response.ResponseStream, buffer, cancellationToken);
                    return buffer.ToArray();
                },
                message => Tracer.Debug(context, message),
                message => Tracer.Warning(context, message),
                cancellationToken);

            return succeeded ? bytes : null;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private static async Task CopyToAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[CopyBufferSize];
        int read;
#if NETFRAMEWORK
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) != 0)
        {
            await destination.WriteAsync(buffer, 0, read, cancellationToken);
        }
#else
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
#endif
    }

    private async Task<GetObjectMetadataResponse?> TryGetObjectMetadataAsync(Context context, string key, CancellationToken cancellationToken)
    {
        try
        {
            (bool succeeded, GetObjectMetadataResponse? metadata) = await _readRetry.TryRunAsync(
                $"Metadata of `{GetObjectUrl(key)}`",
                () => _s3.GetObjectMetadataAsync(_bucketName, key, cancellationToken),
                message => Tracer.Debug(context, message),
                message => Tracer.Warning(context, message),
                cancellationToken);

            return succeeded ? metadata : null;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
    }

    private async Task<long?> TryGetObjectLengthAsync(Context context, string key, CancellationToken cancellationToken)
        => (await TryGetObjectMetadataAsync(context, key, cancellationToken))?.ContentLength;

    // Returns false when the caller must upload the content instead. A failed renewal is not safe to ignore,
    // because the entry written afterwards would name content that can expire first.
    private async Task<bool> RenewObjectAsync(Context context, string key, GetObjectMetadataResponse metadata, CancellationToken cancellationToken)
    {
        if (_contentRenewalAge > TimeSpan.Zero)
        {
            TimeSpan age = DateTime.UtcNow - metadata.LastModified.ToUniversalTime();
            if (age >= TimeSpan.Zero && age < _contentRenewalAge)
            {
                return true;
            }
        }

        if (metadata.ContentLength >= MaximumSingleRequestCopyBytes)
        {
            return false;
        }

        try
        {
            _ = await _s3.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = _bucketName,
                    SourceKey = key,
                    DestinationBucket = _bucketName,
                    DestinationKey = key,
                    MetadataDirective = S3MetadataDirective.REPLACE,
                    StorageClass = metadata.StorageClass,
                },
                cancellationToken);

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Tracer.Warning(context, $"Failed to renew `{GetObjectUrl(key)}`, uploading it again: {ex}");
            return false;
        }
    }

    private async Task UploadContentAsync(string key, Stream stream, CancellationToken cancellationToken)
    {
        // TransferUtility can only split a seekable stream.
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
