// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.Build.Graph;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Fingerprint = Microsoft.MSBuildCache.Fingerprinting.Fingerprint;
using WeakFingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.Caching;

public abstract class CacheClient : ICacheClient
{
    private static readonly byte[] EmptySelectorOutput = new byte[1];
    private readonly OutputHasher _outputHasher;
    private readonly ConcurrentDictionary<NodeContext, Task> _publishingTasks = new();
    private readonly ConcurrentDictionary<NodeContext, Task> _materializationTasks = new();
    private readonly IContentHasher _hasher;
    private readonly IFingerprintFactory _fingerprintFactory;
    private readonly INodeContextRepository _nodeContextRepository;
    private readonly bool _enableAsyncMaterialization;

    protected CacheClient(
        Context rootContext,
        IFingerprintFactory fingerprintFactory,
        HashType hashType,
        AbsolutePath repoRoot,
        INodeContextRepository nodeContextRepository,
        Func<string, FileRealizationMode> getFileRealizationMode,
        IContentSession localCas,
        int maxConcurrentCacheContentOperations,
        bool enableAsyncPublishing,
        bool enableAsyncMaterialization)
    {
        RootContext = rootContext;
        _fingerprintFactory = fingerprintFactory;
        HashInfo hashInfo = HashInfoLookup.Find(hashType);
        _hasher = hashInfo.CreateContentHasher();
        EmptySelector = new(hashInfo.EmptyHash, EmptySelectorOutput);
        RepoRoot = repoRoot;
        _nodeContextRepository = nodeContextRepository;
        LocalCacheSession = localCas;
        EnableAsyncPublishing = enableAsyncPublishing;
        _enableAsyncMaterialization = enableAsyncMaterialization;
        GetFileRealizationMode = getFileRealizationMode;

        PutOrPlaceFileGate = new SemaphoreSlim(maxConcurrentCacheContentOperations);

        // When async publishing, we actually need to capture the contents of the files into the L1 to avoid
        // access contention with ongoing build operations.
        if (EnableAsyncPublishing)
        {
            _outputHasher = new OutputHasher((path, ct) => PutOrPlaceFileGate.GatedOperationAsync(async (_, _) =>
            {
                var result = await LocalCacheSession.PutFileAsync(RootContext, _hasher.Info.HashType, new AbsolutePath(path), GetFileRealizationMode(path), ct);
                result.ThrowIfFailure();
                PutLocalTaskCache.TryAdd(result.ContentHash, Task.FromResult(new PutFileOperation(result.ContentHash, result)));
                return result.ContentHash;
            }));
        }
        else
        {
            _outputHasher = new OutputHasher(_hasher);
        }
    }

    protected Tracer Tracer { get; } = new Tracer(nameof(CacheClient));

    protected Context RootContext { get; }

    protected AbsolutePath RepoRoot { get; }

    protected Selector EmptySelector { get; }

    protected IContentSession LocalCacheSession { get; }

    protected bool EnableAsyncPublishing { get; }

    protected SemaphoreSlim PutOrPlaceFileGate { get; }

    protected ConcurrentDictionary<ContentHash, Task<PutFileOperation>> PutLocalTaskCache { get; } = new();

    protected Func<string, FileRealizationMode> GetFileRealizationMode { get; }

