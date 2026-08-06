// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace Microsoft.MSBuildCache.Caching;

internal static class SelectorManifestHistory
{
    public static string CreateOrderPath(string basePath, Selector selector, int index)
        => $"{basePath}/{index:D8}/{selector.ContentHash}/{HexUtilities.BytesToHex(selector.Output)}";

    public static IReadOnlyList<Selector> ReadSelectors(
        IEnumerable<string> manifestPaths,
        string legacyBasePath,
        string orderBasePath)
    {
        List<string> paths = manifestPaths.ToList();
        string orderPrefix = orderBasePath + "/";
        List<Selector> orderedSelectors = paths
            .Where(path => path.StartsWith(orderPrefix, StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => ParseSelector(path, orderPrefix, contentHashIndex: 1))
            .ToList();

        if (orderedSelectors.Count > 0)
        {
            return orderedSelectors;
        }

        string legacyPrefix = legacyBasePath + "/";
        return paths
            .Where(path => path.StartsWith(legacyPrefix, StringComparison.Ordinal))
            .Select(path => ParseSelector(path, legacyPrefix, contentHashIndex: 0))
            .ToList();
    }

    private static Selector ParseSelector(string path, string prefix, int contentHashIndex)
    {
        string[] tokens = path.Substring(prefix.Length).Split('/');
        return new Selector(
            new ContentHash(tokens[contentHashIndex]),
            HexUtilities.HexToBytes(tokens[contentHashIndex + 1]));
    }
}
