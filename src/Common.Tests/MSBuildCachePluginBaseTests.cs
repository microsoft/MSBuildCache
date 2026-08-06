// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using BuildXL.Cache.ContentStore.Hashing;
using DotNet.Globbing;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
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
}
