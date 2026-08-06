// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public sealed class RecursiveCacheResultProviderTests
{
    [TestMethod]
    public async Task ParentThenDependencyMaterializesDependencyOnceWithoutSecondLookup()
    {
        NodeContext dependency = CreateNode("dependency.proj");
        NodeContext parent = CreateNode("parent.proj", dependencies: [dependency]);
        int dependencyLookupCount = 0;
        int dependencyMaterializationCount = 0;
        int dependencySetBuildResultCount = 0;
        int parentLookupCount = 0;
        RecursiveCacheResultProvider provider = CreateProvider(async (nodeContext, materializeOutputs, _, _) =>
        {
            await Task.CompletedTask;
            if (ReferenceEquals(nodeContext, dependency))
            {
                Assert.IsFalse(materializeOutputs);
                Interlocked.Increment(ref dependencyLookupCount);
                return CreateCacheHit(
                    dependency,
                    () => Interlocked.Increment(ref dependencySetBuildResultCount),
                    () => Interlocked.Increment(ref dependencyMaterializationCount));
            }

            Assert.IsTrue(materializeOutputs);
            Interlocked.Increment(ref parentLookupCount);
            return CreateCacheHit(parent);
        });

        CacheResult parentResult = await provider.GetCacheResultAsync(
            parent,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(CacheResultType.CacheHit, parentResult.ResultType);
        Assert.AreEqual(1, dependencyLookupCount);
        Assert.AreEqual(1, dependencySetBuildResultCount);
        Assert.AreEqual(0, dependencyMaterializationCount);
        Assert.AreEqual(1, parentLookupCount);

        CacheResult dependencyResult = await provider.GetCacheResultAsync(
            dependency,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(CacheResultType.CacheHit, dependencyResult.ResultType);
        Assert.AreEqual(1, dependencyLookupCount);
        Assert.AreEqual(1, dependencySetBuildResultCount);
        Assert.AreEqual(1, dependencyMaterializationCount);
        Assert.AreEqual(1, parentLookupCount);

        await provider.GetCacheResultAsync(
            dependency,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(1, dependencyLookupCount);
        Assert.AreEqual(1, dependencySetBuildResultCount);
        Assert.AreEqual(1, dependencyMaterializationCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentParentAndDependencyMaterializeAndLookupDependencyOnce()
    {
        NodeContext dependency = CreateNode("dependency.proj");
        NodeContext parent = CreateNode("parent.proj", dependencies: [dependency]);
        TaskCompletionSource<bool> dependencyLookupStarted = CreateGate();
        TaskCompletionSource<bool> releaseDependencyLookup = CreateGate();
        TaskCompletionSource<bool> dependencyMaterializationStarted = CreateGate();
        TaskCompletionSource<bool> releaseDependencyMaterialization = CreateGate();
        int dependencyLookupCount = 0;
        int dependencyMaterializationCount = 0;
        int dependencySetBuildResultCount = 0;
        int parentLookupCount = 0;
        RecursiveCacheResultProvider provider = CreateProvider(async (nodeContext, materializeOutputs, _, _) =>
        {
            if (ReferenceEquals(nodeContext, dependency))
            {
                Assert.IsFalse(materializeOutputs);
                Interlocked.Increment(ref dependencyLookupCount);
                dependencyLookupStarted.SetResult(true);
                await releaseDependencyLookup.Task;

                return CreateCacheHit(
                    dependency,
                    () => Interlocked.Increment(ref dependencySetBuildResultCount),
                    async () =>
                    {
                        Interlocked.Increment(ref dependencyMaterializationCount);
                        dependencyMaterializationStarted.SetResult(true);
                        await releaseDependencyMaterialization.Task;
                    });
            }

            Assert.IsTrue(materializeOutputs);
            Interlocked.Increment(ref parentLookupCount);
            return CreateCacheHit(parent);
        });

        Task<CacheResult> parentQuery = provider.GetCacheResultAsync(
            parent,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);
        await dependencyLookupStarted.Task;

        Task<CacheResult> dependencyQuery = provider.GetCacheResultAsync(
            dependency,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(1, dependencyLookupCount);

        releaseDependencyLookup.SetResult(true);
        await dependencyMaterializationStarted.Task;

        Assert.AreEqual(1, dependencyLookupCount);
        Assert.AreEqual(1, dependencySetBuildResultCount);
        Assert.AreEqual(1, dependencyMaterializationCount);

        releaseDependencyMaterialization.SetResult(true);
        CacheResult[] results = await Task.WhenAll(parentQuery, dependencyQuery);

        Assert.AreEqual(CacheResultType.CacheHit, results[0].ResultType);
        Assert.AreEqual(CacheResultType.CacheHit, results[1].ResultType);
        Assert.AreEqual(1, dependencyLookupCount);
        Assert.AreEqual(1, dependencySetBuildResultCount);
        Assert.AreEqual(1, dependencyMaterializationCount);
        Assert.AreEqual(1, parentLookupCount);
    }

    [TestMethod]
    public async Task OuterBuildThenInnerBuildMaterializesInnerBuildOnce()
    {
        NodeContext innerBuild = CreateNode("multitargeting.proj", CreateInnerBuildProject());
        NodeContext outerBuild = CreateNode(
            "multitargeting.proj",
            CreateOuterBuildProject(),
            [innerBuild]);
        int innerLookupCount = 0;
        int innerMaterializationCount = 0;
        int innerSetBuildResultCount = 0;
        int outerLookupCount = 0;
        RecursiveCacheResultProvider provider = CreateProvider((nodeContext, materializeOutputs, _, _) =>
        {
            if (ReferenceEquals(nodeContext, innerBuild))
            {
                Assert.IsTrue(materializeOutputs);
                Interlocked.Increment(ref innerLookupCount);
                return Task.FromResult(CreateCacheHit(
                    innerBuild,
                    () => Interlocked.Increment(ref innerSetBuildResultCount),
                    () => Interlocked.Increment(ref innerMaterializationCount)));
            }

            Assert.IsTrue(materializeOutputs);
            Interlocked.Increment(ref outerLookupCount);
            return Task.FromResult(CreateCacheHit(outerBuild));
        });

        CacheResult outerResult = await provider.GetCacheResultAsync(
            outerBuild,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(CacheResultType.CacheHit, outerResult.ResultType);
        Assert.AreEqual(1, innerLookupCount);
        Assert.AreEqual(1, innerSetBuildResultCount);
        Assert.AreEqual(1, innerMaterializationCount);
        Assert.AreEqual(1, outerLookupCount);

        CacheResult innerResult = await provider.GetCacheResultAsync(
            innerBuild,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(CacheResultType.CacheHit, innerResult.ResultType);
        Assert.AreEqual(1, innerLookupCount);
        Assert.AreEqual(1, innerSetBuildResultCount);
        Assert.AreEqual(1, innerMaterializationCount);
        Assert.AreEqual(1, outerLookupCount);
    }

    [TestMethod]
    public async Task DependencyCacheMissDoesNotQueryParent()
    {
        NodeContext dependency = CreateNode("dependency.proj");
        NodeContext parent = CreateNode("parent.proj", dependencies: [dependency]);
        int dependencyLookupCount = 0;
        int dependencyCacheMissCount = 0;
        int parentLookupCount = 0;
        RecursiveCacheResultProvider provider = CreateProvider(
            (nodeContext, materializeOutputs, _, _) =>
            {
                if (ReferenceEquals(nodeContext, dependency))
                {
                    Assert.IsFalse(materializeOutputs);
                    Interlocked.Increment(ref dependencyLookupCount);
                    return Task.FromResult(new NodeCacheResult(
                        CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss),
                        null));
                }

                Interlocked.Increment(ref parentLookupCount);
                return Task.FromResult(CreateCacheHit(parent));
            },
            () => Interlocked.Increment(ref dependencyCacheMissCount));

        CacheResult result = await provider.GetCacheResultAsync(
            parent,
            materializeOutputs: true,
            NullPluginLogger.Instance,
            CancellationToken.None);

        Assert.AreEqual(CacheResultType.CacheMiss, result.ResultType);
        Assert.AreEqual(1, dependencyLookupCount);
        Assert.AreEqual(1, dependencyCacheMissCount);
        Assert.AreEqual(0, parentLookupCount);
    }

    private static RecursiveCacheResultProvider CreateProvider(
        Func<NodeContext, bool, PluginLoggerBase, CancellationToken, Task<NodeCacheResult>> getCacheResultAsync,
        Action? recordDependencyCacheMiss = null)
        => new(2, getCacheResultAsync, recordDependencyCacheMiss ?? (() => { }));

    private static NodeCacheResult CreateCacheHit(
        NodeContext nodeContext,
        Action? setBuildResult = null,
        Func<Task>? materializeOutputsAsync = null)
    {
        NodeBuildResult buildResult = new(
            new SortedDictionary<string, BuildXL.Cache.ContentStore.Hashing.ContentHash>(),
            new SortedDictionary<string, string>(),
            [new NodeTargetResult("Build", Array.Empty<NodeTargetResultTaskItem>())],
            new DateTime(1970, 1, 1),
            new DateTime(1970, 1, 1),
            buildId: null);
        setBuildResult?.Invoke();
        nodeContext.SetBuildResult(buildResult);

        CacheQueryResult queryResult = new(
            pathSet: null,
            buildResult,
            materializeOutputsAsync is null ? null : _ => materializeOutputsAsync(),
            waitForMaterialization: true);
        return new NodeCacheResult(
            CacheResult.IndicateCacheHit(
                [new PluginTargetResult("Build", Array.Empty<ITaskItem2>(), BuildResultCode.Success)]),
            queryResult);
    }

    private static NodeCacheResult CreateCacheHit(
        NodeContext nodeContext,
        Action? setBuildResult,
        Action materializeOutputs)
        => CreateCacheHit(
            nodeContext,
            setBuildResult,
            () =>
            {
                materializeOutputs();
                return Task.CompletedTask;
            });

    private static NodeContext CreateNode(
        string projectFileRelativePath,
        ProjectInstance? projectInstance = null,
        IReadOnlyList<NodeContext>? dependencies = null)
        => new(
            baseLogDirectory: string.Empty,
            projectInstance ?? CreateProjectInstance(),
            dependencies ?? Array.Empty<NodeContext>(),
            projectFileRelativePath,
            new SortedDictionary<string, string>(),
            Array.Empty<string>(),
            referenceAssemblyRelativePath: null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static ProjectInstance CreateProjectInstance(params (string Name, string Value)[] properties)
    {
        ProjectRootElement project = ProjectRootElement.Create();
        foreach ((string name, string value) in properties)
        {
            project.AddProperty(name, value);
        }

        return new ProjectInstance(project);
    }

    private static ProjectInstance CreateOuterBuildProject()
        => CreateProjectInstance(
            ("InnerBuildProperty", "TargetFramework"),
            ("InnerBuildPropertyValues", "TargetFrameworks"),
            ("TargetFrameworks", "net8.0;net9.0"));

    private static ProjectInstance CreateInnerBuildProject()
        => CreateProjectInstance(
            ("InnerBuildProperty", "TargetFramework"),
            ("InnerBuildPropertyValues", "TargetFrameworks"),
            ("TargetFrameworks", "net8.0;net9.0"),
            ("TargetFramework", "net9.0"));

    private static TaskCompletionSource<bool> CreateGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
