// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using Microsoft.MSBuildCache.AzurePipelines;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class LoggingTests
{
    [TestMethod]
    public void CacheContextIsEmbedded()
    {
        using ILogger logger = new Logger();
        Context defaultContext = new(logger);
        Context cacheContext1 = new(logger);
        string message1 = "Hello world!";
        string embedded = PipelineCachingCacheClient.EmbedCacheContext(cacheContext1, message1);
        PipelineCachingCacheClient.TryExtractContext(embedded, defaultContext, out Context cacheContext2, out string message2);
        Assert.AreEqual(message1, message2);
        Assert.AreEqual(cacheContext1.TraceId, cacheContext2.TraceId);
    }

    [TestMethod]
    public void CacheContextIsNotEmbedded()
    {
        using ILogger logger = new Logger();
        Context defaultContext = new(logger);
        Context cacheContext1 = new(logger);
        string message1 = "Hello world!";
        PipelineCachingCacheClient.TryExtractContext(message1, defaultContext, out Context cacheContext2, out string message2);
        Assert.AreEqual(message1, message2);
        Assert.AreEqual(defaultContext.TraceId, cacheContext2.TraceId);
    }
}