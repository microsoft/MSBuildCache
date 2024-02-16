// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Hashing;

[TestClass]
public class CompositeInputHasherTests
{
    [TestMethod]
    public async Task PrecedenceAndFallback()
    {
        Dictionary<string, byte[]> hashes1 = new(StringComparer.OrdinalIgnoreCase)
        {
            { @"X:\1-Only.txt", new byte[] { 0x01, 0x01 } },
            { @"X:\Shared.txt", new byte[] { 0x01, 0x02 } },
        };
        Dictionary<string, byte[]> hashes2 = new(StringComparer.OrdinalIgnoreCase)
        {
            { @"X:\2-Only.txt", new byte[] { 0x02, 0x01 } },
            { @"X:\Shared.txt", new byte[] { 0x02, 0x02 } },
        };

        CompositeInputHasher hasher = new(new[] { new MockInputHasher(hashes1), new MockInputHasher(hashes2) });

        Assert.IsTrue(hasher.ContainsPath(@"X:\1-Only.txt"));
        Assert.IsTrue(hasher.ContainsPath(@"X:\2-Only.txt"));
        Assert.IsTrue(hasher.ContainsPath(@"X:\Shared.txt"));
        Assert.IsFalse(hasher.ContainsPath(@"X:\DoesNotExist.txt"));

        await AssertHashAsync(@"X:\1-Only.txt", hashes1);
        await AssertHashAsync(@"X:\2-Only.txt", hashes2);
        await AssertHashAsync(@"X:\Shared.txt", hashes1); // the first one wins
        Assert.IsNull(await hasher.GetHashAsync(@"X:\DoesNotExist.txt"));

        async Task AssertHashAsync(string path, Dictionary<string, byte[]> hashes)
        {
            Assert.IsTrue(hashes.TryGetValue(path, out byte[]? expectedHash));
            CollectionAssert.AreEqual(expectedHash, await hasher.GetHashAsync(path));
        }
    }

    private sealed class MockInputHasher(IReadOnlyDictionary<string, byte[]> hashes) : IInputHasher
    {
        public bool ContainsPath(string absolutePath) => hashes.ContainsKey(absolutePath);

        public ValueTask<byte[]?> GetHashAsync(string absolutePath) => new ValueTask<byte[]?>(hashes.TryGetValue(absolutePath, out byte[]? hash) ? hash : null);
    }
}
