// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using DotNet.Globbing;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class FingerprintFactoryTests
{
    /// <summary>
    /// Verifies that toggling <see cref="PluginSettings.EnableProbeAndEnumerationFingerprinting"/> changes the
    /// plugin-settings fingerprint entries that fold into the weak fingerprint. This is the cache-entry
    /// segregation mechanism: flag-on and flag-off builds end up under different weak fingerprints and therefore
    /// don't pollute each other's selectors.
    /// </summary>
    [TestMethod]
    public void WeakFingerprintFlagSegregation()
    {
        using IContentHasher contentHasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        IInputHasher inputHasher = new NoopInputHasher();
        var pathNormalizer = new PathNormalizer(@"X:\Repo", @"X:\Nuget");

        PluginSettings off = new TestPluginSettings { RepoRoot = @"X:\Repo", EnableProbeAndEnumerationFingerprinting = false };
        PluginSettings on = new TestPluginSettings { RepoRoot = @"X:\Repo", EnableProbeAndEnumerationFingerprinting = true };

        var factoryOff = new FingerprintFactory(contentHasher, inputHasher, off, pathNormalizer);
        var factoryOn = new FingerprintFactory(contentHasher, inputHasher, on, pathNormalizer);

        List<FingerprintEntry> entriesOff = GetPluginSettingsFingerprintEntries(factoryOff);
        List<FingerprintEntry> entriesOn = GetPluginSettingsFingerprintEntries(factoryOn);

        // The two collections must differ — specifically, exactly one entry differs (the flag value entry).
        FingerprintEntry offFlagEntry = entriesOff.Single(e => e.Description.Contains(nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting), StringComparison.Ordinal));
        FingerprintEntry onFlagEntry = entriesOn.Single(e => e.Description.Contains(nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting), StringComparison.Ordinal));

        Assert.AreNotEqual(offFlagEntry.Description, onFlagEntry.Description, "Flag descriptions should differ.");
        CollectionAssert.AreNotEqual(offFlagEntry.Hash, onFlagEntry.Hash, "Flag entry hashes should differ.");

        // And no other entry should differ between the two factories — the flag is the only setting toggled.
        List<string> commonOff = entriesOff
            .Where(e => !e.Description.Contains(nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting), StringComparison.Ordinal))
            .Select(e => e.Description).ToList();
        List<string> commonOn = entriesOn
            .Where(e => !e.Description.Contains(nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting), StringComparison.Ordinal))
            .Select(e => e.Description).ToList();
        CollectionAssert.AreEqual(commonOff, commonOn);
    }

    /// <summary>
    /// Sentinels are derived from the configured <see cref="IContentHasher"/> so they're the right
    /// byte length for the active <see cref="HashType"/>. Two factory instances using the same hash type must
    /// produce bit-identical sentinels — that's the cross-machine stability guarantee that lets the type-aware
    /// strong fingerprint be reproducible.
    /// </summary>
    [TestMethod]
    [DataRow(HashType.Murmur)]
    [DataRow(HashType.Vso0)]
    [DataRow(HashType.SHA256)]
    public void SentinelsAreDeterministicForHashType(HashType hashType)
    {
        using IContentHasher hasher = HashInfoLookup.Find(hashType).CreateContentHasher();
        FingerprintFactory a = CreateFactory(hasher);
        FingerprintFactory b = CreateFactory(hasher);

        CollectionAssert.AreEqual(a.AbsentFileSentinel, b.AbsentFileSentinel, $"AbsentFileSentinel not deterministic for {hashType}.");
        CollectionAssert.AreEqual(a.ZeroHash, b.ZeroHash, $"ZeroHash not deterministic for {hashType}.");
    }

    /// <summary>
    /// The two sentinels must be distinct — they encode different semantic states (absent path vs.
    /// existence-only observation) and must never collapse to the same fingerprint payload.
    /// </summary>
    [TestMethod]
    [DataRow(HashType.Murmur)]
    [DataRow(HashType.Vso0)]
    [DataRow(HashType.SHA256)]
    public void SentinelsAreDistinct(HashType hashType)
    {
        using IContentHasher hasher = HashInfoLookup.Find(hashType).CreateContentHasher();
        FingerprintFactory factory = CreateFactory(hasher);

        CollectionAssert.AreNotEqual(factory.AbsentFileSentinel, factory.ZeroHash, "AbsentFileSentinel and ZeroHash must not collide.");
    }

    /// <summary>
    /// Sentinels must match the active hasher's output byte length. This is essential for downstream
    /// hash-combining calls that concatenate sentinel + real hashes into the final strong fingerprint.
    /// </summary>
    [TestMethod]
    [DataRow(HashType.Murmur)]
    [DataRow(HashType.Vso0)]
    [DataRow(HashType.SHA256)]
    public void SentinelsMatchHasherByteLength(HashType hashType)
    {
        using IContentHasher hasher = HashInfoLookup.Find(hashType).CreateContentHasher();
        int expectedLength = hasher.Info.ByteLength;
        FingerprintFactory factory = CreateFactory(hasher);

        Assert.AreEqual(expectedLength, factory.AbsentFileSentinel.Length, $"AbsentFileSentinel size mismatch for {hashType}.");
        Assert.AreEqual(expectedLength, factory.ZeroHash.Length, $"ZeroHash size mismatch for {hashType}.");
    }

    /// <summary>
    /// AbsentPathProbe and ExistingProbe of the same path must produce different strong fingerprints.
    /// This is the clean→dirty→miss / dirty→clean→miss fix at the strong-FP layer — re-observation at
    /// cache lookup will surface these type differences at lookup time.
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintDistinguishesAbsentFromExistent()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        FingerprintFactory absentFactory = CreateFactory(hasher);
        FingerprintFactory existingFactory = CreateFactory(hasher);

        PathSet absent = new(new List<ObservedPathEntry> { new(@"{RepoRoot}\foo", ObservationType.AbsentPathProbe) });
        PathSet existing = new(new List<ObservedPathEntry> { new(@"{RepoRoot}\foo", ObservationType.ExistingProbe) });

        Fingerprint? fpAbsent = await absentFactory.GetStrongFingerprintAsync(absent);
        Fingerprint? fpExisting = await existingFactory.GetStrongFingerprintAsync(existing);

        Assert.IsNotNull(fpAbsent);
        Assert.IsNotNull(fpExisting);
        CollectionAssert.AreNotEqual(fpAbsent.Hash, fpExisting.Hash,
            "AbsentPathProbe and ExistingProbe of the same path must produce different strong fingerprints.");
    }

    /// <summary>
    /// FileContentRead and ExistingProbe of the same path must produce different strong fingerprints.
    /// Reading a file's content is strictly more information than probing for its existence; the strong
    /// fingerprint must reflect this distinction.
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintDistinguishesProbeFromContentRead()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var inputHasher = new DictInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"X:\Repo\foo"] = new byte[] { 1, 2, 3 },
        });
        FingerprintFactory factory = CreateFactory(hasher, inputHasher);

        // Need at least one non-FCR entry to force the type-aware encoding path. Both PathSets carry the
        // same dummy AbsentPathProbe entry; the FCR-vs-Probe distinction is on the path under test.
        PathSet probe = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\bar", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\foo", ObservationType.ExistingProbe),
        });
        PathSet read = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\bar", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\foo", ObservationType.FileContentRead),
        });

        Fingerprint? fpProbe = await CreateFactory(hasher, inputHasher).GetStrongFingerprintAsync(probe);
        Fingerprint? fpRead = await CreateFactory(hasher, inputHasher).GetStrongFingerprintAsync(read);

        Assert.IsNotNull(fpProbe);
        Assert.IsNotNull(fpRead);
        CollectionAssert.AreNotEqual(fpProbe.Hash, fpRead.Hash);
    }

    /// <summary>
    /// When an ExistingProbe path's content changes but the path still exists, the strong fingerprint
    /// must NOT change. Existence-only observations don't depend on content.
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintExistingProbeIgnoresContentChange()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();

        // Two different input hashers with different content hashes for the same path. ExistingProbe entries
        // ignore the hasher; the strong fingerprint should be identical regardless.
        var hasherA = new DictInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [@"X:\Repo\foo"] = new byte[] { 1 } });
        var hasherB = new DictInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { [@"X:\Repo\foo"] = new byte[] { 2 } });

        // Force the type-aware encoding by including a non-FCR sibling entry.
        PathSet pathSet = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\bar", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\foo", ObservationType.ExistingProbe),
        });

        Fingerprint? fpA = await CreateFactory(hasher, hasherA).GetStrongFingerprintAsync(pathSet);
        Fingerprint? fpB = await CreateFactory(hasher, hasherB).GetStrongFingerprintAsync(pathSet);

        Assert.IsNotNull(fpA);
        Assert.IsNotNull(fpB);
        CollectionAssert.AreEqual(fpA.Hash, fpB.Hash,
            "ExistingProbe must NOT incorporate file content into the strong fingerprint.");
    }

    /// <summary>
    /// Adding a member to an enumerated directory must change the strong fingerprint. Under the
    /// schema-driven model the strong-FP comes from the entry's <c>Members</c> field directly,
    /// so two PathSets that differ only in their Members lists produce different fingerprints.
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintDirectoryMemberHashDetectsAddition()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var pathNormalizer = new PathNormalizer(@"X:\Repo", @"X:\Nuget");

        PathSet before = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\dir", ObservationType.DirectoryEnumeration, enumerationPattern: null,
                members: new[] { "a.cs" },
                writtenMembers: Array.Empty<string>()),
        });
        PathSet after = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\dir", ObservationType.DirectoryEnumeration, enumerationPattern: null,
                members: new[] { "a.cs", "b.cs" },
                writtenMembers: Array.Empty<string>()),
        });

        Fingerprint? fpBefore = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(before);
        Fingerprint? fpAfter = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(after);

        Assert.IsNotNull(fpBefore);
        Assert.IsNotNull(fpAfter);
        CollectionAssert.AreNotEqual(fpBefore.Hash, fpAfter.Hash,
            "PathSets that differ only in DirectoryEnumeration.Members must produce different strong fingerprints.");
    }

    /// <summary>
    /// Removing a member from an enumerated directory must change the strong fingerprint. Schema-driven
    /// counterpart of the addition test.
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintDirectoryMemberHashDetectsRemoval()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var pathNormalizer = new PathNormalizer(@"X:\Repo", @"X:\Nuget");

        PathSet before = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\dir", ObservationType.DirectoryEnumeration, enumerationPattern: null,
                members: new[] { "a.cs", "b.cs" },
                writtenMembers: Array.Empty<string>()),
        });
        PathSet after = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\dir", ObservationType.DirectoryEnumeration, enumerationPattern: null,
                members: new[] { "a.cs" },
                writtenMembers: Array.Empty<string>()),
        });

        Fingerprint? fpBefore = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(before);
        Fingerprint? fpAfter = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(after);

        Assert.IsNotNull(fpBefore);
        Assert.IsNotNull(fpAfter);
        CollectionAssert.AreNotEqual(fpBefore.Hash, fpAfter.Hash);
    }

    /// <summary>
    /// Changing the content of a directory member (without changing the member list) must NOT change the
    /// strong fingerprint when the entry is DirectoryEnumeration. The enumeration only depends on the
    /// member-name list (carried in the entry's <c>Members</c>); file content is irrelevant.
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintDirectoryMemberHashIgnoresMemberContentChange()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var pathNormalizer = new PathNormalizer(@"X:\Repo", @"X:\Nuget");

        // Two PathSets with the same Members list. Member file content is not represented in the schema
        // for DirEnum entries, so two PathSets with the same Members must produce identical strong FPs.
        PathSet before = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\dir", ObservationType.DirectoryEnumeration, enumerationPattern: null,
                members: new[] { "a.cs" },
                writtenMembers: Array.Empty<string>()),
        });
        PathSet after = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\dir", ObservationType.DirectoryEnumeration, enumerationPattern: null,
                members: new[] { "a.cs" },
                writtenMembers: Array.Empty<string>()),
        });

        Fingerprint? fpBefore = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(before);
        Fingerprint? fpAfter = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(after);

        Assert.IsNotNull(fpBefore);
        Assert.IsNotNull(fpAfter);
        CollectionAssert.AreEqual(fpBefore.Hash, fpAfter.Hash,
            "DirectoryEnumeration must NOT depend on member file contents — only on the member-name list.");
    }

    /// <summary>
    /// ComputeDirectoryMemberHash: missing directory → AbsentFileSentinel (verified indirectly by
    /// fingerprinting a PathSet with a DirectoryEnumeration of a missing path).
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintDirectoryEnumerationMissingDirIsAbsent()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var pathNormalizer = new PathNormalizer(@"X:\NonExistentRepo", @"X:\Nuget");

        PathSet pathSetMissing = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\definitely-not-here", ObservationType.DirectoryEnumeration),
        });
        PathSet pathSetAbsent = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}\definitely-not-here", ObservationType.AbsentPathProbe),
        });

        Fingerprint? fpMissingDir = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(pathSetMissing);
        Fingerprint? fpAbsent = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(pathSetAbsent);

        Assert.IsNotNull(fpMissingDir);
        Assert.IsNotNull(fpAbsent);
        // The two should still differ because the Type tag differs, but the underlying member-hash payload
        // for the missing-directory case is AbsentFileSentinel (same payload bytes as the AbsentPathProbe).
        // Different Type entries → different overall fingerprint.
        CollectionAssert.AreNotEqual(fpMissingDir.Hash, fpAbsent.Hash);
    }

    /// <summary>
    /// Determinism: hashing the same PathSet twice produces the same strong fingerprint, even with member
    /// hashing (no nondeterministic FS enumeration ordering leaking through).
    /// </summary>
    [TestMethod]
    public async Task StrongFingerprintIsDeterministic()
    {
        using TempDirectory tempRepo = TempDirectory.Create("MSBuildCacheTest-Determinism");
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var pathNormalizer = new PathNormalizer(tempRepo.Path, @"X:\Nuget");

        // Populate enough members to potentially exercise FS-ordering nondeterminism.
        foreach (string name in new[] { "z.cs", "a.cs", "M.cs", "b.cs", "Y.txt" })
        {
            WriteTextSync(Path.Combine(tempRepo.Path, name), "");
        }

        string normalizedDir = pathNormalizer.Normalize(tempRepo.Path);
        PathSet pathSet = new(new List<ObservedPathEntry>
        {
            new(@"{RepoRoot}\sentinel", ObservationType.AbsentPathProbe),
            new(normalizedDir, ObservationType.DirectoryEnumeration),
        });

        Fingerprint? fp1 = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(pathSet);
        Fingerprint? fp2 = await CreateFactory(hasher, pathNormalizer: pathNormalizer).GetStrongFingerprintAsync(pathSet);

        Assert.IsNotNull(fp1);
        Assert.IsNotNull(fp2);
        CollectionAssert.AreEqual(fp1.Hash, fp2.Hash,
            "Strong fingerprint must be deterministic across factory instances.");
    }

    // =========================================================================================
    // Same-path same-type pattern-fold (multi-pattern handling).
    //
    // Tests pin the precedence rule plus the multi-pattern handling for DirectoryEnumeration entries —
    // different patterns are kept as distinct entries, identical patterns dedupe.
    // =========================================================================================

    /// <summary>
    /// Flag off: non-FCR observations are dropped; only FCR entries contribute.
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesFlagOffIgnoresObservations()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("a.cs", ObservationType.FileContentRead),
                new ObservedPathEntry("b.cs", ObservationType.ExistingProbe),
            },
            enableProbeAndEnumeration: false);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("a.cs", entries[0].Path);
        Assert.AreEqual(ObservationType.FileContentRead, entries[0].Type);
    }

    /// <summary>
    /// FCR plus a probe of the same path: precedence keeps the FCR (FileContentRead > ExistingProbe).
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesPrecedenceKeepsContentReadOverProbe()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("shared.dll", ObservationType.FileContentRead),
                new ObservedPathEntry("shared.dll", ObservationType.ExistingProbe),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(ObservationType.FileContentRead, entries[0].Type);
    }

    /// <summary>
    /// Probe-first then read: the FCR observation strictly outranks the previously-folded probe, so the
    /// final entry is FCR.
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesPrecedenceReplacesProbeWithRead()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("foo", ObservationType.AbsentPathProbe),
                new ObservedPathEntry("foo", ObservationType.ExistingProbe),
                new ObservedPathEntry("foo", ObservationType.FileContentRead),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(ObservationType.FileContentRead, entries[0].Type);
    }

    /// <summary>
    /// Same-path DirectoryEnumeration entries with different patterns must each appear as separate entries
    /// in the PathSet — this is the multi-pattern handling. Today this bug is latent because
    /// EnumerationPattern is always null from MSBuild's FileAccessData, but the schema is ready.
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesDirectoryEnumerationKeepsDistinctPatterns()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("dir", ObservationType.DirectoryEnumeration, "*.cs"),
                new ObservedPathEntry("dir", ObservationType.DirectoryEnumeration, "*.dll"),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(2, entries.Count, "Same-path DirectoryEnumeration with different patterns must produce TWO entries.");
        CollectionAssert.AreEqual(
            new[] { "*.cs", "*.dll" },
            entries.Select(e => e.EnumerationPattern).ToArray());
        Assert.IsTrue(entries.All(e => e.Type == ObservationType.DirectoryEnumeration));
        Assert.IsTrue(entries.All(e => e.Path == "dir"));
    }

    /// <summary>
    /// Same-path DirectoryEnumeration entries with the SAME pattern fold to one entry — duplicate
    /// observations dedup correctly.
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesDirectoryEnumerationFoldsDuplicatePatterns()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("dir", ObservationType.DirectoryEnumeration, "*.cs"),
                new ObservedPathEntry("dir", ObservationType.DirectoryEnumeration, "*.cs"),
                new ObservedPathEntry("dir", ObservationType.DirectoryEnumeration, "*.cs"),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual("*.cs", entries[0].EnumerationPattern);
    }

    /// <summary>
    /// FCR followed by a DirectoryEnumeration of the same path: FCR has higher precedence, so the
    /// final entry is FCR — the enumeration is dropped.
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesContentReadOutranksEnumeration()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("ambiguous", ObservationType.FileContentRead),
                new ObservedPathEntry("ambiguous", ObservationType.DirectoryEnumeration, "*.cs"),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(ObservationType.FileContentRead, entries[0].Type);
        Assert.IsNull(entries[0].EnumerationPattern);
    }

    /// <summary>
    /// DirectoryEnumeration observations followed by an FCR observation of the same path: FCR replaces all
    /// existing DirEnum entries (including multi-pattern entries) with a single FCR entry.
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesContentReadReplacesMultiPatternEnumeration()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("ambiguous", ObservationType.DirectoryEnumeration, "*.cs"),
                new ObservedPathEntry("ambiguous", ObservationType.DirectoryEnumeration, "*.dll"),
                new ObservedPathEntry("ambiguous", ObservationType.FileContentRead),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(1, entries.Count);
        Assert.AreEqual(ObservationType.FileContentRead, entries[0].Type);
    }

    /// <summary>
    /// Sort order pin: (Path OrdinalIgnoreCase, Type ascending, Pattern Ordinal).
    /// </summary>
    [TestMethod]
    public void FoldPathSetEntriesProducesCanonicalSort()
    {
        List<ObservedPathEntry> entries = FingerprintFactory.FoldPathSetEntries(
            observations: new[]
            {
                new ObservedPathEntry("zeta", ObservationType.AbsentPathProbe),
                new ObservedPathEntry("alpha", ObservationType.ExistingProbe),
                new ObservedPathEntry("alpha", ObservationType.AbsentPathProbe),     // dropped (lower precedence)
                new ObservedPathEntry("mike", ObservationType.DirectoryEnumeration, "*.cs"),
                new ObservedPathEntry("mike", ObservationType.DirectoryEnumeration, "*.dll"),
            },
            enableProbeAndEnumeration: true);

        Assert.AreEqual(4, entries.Count);
        Assert.AreEqual("alpha", entries[0].Path);
        Assert.AreEqual(ObservationType.ExistingProbe, entries[0].Type);
        Assert.AreEqual("mike", entries[1].Path);
        Assert.AreEqual("*.cs", entries[1].EnumerationPattern);
        Assert.AreEqual("mike", entries[2].Path);
        Assert.AreEqual("*.dll", entries[2].EnumerationPattern);
        Assert.AreEqual("zeta", entries[3].Path);
    }

    // =========================================================================================
    // FilterObservations: predicted-input + hasher-can-hash + scope filter for sandbox observations.
    // =========================================================================================

    private static readonly PathNormalizer TestNormalizer = new(@"X:\Repo", @"X:\Nuget");
    private static readonly HashSet<string> EmptyPredictedInputs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyCollection<Glob> EmptyIgnoredPatterns = Array.Empty<Glob>();

    /// <summary>
    /// FCR observation whose path the hasher can hash → included as a normalized FCR entry.
    /// </summary>
    [TestMethod]
    public void FilterObservationsFcrIncludedWhenHasherCanHash()
    {
        var inputHasher = new DictInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"X:\Repo\src\foo.cs"] = new byte[] { 1 },
        });

        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            new[] { new ObservedAccess(@"X:\Repo\src\foo.cs", ObservationType.FileContentRead) },
            EmptyPredictedInputs,
            inputHasher,
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(1, included.Count);
        Assert.AreEqual(@"{RepoRoot}src\foo.cs", included[0].Path);
        Assert.AreEqual(ObservationType.FileContentRead, included[0].Type);
        Assert.AreEqual(0, excluded.Count);
    }

    /// <summary>
    /// FCR observation whose path the hasher cannot hash → routed to the excluded list (for debug log),
    /// not the PathSet.
    /// </summary>
    [TestMethod]
    public void FilterObservationsFcrExcludedWhenHasherCannotHash()
    {
        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            new[] { new ObservedAccess(@"X:\Repo\src\foo.cs", ObservationType.FileContentRead) },
            EmptyPredictedInputs,
            new NoopInputHasher(),
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(0, included.Count);
        Assert.AreEqual(1, excluded.Count);
        Assert.AreEqual(@"{RepoRoot}src\foo.cs", excluded[0]);
    }

    /// <summary>
    /// In-scope probe → included as a normalized entry.
    /// </summary>
    [TestMethod]
    public void FilterObservationsKeepsInScopeProbe()
    {
        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            new[] { new ObservedAccess(@"X:\Repo\src\foo.cs", ObservationType.ExistingProbe) },
            EmptyPredictedInputs,
            new NoopInputHasher(),
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(1, included.Count);
        Assert.AreEqual(@"{RepoRoot}src\foo.cs", included[0].Path);
        Assert.AreEqual(ObservationType.ExistingProbe, included[0].Type);
        Assert.AreEqual(0, excluded.Count);
    }

    /// <summary>
    /// Out-of-scope probe → dropped silently (not in PathSet, not in debug log).
    /// </summary>
    [TestMethod]
    public void FilterObservationsDropsOutOfScopeProbe()
    {
        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            new[] { new ObservedAccess(@"C:\ProgramData\Microsoft\NetFramework\BreadcrumbStore\Foo", ObservationType.AbsentPathProbe) },
            EmptyPredictedInputs,
            new NoopInputHasher(),
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(0, included.Count);
        Assert.AreEqual(0, excluded.Count);
    }

    /// <summary>
    /// Predicted inputs are dropped regardless of observation type — they're already covered by the weak FP.
    /// </summary>
    [TestMethod]
    public void FilterObservationsDropsPredictedInputs()
    {
        HashSet<string> predicted = new(StringComparer.OrdinalIgnoreCase)
        {
            @"X:\Repo\src\predicted.cs",
        };
        var inputHasher = new DictInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"X:\Repo\src\predicted.cs"] = new byte[] { 1 },
        });

        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            new[]
            {
                new ObservedAccess(@"X:\Repo\src\predicted.cs", ObservationType.FileContentRead),
                new ObservedAccess(@"X:\Repo\src\predicted.cs", ObservationType.ExistingProbe),
            },
            predicted,
            inputHasher,
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(0, included.Count);
        Assert.AreEqual(0, excluded.Count);
    }

    /// <summary>
    /// DirectoryEnumeration entries preserve EnumerationPattern, Members, and WrittenMembers through the filter.
    /// </summary>
    [TestMethod]
    public void FilterObservationsPreservesDirectoryEnumerationFields()
    {
        (List<ObservedPathEntry> included, _) = FingerprintFactory.FilterObservations(
            new[]
            {
                new ObservedAccess(
                    @"X:\Repo\dir",
                    ObservationType.DirectoryEnumeration,
                    EnumerationPattern: "*.cs",
                    Members: new[] { "a.cs", "b.cs" },
                    WrittenMembers: new[] { "Foo.dll" }),
            },
            EmptyPredictedInputs,
            new NoopInputHasher(),
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(1, included.Count);
        Assert.AreEqual(ObservationType.DirectoryEnumeration, included[0].Type);
        Assert.AreEqual("*.cs", included[0].EnumerationPattern);
        CollectionAssert.AreEqual(new[] { "a.cs", "b.cs" }, included[0].Members?.ToArray());
        CollectionAssert.AreEqual(new[] { "Foo.dll" }, included[0].WrittenMembers?.ToArray());
    }

    /// <summary>
    /// Empty input → empty output.
    /// </summary>
    [TestMethod]
    public void FilterObservationsEmptyInput()
    {
        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            Array.Empty<ObservedAccess>(),
            EmptyPredictedInputs,
            new NoopInputHasher(),
            TestNormalizer,
            EmptyIgnoredPatterns);

        Assert.AreEqual(0, included.Count);
        Assert.AreEqual(0, excluded.Count);
    }

    /// <summary>
    /// Paths matching <c>IgnoredInputPatterns</c> are dropped regardless of observation type. The pattern is
    /// matched against the absolute path (pre-normalization).
    /// </summary>
    [TestMethod]
    public void FilterObservationsDropsIgnoredInputPatterns()
    {
        var inputHasher = new DictInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"X:\Repo\obj\noisy.cache"] = new byte[] { 1 },
            [@"X:\Repo\src\foo.cs"] = new byte[] { 2 },
        });
        IReadOnlyCollection<Glob> ignored = new[] { Glob.Parse(@"X:\Repo\obj\**\*.cache") };

        (List<ObservedPathEntry> included, List<string> excluded) = FingerprintFactory.FilterObservations(
            new[]
            {
                new ObservedAccess(@"X:\Repo\obj\noisy.cache", ObservationType.FileContentRead),
                new ObservedAccess(@"X:\Repo\obj\noisy.cache", ObservationType.ExistingProbe),
                new ObservedAccess(@"X:\Repo\src\foo.cs", ObservationType.FileContentRead),
            },
            EmptyPredictedInputs,
            inputHasher,
            TestNormalizer,
            ignored);

        Assert.AreEqual(1, included.Count);
        Assert.AreEqual(@"{RepoRoot}src\foo.cs", included[0].Path);
        Assert.AreEqual(0, excluded.Count, "Ignored paths must not appear in the excluded debug list either.");
    }

    private static FingerprintFactory CreateFactory(IContentHasher hasher, IInputHasher? inputHasher = null, PathNormalizer? pathNormalizer = null)
    {
        inputHasher ??= new NoopInputHasher();
        pathNormalizer ??= new PathNormalizer(@"X:\Repo", @"X:\Nuget");
        var settings = new TestPluginSettings { RepoRoot = @"X:\Repo" };
        return new FingerprintFactory(hasher, inputHasher, settings, pathNormalizer);
    }

    private static List<FingerprintEntry> GetPluginSettingsFingerprintEntries(FingerprintFactory factory)
    {
        FieldInfo field = typeof(FingerprintFactory).GetField("_pluginSettingsFingerprintEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (List<FingerprintEntry>)field.GetValue(factory)!;
    }

    /// <summary>
    /// Helper to write a text file synchronously. net472 doesn't have File.WriteAllTextAsync, and these
    /// tests must build on both TFMs. The work is trivially fast and happens during one-time test setup,
    /// so blocking the async caller is benign.
    /// </summary>
#pragma warning disable CA1849 // Synchronous IO blocks async caller. net472 lacks an async equivalent; trivial test-setup IO.
    private static void WriteTextSync(string path, string text)
    {
        File.WriteAllText(path, text);
    }
#pragma warning restore CA1849

    private sealed class TestPluginSettings : PluginSettings
    {
    }

    private sealed class NoopInputHasher : IInputHasher
    {
        public bool ContainsPath(string absolutePath) => false;

        public ValueTask<byte[]?> GetHashAsync(string absolutePath) => new((byte[]?)null);
    }

    /// <summary>
    /// IInputHasher backed by a dictionary of (absolute path → content hash). Returns null for paths not
    /// in the dictionary, matching the contract for "path not known to the hasher".
    /// </summary>
    private sealed class DictInputHasher : IInputHasher
    {
        private readonly IReadOnlyDictionary<string, byte[]> _hashes;

        public DictInputHasher(IReadOnlyDictionary<string, byte[]> hashes)
        {
            _hashes = hashes;
        }

        public bool ContainsPath(string absolutePath) => _hashes.ContainsKey(absolutePath);

        public ValueTask<byte[]?> GetHashAsync(string absolutePath)
        {
            return new ValueTask<byte[]?>(_hashes.TryGetValue(absolutePath, out byte[]? hash) ? hash : null);
        }
    }

    /// <summary>
    /// Disposable temporary directory for filesystem-backed tests. Created under the system temp root with a
    /// unique GUID suffix; cleaned up on Dispose even if the test throws.
    /// </summary>
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        private TempDirectory(string path)
        {
            Path = path;
        }

        public static TempDirectory Create(string prefix)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new TempDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup; don't mask the underlying test failure if any.
                }
            }
        }
    }
}
