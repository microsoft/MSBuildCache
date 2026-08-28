// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.S3.Tests;

[TestClass]
public class S3ObjectKeysTests
{
    private const string KeyPrefix = "msbuildcache";
    private const string Universe = "abc123";

    private static readonly Fingerprint WeakFingerprint = new(new byte[] { 1, 2, 3, 4 });
    private static readonly ContentHash PathSetHash = new(HashType.Vso0, new byte[33]);

    private static S3ObjectKeys CreateKeys(string keyPrefix = KeyPrefix, HashType hashType = HashType.Vso0, string universe = Universe)
        => new(keyPrefix, hashType, universe);

    private static StrongFingerprint CreateStrongFingerprint(byte[] strongFingerprintBytes)
        => new(WeakFingerprint, new Selector(PathSetHash, strongFingerprintBytes));

    [TestMethod]
    public void KeysAreScopedByPrefixAndUniverse()
    {
        S3ObjectKeys keys = CreateKeys();

        string casKey = keys.GetCas(PathSetHash);

        StringAssert.StartsWith(casKey, $"{KeyPrefix}/", StringComparison.Ordinal);
        StringAssert.Contains(casKey, $"/{Universe}/", StringComparison.Ordinal);
        StringAssert.Contains(casKey, "/cas/", StringComparison.Ordinal);
    }

    [TestMethod]
    public void KeysAreScopedByHashType()
    {
        string vso0 = CreateKeys(hashType: HashType.Vso0).GetCas(PathSetHash);
        string sha256 = CreateKeys(hashType: HashType.SHA256).GetCas(PathSetHash);

        Assert.AreNotEqual(vso0, sha256);
    }

    [TestMethod]
    public void KeysAreScopedByUniverse()
    {
        string one = CreateKeys(universe: "one").GetCas(PathSetHash);
        string two = CreateKeys(universe: "two").GetCas(PathSetHash);

        Assert.AreNotEqual(one, two);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("/")]
    public void EmptyKeyPrefixFallsBackToDefault(string keyPrefix)
    {
        string casKey = CreateKeys(keyPrefix: keyPrefix).GetCas(PathSetHash);

        StringAssert.StartsWith(casKey, "msbuildcache/", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("prefix/")]
    [DataRow("/prefix")]
    [DataRow(" prefix ")]
    public void KeyPrefixIsNormalized(string keyPrefix)
    {
        string casKey = CreateKeys(keyPrefix: keyPrefix).GetCas(PathSetHash);

        StringAssert.StartsWith(casKey, "prefix/", StringComparison.Ordinal);
        Assert.IsFalse(casKey.Contains("//", StringComparison.Ordinal), $"Key '{casKey}' contains an empty segment.");
    }

    [TestMethod]
    public void SerializedHashesDoNotAppearInKeys()
    {
        // ':' is legal in an S3 key but breaks tooling which maps keys onto file paths.
        string casKey = CreateKeys().GetCas(PathSetHash);

        Assert.IsFalse(casKey.Contains(':', StringComparison.Ordinal), $"Key '{casKey}' contains a colon.");
    }

    [TestMethod]
    public void EntryAndSelectorKeysDiffer()
    {
        S3ObjectKeys keys = CreateKeys();
        StrongFingerprint fingerprint = CreateStrongFingerprint(new byte[] { 42 });

        Assert.AreNotEqual(keys.GetEntry(fingerprint), keys.GetSelector(fingerprint));
    }

    [TestMethod]
    public void SelectorPrefixMatchesItsSelectors()
    {
        S3ObjectKeys keys = CreateKeys();
        StrongFingerprint fingerprint = CreateStrongFingerprint(new byte[] { 42 });

        StringAssert.StartsWith(keys.GetSelector(fingerprint), keys.GetSelectorPrefix(WeakFingerprint), StringComparison.Ordinal);
    }

    [TestMethod]
    // A single zero byte is what the base cache client uses for its empty selector.
    [DataRow(new byte[] { 0 })]
    [DataRow(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })]
    [DataRow(new byte[0])]
    public void SelectorsRoundTrip(byte[] strongFingerprintBytes)
    {
        S3ObjectKeys keys = CreateKeys();
        StrongFingerprint fingerprint = CreateStrongFingerprint(strongFingerprintBytes);

        bool parsed = keys.TryParseSelector(keys.GetSelector(fingerprint), WeakFingerprint, out Selector selector);

        Assert.IsTrue(parsed);
        Assert.AreEqual(fingerprint.Selector, selector);
    }

    [TestMethod]
    public void SelectorsOfAnotherWeakFingerprintAreRejected()
    {
        S3ObjectKeys keys = CreateKeys();
        string selectorKey = keys.GetSelector(CreateStrongFingerprint(new byte[] { 42 }));

        Assert.IsFalse(keys.TryParseSelector(selectorKey, new Fingerprint(new byte[] { 9, 9, 9, 9 }), out _));
    }

    [TestMethod]
    public void SelectorsOfAnotherUniverseAreRejected()
    {
        string selectorKey = CreateKeys(universe: "other").GetSelector(CreateStrongFingerprint(new byte[] { 42 }));

        Assert.IsFalse(CreateKeys().TryParseSelector(selectorKey, WeakFingerprint, out _));
    }

    [TestMethod]
    public void MalformedSelectorsAreRejected()
    {
        S3ObjectKeys keys = CreateKeys();
        string prefix = keys.GetSelectorPrefix(WeakFingerprint);

        // A listing can contain keys written by a different version of the plugin, so these must not throw.
        Assert.IsFalse(keys.TryParseSelector(prefix, WeakFingerprint, out _), "Empty remainder.");
        Assert.IsFalse(keys.TryParseSelector($"{prefix}00", WeakFingerprint, out _), "Missing the strong fingerprint.");
        Assert.IsFalse(keys.TryParseSelector($"{prefix}not-a-hash/00", WeakFingerprint, out _), "Unparsable PathSet hash.");
        Assert.IsFalse(keys.TryParseSelector($"{prefix}a/b/c", WeakFingerprint, out _), "Too many segments.");

        string pathSetHash = keys.GetSelector(CreateStrongFingerprint(new byte[] { 42 })).Substring(prefix.Length).Split('/')[0];
        Assert.IsFalse(keys.TryParseSelector($"{prefix}{pathSetHash}/zz", WeakFingerprint, out _), "Unparsable strong fingerprint.");
    }
}