    /* abstract methods for subclasses to implement */
    protected abstract Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken);

    protected abstract Task AddNodeAsync(
        Context context,
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<AbsolutePath, ContentHash> outputs,
        (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
        (ContentHash hash, byte[] bytes)? pathSetBytes,
        CancellationToken cancellationToken);

    protected abstract IAsyncEnumerable<Selector> GetSelectors(
        Context context,
        WeakFingerprint fingerprint,
        CancellationToken cancellationToken);

    protected abstract Task<ICacheEntry?> GetCacheEntryAsync(
        Context context,
        StrongFingerprint cacheStrongFingerprint,
        CancellationToken cancellationToken);

    protected record FilePlacement(ContentHash Hash, DateTime? LastModifiedUTC, FileRealizationMode RealizationMode);

    protected interface ICacheEntry : IDisposable
    {

        Task<Stream?> GetNodeBuildResultAsync(Context context, CancellationToken cancellationToken);
        Task PlaceFilesAsync(Context context, IReadOnlyDictionary<AbsolutePath, FilePlacement> files, CancellationToken cancellationToken);
    }

    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_outputHasher != null)
        {
            await _outputHasher.DisposeAsync();
        }

        _hasher.Dispose();
        RootContext.Logger.Dispose();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        List<Exception> exceptions = new(0);
        await DrainTasksAsync(_publishingTasks, "publishing");
        await DrainTasksAsync(_materializationTasks, "materialization");

        if (exceptions.Count > 0)
        {
            throw new AggregateException(exceptions);
        }

        async Task DrainTasksAsync(ConcurrentDictionary<NodeContext, Task> tasks, string name)
        {
            RootContext.Logger.Debug($"Draining {tasks.Count} {name} tasks");
            foreach (KeyValuePair<NodeContext, Task> pair in tasks)
            {
                try
                {
                    await pair.Value;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, ContentHash>> AddContentAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        Context context = new(RootContext);
        ConcurrentDictionary<string, ContentHash> outputs = new(StringComparer.OrdinalIgnoreCase);
        var outputProcessingTasks = new Task[paths.Count];
        int i = 0;
        foreach (string path in paths)
        {
            outputProcessingTasks[i++] = Task.Run(
                async () =>
                {
                    outputs.TryAdd(path, await _outputHasher!.ComputeHashAsync(path, cancellationToken));
                },
                cancellationToken);
        }

        await Task.WhenAll(outputProcessingTasks);

        return outputs;
    }

    public async Task<NodeBuildResult> AddNodeAsync(
        NodeContext nodeContext,
        PathSet? pathSet,
        IReadOnlyCollection<string> outputPaths,
        Func<IReadOnlyDictionary<string, ContentHash>, NodeBuildResult> nodeBuildResultBuilder,
        CancellationToken cancellationToken)
    {
        // Even when publishing async, we still need to capture the outputs synchronously - before they are overwritten
        IReadOnlyDictionary<string, ContentHash> hashedOutputs = await AddContentAsync(outputPaths, cancellationToken);
        NodeBuildResult nodeBuildResult = nodeBuildResultBuilder(hashedOutputs);

        Func<CancellationToken, Task> addNodeAsync = (ct) => AddNodeInternalAsync(nodeContext, pathSet, nodeBuildResult, ct);
        if (EnableAsyncPublishing)
        {
            _publishingTasks.TryAdd(
                nodeContext,
                // Avoid using a cancellation token since MSBuild will cancel it when it thinks the build is finished and we await these tasks at that point.
                // Note that this means that we effectively cannot cancel this operation once started and the user will have to wait.
                Task.Run(
                    async () =>
                    {
                        await addNodeAsync(CancellationToken.None);
                        _publishingTasks.TryRemove(nodeContext, out _);
                    },
                    CancellationToken.None));
        }
        else
        {
            await addNodeAsync(cancellationToken);
        }

        return nodeBuildResult;
    }

    public async Task AddNodeInternalAsync(
        NodeContext nodeContext,
        PathSet? pathSet,
        NodeBuildResult nodeBuildResult,
        CancellationToken cancellationToken)
    {
        Context context = new(RootContext);

        // compute the metadata content.
        byte[] nodeBuildResultBytes = await SerializeAsync(nodeBuildResult, cancellationToken);
        ContentHash nodeBuildResultHash = _hasher.GetContentHash(nodeBuildResultBytes)!;

        Tracer.Debug(context, $"Computed node metadata {nodeBuildResultHash.ToShortString()} to the cache for {nodeContext.Id}");

        Selector selector;
        (ContentHash, byte[])? pathSetBytes;
        if (pathSet != null)
        {
            // Add the PathSet to the ContentStore
            byte[] pathSetByteArray = await SerializeAsync(pathSet, cancellationToken);
            ContentHash pathSetBytesHash = _hasher.GetContentHash(pathSetByteArray)!;

            Tracer.Debug(context, $"Computed PathSet {pathSetBytesHash.ToShortString()} to the cache for {nodeContext.Id}");

            Fingerprint? strongFingerprint = _fingerprintFactory.GetStrongFingerprint(pathSet);
            selector = strongFingerprint is null
                ? EmptySelector
                : new Selector(pathSetBytesHash, strongFingerprint.Hash);

            pathSetBytes = (pathSetBytesHash, pathSetByteArray);
        }
        else
        {
            // If the PathSet is null that means all observed inputs were predicted or not hash-impacting.
            // This means the weak fingerprint is sufficient as a cache key and we can use the empty selector.
            Tracer.Debug(context, $"PathSet was null. Using empty selector for {nodeContext.Id}");
            selector = EmptySelector;
            pathSetBytes = null;
        }

        Dictionary<AbsolutePath, ContentHash> outputs = nodeBuildResult.Outputs.ToDictionary(
            kvp => RepoRoot / kvp.Key,
            kvp => kvp.Value.Hash);

        Fingerprint? weakFingerprint = _fingerprintFactory.GetWeakFingerprint(nodeContext);
        if (weakFingerprint is null)
        {
            throw new CacheException($"Weak fingerprint is null for {nodeContext.Id}");
        }

        WeakFingerprint cacheWeakFingerprint = new(weakFingerprint.Hash);

        StrongFingerprint cacheStrongFingerprint = new(cacheWeakFingerprint, selector);

        Tracer.Debug(context, $"StrongFingerprint is {cacheStrongFingerprint} for {nodeContext.Id}");

        await AddNodeAsync(
            context,
            cacheStrongFingerprint,
            outputs,
            (nodeBuildResultHash, nodeBuildResultBytes),
            pathSetBytes,
            cancellationToken);
    }

    public async Task<(PathSet?, NodeBuildResult?)> GetNodeAsync(
        NodeContext nodeContext,
        CancellationToken cancellationToken)
    {
        (PathSet? PathSet, NodeBuildResult? NodeBuildResult) result = await GetNodeInternalAsync(nodeContext, cancellationToken);

        // On cache miss ensure all dependencies are materialized before returning to MSBuild so that MSBuild's execution will actually work.
        if (_enableAsyncMaterialization && result.NodeBuildResult == null)
        {
            foreach (ProjectGraphNode dependencyNode in nodeContext.Node.ProjectReferences)
            {
                if (_nodeContextRepository.TryGetNodeContext(dependencyNode.ProjectInstance, out NodeContext? dependency)
                    && _materializationTasks.TryGetValue(dependency, out Task? dependencyMaterializationTask))
                {
                    await dependencyMaterializationTask;
                }
            }
        }

        return result;
    }

    public async Task<(PathSet?, NodeBuildResult?)> GetNodeInternalAsync(
        NodeContext nodeContext,
        CancellationToken cancellationToken)
    {
        Context context = new(RootContext);

        Tracer.Debug(context, $"{nameof(GetNodeAsync)}: {nodeContext.Id}");

        Fingerprint? weakFingerprint = _fingerprintFactory.GetWeakFingerprint(nodeContext);
        if (weakFingerprint == null)
        {
            Tracer.Debug(context, $"Weak fingerprint is null for {nodeContext.Id}");
            return (null, null);
        }

        WeakFingerprint cacheWeakFingerprint = new(weakFingerprint.Hash);

        (Selector? selector, PathSet? pathSet) = await GetMatchingSelectorAsync(context, cacheWeakFingerprint, cancellationToken);
        if (!selector.HasValue)
        {
            // GetMatchingSelectorAsync logs sufficiently
            return (null, null);
        }

        StrongFingerprint cacheStrongFingerprint = new(cacheWeakFingerprint, selector.Value);

        ICacheEntry? cacheEntry = await GetCacheEntryAsync(context, cacheStrongFingerprint, cancellationToken);
        if (cacheEntry is null)
        {
            Tracer.Debug(context, $"{nameof(GetCacheEntryAsync)} did not find an entry for {cacheStrongFingerprint}.");
            return (null, null);
        }

        using Stream? nodeBuildResultStream = await cacheEntry.GetNodeBuildResultAsync(context, cancellationToken);
        if (nodeBuildResultStream is null)
        {
            Tracer.Debug(context, $"Failed to fetch NodeBuildResult for {cacheStrongFingerprint}");
            return (null, null);
        }

        // The first file is special: it is a serialized NodeBuildResult file.
        NodeBuildResult? nodeBuildResult = await DeserializeAsync<NodeBuildResult>(context, nodeBuildResultStream, cancellationToken);
        if (nodeBuildResult is null)
        {
            Tracer.Debug(context, $"Failed to deserialize NodeBuildResult for {cacheStrongFingerprint}");
            return (null, null);
        }

        Dictionary<AbsolutePath, FilePlacement> placements = new();
        foreach (KeyValuePair<string, OutputInfo> output in nodeBuildResult.Outputs)
        {
            AbsolutePath outputPath = RepoRoot / output.Key;
            FileRealizationMode realizationMode = GetFileRealizationMode(outputPath.Path);
            placements.Add(
                outputPath,
                new FilePlacement(
                    output.Value.Hash,
                    realizationMode == FileRealizationMode.Copy ? output.Value.LastModified : null,
                    realizationMode));
        }

        Func<CancellationToken, Task> placeFilesAsync = (ct) => cacheEntry.PlaceFilesAsync(context, placements, ct);

        if (_enableAsyncMaterialization)
        {
            _materializationTasks.TryAdd(
                nodeContext,
                // Avoid using a cancellation token since MSBuild will cancel it when it thinks the build is finished and we await these tasks at that point.
                // Note that this means that we effectively cannot cancel this operation once started and the user will have to wait.
                Task.Run(
                    async () =>
                    {
                        await placeFilesAsync(CancellationToken.None);
                        _materializationTasks.TryRemove(nodeContext, out _);
                    },
                    CancellationToken.None));
        }
        else
        {
            await placeFilesAsync(cancellationToken);
        }

        return (pathSet, nodeBuildResult);
    }

    private async Task<(Selector? Selector, PathSet? PathSet)> GetMatchingSelectorAsync(
        Context context,
        WeakFingerprint weakFingerprint,
        CancellationToken cancellationToken)
    {
        context = new(context);

        await foreach (Selector selector in GetSelectors(context, weakFingerprint, cancellationToken))
        {
            if (selector == EmptySelector)
            {
                // Special-case for the empty selector, which always matches.
                Tracer.Debug(context, $"Matched empty selector for weak fingerprint {weakFingerprint}");
                return (selector, null);
            }

            ContentHash pathSetHash = selector.ContentHash;
            byte[]? selectorStrongFingerprint = selector.Output;

            PathSet? pathSet = await FetchAndDeserializeFromCacheAsync<PathSet>(context, pathSetHash, cancellationToken);

            if (pathSet is null)
            {
                Tracer.Debug(context, $"Skipping selector. Failed to fetch PathSet with content hash {pathSetHash} for weak fingerprint {weakFingerprint}");
                continue;
            }

            // Create a strong fingerprint from the PathSet and see if it matches the selector's strong fingerprint.
            Fingerprint? possibleStrongFingerprint = _fingerprintFactory.GetStrongFingerprint(pathSet);
            if (possibleStrongFingerprint != null && ByteArrayComparer.ArraysEqual(possibleStrongFingerprint.Hash, selectorStrongFingerprint))
            {
                Tracer.Debug(context, $"Matched matching selector with PathSet hash {pathSetHash} for weak fingerprint {weakFingerprint}");
                return (selector, pathSet);
            }
        }

        Tracer.Debug(context, $"No matching selectors for weak fingerprint {weakFingerprint}");
        return (null, null);
    }

    private static async Task<byte[]> SerializeAsync<T>(T data, CancellationToken cancellationToken)
        where T : class
    {
        using (var memoryStream = new MemoryStream())
        {
            await JsonSerializer.SerializeAsync(memoryStream, data, SerializationHelper.SerializerOptions, cancellationToken);
            return memoryStream.ToArray();
        }
    }

    private async Task<T?> DeserializeAsync<T>(Context context, Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        T? data = await stream.DeserializeAsync<T>(SerializationHelper.SerializerOptions, cancellationToken);
        if (data is null)
        {
            Tracer.Debug(context, $"Content deserialized as null");
        }

        return data;
    }

    protected async Task<T?> FetchAndDeserializeFromCacheAsync<T>(Context context, ContentHash contentHash, CancellationToken cancellationToken)
    where T : class
    {
        context = new(context);

        OpenStreamResult streamResult = await OpenStreamAsync(context, contentHash, cancellationToken);
        if (!streamResult.Succeeded)
        {
            Tracer.Debug(context, $"{nameof(OpenStreamAsync)} failed for content {contentHash.ToShortHash()}: {streamResult}");
            return null;
        }

        using (streamResult.Stream)
        {
            return await DeserializeAsync<T>(context, streamResult.Stream!, cancellationToken);
        }
    }

    protected readonly record struct PutFileOperation(ContentHash Hash, ResultBase Result);
}
