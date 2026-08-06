// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Caching;

[TestClass]
public sealed class CacheQueryResultTests
{
    [TestMethod]
    public async Task MaterializeOutputsAsyncSequentialCallsMaterializesOnce()
    {
        int materializationCount = 0;
        CacheQueryResult result = new(
            pathSet: null,
            nodeBuildResult: null,
            _ =>
            {
                Interlocked.Increment(ref materializationCount);
                return Task.CompletedTask;
            },
            waitForMaterialization: true);

        await result.MaterializeOutputsAsync(CancellationToken.None);
        await result.MaterializeOutputsAsync(CancellationToken.None);

        Assert.AreEqual(1, materializationCount);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task MaterializeOutputsAsyncConcurrentCallsMaterializesOnce()
    {
        TaskCompletionSource<bool> materializationStarted = CreateGate();
        TaskCompletionSource<bool> releaseMaterialization = CreateGate();
        int materializationCount = 0;
        CacheQueryResult result = new(
            pathSet: null,
            nodeBuildResult: null,
            async _ =>
            {
                Interlocked.Increment(ref materializationCount);
                materializationStarted.SetResult(true);
                await releaseMaterialization.Task;
            },
            waitForMaterialization: true);

        Task firstCall = result.MaterializeOutputsAsync(CancellationToken.None);
        await materializationStarted.Task;
        Task secondCall = result.MaterializeOutputsAsync(CancellationToken.None);

        Assert.AreEqual(1, materializationCount);

        releaseMaterialization.SetResult(true);
        await Task.WhenAll(firstCall, secondCall);

        Assert.AreEqual(1, materializationCount);
    }

    private static TaskCompletionSource<bool> CreateGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
