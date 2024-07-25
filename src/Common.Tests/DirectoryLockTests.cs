// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class DirectoryLockTests
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable. Justification: Always set by MSTest
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    [TestMethod]
    public void Acquire()
    {
        string lockFilePath = GetLockFilePath();
        using (DirectoryLock directoryLock1 = new(lockFilePath, NullPluginLogger.Instance))
        {
            Assert.IsTrue(directoryLock1.Acquire());
        }
    }

    [TestMethod]
    public void Reentry()
    {
        string lockFilePath = GetLockFilePath();
        using (DirectoryLock directoryLock1 = new(lockFilePath, NullPluginLogger.Instance))
        {
            for (int i = 0; i < 100; i++)
            {
                Assert.IsTrue(directoryLock1.Acquire());
            }
        }
    }

    [TestMethod]
    public void Contention()
    {
        string lockFilePath = GetLockFilePath();
        using (DirectoryLock directoryLock1 = new(lockFilePath, NullPluginLogger.Instance))
        using (DirectoryLock directoryLock2 = new(lockFilePath, NullPluginLogger.Instance))
        {
            Assert.IsTrue(directoryLock1.Acquire());

            // Second locker cannot acquire
            Assert.IsFalse(directoryLock2.Acquire());

            directoryLock1.Dispose();

            // Second locker can now acquire
            Assert.IsTrue(directoryLock2.Acquire());
        }
    }

    [TestMethod]
    public void StressTest()
    {
        string lockFilePath = GetLockFilePath();

        int lockCount = Environment.ProcessorCount * 100;
        int successCount = 0;
        Parallel.For(
            0,
            lockCount,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            i =>
            {
                using (DirectoryLock directoryLock = new(lockFilePath, NullPluginLogger.Instance))
                {
                    // Keep trying in a tight loop
                    while (!directoryLock.Acquire())
                    {
                    }

                    Interlocked.Increment(ref successCount);
                }
            });

        Assert.AreEqual(lockCount, successCount);
    }

    private string GetLockFilePath() => Path.Combine(TestContext.TestRunDirectory!, TestContext.TestName! + ".lock");
}
