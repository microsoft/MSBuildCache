// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class CasCacheClientTests
{
    [TestMethod]
    public void NullContentHashListMeansSubmittedValueWasAccepted()
    {
        AddOrGetContentHashListResult result = new(default(ContentHashListWithDeterminism));

        Assert.AreEqual(AddNodeResult.Added, CasCacheClient.GetAddNodeResult(result));
    }

    [TestMethod]
    public void ReturnedContentHashListMeansAnotherValueWon()
    {
        ContentHashList contentHashList = new(Array.Empty<ContentHash>(), null);
        AddOrGetContentHashListResult result = new(new ContentHashListWithDeterminism(contentHashList, CacheDeterminism.None));

        Assert.AreEqual(AddNodeResult.AlreadyExists, CasCacheClient.GetAddNodeResult(result));
    }
}
