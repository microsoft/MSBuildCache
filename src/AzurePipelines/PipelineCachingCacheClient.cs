// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Native.IO;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.Services.BlobStore.Common;
using Microsoft.VisualStudio.Services.BlobStore.Common.Telemetry;
using Microsoft.VisualStudio.Services.BlobStore.WebApi;
using Microsoft.VisualStudio.Services.BlobStore.WebApi.Cache;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Content.Common;
using Microsoft.VisualStudio.Services.Content.Common.Tracing;
using Microsoft.VisualStudio.Services.PipelineCache.Common;
using Microsoft.VisualStudio.Services.PipelineCache.WebApi;
using BxlContentHashWithPath = BuildXL.Cache.ContentStore.Interfaces.Sessions.ContentHashWithPath;
using FileInfo = System.IO.FileInfo;

namespace Microsoft.MSBuildCache.AzurePipelines;

#pragma warning disable CS1570 // XML comment has badly formed XML
/// <summary>
/// 
/// BuildXL/QuickBuild Two-Phase Cache lookup:
///  Selector[] GetSelectors(WeakFingerprint) (1->N)
///  (ContentHash[], some metadata) GetContentHashLIst(StrongFingerprint) (1->1)
/// 
/// Pipeline Caching:
///  Manifest Query(FingerprintSegment[])  (1->1)
///  (Manifest is basically: Dictionary<RelativePath, ContentHash>) (1->1)
/// 
/// To make the second work with the first two, we'll extend the manifest to include
/// extra files.
/// 
/// For first phase we'll have the Manifest:
///  1. itself include any PathSet content (not really working yet)
///  2. include all selectors as custom metadata
/// Because this is 1->1, we'll basically accumulate selectors here.
/// We'll probably need to add some kind of LRU so it doesn't grow unbounded.
///  
/// For second phase we'll have the Manifest
///  1. itself include references to the output content 
///  2. NodeBuildResult as an extra file
/// 
/// </summary>
internal sealed class PipelineCachingCacheClient : CacheClient
#pragma warning restore CS1570 // XML comment has badly formed XML
{
    private readonly record struct ContentHashWithPath(ContentHash Hash, string Path);

    private const string InternalMetadataPathPrefix = "/???";

    private const string NodeBuildResultRelativePath = $"{InternalMetadataPathPrefix}/NodeBuildResult";
    private const string PathSetRelativePathBase = $"{InternalMetadataPathPrefix}/PathSets";
    private static string PathSetRelativePath(Selector s) => $"{PathSetRelativePathBase}/{s.ContentHash}";
    private const string SelectorsRelativePathBase = $"{InternalMetadataPathPrefix}/Selectors";
    internal static string SelectorRelativePath(Selector s) => $"{SelectorsRelativePathBase}/{s.ContentHash}/{s.Output.ToHexString()}";

    // Prefer a temp directory on the same drive as the repo root so that hard links work.
    private static readonly string TempFolder = Environment.GetEnvironmentVariable("AGENT_TEMPDIRECTORY") ?? Path.GetTempPath();

    private const char KeySegmentSeperator = '|';
    private const int InternalSeed = 5;
    private readonly bool _remoteCacheIsReadOnly;
    private readonly string _universe;
    private readonly IAppTraceSource _azureDevopsTracer;
    private readonly PipelineCacheHttpClient _cacheClient;
    private readonly DedupStoreHttpClient _dedupHttpClient;
    private readonly DedupStoreClientWithDataport _dedupClient;
    private readonly DedupManifestArtifactClient _manifestClient;
    private readonly Task _startupTask;
    private readonly KeepUntilBlobReference _keepUntil = new KeepUntilBlobReference(DateTimeOffset.Now.AddHours(4));
    private readonly HashType _hashType;

    public PipelineCachingCacheClient(
        Context rootContext,
        IFingerprintFactory fingerprintFactory,
        IContentHasher hasher,
        ICache localCache,
        IContentSession localCAS,
        ILogger logger,
        string universe,
        string repoRoot,
        string nugetPackageRoot,
        INodeContextRepository nodeContextRepository,
        Func<string, FileRealizationMode> getFileRealizationMode,
        int maxConcurrentCacheContentOperations,
        bool remoteCacheIsReadOnly,
        bool enableAsyncPublishing,
        bool enableAsyncMaterialization)
        : base(rootContext, fingerprintFactory, hasher, repoRoot, nugetPackageRoot, nodeContextRepository, getFileRealizationMode, localCache, localCAS, maxConcurrentCacheContentOperations, enableAsyncPublishing, enableAsyncMaterialization)
    {
        _remoteCacheIsReadOnly = remoteCacheIsReadOnly;
        _universe = $"pccc-{(int)hasher.Info.HashType}-{InternalSeed}-" + (string.IsNullOrEmpty(universe) ? "DEFAULT" : universe);

        _azureDevopsTracer = new CallbackAppTraceSource(
            (message, level) =>
            {
                message = $"PipelineCachingCacheClient [{level}]: {message}";
                switch (level)
                {
                    case SourceLevels.Critical:
                    case SourceLevels.Error:
                        Tracer.Error(rootContext, message);
                        break;
                    case SourceLevels.Warning:
                        Tracer.Warning(rootContext, message);
                        break;
                    case SourceLevels.Information:
                        Tracer.Info(rootContext, message);
                        break;
                    case SourceLevels.Verbose:
                        Tracer.Debug(rootContext, message);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected SourceLevel:{level}");
                }
            },
            SourceLevels.All);

        VssBasicCredential token = AzDOHelpers.GetCredentials();
        Uri artifacts = AzDOHelpers.GetServiceUriFromEnv("artifacts");

        int timeoutSeconds = Environment.GetEnvironmentVariable("MSBUILDCACHE_PIPELINECACHING_HTTP_TIMEOUT") switch
        {
            string s when int.TryParse(s, out int i) => i,
            _ => 10,
        };

        var settings = new VssHttpRequestSettings(AzDOHelpers.SessionGuid)
        {
            SendTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };

        _cacheClient = new PipelineCacheHttpClient(artifacts, token, settings);

        Uri blob = AzDOHelpers.GetServiceUriFromEnv("vsblob");
        _dedupHttpClient = new DedupStoreHttpClient(blob, token, settings);
        _dedupHttpClient.SetRedirectTimeout(timeoutSeconds);

        // https://dev.azure.com/mseng/1ES/_workitems/edit/2060777
        _hashType = hasher.Info.HashType;
        if (hasher.Info.HashType == HashType.Dedup1024K)
        {
            _dedupHttpClient.RecommendedChunkCountPerCall = 8;
        }

        var dedupHttpClientWithCache = new DedupStoreHttpClientWithCache(_dedupHttpClient, localCAS, logger, cacheChunks: true, cacheNodes: true);

        int maxParallelism = Environment.GetEnvironmentVariable("MSBUILDCACHE_PIPELINECACHING_HTTP_PARALLELISM") switch
        {
            string s when int.TryParse(s, out int i) => i,
            _ => 128,
        };

        var cacheClientContext = new DedupStoreClientContext(maxParallelism);
        _dedupClient = new DedupStoreClientWithDataport(dedupHttpClientWithCache, cacheClientContext, hasher.Info.HashType, canRedirect: true);

        _manifestClient = new DedupManifestArtifactClient(
            blobStoreClientTelemetry: NoOpBlobStoreClientTelemetry.Instance,
            _dedupClient,
            _azureDevopsTracer);

        // seed the OPTIONS call
        _startupTask = Task.Run(() => QueryPipelineCaching(rootContext, new VisualStudio.Services.PipelineCache.WebApi.Fingerprint("init"), CancellationToken.None));
    }

    protected override async Task AddNodeAsync(
        Context context,
        StrongFingerprint fingerprint,
        IReadOnlyDictionary<string, ContentHash> outputs,
        (ContentHash hash, byte[] bytes) nodeBuildResultBytes,
        (ContentHash hash, byte[] bytes)? pathSetBytes,
        CancellationToken cancellationToken)
    {
        if (_remoteCacheIsReadOnly)
        {
            return;
        }

        // write the SFP -> manifest
        List<string> tempFilePaths = new();
        try
        {
            // It is unfortunate that here we need to link out the files from the cache, and then the
            // the DedupManifestArtifactClient has to re-hash them.  With better interfaces for the cache
            // and the DMAC, we could avoid this.
            //
            // Regardless, at this point, it unblocks async publishing which results in gains way
            // larger than this inefficiency.

            // 1. Handle the metadata content

            using var nodeBuildResultTempFile = new TempFile(FileSystem.Instance, TempFolder);

#if NETFRAMEWORK
            File.WriteAllBytes(nodeBuildResultTempFile.Path, nodeBuildResultBytes.bytes);
#else
            await File.WriteAllBytesAsync(nodeBuildResultTempFile.Path, nodeBuildResultBytes.bytes, cancellationToken);
#endif

            Dictionary<string, FileInfo> extras = new(StringComparer.OrdinalIgnoreCase)
            {
                { NodeBuildResultRelativePath, new FileInfo(nodeBuildResultTempFile.Path) }
            };

            // If we are async publishing, then we need to grab content from the L1 and remap it.
            // If we are sync publishing, then we can point directly to it.
            PublishResult publishResult;
            if (EnableAsyncPublishing)
            {
                // map the hash types
                Dictionary<DedupIdentifier, ContentHash> dedupToHash = outputs.Values.ToDictionaryFirstKeyWins(
                    hash => hash.ToBlobIdentifier().ToDedupIdentifier(),
                    hash => hash);

                // open a stream to get the length of all content
                Dictionary<DedupIdentifier, long> dedupToSize = new();
                foreach (ContentHash hash in dedupToHash.Values)
                {
                    StreamWithLength? streamWithLength = await LocalCacheSession
                        .OpenStreamAsync(context, hash, cancellationToken)
                        .ThrowIfFailureAsync(r => r.StreamWithLength)!;
                    DedupIdentifier dedupId = hash.ToBlobIdentifier().ToDedupIdentifier();
                    dedupToSize.Add(dedupId, streamWithLength.Value.Length);
                }

                // create the manifest and add extras to local cache
                Manifest manifest;
                {
                    var items = new List<ManifestItem>(outputs.Count + extras.Count);

                    // put extras in local cache to simplify the code below
                    foreach (KeyValuePair<string, FileInfo> extra in extras)
                    {
                        DedupNode node = await ChunkerHelper.CreateFromFileAsync(FileSystem.Instance, extra.Key, cancellationToken, configureAwait: false);
                        DedupIdentifier dedupId = node.GetDedupIdentifier();
                        dedupToSize[dedupId] = extra.Value.Length;
                        dedupToHash[dedupId] = node.ToContentHash(_hashType);
                        await LocalCacheSession.PutFileAsync(context, node.ToContentHash(_hashType), new AbsolutePath(extra.Value.FullName), FileRealizationMode.Any, cancellationToken);
                        items.Add(new ManifestItem(extra.Key, new DedupInfo(dedupId.ValueString, node.TransitiveContentBytes)));
                    }

                    foreach (KeyValuePair<string, ContentHash> output in outputs)
                    {
                        string relativePath = output.Key.MakePathRelativeTo(RepoRoot)!.Replace("\\", "/", StringComparison.Ordinal);
                        DedupIdentifier dedupId = output.Value.ToBlobIdentifier().ToDedupIdentifier();
                        items.Add(new ManifestItem(
                            relativePath,
                            new DedupInfo(dedupId.ValueString, (ulong)dedupToSize[dedupId])));
                    }
                    items.Sort((i1, i2) => StringComparer.Ordinal.Compare(i1.Path, i2.Path));

                    manifest = new Manifest(items);
                }

                // Store the manifest in local cache to simplify the code below
                using MemoryStream manifestStream = new(JsonSerializer.Serialize(manifest).GetUTF8Bytes());
                PutResult manifestResult = await LocalCacheSession.PutStreamAsync(context, _hashType, manifestStream, cancellationToken);
                manifestResult.ThrowIfFailure();
                ContentHash manifestHash = manifestResult.ContentHash;
                DedupIdentifier manifestId = manifestHash.ToBlobIdentifier().ToDedupIdentifier();
                dedupToSize[manifestId] = manifestStream.Length;
                dedupToHash[manifestId] = manifestHash;

                // now that we have everything in the L1, we can efficiently ask the service what it already has
                IDedupUploadSession uploadSession = _dedupClient.CreateUploadSession(
                    _keepUntil,
                    tracer: _azureDevopsTracer,
                    FileSystem.Instance);

                // upload whatever (outputs, extras, and manifest) is needed
                Dictionary<DedupIdentifier, CheckIfUploadNeededResult> uploadCheckResults =
                    await uploadSession.CheckIfUploadIsNeededAsync(dedupToSize, cancellationToken);

                IEnumerable<DedupIdentifier> hashesToupload = uploadCheckResults
                    .Where(kvp => kvp.Value == CheckIfUploadNeededResult.UploadNeeded)
                    .Select(kvp => kvp.Key);

                var pageRoots = new List<DedupNode>();
                // upload the files in batches of DedupNode.MaxDirectChildrenPerNode == 512
                foreach (List<DedupIdentifier> hashPage in hashesToupload.GetPages(DedupNode.MaxDirectChildrenPerNode))
                {
                    // we'll need to materialize to upload because the cache won't give us its path to the content
                    Dictionary<ContentHash, string> tempFilesPerHash = hashPage.ToDictionary(
                        hash => dedupToHash[hash],
                        hash =>
                        {
                            string tempFilePath = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + ".tmp");
                            tempFilePaths.Add(tempFilePath);
                            return tempFilePath;
                        });

                    // munge to a different format
                    List<ContentHashWithPath> tempFiles = tempFilesPerHash
                        .Select(kvp => new ContentHashWithPath(kvp.Key, kvp.Value))
                        .ToList();

                    // materialize the files
                    Dictionary<string, PlaceFileResult> placeResults = await TryPlaceFilesFromCacheAsync(
                        context,
                        tempFiles,
                        realizationModeOverride: FileRealizationMode.Any, // hard links are fine for these
                        cancellationToken);
                    foreach (KeyValuePair<string, PlaceFileResult> placeResult in placeResults)
                    {
                        // Everything should already be in the L1
                        placeResult.Value.ThrowIfFailure();
                    }

                    // compute the merkle tree
                    Dictionary<DedupIdentifier, string> paths = tempFilesPerHash.ToDictionary(kvp => kvp.Key.ToBlobIdentifier().ToDedupIdentifier(), kvp => kvp.Value);
                    var files = new List<DedupNode>(tempFilesPerHash.Count);
                    foreach (KeyValuePair<ContentHash, string> kvp in tempFilesPerHash)
                    {
                        // UploadAsync requires "filled" nodes.
                        // For single-chunk files, they are already filled as they have no children nodes.
                        // For multi-chunk files, we need to re-chunk them here as the LocalCAS
                        //   only stores the hash of the top node and not the inner node tree that upload needs.
                        DedupIdentifier dedupId = kvp.Key.ToBlobIdentifier().ToDedupIdentifier();
                        if (dedupId.AlgorithmId == ChunkDedupIdentifier.ChunkAlgorithmId)
                        {
                            files.Add(new DedupNode(new ChunkInfo(0, (uint)dedupToSize[dedupId], dedupId.AlgorithmResult)));
                        }
                        else
                        {
                            DedupNode node = await ChunkFileAsync(kvp.Value, cancellationToken);
                            files.Add(node);
                        }
                    }

                    // create the root node and upload
                    var pageRootNode = new DedupNode(files);
                    await uploadSession.UploadAsync(pageRootNode, paths, cancellationToken);
                    // extras.Add(ConvertAbsolutePathToUriPath(output.Key), new FileInfo(tempFilesPerHash[output.Value]));
                }

                while (pageRoots.Count > 1)
                {
                    var newPageRoots = new List<DedupNode>();
                    foreach (List<DedupNode> page in pageRoots.GetPages(DedupNode.MaxDirectChildrenPerNode))
                    {
                        var pageRootNode = new DedupNode(page);
                        newPageRoots.Add(pageRootNode);
                    }
                    pageRoots = newPageRoots;
                }

                DedupNode root = pageRoots.Single();

                HashSet<DedupNode> proofNodes = ProofHelper.CreateProofNodes(
                    uploadSession.AllNodes,
                    uploadSession.ParentLookup,
                    dedupToSize.Keys);

                string[] proofNodesSerialized = proofNodes.Select(n => Convert.ToBase64String(n.Serialize())).ToArray();

                publishResult = new PublishResult(manifestId, root.GetDedupIdentifier(), proofNodesSerialized, manifest.Items.Count, (long)root.TransitiveContentBytes);
            }
            else
            {
                FileInfo[] infos = outputs.Keys.Select(f => new FileInfo(f)).ToArray();
                publishResult = await WithHttpRetries(
                    () => _manifestClient.PublishAsync(RepoRoot, infos, extras, new ArtifactPublishOptions(), manifestFileOutputPath: null, cancellationToken),
                    context: $"Publishing content for {fingerprint}",
                    cancellationToken);
            }

            // double check
            {
                using var manifestStream = new MemoryStream(await GetBytes(context, publishResult.ManifestId, cancellationToken));
                Manifest manifest = JsonSerializer.Deserialize<Manifest>(manifestStream)!;
                var manifestFiles = CreateNormalizedManifest(manifest);
                var outputFiles = CreateNormalizedManifest(outputs);
                ThrowIfDifferent(manifestFiles, outputFiles, $"With {nameof(EnableAsyncPublishing)}:{EnableAsyncPublishing}, Manifest `{publishResult.ManifestId}` and Outputs don't match:");
            }

            var key = ComputeKey(fingerprint, forWrite: true);
            var entry = new CreatePipelineCacheArtifactContract(
                new VisualStudio.Services.PipelineCache.WebApi.Fingerprint(key.Split(KeySegmentSeperator)),
                publishResult.ManifestId,
                publishResult.RootId,
                publishResult.ProofNodes,
                ContentFormatConstants.Files);

            CreateResult createResult = await WithHttpRetries(
                () => _cacheClient.CreatePipelineCacheArtifactAsync(entry, null, cancellationToken),
                context.ToString()!,
                cancellationToken);
            Tracer.Debug(context, $"Cache entry stored in scope `{createResult.ScopeUsed}`");
        }
        finally
        {
            foreach (string tempFilePath in tempFilePaths)
            {
                FileUtilities.DeleteFile(tempFilePath);
            }

            tempFilePaths.Clear();
        }

        // add the WFP -> Selector mapping
        List<TempFile> pathSetTempFiles = new();
        try
        {
            using TempFile emptyFile = new(FileSystem.Instance, TempFolder);
            using (File.OpenWrite(emptyFile.Path))
            { } // touch the file to create it
            FileInfo emptyFileInfo = new(emptyFile.Path);

            List<FileInfo> infos = new();

            string key = ComputeSelectorsKey(fingerprint.WeakFingerprint, forWrite: true);

            var selectors = await GetSelectors(context, fingerprint.WeakFingerprint, cancellationToken).ToHashSetAsync(cancellationToken);

            selectors.Add(fingerprint.Selector);

            // TODO: limit the number of selectors we store.

            Dictionary<string, FileInfo> extras = new(selectors.Count);

            foreach (Selector selector in selectors)
            {
                // the selector is just a fake file
                extras.Add(SelectorRelativePath(selector), emptyFileInfo);

                // Multiple selectors may have the same pathset hash but different outputs (eg, if a non-predicted output changed),
                // so only add it once.
                string pathSetRelativePath = PathSetRelativePath(selector);
                if (selector.ContentHash != EmptySelector.ContentHash
                    && !extras.ContainsKey(pathSetRelativePath))
                {
#if NET8_0
#pragma warning disable IDE0079
#pragma warning disable CA2000
#endif
                    var pathSetTempFile = new TempFile(FileSystem.Instance, TempFolder);
#if NET8_0
#pragma warning restore CA2000
#pragma warning restore IDE0079
#endif
                    var bytes = selector.ContentHash == pathSetBytes?.hash
                        ? pathSetBytes.Value.bytes
                        : await GetBytes(context, selector.ContentHash.ToBlobIdentifier().ToDedupIdentifier(), cancellationToken);
#if NETFRAMEWORK
                    File.WriteAllBytes(pathSetTempFile.Path, bytes);
#else
                    await File.WriteAllBytesAsync(pathSetTempFile.Path, bytes, cancellationToken);
#endif
                    extras.Add(pathSetRelativePath, new FileInfo(pathSetTempFile.Path));
                    pathSetTempFiles.Add(pathSetTempFile);
                }
            }

            var result = await WithHttpRetries(
                () => _manifestClient.PublishAsync(TempFolder, infos, extras, new ArtifactPublishOptions(), manifestFileOutputPath: null, cancellationToken),
                context.ToString()!,
                cancellationToken);

            var entry = new CreatePipelineCacheArtifactContract(
                new VisualStudio.Services.PipelineCache.WebApi.Fingerprint(key.Split(KeySegmentSeperator)),
                result.ManifestId,
                result.RootId,
                result.ProofNodes,
                ContentFormatConstants.Files);

            CreateResult createResult = await WithHttpRetries(
                () => _cacheClient.CreatePipelineCacheArtifactAsync(entry, null, cancellationToken),
                context.ToString()!,
                cancellationToken);

            Tracer.Debug(context, $"SFP `{fingerprint}` stored in scope `{createResult.ScopeUsed}`");
        }
        finally
        {
            foreach (var pathSetTempFile in pathSetTempFiles)
            {
                pathSetTempFile.Dispose();
            }
        }
    }

    private static Task<DedupNode> ChunkFileAsync(string path, CancellationToken cancellationToken) =>
        ChunkerHelper.CreateFromFileAsync(FileSystem.Instance, path, cancellationToken, configureAwait: false);

    private static byte GetAlgorithmId(ContentHash hash)
    {
        switch (hash._hashType)
        {
            case HashType.Dedup1024K:
            case HashType.Dedup64K:
                return hash[hash.Length - 1];
            default:
                throw new NotSupportedException($"Hash type {hash._hashType} is not supported");
        }
    }

    private async Task<Dictionary<string, PlaceFileResult>> TryPlaceFilesFromCacheAsync(
        Context context,
        List<ContentHashWithPath> files,
        FileRealizationMode? realizationModeOverride,
        CancellationToken cancellationToken)
    {
        // cache expects destination directories already exist
        foreach (ContentHashWithPath file in files)
        {
            CreateParentDirectory(file.Path);
        }

        Dictionary<string, PlaceFileResult> results = new(files.Count);
        List<BxlContentHashWithPath> places = new(files.Count);

        var operationGroups = files.GroupBy(f => (GetAlgorithmId(f.Hash), realizationModeOverride ?? GetFileRealizationMode(f.Path)));

        foreach (IGrouping<(byte algoId, FileRealizationMode mode), ContentHashWithPath>? filesGroup in operationGroups)
        {
            FileRealizationMode realizationMode = filesGroup.Key.mode;
            FileAccessMode accessMode = realizationMode == FileRealizationMode.CopyNoVerify
                ? FileAccessMode.Write
                : FileAccessMode.ReadOnly;

            places.Clear();
            foreach (ContentHashWithPath file in filesGroup)
            {
                places.Add(new BxlContentHashWithPath(file.Hash, new AbsolutePath(file.Path)));
            }

            List<Task<Indexed<PlaceFileResult>>> groupResults = (await LocalCacheSession.PlaceFileAsync(
                context, places, accessMode, FileReplacementMode.ReplaceExisting, realizationMode, cancellationToken)).ToList();

            // try to pull single-chunk files from chunk store
            if (filesGroup.Key.algoId == ChunkDedupIdentifier.ChunkAlgorithmId)
            {
                for (int i = 0; i < groupResults.Count; i++)
                {
                    Indexed<PlaceFileResult> result = await groupResults[i];
                    if (!result.Item.Succeeded)
                    {
                        byte[] hashBytes = places[result.Index].Hash.ToHashByteArray();

                        groupResults[i] = Task.Run(async () => (await LocalCacheSession.PlaceFileAsync(
                            context, new ContentHash(HashType.DedupSingleChunk, hashBytes), places[result.Index].Path, accessMode,
                            FileReplacementMode.ReplaceExisting, realizationMode, cancellationToken)).WithIndex(result.Index));
                    }
                }
            }

            foreach (Task<Indexed<PlaceFileResult>> resultTask in groupResults)
            {
                Indexed<PlaceFileResult> result = await resultTask;
                string path = PathHelper.RemoveLongPathPrefixes(places[result.Index].Path.Path);
                results.Add(path, result.Item);
            }
        }

        return results;
    }

    protected override async Task<ICacheEntry?> GetCacheEntryAsync(Context context, StrongFingerprint cacheStrongFingerprint, CancellationToken cancellationToken)
    {
        string key = ComputeKey(cacheStrongFingerprint, forWrite: false);
        PipelineCacheArtifact? result = await QueryPipelineCaching(
            context,
            new VisualStudio.Services.PipelineCache.WebApi.Fingerprint(key.Split(KeySegmentSeperator)),
            cancellationToken);

        if (result == null)
        {
            return null;
        }

        using var manifestStream = new MemoryStream(await GetBytes(context, result.ManifestId, cancellationToken));
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(manifestStream)!;

        var message = new StringBuilder($"For entry `{cacheStrongFingerprint}`, found manifest `{result.ManifestId.ValueString}`:\n");
        foreach (ManifestItem? file in manifest.Items)
        {
            message.AppendFormat(" `{0}` [{1} bytes]: `{2}`\n", file.Path, file.Blob.Size, file.Blob.Id);
        }
        Tracer.Debug(context, message.ToString());

        ManifestItem nodeBuildResultItem = manifest.Items.Single(mi => mi.Path == NodeBuildResultRelativePath);
        byte[] nodeBuildResult = await GetBytes(context, DedupIdentifier.Create(nodeBuildResultItem.Blob.Id), cancellationToken);

        return new CacheResult(manifest, this, result.ManifestId, nodeBuildResult);
    }

    private sealed class CacheResult : ICacheEntry
    {
        private readonly DedupIdentifier _manifestId;
        private readonly Manifest _manifest;
        private readonly byte[] _nodeBuildResultBytes;
        private readonly PipelineCachingCacheClient _client;

        public CacheResult(Manifest manifest, PipelineCachingCacheClient client, DedupIdentifier manifestId, byte[] nodeBuildResultBytes)
        {
            _manifest = manifest;
            _client = client;
            _manifestId = manifestId;
            _nodeBuildResultBytes = nodeBuildResultBytes;
        }

        public void Dispose() { }

        public Task<Stream?> GetNodeBuildResultAsync(Context context, CancellationToken cancellationToken) =>
            Task.FromResult((Stream?)new MemoryStream(_nodeBuildResultBytes));

        public async Task PlaceFilesAsync(Context context, IReadOnlyDictionary<string, ContentHash> files, CancellationToken cancellationToken)
        {
            _client.Tracer.Debug(context, $"Placing manifest `{_manifestId}`.");

            var manifestFiles = CreateNormalizedManifest(_manifest);
            var requestFiles = _client.CreateNormalizedManifest(files);
            ThrowIfDifferent(manifestFiles, requestFiles, $"Manifest `{_manifestId}` and PlaceFiles don't match:");

            // try to pull whole files from the cache
            var places = files.Select(f => new ContentHashWithPath(f.Value, f.Key)).ToList();

            Dictionary<string, PlaceFileResult> placeResults = await _client.TryPlaceFilesFromCacheAsync(context, places, realizationModeOverride: null, cancellationToken);

            Dictionary<string, ManifestItem> manifestItems = _manifest.Items.ToDictionary(i => _client.ConvertUriPathToAbsolutePath(i.Path), i => i);
            var itemsToDownload = new List<ManifestItem>();
            var toAddToCacheAsWholeFile = new Dictionary<ContentHash, string>();
            foreach (KeyValuePair<string, PlaceFileResult> placeResult in placeResults)
            {
                if (!placeResult.Value.Succeeded)
                {
                    string path = placeResult.Key;
                    itemsToDownload.Add(manifestItems[path]);

                    ContentHash hash = files[path];
                    // We don't need to add single-chunk files as whole files because they are already stored as a chunk
                    if (GetAlgorithmId(hash) != ChunkDedupIdentifier.ChunkAlgorithmId)
                    {
                        toAddToCacheAsWholeFile.TryAdd(hash, path);
                    }
                }
            }

            if (itemsToDownload.Count == 0)
            {
                return;
            }

            using var tempManifestFile = new TempFile(FileSystem.Instance, TempFolder);
            var tempManifest = new Manifest(itemsToDownload);

#if NETFRAMEWORK
            File.WriteAllText(tempManifestFile.Path, JsonSerializer.Serialize(tempManifest));
#else
            await File.WriteAllTextAsync(tempManifestFile.Path, JsonSerializer.Serialize(tempManifest), cancellationToken);
#endif

            var manifestOptions = DownloadDedupManifestArtifactOptions.CreateWithManifestPath(tempManifestFile.Path, _client.RepoRoot);

            await _client.WithHttpRetries(
                async () =>
                {
                    await _client._manifestClient.DownloadAsyncWithManifestPath(manifestOptions, cancellationToken);
                    return 0;
                },
                context: context.ToString()!,
                cancellationToken);

            foreach (KeyValuePair<ContentHash, string> addToCache in toAddToCacheAsWholeFile)
            {
                ContentHash hash = addToCache.Key;
                string path = addToCache.Value;
                await _client.LocalCacheSession.PutFileAsync(context, hash, new AbsolutePath(path), _client.GetFileRealizationMode(path), cancellationToken);
            }
        }
    }

    protected override async IAsyncEnumerable<Selector> GetSelectors(
        Context context,
        BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint fingerprint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _startupTask;

        string key = ComputeSelectorsKey(fingerprint, forWrite: false);
        PipelineCacheArtifact? result = await QueryPipelineCaching(
            context,
            new VisualStudio.Services.PipelineCache.WebApi.Fingerprint(key.Split(KeySegmentSeperator)),
            cancellationToken);

        if (result == null)
        {
            yield break;
        }

        using var manifestStream = new MemoryStream(await GetBytes(context, result.ManifestId, cancellationToken));
        Manifest manifest = JsonSerializer.Deserialize<Manifest>(manifestStream)!;
        foreach (ManifestItem selectorItem in manifest.Items.Where(i => i.Path.StartsWith(SelectorsRelativePathBase, StringComparison.Ordinal)))
        {
            string[] tokens = selectorItem.Path.Substring(SelectorsRelativePathBase.Length + 1).Split('/');
            yield return new Selector(new ContentHash(tokens[0]), HexUtilities.HexToBytes(tokens[1]));
        }
    }

    protected override async Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cancellationToken)
    {
        return new OpenStreamResult(new MemoryStream(await GetBytes(context, contentHash.ToBlobIdentifier().ToDedupIdentifier(), cancellationToken)));
    }

    private async Task<byte[]> GetBytes(Context context, DedupIdentifier dedupId, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        return await WithHttpRetries(async () =>
        {
            ms.Position = 0;
            await _manifestClient.DownloadToStreamAsync(dedupId, ms, proxyUri: null, cancellationToken);
            return ms.ToArray();
        },
            context.ToString()!,
            cancellationToken);
    }

    private static SortedDictionary<string, DedupIdentifier> CreateNormalizedManifest(Manifest m)
    {
        SortedDictionary<string, DedupIdentifier> sorted = new(StringComparer.OrdinalIgnoreCase);

        foreach (ManifestItem item in m.Items)
        {
            if (item.Path.StartsWith(InternalMetadataPathPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            sorted.Add(item.Path, DedupIdentifier.Create(item.Blob.Id));
        }

        return sorted;
    }

    private SortedDictionary<string, DedupIdentifier> CreateNormalizedManifest(IReadOnlyDictionary<string, ContentHash> files)
    {
        SortedDictionary<string, DedupIdentifier> sorted = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, ContentHash> f in files)
        {
            sorted.Add(ConvertAbsolutePathToUriPath(f.Key), f.Value.ToBlobIdentifier().ToDedupIdentifier());
        }

        return sorted;
    }

    private static void ThrowIfDifferent(
        SortedDictionary<string, DedupIdentifier> left,
        SortedDictionary<string, DedupIdentifier> right,
        string message
    )
    {
        if (left.SequenceEqual(right))
        {
            return;
        }

        SortedSet<(string, DedupIdentifier)> leftOnly = new(left.Select(kvp => (kvp.Key, kvp.Value)));
        SortedSet<(string, DedupIdentifier)> rightOnly = new(right.Select(kvp => (kvp.Key, kvp.Value)));

        SortedSet<(string, DedupIdentifier)> both = new(leftOnly);
        both.IntersectWith(rightOnly);

        leftOnly.ExceptWith(both);
        rightOnly.ExceptWith(both);

        throw new InvalidDataException($"{message} [{string.Join(", ", leftOnly)}] vs [{string.Join(", ", rightOnly)}]");
    }

    private string ComputeKey(StrongFingerprint sfp, bool forWrite) =>
        forWrite
            ? $"outputs{InternalSeed}{KeySegmentSeperator}{_universe}{KeySegmentSeperator}{sfp.WeakFingerprint.Serialize()}{KeySegmentSeperator}{sfp.Selector.ContentHash.Serialize()}{KeySegmentSeperator}{DateTime.UtcNow.Ticks}"
            : $"outputs{InternalSeed}{KeySegmentSeperator}{_universe}{KeySegmentSeperator}{sfp.WeakFingerprint.Serialize()}{KeySegmentSeperator}{sfp.Selector.ContentHash.Serialize()}{KeySegmentSeperator}**";

    private string ComputeSelectorsKey(BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint wfp, bool forWrite) =>
        forWrite
            ? $"selector{InternalSeed}{KeySegmentSeperator}{_universe}{KeySegmentSeperator}{wfp.Serialize()}{KeySegmentSeperator}{DateTime.UtcNow.Ticks}"
            : $"selector{InternalSeed}{KeySegmentSeperator}{_universe}{KeySegmentSeperator}{wfp.Serialize()}{KeySegmentSeperator}**";

    private Task<PipelineCacheArtifact?> QueryPipelineCaching(Context context, VisualStudio.Services.PipelineCache.WebApi.Fingerprint key, CancellationToken cancellationToken)
    {
        return WithHttpRetries(
            async () =>
            {
                try
                {
                    PipelineCacheArtifact result = await _cacheClient.GetPipelineCacheArtifactWithFallbackAsync(
                        new[] { key },
                        null,
                        cancellationToken);

                    if (result == null)
                    {
                        return null;
                    }

                    var message = new StringBuilder($"Query `{key}` found entry `{result.Fingerprint}` with manifest `{result.ManifestId.ValueString}`in scope `{result.Scope}`.");
                    foreach (string missedScope in result.MissedScopes)
                    {
                        message.Append($" Missed scope: `{missedScope}`.");
                    }

                    Tracer.Debug(context, message.ToString());

                    return result;
                }
                catch (PipelineCacheItemDoesNotExistException)
                {
                    Tracer.Debug(context, $"Key not found: `{key}");
                    // return null on 404
                    return null;
                }
            },
            $"Querying cache for '{key}'",
            cancellationToken);
    }

    private Task<T> WithHttpRetries<T>(Func<Task<T>> taskFactory, string context, CancellationToken token)
    {
        return AsyncHttpRetryHelper<T>.InvokeAsync(
                taskFactory,
                maxRetries: 10,
                tracer: _azureDevopsTracer,
                canRetryDelegate: _ => true, // retry on any exception
                cancellationToken: token,
                continueOnCapturedContext: false,
                context: context);
    }

    public override async ValueTask DisposeAsync()
    {
        _manifestClient.Dispose();
        _dedupHttpClient.Dispose();
        _cacheClient.Dispose();
        await base.DisposeAsync();
    }

    private string ConvertUriPathToAbsolutePath(string path)
    {
        // Trim off the leading '/' so it isn't interpreted as absolute
        if (path.Length > 0 && path[0] == '/')
        {
            path = path.Substring(1);
        }

        // Replace '/' with the platform-specific directory separator
        if (Path.DirectorySeparatorChar != '/')
        {
            path = path.Replace('/', Path.DirectorySeparatorChar);
        }

        // Make the path absolute
        return Path.Combine(RepoRoot, path);
    }

    private string ConvertAbsolutePathToUriPath(string path)
    {
        // Make the path relative
        path = path.MakePathRelativeTo(RepoRoot)!;

        // Replace platform-specific directory separator with '/'
        if (Path.DirectorySeparatorChar != '/')
        {
            path = path.Replace(Path.DirectorySeparatorChar, '/');
        }

        // Prepend a '/'
        return $"/{path}";
    }
}
