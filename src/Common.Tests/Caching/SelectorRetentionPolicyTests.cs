// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Caching;

[TestClass]
public sealed class SelectorRetentionPolicyTests
{
    [TestMethod]
    public void SelectorChurnKeepsTheMostRecentWindow()
    {
        const int retentionLimit = 4;
        var publishedSelectors = new List<Selector>();
        var selectorsByAge = new List<Selector>();
        ContentHash emptySelectorHash = ContentHash.Random();

        for (int i = 0; i < 20; i++)
        {
            Selector currentSelector = CreateSelector(ContentHash.Random(), i);
            selectorsByAge.Insert(0, currentSelector);

            SelectorManifestPlan plan = SelectorRetentionPolicy.CreatePlan(
                publishedSelectors,
                currentSelector,
                emptySelectorHash,
                currentSelector.ContentHash,
                retentionLimit);

            publishedSelectors = plan.Selectors.ToList();
        }

        CollectionAssert.AreEqual(selectorsByAge.Take(retentionLimit).ToList(), publishedSelectors);
    }

    [TestMethod]
    public void DuplicateSelectorsAndPathSetsArePublishedOnce()
    {
        ContentHash emptySelectorHash = ContentHash.Random();
        ContentHash sharedPathSetHash = ContentHash.Random();
        Selector currentSelector = CreateSelector(sharedPathSetHash, 1);
        Selector samePathSetDifferentOutput = CreateSelector(sharedPathSetHash, 2);

        SelectorManifestPlan plan = SelectorRetentionPolicy.CreatePlan(
            new[] { currentSelector, samePathSetDifferentOutput, currentSelector },
            currentSelector,
            emptySelectorHash,
            sharedPathSetHash,
            maxSelectors: 10);

        CollectionAssert.AreEqual(
            new[] { currentSelector, samePathSetDifferentOutput },
            plan.Selectors.ToList());
        Assert.HasCount(1, plan.PathSets);
        Assert.IsFalse(plan.PathSets[0].RequiresDownload);
    }

    [TestMethod]
    public void OldestSelectorIsEvictedAtTheLimit()
    {
        ContentHash emptySelectorHash = ContentHash.Random();
        Selector currentSelector = CreateSelector(ContentHash.Random(), 3);
        Selector recentSelector = CreateSelector(ContentHash.Random(), 2);
        Selector oldestSelector = CreateSelector(ContentHash.Random(), 1);

        SelectorManifestPlan plan = SelectorRetentionPolicy.CreatePlan(
            new[] { recentSelector, oldestSelector },
            currentSelector,
            emptySelectorHash,
            currentSelector.ContentHash,
            maxSelectors: 2);

        CollectionAssert.AreEqual(new[] { currentSelector, recentSelector }, plan.Selectors.ToList());
    }

    [TestMethod]
    public void HistoricalPathSetDownloadsAreBounded()
    {
        const int retentionLimit = 8;
        ContentHash emptySelectorHash = ContentHash.Random();
        Selector currentSelector = CreateSelector(ContentHash.Random(), 100);
        var historicalSelectors = Enumerable.Range(0, 100)
            .Select(i => CreateSelector(ContentHash.Random(), i))
            .ToList();

        SelectorManifestPlan plan = SelectorRetentionPolicy.CreatePlan(
            historicalSelectors,
            currentSelector,
            emptySelectorHash,
            currentSelector.ContentHash,
            retentionLimit);

        Assert.HasCount(retentionLimit, plan.Selectors);
        Assert.HasCount(retentionLimit, plan.PathSets);
        Assert.AreEqual(retentionLimit - 1, plan.PathSets.Count(pathSet => pathSet.RequiresDownload));
    }

    [TestMethod]
    public void NonPositiveRetentionLimitIsRejected()
    {
        Selector selector = CreateSelector(ContentHash.Random(), 1);

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SelectorRetentionPolicy.CreatePlan(
            Array.Empty<Selector>(),
            selector,
            ContentHash.Random(),
            selector.ContentHash,
            maxSelectors: 0));
    }

    private static Selector CreateSelector(ContentHash pathSetHash, int output)
        => new(pathSetHash, BitConverter.GetBytes(output));
}
