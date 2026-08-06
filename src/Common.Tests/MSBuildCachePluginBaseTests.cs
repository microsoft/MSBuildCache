// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using DotNet.Globbing;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public sealed class MSBuildCachePluginBaseTests
{
    private const string RepoRoot = @"X:\Repo";
    private const string UniqueOutputPath = "a.unique";
    private const string DuplicateOutputPath = "z.duplicate";
    private const string TrailingUniqueOutputPath = "zz.unique";

    [TestMethod]
    public void CheckForDuplicateOutputsDetectsConflictingDuplicateAfterUniqueOutput()
    {
        ContentHash previousHash = ContentHash.Random();
        ContentHash newHash = ContentHash.Random();
        while (newHash == previousHash)
        {
            newHash = ContentHash.Random();
        }

        NodeContext previousNode = CreateNode("previous.proj");
        previousNode.SetBuildResult(CreateBuildResult(previousHash));
        NodeContext currentNode = CreateNode("current.proj", [previousNode]);
        ConcurrentDictionary<string, NodeContext> outputProducer = CreateOutputProducer(previousNode);
        MockPluginLogger logger = new();

        CheckForDuplicateOutputs(
            logger,
            outputProducer,
            currentNode,
            newHash,
            [Glob.Parse(Path.Combine(RepoRoot, DuplicateOutputPath))]);

        Assert.AreSame(currentNode, outputProducer[UniqueOutputPath]);
        Assert.AreSame(previousNode, outputProducer[DuplicateOutputPath]);
        Assert.AreSame(currentNode, outputProducer[TrailingUniqueOutputPath]);
        Assert.HasCount(2, logger.LogEntries);
        Assert.AreEqual(PluginLogLevel.Error, logger.LogEntries[1].LogLevel);
        StringAssert.Contains(logger.LogEntries[1].Message, "with a different hash", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CheckForDuplicateOutputsAllowsOrderedIdenticalDuplicateAfterUniqueOutput()
    {
        ContentHash hash = ContentHash.Random();
        NodeContext previousNode = CreateNode("previous.proj");
        previousNode.SetBuildResult(CreateBuildResult(hash));
        NodeContext currentNode = CreateNode("current.proj", [previousNode]);
        ConcurrentDictionary<string, NodeContext> outputProducer = CreateOutputProducer(previousNode);
        MockPluginLogger logger = new();

        CheckForDuplicateOutputs(
            logger,
            outputProducer,
            currentNode,
            hash,
            [Glob.Parse(Path.Combine(RepoRoot, DuplicateOutputPath))]);

        Assert.AreSame(currentNode, outputProducer[UniqueOutputPath]);
        Assert.AreSame(previousNode, outputProducer[DuplicateOutputPath]);
        Assert.AreSame(currentNode, outputProducer[TrailingUniqueOutputPath]);
        Assert.HasCount(2, logger.LogEntries);
        Assert.AreEqual(PluginLogLevel.Message, logger.LogEntries[1].LogLevel);
        StringAssert.Contains(logger.LogEntries[1].Message, "Allowing as content is the same", StringComparison.Ordinal);
    }

    [TestMethod]
    public void CheckForDuplicateOutputsWarnsForUnorderedIdenticalDuplicateAfterUniqueOutput()
    {
        ContentHash hash = ContentHash.Random();
        NodeContext previousNode = CreateNode("previous.proj");
        previousNode.SetBuildResult(CreateBuildResult(hash));
        NodeContext currentNode = CreateNode("current.proj");
        ConcurrentDictionary<string, NodeContext> outputProducer = CreateOutputProducer(previousNode);
        MockPluginLogger logger = new();

        CheckForDuplicateOutputs(
            logger,
            outputProducer,
            currentNode,
            hash,
            [Glob.Parse(Path.Combine(RepoRoot, DuplicateOutputPath))]);

        Assert.AreSame(currentNode, outputProducer[UniqueOutputPath]);
        Assert.AreSame(previousNode, outputProducer[DuplicateOutputPath]);
        Assert.AreSame(currentNode, outputProducer[TrailingUniqueOutputPath]);
        Assert.HasCount(2, logger.LogEntries);
        Assert.AreEqual(PluginLogLevel.Warning, logger.LogEntries[1].LogLevel);
        StringAssert.Contains(logger.LogEntries[1].Message, "there is no ordering between the two nodes", StringComparison.Ordinal);
    }

    private static void CheckForDuplicateOutputs(
        MockPluginLogger logger,
        ConcurrentDictionary<string, NodeContext> outputProducer,
        NodeContext currentNode,
        ContentHash duplicateOutputHash,
        IReadOnlyCollection<Glob> identicalDuplicateOutputPatterns)
    {
        SortedDictionary<string, ContentHash> outputs = new(StringComparer.OrdinalIgnoreCase)
        {
            [UniqueOutputPath] = ContentHash.Random(),
            [DuplicateOutputPath] = duplicateOutputHash,
            [TrailingUniqueOutputPath] = ContentHash.Random(),
        };

        MSBuildCachePluginBase<PluginSettings>.CheckForDuplicateOutputs(
            logger,
            outputs,
            currentNode,
            outputProducer,
            RepoRoot,
            identicalDuplicateOutputPatterns);
    }

    private static ConcurrentDictionary<string, NodeContext> CreateOutputProducer(NodeContext previousNode)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            [DuplicateOutputPath] = previousNode,
        };

    private static NodeBuildResult CreateBuildResult(ContentHash duplicateOutputHash)
        => new(
            new SortedDictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase)
            {
                [DuplicateOutputPath] = duplicateOutputHash,
            },
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<NodeTargetResult>(),
            DateTime.UtcNow,
            DateTime.UtcNow,
            null);

    private static NodeContext CreateNode(string projectFileRelativePath, IReadOnlyList<NodeContext>? dependencies = null)
        => new(
            RepoRoot,
            new ProjectInstance(ProjectRootElement.Create()),
            dependencies ?? Array.Empty<NodeContext>(),
            projectFileRelativePath,
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    [TestMethod]
    [DoNotParallelize]
#pragma warning disable CA2000 // Ownership is transferred to the plugin; the finally block disposes only if EndBuildAsync did not.
    public async Task EndBuildAsyncDisposesCacheClientAndPreservesExceptionWhenAsynchronousPublishingFails()
    {
        FaultingCacheClient cacheClient = new("publishing");
        TestPlugin plugin = new();
        SetCacheClient(plugin, cacheClient);

        try
        {
            AggregateException exception = await Assert.ThrowsExactlyAsync<AggregateException>(
                () => plugin.EndBuildAsync(NullPluginLogger.Instance, CancellationToken.None));

            Assert.AreSame(cacheClient.ShutdownFailure, exception);
            Assert.IsTrue(cacheClient.DisposeCalled, "The cache client must be disposed after shutdown fails.");
        }
        finally
        {
            if (!cacheClient.DisposeCalled)
            {
                await plugin.DisposeAsync();
            }
        }
    }
#pragma warning restore CA2000

    [TestMethod]
    [DoNotParallelize]
#pragma warning disable CA2000 // Ownership is transferred to the plugins; the finally block avoids releasing a shared lock twice.
    public async Task EndBuildAsyncReleasesProcessAndDirectoryLocksWhenAsynchronousMaterializationFails()
    {
        string cacheRoot = Path.Combine(Path.GetTempPath(), "MSBuildCacheTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheRoot);

        PluginSettings settings = new()
        {
            RepoRoot = cacheRoot,
            LocalCacheRootPath = cacheRoot,
        };

        FaultingCacheClient cacheClient = new("materialization");
        TestPlugin firstPlugin = new();
        TestPlugin secondPlugin = new();
        bool firstPluginAcquiredLocks = false;
        bool secondPluginAcquiredLocks = false;

        try
        {
            firstPluginAcquiredLocks = TryAcquireLock(firstPlugin, settings);
            Assert.IsTrue(firstPluginAcquiredLocks, "The first plugin must acquire both locks for the test to be valid.");
            SetCacheClient(firstPlugin, cacheClient);

            AggregateException exception = await Assert.ThrowsExactlyAsync<AggregateException>(
                () => firstPlugin.EndBuildAsync(NullPluginLogger.Instance, CancellationToken.None));
            Assert.AreSame(cacheClient.ShutdownFailure, exception);

            secondPluginAcquiredLocks = TryAcquireLock(secondPlugin, settings);
            Assert.IsTrue(
                secondPluginAcquiredLocks,
                "A subsequent plugin must be able to reacquire both the process-wide semaphore and cache directory lock.");
        }
#pragma warning restore CA2000
        finally
        {
            if (firstPluginAcquiredLocks && !cacheClient.DisposeCalled)
            {
                await firstPlugin.DisposeAsync();
            }

            if (secondPluginAcquiredLocks)
            {
                await secondPlugin.DisposeAsync();
            }

            Directory.Delete(cacheRoot, recursive: true);
        }
    }

    private static void SetCacheClient(TestPlugin plugin, ICacheClient cacheClient)
    {
        FieldInfo cacheClientField = typeof(MSBuildCachePluginBase<PluginSettings>).GetField(
            "_cacheClient",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        cacheClientField.SetValue(plugin, cacheClient);
    }

    private static bool TryAcquireLock(TestPlugin plugin, PluginSettings settings)
    {
        MethodInfo tryAcquireLockMethod = typeof(MSBuildCachePluginBase<PluginSettings>).GetMethod(
            "TryAcquireLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)tryAcquireLockMethod.Invoke(plugin, [settings, NullPluginLogger.Instance])!;
    }

    private sealed class TestPlugin : MSBuildCachePluginBase
    {
        protected override HashType HashType => HashType.Murmur;

        protected override Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class FaultingCacheClient : ICacheClient
    {
        private readonly Task _backgroundOperation;

        public FaultingCacheClient(string operationName)
        {
            IOException backgroundFailure = new($"Asynchronous {operationName} failed.");
            _backgroundOperation = Task.FromException(backgroundFailure);
            ShutdownFailure = new AggregateException(backgroundFailure);
        }

        public bool DisposeCalled { get; private set; }

        public AggregateException ShutdownFailure { get; }

        public Task<NodeBuildResult> AddNodeAsync(
            NodeContext nodeContext,
            PathSet? pathSet,
            IReadOnlyCollection<string> outputPaths,
            Func<IReadOnlyDictionary<string, ContentHash>, NodeBuildResult> nodeBuildResultBuilder,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<(PathSet?, NodeBuildResult?)> GetNodeAsync(
            NodeContext nodeContext,
            bool materializeOutputs,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _backgroundOperation;
            }
            catch (IOException)
            {
                throw ShutdownFailure;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return default;
        }
    }
}
