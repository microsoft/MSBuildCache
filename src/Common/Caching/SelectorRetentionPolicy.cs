// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace Microsoft.MSBuildCache.Caching;

internal readonly record struct SelectorPathSet(ContentHash ContentHash, bool RequiresDownload);

internal sealed class SelectorManifestPlan
{
    public SelectorManifestPlan(IReadOnlyList<Selector> selectors, IReadOnlyList<SelectorPathSet> pathSets)
    {
        Selectors = selectors;
        PathSets = pathSets;
    }

    public IReadOnlyList<Selector> Selectors { get; }

    public IReadOnlyList<SelectorPathSet> PathSets { get; }
}

internal static class SelectorRetentionPolicy
{
    public static SelectorManifestPlan CreatePlan(
        IEnumerable<Selector> previousSelectors,
        Selector currentSelector,
        ContentHash emptySelectorContentHash,
        ContentHash? locallyAvailablePathSetHash,
        int maxSelectors)
    {
        if (maxSelectors <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSelectors), maxSelectors, "The selector retention limit must be positive.");
        }

        var selectors = new List<Selector>(maxSelectors);
        var seenSelectors = new HashSet<Selector>();

        AddSelector(currentSelector);
        foreach (Selector selector in previousSelectors)
        {
            if (selectors.Count == maxSelectors)
            {
                break;
            }

            AddSelector(selector);
        }

        var pathSets = new List<SelectorPathSet>(selectors.Count);
        var seenPathSets = new HashSet<ContentHash>();
        foreach (Selector selector in selectors)
        {
            ContentHash pathSetHash = selector.ContentHash;
            if (pathSetHash != emptySelectorContentHash && seenPathSets.Add(pathSetHash))
            {
                pathSets.Add(new SelectorPathSet(
                    pathSetHash,
                    RequiresDownload: pathSetHash != locallyAvailablePathSetHash));
            }
        }

        return new SelectorManifestPlan(selectors, pathSets);

        void AddSelector(Selector selector)
        {
            if (seenSelectors.Add(selector))
            {
                selectors.Add(selector);
            }
        }
    }
}
