// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Caching;

namespace Microsoft.MSBuildCache;

internal sealed record NodeCacheResult(CacheResult Result, CacheQueryResult? QueryResult)
{
    public Task MaterializeOutputsAsync(CancellationToken cancellationToken)
        => QueryResult?.MaterializeOutputsAsync(cancellationToken) ?? Task.CompletedTask;
}

internal sealed class RecursiveCacheResultProvider
{
    private readonly ConcurrentDictionary<NodeContext, Lazy<Task<NodeCacheResult>>> _cacheResults;
    private readonly Func<NodeContext, bool, PluginLoggerBase, CancellationToken, Task<NodeCacheResult>> _getCacheResultSingleAsync;
    private readonly Action _recordDependencyCacheMiss;

    public RecursiveCacheResultProvider(
        int nodeCount,
        Func<NodeContext, bool, PluginLoggerBase, CancellationToken, Task<NodeCacheResult>> getCacheResultSingleAsync,
        Action recordDependencyCacheMiss)
    {
        _cacheResults = new ConcurrentDictionary<NodeContext, Lazy<Task<NodeCacheResult>>>(Environment.ProcessorCount, nodeCount);
        _getCacheResultSingleAsync = getCacheResultSingleAsync;
        _recordDependencyCacheMiss = recordDependencyCacheMiss;
    }

    public async Task<CacheResult> GetCacheResultAsync(
        NodeContext nodeContext,
        bool materializeOutputs,
        PluginLoggerBase logger,
        CancellationToken cancellationToken)
    {
        NodeCacheResult result = await _cacheResults.GetOrAdd(
            nodeContext,
            new Lazy<Task<NodeCacheResult>>(
                () => GetCacheResultWithDependenciesAsync(nodeContext, materializeOutputs, logger, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        if (materializeOutputs && result.Result.ResultType == CacheResultType.CacheHit)
        {
            await result.MaterializeOutputsAsync(cancellationToken);
        }

        return result.Result;
    }

    private async Task<NodeCacheResult> GetCacheResultWithDependenciesAsync(
        NodeContext nodeContext,
        bool materializeOutputs,
        PluginLoggerBase logger,
        CancellationToken cancellationToken)
    {
        bool isOuterBuild = nodeContext.ProjectInstance.IsOuterBuild();

        foreach (NodeContext dependency in nodeContext.Dependencies)
        {
            if (dependency.BuildResult == null)
            {
                bool materializeDependencyOutputs = isOuterBuild && dependency.ProjectInstance.IsInnerBuild();

                logger.LogMessage($"Querying cache for missing build result for dependency '{dependency.Id}'");
                CacheResult dependencyResult = await GetCacheResultAsync(dependency, materializeDependencyOutputs, logger, cancellationToken);
                logger.LogMessage($"Dependency '{dependency.Id}' cache result: '{dependencyResult.ResultType}'");

                if (dependencyResult.ResultType != CacheResultType.CacheHit)
                {
                    logger.LogMessage($"Cache miss due to failed build result for dependency '{dependency.Id}'");
                    _recordDependencyCacheMiss();
                    return new NodeCacheResult(CacheResult.IndicateNonCacheHit(CacheResultType.CacheMiss), null);
                }
            }
        }

        return await _getCacheResultSingleAsync(nodeContext, materializeOutputs, logger, cancellationToken);
    }
}
