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

    private readonly ConcurrentDictionary<string, NodeContext> _outputProducer = new(StringComparer.OrdinalIgnoreCase);

    private string? _repoRoot;
    private string? _buildId;

    // Set if we've received any file access report. Ideally MSBuild would tell us in the CacheContext
    private bool _hasHadFileAccessReport;

    private PluginLoggerBase? _pluginLogger;
    private NodeDescriptorFactory? _nodeDescriptorFactory;
    private NodeContextRepository? _nodeContextRepository;
    private FileAccessRepository? _fileAccessRepository;
    private ICacheClient? _cacheClient;
    private IReadOnlyCollection<Glob>? _ignoredOutputPatterns;
    private IReadOnlyCollection<Glob>? _identicalDuplicateOutputPatterns;
    private DirectoryLock? _localCacheDirectoryLock;
    private PathNormalizer? _pathNormalizer;

    private int _cacheHitCount;
    private long _cacheHitDurationMilliseconds;
    private int _cacheMissCount;

    static MSBuildCachePluginBase() =>
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var assemblyName = new AssemblyName(args.Name);

            var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var loadedAssembly = loadedAssemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);

            if (loadedAssembly != null)
            {
                return loadedAssembly;
            }

            string candidateAssemblyPath = Path.Combine(PluginAssemblyDirectory, $"{assemblyName.Name}.dll");

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
        nameof(_pathNormalizer),
        nameof(ContentHasher),
        nameof(InputHasher),
        nameof(_nodeDescriptorFactory),
        nameof(_nodeContextRepository),
        nameof(NodeContextRepository),
        nameof(FingerprintFactory),
        nameof(_fileAccessRepository),
        nameof(_cacheClient),
        nameof(_ignoredOutputPatterns),
        nameof(_identicalDuplicateOutputPatterns)
    )]
    protected bool Initialized { get; private set; }

    protected TPluginSettings? Settings { get; private set; }

    protected abstract HashType HashType { get; }

    protected IContentHasher? ContentHasher { get; private set; }

    protected IInputHasher? InputHasher { get; private set; }

    protected INodeContextRepository? NodeContextRepository => _nodeContextRepository;

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

    protected virtual IFingerprintFactory CreateFingerprintFactory()
    {
        if (ContentHasher == null
            || InputHasher == null
            || NodeContextRepository == null
            || Settings == null
            || _pathNormalizer == null)
        {
            throw new InvalidOperationException();
        }

        return new FingerprintFactory(ContentHasher, InputHasher, NodeContextRepository, Settings, _pathNormalizer);
    }

    protected abstract Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken);

    protected FileRealizationMode GetFileRealizationMode(string path)
        => IsDuplicateIdenticalOutputAbsolutePath(_pluginLogger!, path) ? FileRealizationMode.CopyNoVerify : FileRealizationMode.Any;

    public async override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        try
        {
            await BeginBuildInnerAsync(context, logger, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning($"{nameof(BeginBuildAsync)}: {e}");
            throw;
        }
    }

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
        // Note: Ensure this doesn't collide with the local cache's directory lock by using a unique file name.
        string directoryLockFile = Path.Combine(Settings.LocalCacheRootPath, "MSBuildCache.lock");
        _localCacheDirectoryLock = new DirectoryLock(directoryLockFile, logger);
        if (!_localCacheDirectoryLock.Acquire())
        {
            logger.LogMessage("Another instance of MSBuildCache is already running. This build will not receive any cache hits.", MessageImportance.High);

            // Note: by returning early, many fields won't be populated. Other methods are responsible for handling this and interpreting it as "not enabled".
            return;
        }

        string nugetPackageRoot = GetNuGetPackageRoot();
        _pathNormalizer = new PathNormalizer(_repoRoot, nugetPackageRoot);

        if (Directory.Exists(Settings.LogDirectory))
        {
            Directory.Delete(Settings.LogDirectory, recursive: true);
        }

        Directory.CreateDirectory(Settings.LogDirectory);

        ContentHasher = HashInfoLookup.Find(HashType).CreateContentHasher();

        IReadOnlyDictionary<string, byte[]> fileHashes = await GetSourceControlFileHashesAsync(logger, cancellationToken);

        InputHasher = new InputHasher(ContentHasher, fileHashes);

        HashSet<string> globalPropertiesToIgnore = new(Settings.GlobalPropertiesToIgnore, StringComparer.OrdinalIgnoreCase);
        _nodeDescriptorFactory = new NodeDescriptorFactory(globalPropertiesToIgnore);

        ProjectGraph graph = GetProjectGraph(context, logger);
        Parser parser = new(logger, _repoRoot, fileHashes);
        IReadOnlyDictionary<ProjectGraphNode, ParserInfo> parserInfoForNodes = parser.Parse(graph);

        // TODO: MSBuild should give this to us via CacheContext
        var entryProjectTargets = Array.Empty<string>();
        IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> targetListPerNode = graph.GetTargetLists(entryProjectTargets);

        var nodeContexts = new Dictionary<NodeDescriptor, NodeContext>(parserInfoForNodes.Count);
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
                inputs[i] = parserInfo.Inputs[i].RelativePath;
            }

            NodeDescriptor nodeDescriptor = _nodeDescriptorFactory.Create(node.ProjectInstance);
            NodeContext nodeContext = new(Settings.LogDirectory, node, parserInfo.NormalizedProjectFilePath, nodeDescriptor.GlobalProperties, inputs, targetNames);

            await DumpParserInfoAsync(logger, nodeContext, parserInfo);

            nodeContexts.Add(nodeDescriptor, nodeContext);
        }

        _nodeContextRepository = new NodeContextRepository(nodeContexts, _nodeDescriptorFactory);
        FingerprintFactory = CreateFingerprintFactory();
        _fileAccessRepository = new FileAccessRepository(logger, Settings);
        _cacheClient = await CreateCacheClientAsync(logger, cancellationToken);
        _ignoredOutputPatterns = Settings.IgnoredOutputPatterns;
        _identicalDuplicateOutputPatterns = Settings.IdenticalDuplicateOutputPatterns;
        Initialized = true;
    }

    public override async Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        try
        {
            await EndBuildInnerAsync(logger, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning($"{nameof(EndBuildAsync)}: {e}");
            throw;
        }
    }

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

    public override async Task<CacheResult> GetCacheResultAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        try
        {
            return await GetCacheResultInnerAsync(buildRequest, logger, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning($"{nameof(GetCacheResultAsync)}: {e}");
            throw;
        }
    }

    private async Task<CacheResult> GetCacheResultInnerAsync(BuildRequestData buildRequest, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (!Initialized)
        {
            // BeginBuild didn't finish successfully. It's expected to log sufficiently, so just bail.
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        ProjectInstance projectInstance = buildRequest.ProjectInstance;
        if (projectInstance == null)
        {
            logger.LogWarning($"Project instance was unexpectedly null for build request for project {buildRequest.ProjectFullPath}");
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        if (!NodeContextRepository.TryGetNodeContext(projectInstance, out NodeContext? nodeContext))
        {
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        nodeContext.SetStartTime();

        bool isBuildAndTestRequest = IsBuildAndTestRequest(buildRequest);
        if (!nodeContext.TargetNames.SetEquals(buildRequest.TargetNames) && !isBuildAndTestRequest)
        {
            logger.LogMessage($"`TargetNames` does not match for {nodeContext.Id}. `{string.Join(";", nodeContext.TargetNames)}` vs `{string.Join(";", buildRequest.TargetNames)}`.");
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable);
        }

        (PathSet? pathSet, NodeBuildResult? nodeBuildResult) = await _cacheClient.GetNodeAsync(nodeContext, cancellationToken);
        if (nodeBuildResult is null)
        {
            Interlocked.Increment(ref _cacheMissCount);
            return CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss);
        }

        CheckForDuplicateOutputs(logger, nodeBuildResult.Outputs, nodeContext);

        await FinishNodeAsync(logger, nodeContext, pathSet, nodeBuildResult);
        // In the case of a build-and-test request, we want to cache the build results but and the test execution (i.e, only execute tests on cache misses).
        _ = bool.TryParse(projectInstance.GetPropertyValue("IsTestProject"), out bool isTestProject);
        if (isBuildAndTestRequest && isTestProject)
        {
            // this property can be read by downstream tasks, such as a Test Target, to determine if the test execution should be skipped
            _ = projectInstance.SetProperty("SkipExecution", "true");
            string skippedMsg = $"Test Project Cache Hit, Test Execution Skipped: {projectInstance.GetPropertyValue("MSBuildProjectName")}";
            logger.LogMessage(skippedMsg, MessageImportance.High);
        }

        Interlocked.Increment(ref _cacheHitCount);
        Interlocked.Add(ref _cacheHitDurationMilliseconds, (int)(nodeBuildResult.EndTimeUtc - nodeBuildResult.StartTimeUtc).TotalMilliseconds);
        return nodeBuildResult.ToCacheResult(_pathNormalizer);
    }

    public override void HandleFileAccess(FileAccessContext fileAccessContext, FileAccessData fileAccessData)
    {
        _hasHadFileAccessReport = true;

        try
        {
            HandleFileAccessInner(fileAccessContext, fileAccessData);
        }
        catch (Exception e)
        {
            _pluginLogger?.LogWarning($"{nameof(HandleFileAccess)}: {e}");
            throw;
        }
    }
    /// <summary>
    /// Determines if the build request is a build and test request.
    /// </summary>
    /// <param name="buildRequest">The build request data</param>
    /// <returns>A bool value representing whether the build request is a build and test request.</returns>
    private static bool IsBuildAndTestRequest(BuildRequestData buildRequest)
    {
        if (buildRequest == null)
        {
            return false;
        }
        // Check if the build request includes both "Build" and "Test" targets
        bool isBuildAndTestRequest = buildRequest.TargetNames.Contains("Build") && buildRequest.TargetNames.Contains("Test");
        return isBuildAndTestRequest;
    }

    private void HandleFileAccessInner(FileAccessContext fileAccessContext, FileAccessData fileAccessData)
    {
        if (!Initialized)
        {
            return;
        }

        // Ignore file accesses outside the repository
        ReadOnlySpan<char> path = PathHelper.RemoveLongPathPrefixes(fileAccessData.Path.AsSpan());
        if (!path.StartsWith(_repoRoot.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        NodeContext? nodeContext = GetNodeContext(fileAccessContext);
        if (nodeContext == null)
        {
            return;
        }

        _fileAccessRepository.AddFileAccess(nodeContext, fileAccessData);
    }

    public override void HandleProcess(FileAccessContext fileAccessContext, ProcessData processData)
    {
        try
        {
            HandleProcessInner(fileAccessContext, processData);
        }
        catch (Exception e)
        {
            _pluginLogger?.LogWarning($"{nameof(HandleProcessInner)}: {e}");
            throw;
        }
    }

    private void HandleProcessInner(FileAccessContext fileAccessContext, ProcessData processData)
    {
        if (!Initialized)
        {
            return;
        }

        NodeContext? nodeContext = GetNodeContext(fileAccessContext);
        if (nodeContext == null)
        {
            return;
        }

        _fileAccessRepository.AddProcess(nodeContext, processData);
    }

    public override async Task HandleProjectFinishedAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        try
        {
            await HandleProjectFinishedInnerAsync(fileAccessContext, buildResult, logger, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning($"{nameof(HandleProjectFinishedAsync)}: {e}");
            throw;
        }
    }

    private async Task HandleProjectFinishedInnerAsync(FileAccessContext fileAccessContext, BuildResult buildResult, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (!Initialized)
        {
            return;
        }

        NodeContext? nodeContext = GetNodeContext(fileAccessContext);
        if (nodeContext == null)
        {
            return;
        }

        nodeContext.SetEndTime();

        // In niche cases, eg traversal projects, the build may be successful despite a dependency failing. Ignore these cases since we can't properly fingerprint failed dependencies.
        foreach (ProjectGraphNode dependencyNode in nodeContext.Node.ProjectReferences)
        {
            if (!NodeContextRepository.TryGetNodeContext(dependencyNode.ProjectInstance, out NodeContext? dependencyNodeContext))
            {
                return;
            }

            if (dependencyNodeContext.BuildResult == null)
            {
                logger.LogMessage($"Ignoring successful build for node {nodeContext.Id} with non-successful dependency: {dependencyNodeContext.Id}");
                return;
            }
        }

        FileAccesses fileAccesses = _fileAccessRepository.FinishProject(nodeContext);

        // If file access reports are disabled in MSBuild we can't cache anything as we don't know what to cache.
        if (!_hasHadFileAccessReport)
        {
            return;
        }

        List<string> normalizedFilesRead = new();
        using var observedInputsWriter = new StreamWriter(Path.Combine(nodeContext.LogDirectory, "observedInputs.txt"));
        foreach (string absolutePath in fileAccesses.Inputs)
        {
            string? normalizedFilePath = absolutePath.MakePathRelativeTo(_repoRoot!);
            if (normalizedFilePath == null)
            {
                continue;
            }

            normalizedFilesRead.Add(normalizedFilePath);
            await observedInputsWriter.WriteLineAsync(normalizedFilePath);

            if (_outputProducer.TryGetValue(normalizedFilePath, out NodeContext? producerContext))
            {
                if (!nodeContext.Node.IsDependentOn(producerContext.Node))
                {
                    logger.LogWarning($"Project `{nodeContext.Id}` read the output `{normalizedFilePath}` from project `{producerContext.Id}` without having dependency path between the two projects.");
                }

                if (IsDuplicateIdenticalOutputAbsolutePath(logger, absolutePath))
                {
                    logger.LogMessage($"Project `{nodeContext.Id}` read the output `{normalizedFilePath}` from project `{producerContext.Id}`, but that file may be re-written.");
                }
            }
        }

        Dictionary<string, string> normalizedOutputPaths = new(StringComparer.OrdinalIgnoreCase);
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

                string? normalizedFilePath = output.MakePathRelativeTo(_repoRoot!);
                if (normalizedFilePath != null)
                {
                    normalizedOutputPaths.Add(output, normalizedFilePath);
                    await observedOutputsWriter.WriteLineAsync(normalizedFilePath);
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

        PathSet? pathSet = FingerprintFactory.GetPathSet(nodeContext, normalizedFilesRead);

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
            normalizedOutputPaths.Keys,
            absolutePathToHash =>
            {
                SortedDictionary<string, ContentHash> outputs = new(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, ContentHash> absolutePathAndHash in absolutePathToHash)
                {
                    outputs.Add(
                        normalizedOutputPaths[absolutePathAndHash.Key], // so we map back to normalized paths
                        absolutePathAndHash.Value);
                }

                CheckForDuplicateOutputs(logger, outputs, nodeContext);

                return NodeBuildResult.FromBuildResult(outputs, buildResult, nodeContext.StartTimeUtc!.Value, nodeContext.EndTimeUtc!.Value, _buildId, _pathNormalizer);
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
        if (!_nodeContextRepository.TryGetNodeContext(nodeDescriptor, out NodeContext? nodeContext))
        {
            return null;
        }

        // Note: Checking if the targets we expect is a subset of the targets we got. InitialTargets in particular may cause extra targets to be executed.
        // We will end up caching these extra results, but this is also intended as they do end up executing with the original request.
        if (!nodeContext.TargetNames.IsSubsetOf(fileAccessContext.Targets))
        {
            return null;
        }

        return nodeContext;
    }

    private static async Task DumpParserInfoAsync(
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

            jsonWriter.WriteString("normalizedProjectFilePath", parserInfo.NormalizedProjectFilePath);

            jsonWriter.WriteStartArray("inputs");

            foreach (PredictedInput input in parserInfo.Inputs)
            {
                jsonWriter.WriteStartObject();

                jsonWriter.WriteString("relativePath", input.RelativePath);

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

        string filePath = Path.Combine(nodeContext.LogDirectory, "fingerprint.json");
        try
        {
            using FileStream fileStream = File.Create(filePath);
            await using var jsonWriter = new Utf8JsonWriter(fileStream, SerializationHelper.WriterOptions);

            jsonWriter.WriteStartObject();
            WriteFingerprintJson(jsonWriter, "weak", FingerprintFactory.GetWeakFingerprint(nodeContext));
            WriteFingerprintJson(jsonWriter, "strong", FingerprintFactory.GetStrongFingerprint(pathSet));
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
            await JsonSerializer.SerializeAsync(fileStream, nodeBuildResult, SerializationHelper.SerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Non-fatal exception while writing {filePath}. {ex.GetType().Name}: {ex.Message}");
        }
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
            string gitDir = Path.Combine(path, ".git");
            if (Directory.Exists(gitDir))
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

    private bool IsDuplicateIdenticalOutputAbsolutePath(PluginLoggerBase logger, string absolutePath)
    {
        if (_identicalDuplicateOutputPatterns != null && _identicalDuplicateOutputPatterns.Count > 0)
        {
            foreach (Glob pattern in _identicalDuplicateOutputPatterns)
            {
                if (pattern.IsMatch(absolutePath))
                {
                    logger.LogMessage($"Absolute path `{absolutePath}` matches identical-global glob pattern `{pattern}`.");
                    return true;
                }
            }
        }

        return false;
    }

    private void CheckForDuplicateOutputs(PluginLoggerBase logger, IReadOnlyDictionary<string, ContentHash> normalizedFilePathToHash, NodeContext nodeContext)
    {
        foreach (KeyValuePair<string, ContentHash> kvp in normalizedFilePathToHash)
        {
            string normalizedFilePath = kvp.Key;
            ContentHash newHash = kvp.Value;

            // If this is the first writer to this path, then we are done.
            NodeContext previousNode = _outputProducer.GetOrAdd(normalizedFilePath, nodeContext);
            if (previousNode == nodeContext)
            {
                return;
            }

            // This is only allowed if marked as a duplicate-identical output
            if (!IsDuplicateIdenticalOutputAbsolutePath(logger, Path.Combine(_repoRoot!, normalizedFilePath)))
            {
                logger.LogError($"Node {nodeContext.Id} produced output {normalizedFilePath} which was already produced by another node {_outputProducer[normalizedFilePath].Id}.");
                return;
            }

            // This should never happen as the previous node is a dependent of this node...
            if (previousNode.BuildResult == null)
            {
                logger.LogError($"Node {nodeContext.Id} produced output {normalizedFilePath} which was already produced by another node {previousNode.Id}, however the hash of that first output is unknown.");
                return;
            }

            // compare the hash of the original output to this output and log/error accordingly.
            ContentHash previousHash = previousNode.BuildResult!.Outputs[normalizedFilePath];
            if (previousHash != newHash)
            {
                logger.LogError($"Node {nodeContext.Id} produced output {normalizedFilePath} with hash {newHash} which was already produced by another node {previousNode.Id} with a different hash {previousHash}.");
                return;
            }

            // Duplicate-identical outputs are only allowed if there is a strict ordering between the multiple writers.
            if (!nodeContext.Node.IsDependentOn(previousNode.Node))
            {
                logger.LogWarning($"Node {nodeContext.Id} produced output {normalizedFilePath} which was already produced by another node {previousNode.Id}, but there is no ordering between the two nodes.");
                return;
            }

            logger.LogMessage($"Node {nodeContext.Id} produced duplicate-identical output {normalizedFilePath} which was already produced by another node {previousNode.Id}. Allowing as content is the same.");
        }
    }
}

public static class ProjectGraphNodeExtensions
{
    public static bool IsDependentOn(this ProjectGraphNode possibleDependent, ProjectGraphNode possibleDependency) =>
        possibleDependent.ProjectReferences.Any(n => n == possibleDependency || n.IsDependentOn(possibleDependency));
}
