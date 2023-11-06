// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Fingerprinting;

namespace Microsoft.MSBuildCache.Caching;

public interface ICacheClient : IAsyncDisposable
{
    Task<NodeBuildResult> AddNodeAsync(
        NodeContext nodeContext,
        PathSet? pathSet,
        IReadOnlyCollection<string> outputPaths,
        Func<IReadOnlyDictionary<string, ContentHash>, NodeBuildResult> nodeBuildResultBuilder,
        CancellationToken cancellationToken);

    Task<(PathSet?, NodeBuildResult?)> GetNodeAsync(NodeContext nodeContext, CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}
