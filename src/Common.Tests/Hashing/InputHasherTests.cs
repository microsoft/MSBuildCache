// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Hashing;

[TestClass]
public class InputHasherTests
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
        InputHasher hasher = new(ContentHasher, PathNormalizer, fileHashes);

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
    public void GetHash()
    {
        Dictionary<string, byte[]> fileHashes = new(StringComparer.OrdinalIgnoreCase)
        {
            { @"X:\Repo\1.txt", new byte[] { 0x01, 0x02, 0x03 } },
            { @"X:\Repo\2.txt", new byte[] { 0x04, 0x05, 0x06 } },
            { @"X:\Repo\3.txt", new byte[] { 0x07, 0x08, 0x09 } },
        };
        InputHasher hasher = new(ContentHasher, PathNormalizer, fileHashes);

        Assert.IsNotNull(hasher.GetHash(@"X:\Repo\1.txt"));
        Assert.IsNotNull(hasher.GetHash(@"X:\Repo\2.txt"));
        Assert.IsNotNull(hasher.GetHash(@"X:\Repo\3.txt"));

        Assert.IsNull(hasher.GetHash(@"X:\Repo\4.txt"));
        Assert.IsNull(hasher.GetHash(@"X:\Repo\5.txt"));
        Assert.IsNull(hasher.GetHash(@"X:\Repo\6.txt"));

        foreach (KeyValuePair<string, byte[]> kvp in fileHashes)
        {
            string file = kvp.Key;
            byte[] fileHash = kvp.Value;

            // The hash should not equal the content hash of the file. It should tak the file path into account too.
            CollectionAssert.AreNotEqual(fileHash, hasher.GetHash(file));

            foreach (string otherFile in fileHashes.Keys)
            {
                if (file == otherFile)
                {
                    CollectionAssert.AreEqual(hasher.GetHash(file), hasher.GetHash(otherFile));
                }
                else
                {
                    CollectionAssert.AreNotEqual(hasher.GetHash(file), hasher.GetHash(otherFile));
                }
            }
        }

        CollectionAssert.AreNotEqual(hasher.GetHash(@"X:\Repo\1.txt"), hasher.GetHash(@"X:\Repo\2.txt"));
        CollectionAssert.AreNotEqual(hasher.GetHash(@"X:\Repo\1.txt"), hasher.GetHash(@"X:\Repo\3.txt"));

        // Case doesn't matter
        CollectionAssert.AreEqual(GetHashFreshHasher(@"X:\Repo\1.txt"), GetHashFreshHasher(@"X:\Repo\1.Txt"));
        CollectionAssert.AreEqual(GetHashFreshHasher(@"X:\Repo\2.txt"), GetHashFreshHasher(@"X:\Repo\2.tXt"));
        CollectionAssert.AreEqual(GetHashFreshHasher(@"X:\Repo\3.txt"), GetHashFreshHasher(@"X:\Repo\3.txT"));

        // Using a new hasher to avoid caching
        byte[]? GetHashFreshHasher(string relativePath) => new InputHasher(ContentHasher, PathNormalizer, fileHashes).GetHash(relativePath);
    }
}
