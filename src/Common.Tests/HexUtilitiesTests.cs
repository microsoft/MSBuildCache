// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class HexUtilitiesTests
{
    [TestMethod]
    [DataRow("0123456789ABCDEFabcdef", new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0xAB, 0xCD, 0xEF, })]
    [DataRow("", null)]
    [DataRow(null, null)]
    // 0x prefix.
    [DataRow("0x1234", new byte[] { 0x12, 0x34 })]
    // Whitespace - should be trimmed.
    [DataRow(" ABCD\t\t", new byte[] { 0xAB, 0xCD })]
    // Whitespace + 0x
    [DataRow("    0x9876", new byte[] { 0x98, 0x76 })]
    public void HexToBytes(string hex, byte[]? expectedBytes)
    {
        byte[] bytes = HexUtilities.HexToBytes(hex);

        expectedBytes ??= Array.Empty<byte>();

        Assert.HasCount(expectedBytes.Length, bytes);
        for (int i = 0; i < expectedBytes.Length; i++)
        {
            Assert.AreEqual(expectedBytes[i], bytes[i], $"Index {i}");
        }
    }

    [TestMethod]
    public void HexToBytesOddChars() => Assert.ThrowsExactly<ArgumentException>(() => HexUtilities.HexToBytes("fAbCd"));

    [TestMethod]
    public void HexToBytesBadChars()
    {
        const string goodChars = "0123456789ABCDEFabcdef";

        var badCharactersMistakenlyAllowed = new List<char>();

        for (char c = '!'; c <= '~'; c++)
        {
#if NETFRAMEWORK
            if (!goodChars.Contains(c))
#else
            if (!goodChars.Contains(c, StringComparison.Ordinal))
#endif
            {
                try
                {
                    HexUtilities.HexToBytes(new string(new[] { c, c }));

                    // Should not get here.
                    badCharactersMistakenlyAllowed.Add(c);
                }
                catch (ArgumentException)
                {
                }
            }
        }

        Assert.IsEmpty(badCharactersMistakenlyAllowed, "Bad characters were allowed that should not have been: " + string.Concat(badCharactersMistakenlyAllowed));
    }
}