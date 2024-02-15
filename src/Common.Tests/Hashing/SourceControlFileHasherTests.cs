// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Hashing;

[TestClass]
public class SourceControlFileHasherTests
{
    private static readonly IContentHasher ContentHasher = HashInfoLookup.Find(HashType.MD5).CreateContentHasher();

    private static readonly PathNormalizer PathNormalizer = new PathNormalizer(@"X:\Repo", @"X:\Nuget");

    [TestMethod]
    public void ContainsPath()
    {
        Dictionary<string, byte[]> fileHashes = new(StringComparer.OrdinalIgnoreCase)
        {
            { @"X:\Repo\1.txt", new byte[] { 0x01, 0x02, 0x03 } },
            { @"X:\Repo\2.txt", new byte[] { 0x04, 0x05, 0x06 } },
            { @"X:\Repo\3.txt", new byte[] { 0x07, 0x08, 0x09 } },
        };
        SourceControlFileHasher hasher = new(ContentHasher, PathNormalizer, fileHashes);

        Assert.IsTrue(hasher.ContainsPath(@"X:\Repo\1.txt"));
        Assert.IsTrue(hasher.ContainsPath(@"X:\Repo\2.txt"));
        Assert.IsTrue(hasher.ContainsPath(@"X:\Repo\3.txt"));

        Assert.IsFalse(hasher.ContainsPath(@"X:\Repo\4.txt"));
        Assert.IsFalse(hasher.ContainsPath(@"X:\Repo\5.txt"));
        Assert.IsFalse(hasher.ContainsPath(@"X:\Repo\6.txt"));

        // Case doesn't matter
        Assert.IsTrue(hasher.ContainsPath(@"X:\Repo\1.Txt"));
        Assert.IsTrue(hasher.ContainsPath(@"X:\Repo\2.tXt"));
        Assert.IsTrue(hasher.ContainsPath(@"X:\Repo\3.txT"));
    }

    [TestMethod]
    public async Task GetHash()
    {
        Dictionary<string, byte[]> fileHashes = new(StringComparer.OrdinalIgnoreCase)
        {
            { @"X:\Repo\1.txt", new byte[] { 0x01, 0x02, 0x03 } },
            { @"X:\Repo\2.txt", new byte[] { 0x04, 0x05, 0x06 } },
            { @"X:\Repo\3.txt", new byte[] { 0x07, 0x08, 0x09 } },
        };
        SourceControlFileHasher hasher = new(ContentHasher, PathNormalizer, fileHashes);

        Assert.IsNotNull(await hasher.GetHashAsync(@"X:\Repo\1.txt"));
        Assert.IsNotNull(await hasher.GetHashAsync(@"X:\Repo\2.txt"));
        Assert.IsNotNull(await hasher.GetHashAsync(@"X:\Repo\3.txt"));

        Assert.IsNull(await hasher.GetHashAsync(@"X:\Repo\4.txt"));
        Assert.IsNull(await hasher.GetHashAsync(@"X:\Repo\5.txt"));
        Assert.IsNull(await hasher.GetHashAsync(@"X:\Repo\6.txt"));

        foreach (KeyValuePair<string, byte[]> kvp in fileHashes)
        {
            string file = kvp.Key;
            byte[] fileHash = kvp.Value;

            // The hash should not equal the content hash of the file. It should tak the file path into account too.
            CollectionAssert.AreNotEqual(fileHash, await hasher.GetHashAsync(file));

            foreach (string otherFile in fileHashes.Keys)
            {
                if (file == otherFile)
                {
                    CollectionAssert.AreEqual(await hasher.GetHashAsync(file), await hasher.GetHashAsync(otherFile));
                }
                else
                {
                    CollectionAssert.AreNotEqual(await hasher.GetHashAsync(file), await hasher.GetHashAsync(otherFile));
                }
            }
        }

        CollectionAssert.AreNotEqual(await hasher.GetHashAsync(@"X:\Repo\1.txt"), await hasher.GetHashAsync(@"X:\Repo\2.txt"));
        CollectionAssert.AreNotEqual(await hasher.GetHashAsync(@"X:\Repo\1.txt"), await hasher.GetHashAsync(@"X:\Repo\3.txt"));

        // Case doesn't matter
        CollectionAssert.AreEqual(await GetHashFreshHasherAsync(@"X:\Repo\1.txt"), await GetHashFreshHasherAsync(@"X:\Repo\1.Txt"));
        CollectionAssert.AreEqual(await GetHashFreshHasherAsync(@"X:\Repo\2.txt"), await GetHashFreshHasherAsync(@"X:\Repo\2.tXt"));
        CollectionAssert.AreEqual(await GetHashFreshHasherAsync(@"X:\Repo\3.txt"), await GetHashFreshHasherAsync(@"X:\Repo\3.txT"));

        // Using a new hasher to avoid caching
        ValueTask<byte[]?> GetHashFreshHasherAsync(string relativePath) => new SourceControlFileHasher(ContentHasher, PathNormalizer, fileHashes).GetHashAsync(relativePath);
    }
}
