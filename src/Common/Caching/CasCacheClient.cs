// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
#if NET472
using BuildXL.Utilities.Collections;
#endif
using Microsoft.MSBuildCache.Fingerprinting;
using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.Caching;

public sealed class CasCacheClient : CacheClient
{
    private readonly record struct PlaceFileOperation(ContentHash Hash, AbsolutePath FilePath, PlaceFileResult Result);

    private readonly ConcurrentDictionary<ContentHash, Task<PutFileOperation>> _putRemoteTaskCache = new();

    private readonly ICache? _remoteCache;

    private readonly ICacheSession? _remoteCacheSession;
    private readonly ICacheSession _twoLevelCacheSession;

    public CasCacheClient(
        Context rootContext,
        IFingerprintFactory fingerprintFactory,
        ICache localCache,
        ICacheSession localCacheSession,
        (ICache cache, ICacheSession session, TwoLevelCacheConfiguration config)? remoteCache,
        HashType hashType,
        AbsolutePath repoRoot,
        INodeContextRepository nodeContextRepository,
        Func<string, FileRealizationMode> getFileRealizationMode,
        int maxConcurrentCacheContentOperations,
        bool enableAsyncPublishing,
        bool enableAsyncMaterialization)
        : base(rootContext, fingerprintFactory, hashType, repoRoot, nodeContextRepository, getFileRealizationMode, localCache, localCacheSession, maxConcurrentCacheContentOperations, enableAsyncPublishing, enableAsyncMaterialization)
    {
        ICacheSession cacheSession;
        if (remoteCache == null)
        {
            cacheSession = localCacheSession;
            _remoteCache = null;
        }
        else
        {
            _remoteCacheSession = remoteCache.Value.session;
            cacheSession = new TwoLevelCacheSession(
                nameof(TwoLevelCacheSession),
                localCacheSession,
                _remoteCacheSession,
                SystemClock.Instance,
                remoteCache.Value.config);
            _remoteCache = remoteCache.Value.cache;
        }

        _twoLevelCacheSession = cacheSession;
    }

    public override async ValueTask DisposeAsync()
    {
        // this also shuts down and disposes _remoteCacheSession
        await _twoLevelCacheSession.ShutdownAsync(RootContext);
        _twoLevelCacheSession.Dispose();

        if (_remoteCache != null)
        {
            await ShutdownCacheAsync(_remoteCache);
        }

        PutOrPlaceFileGate.Dispose();

        await base.DisposeAsync();
    }

    protected override async IAsyncEnumerable<Selector> GetSelectors(Context context, Fingerprint fingerprint, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (GetSelectorResult? selectorResult in _twoLevelCacheSession.GetSelectors(context, fingerprint, cancellationToken))
        {
            if (!selectorResult.Succeeded)
            {
                Tracer.Debug(context, $"{nameof(GetSelectors)} failed for weak fingerprint {fingerprint}: {selectorResult}");
            }
            else
            {
                yield return selectorResult.Selector;
            }
        }
    }

