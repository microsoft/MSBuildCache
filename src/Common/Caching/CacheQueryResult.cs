// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.MSBuildCache.Fingerprinting;

namespace Microsoft.MSBuildCache.Caching;

internal interface IMaterializingCacheClient
{
    Task<CacheQueryResult> QueryNodeAsync(NodeContext nodeContext, CancellationToken cancellationToken);
}

internal sealed class CacheQueryResult
{
    private readonly object _materializationLock = new();
    private readonly Func<CancellationToken, Task>? _materializeOutputsAsync;
    private readonly bool _waitForMaterialization;
    private Task? _materializationTask;

    internal CacheQueryResult(
        PathSet? pathSet,
        NodeBuildResult? nodeBuildResult,
        Func<CancellationToken, Task>? materializeOutputsAsync,
        bool waitForMaterialization)
    {
        PathSet = pathSet;
        NodeBuildResult = nodeBuildResult;
        _materializeOutputsAsync = materializeOutputsAsync;
        _waitForMaterialization = waitForMaterialization;
    }

    public PathSet? PathSet { get; }

    public NodeBuildResult? NodeBuildResult { get; }

    public Task MaterializeOutputsAsync(CancellationToken cancellationToken)
    {
        Task materializationTask = EnsureOutputsMaterializedAsync(cancellationToken);
        return _waitForMaterialization ? materializationTask : Task.CompletedTask;
    }

    internal Task EnsureOutputsMaterializedAsync(CancellationToken cancellationToken)
    {
        if (_materializeOutputsAsync is null)
        {
            return Task.CompletedTask;
        }

        lock (_materializationLock)
        {
            return _materializationTask ??= _materializeOutputsAsync(cancellationToken);
        }
    }
}
