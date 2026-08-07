// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace Microsoft.MSBuildCache.AzurePipelines;

internal static class SelectorPublication
{
    internal static async Task<bool> AddAsync<TState>(
        Selector selector,
        string latestKey,
        Func<string, CancellationToken, Task<TState?>> query,
        Func<string, CancellationToken, Task<TState>> getConflictWinner,
        Func<TState?, CancellationToken, Task<IReadOnlyCollection<Selector>>> getSelectors,
        Func<TState?, string> getWriteKey,
        Func<TState?, IReadOnlyCollection<Selector>, string, CancellationToken, Task<bool>> tryPublish,
        CancellationToken cancellationToken)
        where TState : class
    {
        TState? predecessor = await query(latestKey, cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HashSet<Selector> selectors = new(await getSelectors(predecessor, cancellationToken));
            if (!selectors.Add(selector))
            {
                return false;
            }

            string writeKey = getWriteKey(predecessor);
            if (await tryPublish(predecessor, selectors, writeKey, cancellationToken))
            {
                return true;
            }

            predecessor = await getConflictWinner(writeKey, cancellationToken);
        }
    }
}