    protected override Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken)
        => _twoLevelCacheSession.OpenStreamAsync(context, contentHash, cancellationToken);

    protected override async Task AddNodeAsync(
        Context context,
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<AbsolutePath, ContentHash> outputs,
        (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
        (ContentHash hash, byte[] bytes)? pathSetBytes,
        CancellationToken cancellationToken)
    {
        // Make a reverse lookup
        Dictionary<ContentHash, AbsolutePath> contentAbsolutePaths = new(outputs.Count);
        foreach (KeyValuePair<AbsolutePath, ContentHash> kvp in outputs)
        {
            AbsolutePath filePath = kvp.Key;
            ContentHash contentHash = kvp.Value;
            contentAbsolutePaths.TryAdd(contentHash, filePath);
        }

        // Store metadata blobs in local cache
        List<(ContentHash hash, byte[] bytes)> metadata = new(2);
        metadata.Add(nodeBuildResultBytes);
        if (pathSetBytes != null)
        {
            metadata.Add(pathSetBytes.Value);
        }

        await Task.WhenAll(metadata.Select(metadata => PutLocalTaskCache.GetOrAdd(metadata.hash, async hash =>
        {
            using var ms = new MemoryStream(metadata.bytes);

            BoolResult putResult = await PutOrPlaceFileGate.GatedOperationAsync(
                (_, _) => LocalCacheSession.PutStreamAsync(
                    context,
                    hash.HashType,
                    ms,
                    cancellationToken),
                cancellationToken);

            // We don't want PutLocalTaskCache to cache a failed attempt. 
            putResult.ThrowIfFailure();

            return new PutFileOperation(metadata.hash, putResult);
        })));

        static async Task checkUploadResultsAsync(List<Task<PutFileOperation>> uploadTasks)
        {
            var uploadResults = await Task.WhenAll(uploadTasks);

            if (uploadResults.Any(uploadResult => !uploadResult.Result.Succeeded))
            {
                List<CacheException> exceptions = uploadResults
                    .Where(uploadResult => !uploadResult.Result.Succeeded)
                    .Select(uploadResult => new CacheException($"Add content failed for hash {uploadResult.Hash.ToShortHash()}: {uploadResult.Result}"))
                    .ToList();
                throw new AggregateException($"Putting content failed.", exceptions);
            }
        }

        // calculate the set of content to store
        List<ContentHash> pinContentHashes = new(contentAbsolutePaths.Count + 2);
        pinContentHashes.AddRange(contentAbsolutePaths.Keys);
        pinContentHashes.AddRange(metadata.Select(md => md.hash));

        // With EnableAsyncPublishing=true, content is already in the local cache. When false, we need to place it in the local cache from the repo.
        if (!EnableAsyncPublishing)
        {
            // determine what needs to be uploaded
            PutFileOperation[] pinResults = await PinBulkAsync(context, LocalCacheSession, PutLocalTaskCache, pinContentHashes, cancellationToken);

            // upload the needed content from where it resides
            List<Task<PutFileOperation>> uploadTasks = new(pinContentHashes.Count);
            foreach (PutFileOperation pinResult in pinResults.Where(r => !r.Result.Succeeded))
            {
                uploadTasks.Add(PutLocalTaskCache.GetOrAdd(pinResult.Hash, hash => PutOrPlaceFileGate.GatedOperationAsync(async (_, _) =>
                {
                    PutResult putResult = await PutFileCoreAsync(
                        context,
                        LocalCacheSession,
                        hash,
                        contentAbsolutePaths[hash],
                        cancellationToken);

                    putResult.ThrowIfFailure();

                    return new PutFileOperation(hash, putResult);
                })));
            }

            await checkUploadResultsAsync(uploadTasks);
        }

        // Now that we've ensured everything is in the local cache, we can upload to the remote cache.
        if (_remoteCacheSession != null)
        {
            // determine what needs to be uploaded
            PutFileOperation[] pinResults = await PinBulkAsync(context, _remoteCacheSession, _putRemoteTaskCache, pinContentHashes, cancellationToken);

            // upload the needed content from where it resides
            List<Task<PutFileOperation>> uploadTasks = new(pinContentHashes.Count);
            foreach (PutFileOperation pinResult in pinResults.Where(r => !r.Result.Succeeded))
            {
                uploadTasks.Add(_putRemoteTaskCache.GetOrAdd(pinResult.Hash, hash => PutOrPlaceFileGate.GatedOperationAsync(async (_, _) =>
                {
                    using Stream stream = (await LocalCacheSession!.OpenStreamAsync(context, hash, cancellationToken)).Stream!;
                    PutResult putResult = await _remoteCacheSession.PutStreamAsync(context, hash, stream!, cancellationToken);

                    putResult.ThrowIfFailure();

                    return new PutFileOperation(hash, putResult);
                })));
            }

            await checkUploadResultsAsync(uploadTasks);
        }

        var contentHashes = new ContentHash[contentAbsolutePaths.Count + 1];

        // metadata blob is blob0
        contentHashes[0] = nodeBuildResultBytes.hash;

        // Copy the rest of the content hashes
        {
            int i = 1;
            foreach (ContentHash contentHash in contentAbsolutePaths.Keys)
            {
                contentHashes[i] = contentHash;
                i++;
            }
        }

        var contentHashList = new ContentHashList(contentHashes);

        AddOrGetContentHashListResult addResult = await _twoLevelCacheSession.AddOrGetContentHashListAsync(
            context,
            fingerprint,
            new ContentHashListWithDeterminism(contentHashList, CacheDeterminism.None),
            cancellationToken);

        if (!addResult.Succeeded)
        {
            throw new CacheException($"{nameof(_twoLevelCacheSession.AddOrGetContentHashListAsync)} failed for {fingerprint}.");
        }

        // TODO dfederm: Handle CHL races
    }

    protected override async Task<ICacheEntry?> GetCacheEntryAsync(
        Context context,
        StrongFingerprint cacheStrongFingerprint,
        CancellationToken cancellationToken)
    {
        GetContentHashListResult getContentHashListResult = await _twoLevelCacheSession.GetContentHashListAsync(context, cacheStrongFingerprint, cancellationToken);
        if (!getContentHashListResult.Succeeded)
        {
            Tracer.Debug(context, $"{nameof(_twoLevelCacheSession.GetContentHashListAsync)} failed for {cacheStrongFingerprint}: {getContentHashListResult}");
            return null;
        }

        ContentHashList? contentHashList = getContentHashListResult.ContentHashListWithDeterminism.ContentHashList;
        if (contentHashList is null)
        {
            Tracer.Debug(context, $"ContentHashList is null for {cacheStrongFingerprint}: {getContentHashListResult}");
            return null;
        }

        // Ensure all the content is available before declaring victory
        // All remote cache implementations guarantee the CHLs are backed by content, so we only need to check for the local-only case.
        if (_remoteCacheSession == null)
        {
            PutFileOperation[] pinResults = await PinBulkAsync(context, LocalCacheSession, PutLocalTaskCache, contentHashList.Hashes, cancellationToken);
            if (pinResults.Any(result => !result.Result.Succeeded))
            {
                Tracer.Debug(context, $"Pinning content failed for {cacheStrongFingerprint}: {string.Join(", ", pinResults.Where(result => !result.Result.Succeeded).Select(result => result.Result.ToString()))}");
                return null;
            }
        }

        return new CacheEntry(this, contentHashList);
    }

    private sealed class CacheEntry : ICacheEntry
    {
        private readonly CasCacheClient _casCacheClient;
        private readonly ContentHashList _contentHashList;

        public CacheEntry(CasCacheClient casCacheClient, ContentHashList contentHashList)
        {
            _casCacheClient = casCacheClient;
            _contentHashList = contentHashList;
        }

        public async Task<Stream?> GetNodeBuildResultAsync(Context context, CancellationToken cancellationToken)
        {
            ContentHash hash = _contentHashList.Hashes[0];
            var streamResult = await _casCacheClient.OpenStreamAsync(context, hash, cancellationToken);
            if (streamResult.Succeeded)
            {
                return streamResult.Stream!;
            }
            else
            {
                _casCacheClient.Tracer.Debug(context, $"Failed to open stream to NodeBuildResult {hash}.");
                return null;
            }
        }

        public async Task PlaceFilesAsync(Context context, IReadOnlyDictionary<AbsolutePath, ContentHash> files, CancellationToken cancellationToken)
        {
            // Place all the files on disk
            List<Task<PlaceFileOperation>> placeFileTasks = new(files.Count);
            foreach (KeyValuePair<AbsolutePath, ContentHash> kvp in files)
            {
                AbsolutePath filePath = kvp.Key;
                ContentHash contentHash = kvp.Value;

                Task<PlaceFileOperation> placeResultTask = _casCacheClient.PutOrPlaceFileGate.GatedOperationAsync(
                    async (_, _) =>
                    {
                        PlaceFileResult result = await _casCacheClient.PlaceFileCoreAsync(context, contentHash, filePath, cancellationToken);
                        return new PlaceFileOperation(contentHash, filePath, result);
                    },
                    cancellationToken);

                placeFileTasks.Add(placeResultTask);
            }

            // TODO dfederm: Can we recover from failures? GetNodeAsync will verify the cache content exists, so this indicates a problem strictly with placement.
            // It's clear this should be a cache miss but should we try deleting stuff which was placed to avoid incremental weirdness? Or do we just mark the node as
            // "bad" and non-cacheable for this build session?
            PlaceFileOperation[] placeFileOperations = await Task.WhenAll(placeFileTasks);
            if (placeFileOperations.Any(placeFileOperation => !placeFileOperation.Result.Succeeded))
            {
                List<CacheException> exceptions = placeFileOperations
                    .Where(placeFileOperation => !placeFileOperation.Result.IsPlaced() && placeFileOperation.Result.Code != PlaceFileResult.ResultCode.NotPlacedAlreadyExists)
                    .Select(placeFileOperation => new CacheException($"Get content failed for hash {placeFileOperation.Hash.ToShortHash()} and path {placeFileOperation.FilePath}: {placeFileOperation.Result}"))
                    .ToList();
                throw new AggregateException($"Placing content failed.", exceptions);
            }
        }

        public void Dispose() { }
    }

    private static async Task<PutFileOperation[]> PinBulkAsync(
        Context context,
        IContentSession cacheSession,
        ConcurrentDictionary<ContentHash, Task<PutFileOperation>> putTaskCache,
        IReadOnlyList<ContentHash> contentHashes,
        CancellationToken cancellationToken)
    {
        List<ContentHash> hashesToPin = new(contentHashes.Count);
        List<Task<PutFileOperation>> putTasks = new(contentHashes.Count);

        // no need to try to pin things that we already know are pinned
        foreach (ContentHash contentHash in contentHashes)
        {
            if (putTaskCache.TryGetValue(contentHash, out Task<PutFileOperation>? existingTask))
            {
                putTasks.Add(existingTask!);
            }
            else
            {
                hashesToPin.Add(contentHash);
            }
        }

        IEnumerable<Task<Indexed<PinResult>>> resultSet = await cacheSession.PinAsync(context, hashesToPin, cancellationToken);

        foreach (Task<Indexed<PinResult>> resultTask in resultSet)
        {
            Indexed<PinResult> indexedResult = await resultTask;
            PinResult result = indexedResult.Item;
            ContentHash contentHash = hashesToPin[indexedResult.Index];

            PutFileOperation operation = new(contentHash, result);
            putTasks.Add(Task.FromResult(operation));

            // add successes to the cache
            if (operation.Result.Succeeded)
            {
                putTaskCache.TryAdd(contentHash, Task.FromResult(operation));
            }
        }

        return await Task.WhenAll(putTasks);
    }

    private async Task<PutResult> PutFileCoreAsync(
        Context context,
        IContentSession cacheSession,
        ContentHash contentHash,
        AbsolutePath filePath,
        CancellationToken cancellationToken)
    {
        return await cacheSession.PutFileAsync(
            context,
            contentHash,
            filePath,
            GetFileRealizationMode(filePath.Path),
            cancellationToken);
    }

    private async Task<PlaceFileResult> PlaceFileCoreAsync(
        Context context,
        ContentHash contentHash,
        AbsolutePath filePath,
        CancellationToken cancellationToken)
    {
        FileRealizationMode realizationMode = GetFileRealizationMode(filePath.Path);
        FileAccessMode accessMode = realizationMode == FileRealizationMode.CopyNoVerify
            ? FileAccessMode.Write
            : FileAccessMode.ReadOnly;

        CreateParentDirectory(filePath);

        PlaceFileResult placeResult = await _twoLevelCacheSession.PlaceFileAsync(
            context,
            contentHash,
            filePath,
            accessMode,
            FileReplacementMode.ReplaceExisting,
            realizationMode,
            cancellationToken);

        if (placeResult.Succeeded)
        {
            // Ensure we don't attempt to put content we successfully placed, since we know the cache has it.
            PutLocalTaskCache.TryAdd(contentHash, Task.FromResult(new PutFileOperation(contentHash, BoolResult.Success)));
        }

        return placeResult;
    }
}