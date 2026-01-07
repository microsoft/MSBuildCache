// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Utilities.Collections;
using DotNet.Globbing;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.FileAccess;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.Build.Graph;
using Microsoft.CopyOnWrite;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.MSBuildCache.Parsing;
using Microsoft.MSBuildCache.SourceControl;

namespace Microsoft.MSBuildCache;

public abstract class MSBuildCachePluginBase : MSBuildCachePluginBase<PluginSettings>
{
    // This is a convenience class for subclasses which don't have extended plugin settings
}

public abstract class MSBuildCachePluginBase<TPluginSettings> : ProjectCachePluginBase, IAsyncDisposable
    where TPluginSettings : PluginSettings
{
    private static readonly string PluginAssemblyDirectory = Path.GetDirectoryName(typeof(MSBuildCachePluginBase<TPluginSettings>).Assembly.Location)!;

    private static readonly SemaphoreSlim SinglePluginInstanceLock = new(1, 1);

    // Keys are relative file paths
    private readonly ConcurrentDictionary<string, NodeContext> _outputProducer = new(StringComparer.OrdinalIgnoreCase);

    private string? _repoRoot;
    private string? _buildId;

    // Set if we've received any file access report. Ideally MSBuild would tell us in the CacheContext
    private bool _hasHadFileAccessReport;

    private PluginLoggerBase? _pluginLogger;
    private NodeDescriptorFactory? _nodeDescriptorFactory;
    private Dictionary<NodeDescriptor, NodeContext>? _nodeContexts;
    private FileAccessRepository? _fileAccessRepository;
    private ICacheClient? _cacheClient;
    private IReadOnlyCollection<Glob>? _ignoredOutputPatterns;
    private IReadOnlyCollection<Glob>? _identicalDuplicateOutputPatterns;
    private DirectoryLock? _localCacheDirectoryLock;
    private SemaphoreSlim? _singlePluginInstanceMutex;
    private PathNormalizer? _pathNormalizer;
    private Func<NodeContext, PluginLoggerBase, CancellationToken, Task<CacheResult>>? _getCacheResultAsync;

    private int _cacheHitCount;
    private long _cacheHitDurationMilliseconds;
    private int _cacheMissCount;

    static MSBuildCachePluginBase() =>
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            AssemblyName assemblyName = new(args.Name);

            // First try using any assembly already loaded by MSBuild.
            Assembly[] loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            Assembly? loadedAssembly = loadedAssemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
            if (loadedAssembly != null)
            {
                return loadedAssembly;
            }

            string assemblyFileName = $"{assemblyName.Name}.dll";

            // Next try force loading any of MSBuild's assemblies.
            // This ensures that we always prefer MSBuild's dependencies for shared dependencies to avoid mismatched type issues.
            string candidateAssemblyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyFileName);
            if (File.Exists(candidateAssemblyPath))
            {
                return Assembly.LoadFrom(candidateAssemblyPath);
            }

            // Finally, load anything adjacent to us.
            // This should be dependencies which are unique to us.
            candidateAssemblyPath = Path.Combine(PluginAssemblyDirectory, assemblyFileName);
            if (File.Exists(candidateAssemblyPath))
            {
                return Assembly.LoadFrom(candidateAssemblyPath);
            }
            else
            {
                return null;
            }
        };

    [MemberNotNullWhen(
        true,
        nameof(_pluginLogger),
        nameof(_repoRoot),
        nameof(NugetPackageRoot),
        nameof(_pathNormalizer),
        nameof(ContentHasher),
        nameof(InputHasher),
        nameof(_nodeDescriptorFactory),
        nameof(_nodeContexts),
        nameof(FingerprintFactory),
        nameof(_fileAccessRepository),
        nameof(_cacheClient),
        nameof(_ignoredOutputPatterns),
        nameof(_identicalDuplicateOutputPatterns),
        nameof(_getCacheResultAsync)
    )]
    protected bool Initialized { get; private set; }

    protected string? NugetPackageRoot { get; private set; }

    protected TPluginSettings? Settings { get; private set; }

    protected abstract HashType HashType { get; }

    protected IContentHasher? ContentHasher { get; private set; }

    protected IInputHasher? InputHasher { get; private set; }

    protected IFingerprintFactory? FingerprintFactory { get; private set; }

    public virtual async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (_cacheClient != null)
        {
            await _cacheClient.DisposeAsync();
        }

        ContentHasher?.Dispose();

        if (_fileAccessRepository is IDisposable fileAccessRepositoryDisposable)
        {
            fileAccessRepositoryDisposable.Dispose();
        }

        _outputProducer.Clear();
        _localCacheDirectoryLock?.Dispose();
        _singlePluginInstanceMutex?.Release();
    }

    protected virtual string? GetBuildId()
    {
        if (Environment.GetEnvironmentVariable("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI") != null)
        {
            return Environment.GetEnvironmentVariable("BUILD_BUILDID");
        }

        // Add additional common CI providers here.

        return null;
    }

    protected virtual async Task<IInputHasher> CreateInputHasherAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (ContentHasher is null
            || _pathNormalizer is null
            || NugetPackageRoot is null)
        {
            throw new InvalidOperationException();
        }

        IReadOnlyDictionary<string, byte[]> sourceControlFileHashes = await GetSourceControlFileHashesAsync(logger, cancellationToken);
        SourceControlFileHasher sourceControlFileHasher = new(ContentHasher, _pathNormalizer, sourceControlFileHashes);

        DirectoryFileHasher nugetPackageDirectoryHasher = new(NugetPackageRoot, ContentHasher);

        return new CompositeInputHasher([sourceControlFileHasher, nugetPackageDirectoryHasher]);
    }

    protected virtual IFingerprintFactory CreateFingerprintFactory()
    {
        if (ContentHasher == null
            || InputHasher == null
            || Settings == null
            || _pathNormalizer == null)
        {
            throw new InvalidOperationException();
        }

        return new FingerprintFactory(ContentHasher, InputHasher, Settings, _pathNormalizer);
    }

    protected abstract Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken);

    protected FileRealizationMode GetFileRealizationMode(string path)
        => IsDuplicateIdenticalOutputPath(_pluginLogger!, path) ? FileRealizationMode.CopyNoVerify : FileRealizationMode.Any;

    public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
        => TimeAndLogAsync(logger, () => BeginBuildInnerAsync(context, logger, cancellationToken), cancellationToken);

    private async Task BeginBuildInnerAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        _pluginLogger = logger;

        _repoRoot = GetRepoRoot(context, logger);
        if (_repoRoot == null)
        {
            return;
        }

        _buildId = GetBuildId();

        Settings = PluginSettings.Create<TPluginSettings>(context.PluginSettings, logger, _repoRoot);

        // The local cache does not allow multiple processes to access it at the same time and will block indefinitely while waiting for a lock on the directory.
        // In certain scenarios where MSBuild is invoked recursively, such as is done for Fakes projects, this can lead to a hang as the child MSBuild waits for the
        // lock that the parent has while the parent waits for the child to exit.
        // Because of this, we need to ensure only one instance of this plugin is running at a time.
        if (!TryAcquireLock(Settings, logger))
        {
            // Note: by returning early, many fields won't be populated. Other methods are responsible for handling this and interpreting it as "not enabled".
            return;
        }

        NugetPackageRoot = GetNuGetPackageRoot();
        _pathNormalizer = new PathNormalizer(_repoRoot, NugetPackageRoot);

        WarnOnCowWithDifferingVolumes(logger);

        if (Directory.Exists(Settings.LogDirectory))
        {
            Directory.Delete(Settings.LogDirectory, recursive: true);
        }

        Directory.CreateDirectory(Settings.LogDirectory);

        _ignoredOutputPatterns = Settings.IgnoredOutputPatterns;
        _identicalDuplicateOutputPatterns = Settings.IdenticalDuplicateOutputPatterns;

        ContentHasher = HashInfoLookup.Find(HashType).CreateContentHasher();

        // Kick off async since this may take some time.
        Task<IInputHasher> inputHasherTask = CreateInputHasherAsync(logger, cancellationToken);

        HashSet<string> globalPropertiesToIgnore = new(Settings.GlobalPropertiesToIgnore, StringComparer.OrdinalIgnoreCase);
        _nodeDescriptorFactory = new NodeDescriptorFactory(globalPropertiesToIgnore);

        ProjectGraph graph = GetProjectGraph(context, logger);
        Parser parser = new(logger, _repoRoot);
        IReadOnlyDictionary<ProjectGraphNode, ParserInfo> parserInfoForNodes = parser.Parse(graph);

        // In practice, the underlying type of the IReadOnlyCollection is a ICollection<string> so attempt to cast first. We're not mutating the collection so still abiding by the readonly-ness.
        ICollection<string> entryProjectTargets = context.RequestedTargets as ICollection<string> ?? new List<string>(context.RequestedTargets);
        IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> targetListPerNode = graph.GetTargetLists(entryProjectTargets);

        _nodeContexts = new(parserInfoForNodes.Count);
        Dictionary<ProjectGraphNode, NodeContext> nodeContextsByNode = new(parserInfoForNodes.Count);
        Dictionary<ProjectGraphNode, List<NodeContext>> nodeDependencies = new(parserInfoForNodes.Count);
        List<Task> dumpParserInfoTasks = new(parserInfoForNodes.Count);
        foreach (KeyValuePair<ProjectGraphNode, ParserInfo> pair in parserInfoForNodes)
        {
            ProjectGraphNode node = pair.Key;
            ParserInfo parserInfo = pair.Value;

            if (!targetListPerNode.TryGetValue(node, out ImmutableList<string>? targetList))
            {
                throw new InvalidOperationException($"Missing target list for {node.ProjectInstance.FullPath}");
            }

            HashSet<string> targetNames = new(targetList, StringComparer.OrdinalIgnoreCase);

            string[] inputs = new string[parserInfo.Inputs.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = parserInfo.Inputs[i].AbsolutePath;
            }

            // Create an empty collection to contruct the NodeContext with. It'll be populated later.
            List<NodeContext> dependencies = new(node.ProjectReferences.Count);
            nodeDependencies.Add(node, dependencies);

            NodeDescriptor nodeDescriptor = _nodeDescriptorFactory.Create(node.ProjectInstance);
            NodeContext nodeContext = new(
                Settings.LogDirectory,
                node.ProjectInstance,
                dependencies,
                parserInfo.ProjectFileRelativePath,
                nodeDescriptor.FilteredGlobalProperties,
                inputs,
                parserInfo.ReferenceAssemblyRelativePath,
                targetNames);

            dumpParserInfoTasks.Add(Task.Run(() => DumpParserInfoAsync(logger, nodeContext, parserInfo), cancellationToken));
            _nodeContexts.Add(nodeDescriptor, nodeContext);
            nodeContextsByNode.Add(node, nodeContext);
        }

        // Another pass to populate the NodeContext dependencies list
        foreach (KeyValuePair<ProjectGraphNode, List<NodeContext>> pair in nodeDependencies)
        {
            ProjectGraphNode node = pair.Key;
            List<NodeContext> dependencies = pair.Value;

            foreach (ProjectGraphNode? dependencyNode in node.ProjectReferences)
            {
                // If the parser ignored the project, we won't find the node context. This can happen when the project is outside the repository.
                // By ignoring these we're choosing to accept the risk of an under-build. This is justified since we've already accepted it for input files outside the repo during fingerprinting.
                if (nodeContextsByNode.TryGetValue(dependencyNode, out NodeContext? dependencyNodeContext))
                {
                    dependencies.Add(dependencyNodeContext);
                }
            }
        }

        Task dumpNodeContextsTask = DumpNodeContextsAsync(logger, _nodeContexts);
        InputHasher = await inputHasherTask;
        FingerprintFactory = CreateFingerprintFactory();
        _fileAccessRepository = new FileAccessRepository(logger, Settings);
        _cacheClient = await CreateCacheClientAsync(logger, cancellationToken);

        if (Settings.GetResultsForUnqueriedDependencies)
        {
            ConcurrentDictionary<NodeContext, Lazy<Task<CacheResult>>> cacheResults = new(concurrencyLevel: Environment.ProcessorCount, _nodeContexts.Count);
            _getCacheResultAsync = (nodeContext, logger, cancellationToken) => GetCacheResultRecursivelyAsync(cacheResults, nodeContext, materializeOutputs: true, logger, cancellationToken);
        }
        else
        {
            _getCacheResultAsync = GetCacheResultNonRecursiveAsync;
        }

        // Ensure all logs are written
        await Task.WhenAll(dumpParserInfoTasks);
        await dumpNodeContextsTask;

        Initialized = true;
    }

    public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
        => TimeAndLogAsync(logger, () => EndBuildInnerAsync(logger, cancellationToken), cancellationToken);

    private async Task EndBuildInnerAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (_cacheClient is not null)
        {
            await _cacheClient.ShutdownAsync(cancellationToken);
        }

        await DisposeAsync();

        LogCacheStats(logger);

        _pluginLogger = null;
    }

    public override Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
        => TimeAndLogAsync(
            logger,
            () => GetCacheResultInnerAsync(buildRequest, logger, cancellationToken),
            cancellationToken,
            context: buildRequest.ProjectFullPath);

    private async Task<CacheResult> GetCacheResultInnerAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (!Initialized)
        {
            // BeginBuild didn't finish successfully. It's expected to log sufficiently, so just bail.
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        ProjectInstance? projectInstance = buildRequest.ProjectInstance; // Nullable in newer frameworks
        if (projectInstance == null)
        {
            logger.LogWarning($"Project instance was unexpectedly null for build request for project {buildRequest.ProjectFullPath}");
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        NodeDescriptor nodeDescriptor = _nodeDescriptorFactory.Create(projectInstance!);
        if (!_nodeContexts.TryGetValue(nodeDescriptor, out NodeContext? nodeContext))
        {
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        if (!DoTargetsMatchForRead(nodeContext.TargetNames, buildRequest.TargetNames))
        {
            logger.LogMessage($"`TargetNames` does not match for {nodeContext.Id}. `{string.Join(";", nodeContext.TargetNames)}` vs `{string.Join(";", buildRequest.TargetNames)}`.");
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        return await _getCacheResultAsync(nodeContext, logger, cancellationToken);
    }

    private async Task<CacheResult> GetCacheResultRecursivelyAsync(
        ConcurrentDictionary<NodeContext, Lazy<Task<CacheResult>>> cacheResults,
        NodeContext nodeContext,
        bool materializeOutputs,
        PluginLoggerBase logger,
        CancellationToken cancellationToken)
    {
        // Ensure we only query a node exactly once. MSBuild won't query a project more than once, but when recursion is enabled,
        // we might have multiple build requests querying the same unqueried dependency.
        return await cacheResults.GetOrAdd(nodeContext, new Lazy<Task<CacheResult>>(
            async () =>
            {
                bool isOuterBuild = nodeContext.ProjectInstance.IsOuterBuild();

                foreach (NodeContext dependency in nodeContext.Dependencies)
                {
                    if (dependency.BuildResult == null)
                    {
                        // When querying recursively, avoid materializing the outputs. That node was never directly queried, so its outputs
                        // are not desired from the caller. Note that there is an assumption that the node won't be queried directly later
                        // as that would break the expected "bottom-up" build order of graph builds.
                        // Special-case the outer build of a multitargeting project which dependencies on the inner builds for which we do
                        // want the outputs.
                        bool materializeOutputs = isOuterBuild && dependency.ProjectInstance.IsInnerBuild();

                        logger.LogMessage($"Querying cache for missing build result for dependency '{dependency.Id}'");
                        CacheResult dependencyResult = await GetCacheResultRecursivelyAsync(cacheResults, dependency, materializeOutputs, logger, cancellationToken);
                        logger.LogMessage($"Dependency '{dependency.Id}' cache result: '{dependencyResult.ResultType}'");

                        if (dependencyResult.ResultType != CacheResultType.CacheHit)
                        {
                            logger.LogMessage($"Cache miss due to failed build result for dependency '{dependency.Id}'");
                            Interlocked.Increment(ref _cacheMissCount);
                            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
                        }
                    }
                }

                return await GetCacheResultSingleAsync(nodeContext, materializeOutputs, logger, cancellationToken);
            })).Value;
    }

    private async Task<CacheResult> GetCacheResultNonRecursiveAsync(NodeContext nodeContext, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        foreach (NodeContext dependency in nodeContext.Dependencies)
        {
            if (dependency.BuildResult == null)
            {
                logger.LogMessage($"Cache miss due to failed or missing build result for dependency '{dependency.Id}'");
                Interlocked.Increment(ref _cacheMissCount);
                return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
            }
        }

        return await GetCacheResultSingleAsync(nodeContext, materializeOutputs: true, logger, cancellationToken);
    }

    private async Task<CacheResult> GetCacheResultSingleAsync(NodeContext nodeContext, bool materializeOutputs, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (!Initialized)
        {
            throw new InvalidOperationException();
        }

        nodeContext.SetStartTime();

        (PathSet? pathSet, NodeBuildResult? nodeBuildResult) = await _cacheClient.GetNodeAsync(nodeContext, materializeOutputs, cancellationToken);
        if (nodeBuildResult is null)
        {
            Interlocked.Increment(ref _cacheMissCount);
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
        }

        CheckForDuplicateOutputs(logger, nodeBuildResult.Outputs, nodeContext);

        await FinishNodeAsync(logger, nodeContext, pathSet, nodeBuildResult);

        Interlocked.Increment(ref _cacheHitCount);
        Interlocked.Add(ref _cacheHitDurationMilliseconds, (int)(nodeBuildResult.EndTimeUtc - nodeBuildResult.StartTimeUtc).TotalMilliseconds);
        return nodeBuildResult.ToCacheResult(_pathNormalizer);
    }

    public override void HandleFileAccess(FileAccessContext fileAccessContext, FileAccessData fileAccessData)
    {
        if (Initialized)
        {
            TimeAndLog(_pluginLogger, () => HandleFileAccessInner(fileAccessContext, fileAccessData));
        }
    }

    private void HandleFileAccessInner(FileAccessContext fileAccessContext, FileAccessData fileAccessData)
    {
        _hasHadFileAccessReport = true;

        NodeContext? nodeContext = GetNodeContext(fileAccessContext);
        if (nodeContext is null)
        {
            return;
        }

        if (!DoTargetsMatchForWrite(nodeContext.TargetNames, fileAccessContext.Targets))
        {
            return;
        }

        _fileAccessRepository!.AddFileAccess(nodeContext, fileAccessData);
    }

    public override void HandleProcess(FileAccessContext fileAccessContext, ProcessData processData)
    {
        if (Initialized)
        {
            TimeAndLog(_pluginLogger, () => HandleProcessInner(fileAccessContext, processData));
        }
    }

    private void HandleProcessInner(FileAccessContext fileAccessContext, ProcessData processData)
    {
        NodeContext? nodeContext = GetNodeContext(fileAccessContext);
        if (nodeContext is null)
        {
            return;
        }

        if (!DoTargetsMatchForWrite(nodeContext.TargetNames, fileAccessContext.Targets))
        {
            return;
        }

        _fileAccessRepository!.AddProcess(nodeContext, processData);
    }

    public override Task HandleProjectFinishedAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
        => TimeAndLogAsync(
            logger,
            () => HandleProjectFinishedInnerAsync(fileAccessContext, buildResult, logger, cancellationToken),
            cancellationToken,
            context: fileAccessContext.ProjectFullPath);

    private async Task HandleProjectFinishedInnerAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (!Initialized)
        {
            return;
        }

        NodeContext? nodeContext = GetNodeContext(fileAccessContext);
        if (nodeContext is null)
        {
            return;
        }

        // If the target do not match for write, the result is not sufficient to cache as it's missing targets.
        if (!DoTargetsMatchForWrite(nodeContext.TargetNames, fileAccessContext.Targets))
        {
            // Remove the initial targets, if any, from the reported targets to guess at the originally requested targets.
            IReadOnlyList<string> requestedTargets;
            if (nodeContext.ProjectInstance.InitialTargets.Count > 0)
            {
                List<string> requestedTargetsList = new(fileAccessContext.Targets.Count - nodeContext.ProjectInstance.InitialTargets.Count);
                foreach (string reportedTarget in fileAccessContext.Targets)
                {
                    if (!nodeContext.ProjectInstance.InitialTargets.Contains(reportedTarget, StringComparer.OrdinalIgnoreCase))
                    {
                        requestedTargetsList.Add(reportedTarget);
                    }
                }

                requestedTargets = requestedTargetsList;
            }
            else
            {
                requestedTargets = fileAccessContext.Targets;
            }

            // If the targets match for read, it is very unlikely that we will later get a result that does match for write as the
            // targets ignored for reads should be information-gathering only. To help debug cache misses dump the fingerprint.
            if (DoTargetsMatchForRead(nodeContext.TargetNames, requestedTargets))
            {
                // Because we do not have the file accesses, we cannot know the PathSet.
                // This means that any cache miss analysis will need to take this into account.
                await DumpFingerprintLogAsync(logger, nodeContext, null);
            }

            return;
        }

        nodeContext.SetEndTime();

        // In niche cases, eg traversal projects, the build may be successful despite a dependency failing. Ignore these cases since we can't properly fingerprint failed dependencies.
        foreach (NodeContext dependency in nodeContext.Dependencies)
        {
            if (dependency.BuildResult == null)
            {
                logger.LogMessage($"Ignoring successful build for node {nodeContext.Id} with non-successful dependency: {dependency.Id}");
                return;
            }
        }

        // If file access reports are disabled in MSBuild we can't cache anything as we don't know what to cache.
        if (!_hasHadFileAccessReport)
        {
            return;
        }

        FileAccesses fileAccesses = _fileAccessRepository.FinishProject(nodeContext);

        // Package files are commonly just copied as outputs, so track the package inputs to compare with package outputs to avoid caching them.
        Dictionary<ContentHash, string> hashesToPackageFiles = new();
        List<Task<(byte[]?, string)>> packageFileHashingTasks = new();
        static async Task<(byte[]?, string)> WrapHashingTask(Task<byte[]?> hashTask, string packageRootRelativeFilePath) => (await hashTask, packageRootRelativeFilePath);

        List<string> filesRead = new();
        using var observedInputsWriter = new StreamWriter(Path.Combine(nodeContext.LogDirectory, "observedInputs.txt"));
        foreach (string absolutePath in fileAccesses.Inputs)
        {
            filesRead.Add(absolutePath);

            string? packageRootRelativeFilePath = absolutePath.MakePathRelativeTo(NugetPackageRoot);
            if (packageRootRelativeFilePath != null)
            {
                ValueTask<byte[]?> hashingTask = InputHasher.GetHashAsync(absolutePath);
                if (hashingTask.IsCompletedSuccessfully)
                {
                    byte[]? hashBytes = hashingTask.Result;
                    if (hashBytes != null)
                    {
                        hashesToPackageFiles[new ContentHash(HashType, hashBytes)] = packageRootRelativeFilePath;
                    }
                }
                else
                {
                    packageFileHashingTasks.Add(WrapHashingTask(hashingTask.AsTask(), packageRootRelativeFilePath));
                }

                continue;
            }

            string normalizedFilePath = _pathNormalizer.Normalize(absolutePath);
            await observedInputsWriter.WriteLineAsync(normalizedFilePath);

            string? relativeFilePath = absolutePath.MakePathRelativeTo(_repoRoot);
            if (relativeFilePath != null && _outputProducer.TryGetValue(relativeFilePath, out NodeContext? producerContext))
            {
                if (!nodeContext.IsDependentOn(producerContext))
                {
                    logger.LogWarning($"Project `{nodeContext.Id}` read the output `{relativeFilePath}` from project `{producerContext.Id}` without having dependency path between the two projects.");
                }

                if (IsDuplicateIdenticalOutputPath(logger, absolutePath))
                {
                    logger.LogMessage($"Project `{nodeContext.Id}` read the output `{relativeFilePath}` from project `{producerContext.Id}`, but that file may be re-written.");
                }
            }
        }

        if (packageFileHashingTasks.Count > 0)
        {
            // Wait for each of the hashing tasks to complete and then add them to the collection
            foreach ((byte[]? hashBytes, string packageRootRelativeFilePath) in await Task.WhenAll(packageFileHashingTasks))
            {
                if (hashBytes != null)
                {
                    hashesToPackageFiles[new ContentHash(HashType, hashBytes)] = packageRootRelativeFilePath;
                }
            }
        }

        Dictionary<string, string> outputPathToRelativePath = new(StringComparer.OrdinalIgnoreCase);
        using var observedOutputsWriter = new StreamWriter(Path.Combine(nodeContext.LogDirectory, "observedOutputs.txt"));
        foreach (string output in fileAccesses.Outputs)
        {
            if (File.Exists(output))
            {
                if (MatchesIgnoredOutputPattern(output))
                {
                    continue;
                }

                bool MatchesIgnoredOutputPattern(string path)
                {
                    if (_ignoredOutputPatterns != null && _ignoredOutputPatterns.Count > 0)
                    {
                        foreach (Glob ignoredOutputPattern in _ignoredOutputPatterns)
                        {
                            if (ignoredOutputPattern.IsMatch(path))
                            {
                                logger.LogMessage($"Ignoring output {path} due to matching ignored output pattern: {ignoredOutputPattern}");
                                return true;
                            }
                        }
                    }

                    return false;
                }

                string? relativeFilePath = output.MakePathRelativeTo(_repoRoot!);
                if (relativeFilePath != null)
                {
                    outputPathToRelativePath.Add(output, relativeFilePath);
                    await observedOutputsWriter.WriteLineAsync(relativeFilePath);
                }
                else
                {
                    logger.LogMessage($"Ignoring output outside of the repo root: {output}");
                }
            }
            else
            {
                // Some deletes look like writes so it's hard to tell if a file was deleted as part of the build. These are likely
                // temporary files which are created and then deleted during the build by the same project, so ignore.
                logger.LogMessage($"Ignoring output which no longer exists: {output}");
            }
        }

        PathSet? pathSet = FingerprintFactory.GetPathSet(nodeContext, filesRead);

        if (buildResult.OverallResult != BuildResultCode.Success)
        {
            // We still want to dump the fingerprint even if the build failed.
            await DumpFingerprintLogAsync(logger, nodeContext, pathSet);
            return;
        }

        // TODO dfederm: Handle CHL races
        NodeBuildResult nodeBuildResult = await _cacheClient.AddNodeAsync(
            nodeContext,
            pathSet,
            outputPathToRelativePath.Keys,
            absolutePathToHash =>
            {
                SortedDictionary<string, ContentHash> relativeOutputPaths = new(StringComparer.OrdinalIgnoreCase);
                SortedDictionary<string, string> packageFilesToCopy = new(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, ContentHash> kvp in absolutePathToHash)
                {
                    string outputAbsolutePath = kvp.Key;
                    ContentHash outputHash = kvp.Value;
                    string relativeOutputPath = outputPathToRelativePath[outputAbsolutePath];

                    // Outputs contains *all* outputs, including package files to copy.
                    relativeOutputPaths.Add(relativeOutputPath, outputHash);

                    // If any output hash happens to match the hash of a package input, mark it for replay
                    if (hashesToPackageFiles.TryGetValue(outputHash, out string? packageFilePath))
                    {
                        packageFilesToCopy.Add(relativeOutputPath, packageFilePath);
                    }
                }

                CheckForDuplicateOutputs(logger, relativeOutputPaths, nodeContext);

                return NodeBuildResult.FromBuildResult(relativeOutputPaths, packageFilesToCopy, buildResult, nodeContext.StartTimeUtc!.Value, nodeContext.EndTimeUtc!.Value, _buildId, _pathNormalizer);
            },
            cancellationToken);

        await FinishNodeAsync(logger, nodeContext, pathSet, nodeBuildResult);

        // TODO dfederm: Allow add failures to be just warnings?
    }

    private async Task FinishNodeAsync(
        PluginLoggerBase logger,
        NodeContext nodeContext,
        PathSet? pathSet,
        NodeBuildResult nodeBuildResult)
    {
        nodeContext.SetBuildResult(nodeBuildResult);

        await Task.WhenAll(
            DumpFingerprintLogAsync(logger, nodeContext, pathSet),
            DumpBuildResultLogAsync(logger, nodeContext, nodeBuildResult));
    }

    private NodeContext? GetNodeContext(FileAccessContext fileAccessContext)
    {
        if (!Initialized)
        {
            return null;
        }

        NodeDescriptor nodeDescriptor = _nodeDescriptorFactory.Create(fileAccessContext.ProjectFullPath, fileAccessContext.GlobalProperties);
        return _nodeContexts.TryGetValue(nodeDescriptor, out NodeContext? nodeContext) ? nodeContext : null;
    }

    private bool DoTargetsMatchForRead(HashSet<string> expectedTargets, IEnumerable<string> requestedTargets)
    {
        if (Settings is null || Settings.TargetsToIgnore.Count == 0)
        {
            return expectedTargets.SetEquals(requestedTargets);
        }

        HashSet<string> extraTargets = new(expectedTargets, StringComparer.OrdinalIgnoreCase);
        foreach (string target in requestedTargets)
        {
            if (!expectedTargets.Contains(target))
            {
                // If a target was requested that we weren't expecting, no match
                return false;
            }

            extraTargets.Remove(target);
        }

        extraTargets.ExceptWith(Settings.TargetsToIgnore);
        return extraTargets.Count == 0;
    }

    private static bool DoTargetsMatchForWrite(HashSet<string> expectedTargets, IEnumerable<string> reportedTargets)
    {
        // Check if the targets we expect is a subset of the targets we got. InitialTargets in particular may cause extra targets to be executed.
        // We will end up caching these extra results, but this is also intended as they do end up executing with the original request.
        return expectedTargets.IsSubsetOf(reportedTargets);
    }

    private async Task DumpNodeContextsAsync(PluginLoggerBase logger, Dictionary<NodeDescriptor, NodeContext> nodeContexts)
    {
        if (_nodeContexts is null)
        {
            throw new InvalidOperationException($"{nameof(_nodeContexts)} was unexpectedly null");
        }

        Task[] tasks = new Task[nodeContexts.Count];
        int i = 0;
        foreach (KeyValuePair<NodeDescriptor, NodeContext> kvp in nodeContexts)
        {
            tasks[i++] = Task.Run(() => DumpNodeContextAsync(kvp.Value));
        }

        await Task.WhenAll(tasks);

        async Task DumpNodeContextAsync(NodeContext nodeContext)
        {
            string filePath = Path.Combine(nodeContext.LogDirectory, "nodeInfo.json");
            try
            {
                using FileStream fileStream = File.Create(filePath);
                await using var jsonWriter = new Utf8JsonWriter(fileStream, SerializationHelper.WriterOptions);

                jsonWriter.WriteStartObject();

                jsonWriter.WriteString("id", nodeContext.Id);
                jsonWriter.WriteString("projectFileRelativePath", nodeContext.ProjectFileRelativePath);

                jsonWriter.WriteStartObject("globalProperties");
                foreach (KeyValuePair<string, string> kvp in nodeContext.FilteredGlobalProperties)
                {
                    jsonWriter.WriteString(kvp.Key, kvp.Value);
                }

                jsonWriter.WriteEndObject(); // globalProperties

                jsonWriter.WriteStartArray("targetNames");
                foreach (string targetName in nodeContext.TargetNames)
                {
                    jsonWriter.WriteStringValue(targetName);
                }

                jsonWriter.WriteEndArray(); // targetNames

                jsonWriter.WriteStartArray("dependencies");
                foreach (NodeContext dependency in nodeContext.Dependencies)
                {
                    jsonWriter.WriteStringValue(dependency.Id);
                }

                jsonWriter.WriteEndArray(); // dependencies

                jsonWriter.WriteEndObject();
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Non-fatal exception while writing {filePath}. {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private async Task DumpParserInfoAsync(
        PluginLoggerBase logger,
        NodeContext nodeContext,
        ParserInfo parserInfo)
    {
        string filePath = Path.Combine(nodeContext.LogDirectory, "parserInfo.json");
        try
        {
            using FileStream fileStream = File.Create(filePath);
            await using var jsonWriter = new Utf8JsonWriter(fileStream, SerializationHelper.WriterOptions);

            jsonWriter.WriteStartObject();

            jsonWriter.WriteString("projectFileRelativePath", parserInfo.ProjectFileRelativePath);

            jsonWriter.WriteStartArray("inputs");

            foreach (PredictedInput input in parserInfo.Inputs)
            {
                jsonWriter.WriteStartObject();

                string normalizedPath = _pathNormalizer!.Normalize(input.AbsolutePath);
                jsonWriter.WriteString("path", normalizedPath);

                jsonWriter.WriteStartArray("predictorNames");

                foreach (string predictorName in input.PredictorNames)
                {
                    jsonWriter.WriteStringValue(predictorName);
                }

                jsonWriter.WriteEndArray(); // predictorNames

                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray(); // inputs

            jsonWriter.WriteEndObject();
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Non-fatal exception while writing {filePath}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task DumpFingerprintLogAsync(
        PluginLoggerBase logger,
        NodeContext nodeContext,
        PathSet? pathSet)
    {
        if (!Initialized)
        {
            return;
        }

        Fingerprint? weakFingerprint = await FingerprintFactory.GetWeakFingerprintAsync(nodeContext);
        if (weakFingerprint is null)
        {
            // Nothing to dump. Likely dependencies failed or cache missed.
            return;
        }

        Fingerprint? strongFingerprint = await FingerprintFactory.GetStrongFingerprintAsync(pathSet);

        string filePath = Path.Combine(nodeContext.LogDirectory, "fingerprint.json");
        try
        {
            using FileStream fileStream = File.Create(filePath);
            await using var jsonWriter = new Utf8JsonWriter(fileStream, SerializationHelper.WriterOptions);

            jsonWriter.WriteStartObject();
            WriteFingerprintJson(jsonWriter, "weak", weakFingerprint);
            WriteFingerprintJson(jsonWriter, "strong", strongFingerprint);
            jsonWriter.WriteEndObject();
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Non-fatal exception while writing {filePath}. {ex.GetType().Name}: {ex.Message}");
        }

        static void WriteFingerprintJson(Utf8JsonWriter jsonWriter, string propertyName, Fingerprint? fingerprint)
        {
            jsonWriter.WritePropertyName(propertyName);

            if (fingerprint == null)
            {
                jsonWriter.WriteNullValue();
                return;
            }

            jsonWriter.WriteStartObject();

            jsonWriter.WriteString("hash", fingerprint.Hash.ToHex());

            jsonWriter.WritePropertyName("entries");
            jsonWriter.WriteStartArray();

            foreach (FingerprintEntry entry in fingerprint.Entries)
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("hash", entry.Hash.ToHex());
                jsonWriter.WriteString("description", entry.Description);
                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray(); // entries array

            jsonWriter.WriteEndObject();
        }
    }

    private static async Task DumpBuildResultLogAsync(
        PluginLoggerBase logger,
        NodeContext nodeContext,
        NodeBuildResult nodeBuildResult)
    {
        string filePath = Path.Combine(nodeContext.LogDirectory, "buildResult.json");
        try
        {
            using FileStream fileStream = File.Create(filePath);
            await JsonSerializer.SerializeAsync(fileStream, nodeBuildResult, SourceGenerationContext.Default.NodeBuildResult);
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Non-fatal exception while writing {filePath}. {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryAcquireLock(PluginSettings settings, PluginLoggerBase logger)
    {
        // Acquire a process-wide lock. This way if it fails, we can provide a more targeted warning.
        if (!SinglePluginInstanceLock.Wait(millisecondsTimeout: 0))
        {
            logger.LogError("Another instance of MSBuildCache is already running in this build. This is typically due to a misconfiguration of the plugin settings, in particular different plugin settings across projects.");
            return false;
        }

        // Now that this instance owns the lock, copy it to an instance variable to facilitate proper release on dispose.
        _singlePluginInstanceMutex = SinglePluginInstanceLock;

        // Acquire a system-wide lock. If this fails, a separate build may be running, which might be intentional by the user so this may not be a warning.
        // Note: Ensure this doesn't collide with the local cache's directory lock by using a unique file name.
        string directoryLockFile = Path.Combine(settings.LocalCacheRootPath, "MSBuildCache.lock");
        _localCacheDirectoryLock = new DirectoryLock(directoryLockFile, logger);
        if (!_localCacheDirectoryLock.Acquire())
        {
            logger.LogWarning("Another instance of MSBuildCache is already running in another build. This build will not receive any cache hits.");
            return false;
        }

        return true;
    }

    private static string? GetRepoRoot(CacheContext context, PluginLoggerBase logger)
    {
        IEnumerable<string> projectFilePaths = context.Graph != null
            ? context.Graph.EntryPointNodes.Select(node => node.ProjectInstance.FullPath)
            : context.GraphEntryPoints != null
                ? context.GraphEntryPoints.Select(entryPoint => entryPoint.ProjectFile)
                : throw new InvalidOperationException($"{nameof(CacheContext)} did not contain a {nameof(context.Graph)} or {nameof(context.GraphEntryPoints)}");

        HashSet<string> repoRoots = new(StringComparer.OrdinalIgnoreCase);
        foreach (string projectFilePath in projectFilePaths)
        {
            string? repoRoot = GetRepoRootInternal(Path.GetDirectoryName(projectFilePath)!);

            // Tolerate projects which aren't under any git repo.
            if (repoRoot != null)
            {
                repoRoots.Add(repoRoot);
            }
        }

        if (repoRoots.Count == 0)
        {
            logger.LogWarning("No projects are under git source control. Disabling the cache.");
            return null;
        }

        if (repoRoots.Count == 1)
        {
            string repoRoot = repoRoots.First();
            logger.LogMessage($"Repo root: {repoRoot}");
            return repoRoot;
        }

        logger.LogWarning($"Graph contains projects from multiple git repositories. Disabling the cache. Repo roots: {string.Join(", ", repoRoots)}");
        return null;

        static string? GetRepoRootInternal(string path)
        {
            // Note: When using git worktrees, .git may be a file instead of a directory.
            string gitPath = Path.Combine(path, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return path;
            }

            string? parentDir = Path.GetDirectoryName(path);
            return parentDir != null ? GetRepoRootInternal(parentDir) : null;
        }
    }

    private static string GetNuGetPackageRoot()
    {
        string? nugetPackageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nugetPackageRoot))
        {
            nugetPackageRoot = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE")!, @".nuget\packages");
        }

        return nugetPackageRoot;
    }

    private void WarnOnCowWithDifferingVolumes(PluginLoggerBase logger)
    {
        if (_repoRoot is null
            || NugetPackageRoot is null
            || Settings is null)
        {
            throw new InvalidOperationException();
        }

        ICopyOnWriteFilesystem copyOnWriteFilesystem = CopyOnWriteFilesystemFactory.GetInstance();
        if (copyOnWriteFilesystem.CopyOnWriteLinkSupportedInDirectoryTree(_repoRoot))
        {
            WarnIfCowNotSupportedBetweenRepoRootAndPath(NugetPackageRoot, "NuGet package root");
            WarnIfCowNotSupportedBetweenRepoRootAndPath(Settings.LocalCacheRootPath, "local cache");
        }

        void WarnIfCowNotSupportedBetweenRepoRootAndPath(string path, string pathDescription)
        {
            if (!copyOnWriteFilesystem.CopyOnWriteLinkSupportedBetweenPaths(_repoRoot, path, pathsAreFullyResolved: true))
            {
                logger.LogWarning($"The repository path '{_repoRoot}' supports copy-on-write but the {pathDescription} '{path}' resides on a different volume. This may impact performance.");
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> GetSourceControlFileHashesAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (_repoRoot == null)
        {
            throw new InvalidOperationException($"{nameof(_repoRoot)} was unexpectedly null");
        }

        logger.LogMessage("Source Control: Getting hashes");
        Stopwatch stopwatch = Stopwatch.StartNew();

        GitFileHashProvider hashProvider = new(logger);
        IReadOnlyDictionary<string, byte[]> fileHashes = await hashProvider.GetFileHashesAsync(_repoRoot, cancellationToken);
        logger.LogMessage($"Source Control: File hashes query took {stopwatch.ElapsedMilliseconds} ms");

        return fileHashes;
    }

    private ProjectGraph GetProjectGraph(CacheContext context, PluginLoggerBase logger)
    {
        if (context.Graph != null)
        {
            logger.LogMessage($"Project graph with {context.Graph.ProjectNodes.Count} nodes provided.");
            return context.Graph;
        }

        if (context.GraphEntryPoints != null)
        {
            logger.LogMessage($"Constructing project graph using {context.GraphEntryPoints.Count} entry points.");
            Stopwatch stopwatch = Stopwatch.StartNew();
            ProjectGraph graph = new(context.GraphEntryPoints);
            logger.LogMessage($"Constructed project graph with {graph.ProjectNodes.Count} nodes in {stopwatch.Elapsed.TotalSeconds:F2}s.");
            return graph;
        }

        throw new InvalidOperationException($"{nameof(CacheContext)} did not contain a {nameof(context.Graph)} or {nameof(context.GraphEntryPoints)}");
    }

    private void LogCacheStats(PluginLoggerBase logger)
    {
        int cacheHitCount = _cacheHitCount;
        int cacheMissCount = _cacheMissCount;

        double cacheHitRatio = cacheHitCount + cacheMissCount != 0
            ? ((double)cacheHitCount) / (cacheHitCount + cacheMissCount)
            : double.NaN;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("Project cache statistics:");
        sb.Append("  Cache Hit Count: ");
        sb.Append(cacheHitCount);
        if (_cacheHitDurationMilliseconds > 0)
        {
            double _cacheHitDurationSeconds = _cacheHitDurationMilliseconds / 1000.0;
            sb.Append(" (saved ");
            if (_cacheHitDurationSeconds >= 3600.0)
            {
                sb.AppendFormat("{0:F1}", _cacheHitDurationSeconds / 3600.0);
                sb.Append(" project-hours)");
            }
            else if (_cacheHitDurationSeconds >= 60.0)
            {
                sb.AppendFormat("{0:F1}", _cacheHitDurationSeconds / 60.0);
                sb.Append(" project-minutes)");
            }
            else
            {
                sb.AppendFormat("{0:F1}", _cacheHitDurationSeconds);
                sb.Append(" project-seconds)");
            }
        }
        sb.AppendLine();
        sb.Append("  Cache Miss Count: ");
        sb.Append(cacheMissCount);
        sb.AppendLine();
        sb.Append("  Cache Hit Ratio: ");
        sb.AppendFormat("{0:P1}", cacheHitRatio);
        sb.AppendLine();
        logger.LogMessage(sb.ToString(), MessageImportance.High);
    }

    private bool IsDuplicateIdenticalOutputPath(PluginLoggerBase logger, string absolutePath)
    {
        if (_identicalDuplicateOutputPatterns != null && _identicalDuplicateOutputPatterns.Count > 0)
        {
            foreach (Glob pattern in _identicalDuplicateOutputPatterns)
            {
                if (pattern.IsMatch(absolutePath))
                {
                    logger.LogMessage($"Absolute path `{absolutePath}` matches identical-global glob pattern `{pattern}`.", MessageImportance.Low);
                    return true;
                }
            }
        }

        return false;
    }

    private void CheckForDuplicateOutputs(PluginLoggerBase logger, IReadOnlyDictionary<string, ContentHash> relativeFilePathToHash, NodeContext nodeContext)
    {
        foreach (KeyValuePair<string, ContentHash> kvp in relativeFilePathToHash)
        {
            string relativeFilePath = kvp.Key;
            ContentHash newHash = kvp.Value;

            // If this is the first writer to this path, then we are done.
            NodeContext previousNode = _outputProducer.GetOrAdd(relativeFilePath, nodeContext);
            if (previousNode == nodeContext)
            {
                return;
            }

            // This is only allowed if marked as a duplicate-identical output
            string absoluteFilePath = Path.Combine(_repoRoot!, relativeFilePath);
            if (!IsDuplicateIdenticalOutputPath(logger, absoluteFilePath))
            {
                logger.LogError($"Node {nodeContext.Id} produced output {relativeFilePath} which was already produced by another node {_outputProducer[relativeFilePath].Id}.");
                return;
            }

            // This should never happen as the previous node is a dependent of this node...
            if (previousNode.BuildResult == null)
            {
                logger.LogError($"Node {nodeContext.Id} produced output {relativeFilePath} which was already produced by another node {previousNode.Id}, however the hash of that first output is unknown.");
                return;
            }

            // compare the hash of the original output to this output and log/error accordingly.
            ContentHash previousHash = previousNode.BuildResult.Outputs[relativeFilePath];
            if (previousHash != newHash)
            {
                logger.LogError($"Node {nodeContext.Id} produced output {relativeFilePath} with hash {newHash} which was already produced by another node {previousNode.Id} with a different hash {previousHash}.");
                return;
            }

            // Duplicate-identical outputs are only allowed if there is a strict ordering between the multiple writers.
            if (!nodeContext.IsDependentOn(previousNode))
            {
                logger.LogWarning($"Node {nodeContext.Id} produced output {relativeFilePath} which was already produced by another node {previousNode.Id}, but there is no ordering between the two nodes.");
                return;
            }

            logger.LogMessage($"Node {nodeContext.Id} produced duplicate-identical output {relativeFilePath} which was already produced by another node {previousNode.Id}. Allowing as content is the same.");
        }
    }

    private static async Task<T> TimeAndLogAsync<T>(
        PluginLoggerBase logger,
        Func<Task<T>> innerAsync,
        CancellationToken cancellationToken,
        string? context = null,
        [CallerMemberName] string memberName = "")
    {
        var timer = Stopwatch.StartNew();
        try
        {
            T result = await innerAsync();

            // only log 1+ second operations to avoid debug spew
            if (timer.ElapsedMilliseconds > 1000L)
            {
                if (context == null)
                {
                    logger.LogMessage($"{memberName} succeeded after {timer.ElapsedMilliseconds} ms.");
                }
                else
                {
                    logger.LogMessage($"{memberName}({context}) succeeded after {timer.ElapsedMilliseconds} ms.");
                }
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (context == null)
            {
                logger.LogMessage($"{memberName} cancelled after {timer.ElapsedMilliseconds} ms");
            }
            else
            {
                logger.LogMessage($"{memberName}({context}) cancelled after {timer.ElapsedMilliseconds} ms");
            }
            throw;
        }
        catch (Exception e)
        {
            if (context == null)
            {
                logger.LogWarning($"{memberName} failed after {timer.ElapsedMilliseconds} ms: {e}");
            }
            else
            {
                logger.LogWarning($"{memberName}({context}) failed after {timer.ElapsedMilliseconds} ms: {e}");
            }
            throw;
        }
    }

    private static void TimeAndLog(
        PluginLoggerBase logger,
        Action inner,
        [CallerMemberName] string memberName = "")
    {
        var timer = Stopwatch.StartNew();
        try
        {
            inner();

            // only log 1+ second operations to avoid debug spew
            if (timer.ElapsedMilliseconds > 1000L)
            {
                logger.LogMessage($"{memberName} succeeded after {timer.ElapsedMilliseconds} ms.");
            }
        }
        catch (Exception e)
        {
            logger.LogWarning($"{memberName} failed after {timer.ElapsedMilliseconds} ms: {e}");
            throw;
        }
    }

    private static Task<int> TimeAndLogAsync(PluginLoggerBase logger, Func<Task> innerAsync, CancellationToken cancellationToken, string? context = null, [CallerMemberName] string memberName = "")
        => TimeAndLogAsync(logger, async () => { await innerAsync(); return 0; }, cancellationToken, context, memberName);
}

public static class ProjectGraphNodeExtensions
{
    public static bool IsDependentOn(this NodeContext possibleDependent, NodeContext possibleDependency)
    {
        HashSet<NodeContext> visited = new();
        Queue<NodeContext> toCheck = new();
        toCheck.Enqueue(possibleDependent);
        while (toCheck.Count > 0)
        {
            NodeContext current = toCheck.Dequeue();
            if (current == possibleDependency)
            {
                return true;
            }
            foreach (NodeContext dependency in current.Dependencies)
            {
                if (visited.Add(dependency))
                {
                    toCheck.Enqueue(dependency);
                }
            }
        }

        return false;
    }
}
