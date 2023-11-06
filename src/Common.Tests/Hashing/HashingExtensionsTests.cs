// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Hashing;

[TestClass]
public class HashingExtensionsTests
{
    private static readonly IContentHasher ContentHasher = HashInfoLookup.Find(HashType.MD5).CreateContentHasher();

    [TestMethod]
    public void CombineHashes()
    {
        var hashes = new byte[][]
        {
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[] { 0x04, 0x05, 0x06 },
            new byte[] { 0x07, 0x08, 0x09 },
        };

        // This doesn't mean anything to a human; it's just intended to exercise the code
        byte[] expectedHash = new byte[] { 0x77, 0x8a, 0xaa, 0x14, 0x80, 0x06, 0x5d, 0xf8, 0x87, 0xe0, 0xab, 0xb5, 0x59, 0xd8, 0x26, 0xc5 };
        CollectionAssert.AreEqual(expectedHash, ContentHasher.CombineHashes(hashes));
    }

    [TestMethod]
    public void CombineHashesNullHashes()
    {
        var hashes = new byte[][]
        {
            new byte[] { 0x01, 0x02, 0x03 },
            new byte[] { 0x04, 0x05, 0x06 },
            new byte[] { 0x07, 0x08, 0x09 },
        };

        var hashesWithGaps = new byte[]?[]
        {
            Array.Empty<byte>(),
            hashes[0],
            null,
            hashes[1],
            Array.Empty<byte>(),
            null,
            hashes[2],
            null,
        };

        CollectionAssert.AreEqual(
            ContentHasher.CombineHashes(hashesWithGaps),
            ContentHasher.CombineHashes(hashes));
    }

    [TestMethod]
    public void CombineNoHashes()
    {
        Assert.IsNull(ContentHasher.CombineHashes(Array.Empty<byte[]>()));
        Assert.IsNull(ContentHasher.CombineHashes(new byte[]?[] { Array.Empty<byte>(), null }));
    }
}
