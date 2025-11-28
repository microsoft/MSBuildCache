// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities;
using Microsoft.MSBuildCache.SourceControl.UnityVersionControl;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.SourceControl;

[TestClass]
public class UnityVersionControlFileHashProviderTests
{
    private static readonly byte[] FakeHash = { 0, 1, 2, 3, 4 };

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable. Justification: Always set by MSTest
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    private static Task FakeHasher(List<string> filesToRehash, Dictionary<string, byte[]> fileHashes)
    {
        foreach (string file in filesToRehash)
        {
            fileHashes[file] = FakeHash;
        }

        return Task.CompletedTask;
    }

    [TestMethod, Timeout(10000)]
    public async Task ParseCmLsFiles()
    {
        const string repoRoot = @"C:\work\MSBuildCacheTest";
        // This has two modified and one untracked files
        const string lsFilesOutput = "c:\\work\\MSBuildCacheTest\tFZMuOF2WDemh7irROkxyWw==\nc:\\work\\MSBuildCacheTest\\foo.txt\t9nwry/z6MPzLNvctyiKoFw==\nc:\\work\\MSBuildCacheTest\\bar.txt\t";

        UnityVersionControlFileHashProvider unityFileHashProvider = new(NullPluginLogger.Instance);
        Dictionary<string, byte[]> hashes = await unityFileHashProvider.ParseUnityLsFiles(new StringReader(lsFilesOutput), FakeHasher);
        int filesExpected = 3;
        Assert.AreEqual(filesExpected, hashes.Count, $"should be {filesExpected} files in this output");
        string barPath = Path.Combine(repoRoot, @"bar.txt").ToUpperInvariant();
        Assert.AreEqual(FakeHash, hashes[barPath], $"bytes of {barPath} should be {FakeHash} since it should have gotten hashed by the FakeHasher");
        Assert.AreEqual("0001020304", hashes[Path.Combine(repoRoot, "bar.txt")].ToHex());
    }

    [TestMethod, Ignore, Timeout(10000)]
    public async Task ParseRealCmLsFiles()
    {
        const string repoRoot = @"C:\work\MSBuildCacheTestLarge";
        UnityVersionControlFileHashProvider unityFileHashProvider = new(NullPluginLogger.Instance);
        var dict = await unityFileHashProvider.GetFileHashesAsync(repoRoot, CancellationToken.None);
        int filesExpected = 116921;
        Assert.AreEqual(filesExpected, dict.Count, $"should be {filesExpected} files in this output");
    }
}