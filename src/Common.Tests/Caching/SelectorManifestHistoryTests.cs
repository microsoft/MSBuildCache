// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Caching;

[TestClass]
public sealed class SelectorManifestHistoryTests
{
    private const string LegacyBasePath = "/metadata/Selectors";
    private const string OrderBasePath = "/metadata/SelectorOrder";

    [TestMethod]
    public void ExplicitOrderSurvivesManifestPathSorting()
    {
        Selector newest = CreateSelector(1);
        Selector middle = CreateSelector(2);
        Selector oldest = CreateSelector(3);
        Selector[] expected = [newest, middle, oldest];

        var manifestPaths = new List<string>();
        for (int index = 0; index < expected.Length; index++)
        {
            Selector selector = expected[index];
            manifestPaths.Add(CreateLegacyPath(selector));
            manifestPaths.Add(SelectorManifestHistory.CreateOrderPath(OrderBasePath, selector, index));
        }

        manifestPaths.Sort(System.StringComparer.Ordinal);

        CollectionAssert.AreEqual(
            expected,
            SelectorManifestHistory.ReadSelectors(manifestPaths, LegacyBasePath, OrderBasePath).ToArray());
    }

    [TestMethod]
    public void LegacyManifestRemainsReadable()
    {
        Selector firstByManifestOrder = CreateSelector(1);
        Selector secondByManifestOrder = CreateSelector(2);
        string[] manifestPaths =
        [
            CreateLegacyPath(firstByManifestOrder),
            CreateLegacyPath(secondByManifestOrder),
        ];

        CollectionAssert.AreEqual(
            new[] { firstByManifestOrder, secondByManifestOrder },
            SelectorManifestHistory.ReadSelectors(manifestPaths, LegacyBasePath, OrderBasePath).ToArray());
    }

    private static Selector CreateSelector(int output)
        => new(ContentHash.Random(), System.BitConverter.GetBytes(output));

    private static string CreateLegacyPath(Selector selector)
        => $"{LegacyBasePath}/{selector.ContentHash}/{HexUtilities.BytesToHex(selector.Output)}";
}
