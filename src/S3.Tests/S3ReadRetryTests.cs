// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.S3.Tests;

[TestClass]
public class S3ReadRetryTests
{
    private const string Description = "Download of `s3://bucket/cas/abc`";

    private readonly List<string> _traces = new();
    private readonly List<string> _warnings = new();

    private static S3ReadRetry CreateRetry() => new(S3ReadRetry.DefaultMaxAttempts, TimeSpan.Zero);

    private static IOException ConnectionClosed()
        => new("Unable to read data from the transport connection: The connection was closed.");

    private Task<(bool Succeeded, int Value)> RunAsync(Func<Task<int>> operationAsync, CancellationToken cancellationToken = default)
        => CreateRetry().TryRunAsync(Description, operationAsync, _traces.Add, _warnings.Add, cancellationToken);

    [TestMethod]
    public async Task SuccessIsNotRetried()
    {
        int attempts = 0;

        (bool succeeded, int value) = await RunAsync(() =>
        {
            attempts++;
            return Task.FromResult(42);
        });

        Assert.IsTrue(succeeded);
        Assert.AreEqual(42, value);
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, _warnings.Count);
        Assert.AreEqual(0, _traces.Count);
    }

    [TestMethod]
    public async Task FailureIsRetried()
    {
        int attempts = 0;

        (bool succeeded, int value) = await RunAsync(() =>
        {
            attempts++;
            return attempts < 3 ? throw ConnectionClosed() : Task.FromResult(7);
        });

        Assert.IsTrue(succeeded);
        Assert.AreEqual(7, value);
        Assert.AreEqual(3, attempts);
        Assert.AreEqual(0, _warnings.Count);
    }

    [TestMethod]
    public async Task ExhaustedRetriesReturnFalse()
    {
        int attempts = 0;

        (bool succeeded, _) = await RunAsync(() =>
        {
            attempts++;
            throw ConnectionClosed();
        });

        Assert.IsFalse(succeeded);
        Assert.AreEqual(S3ReadRetry.DefaultMaxAttempts, attempts);
        Assert.AreEqual(1, _warnings.Count);
        StringAssert.Contains(_warnings[0], Description, StringComparison.Ordinal);
        StringAssert.Contains(_warnings[0], "The connection was closed", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task NotFoundIsNotRetried()
    {
        int attempts = 0;

        await Assert.ThrowsExactlyAsync<AmazonS3Exception>(() => RunAsync(() =>
        {
            attempts++;
            throw new AmazonS3Exception("No such key", ErrorType.Sender, "NoSuchKey", "request", HttpStatusCode.NotFound);
        }));

        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, _warnings.Count);
    }

    [TestMethod]
    public async Task CancellationIsNotRetried()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        int attempts = 0;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => RunAsync(
            () =>
            {
                attempts++;
                throw new OperationCanceledException();
            },
            cancellation.Token));

        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, _warnings.Count);
    }

    [TestMethod]
    public async Task SdkTimeoutIsRetried()
    {
        int attempts = 0;

        (bool succeeded, _) = await RunAsync(() =>
        {
            attempts++;
            return attempts < 2 ? throw new TaskCanceledException() : Task.FromResult(1);
        });

        Assert.IsTrue(succeeded);
        Assert.AreEqual(2, attempts);
    }
}
