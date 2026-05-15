// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text.Json;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class PathSetTests
{
    [TestMethod]
    public void EqualsIdenticalEntriesWithNullPattern()
    {
        // Regression: pre-Phase-1 the path was stored as a raw string; the new schema permits a null EnumerationPattern.
        // Equality and hashing must tolerate null on both sides without throwing.
        var a = new PathSet(new List<ObservedPathEntry>
        {
            new("dir/file.cs", ObservationType.FileContentRead),
        });
        var b = new PathSet(new List<ObservedPathEntry>
        {
            new("dir/file.cs", ObservationType.FileContentRead),
        });

        Assert.IsTrue(a.Equals(b));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void EqualsIdenticalEntries()
    {
        var a = new PathSet(new List<ObservedPathEntry>
        {
            new("dir/file.cs", ObservationType.FileContentRead),
            new("dir2/", ObservationType.DirectoryEnumeration, "*.cs"),
        });
        var b = new PathSet(new List<ObservedPathEntry>
        {
            new("DIR/FILE.CS", ObservationType.FileContentRead), // case-insensitive path
            new("dir2/", ObservationType.DirectoryEnumeration, "*.cs"),
        });

        Assert.IsTrue(a.Equals(b));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void EqualsDifferingType()
    {
        var a = new PathSet(new List<ObservedPathEntry>
        {
            new("file", ObservationType.FileContentRead),
        });
        var b = new PathSet(new List<ObservedPathEntry>
        {
            new("file", ObservationType.ExistingProbe),
        });

        Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void EqualsDifferingPattern()
    {
        var a = new PathSet(new List<ObservedPathEntry>
        {
            new("dir", ObservationType.DirectoryEnumeration, "*.cs"),
        });
        var b = new PathSet(new List<ObservedPathEntry>
        {
            new("dir", ObservationType.DirectoryEnumeration, "*.fs"),
        });

        Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void EqualsDifferingPatternCase()
    {
        // Pattern equality is ordinal — a build that enumerated for "*.CS" is semantically distinct from one that
        // enumerated for "*.cs" (the underlying enumeration API may or may not be case-insensitive at the filesystem
        // layer, but the strong fingerprint must be deterministic in the pattern string itself).
        var a = new PathSet(new List<ObservedPathEntry>
        {
            new("dir", ObservationType.DirectoryEnumeration, "*.cs"),
        });
        var b = new PathSet(new List<ObservedPathEntry>
        {
            new("dir", ObservationType.DirectoryEnumeration, "*.CS"),
        });

        Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void EqualsOrderMatters()
    {
        // PathSet equality is positional. Callers are responsible for canonical sort before construction.
        var a = new PathSet(new List<ObservedPathEntry>
        {
            new("a", ObservationType.FileContentRead),
            new("b", ObservationType.FileContentRead),
        });
        var b = new PathSet(new List<ObservedPathEntry>
        {
            new("b", ObservationType.FileContentRead),
            new("a", ObservationType.FileContentRead),
        });

        Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void JsonRoundTripAllObservationTypes()
    {
        var original = new PathSet(new List<ObservedPathEntry>
        {
            new("repo/src/foo.cs", ObservationType.FileContentRead),
            new("repo/src/", ObservationType.DirectoryEnumeration, "*.cs"),
            new("repo/bin/probed.dll", ObservationType.ExistingProbe),
            new("repo/obj/missing.gen.cs", ObservationType.AbsentPathProbe),
        });

        string serialized = JsonSerializer.Serialize(original, SourceGenerationContext.Default.PathSet);
        PathSet? deserialized = JsonSerializer.Deserialize(serialized, SourceGenerationContext.Default.PathSet);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(original, deserialized);
        // Sanity-check that every type round-tripped: equality covers it, but be explicit about the schema field.
        Assert.AreEqual(4, deserialized!.Entries.Count);
        Assert.AreEqual(ObservationType.FileContentRead, deserialized.Entries[0].Type);
        Assert.AreEqual(ObservationType.DirectoryEnumeration, deserialized.Entries[1].Type);
        Assert.AreEqual("*.cs", deserialized.Entries[1].EnumerationPattern);
        Assert.AreEqual(ObservationType.ExistingProbe, deserialized.Entries[2].Type);
        Assert.AreEqual(ObservationType.AbsentPathProbe, deserialized.Entries[3].Type);
    }

    [TestMethod]
    public void ObservationTypeByteValuesAreStable()
    {
        // Schema stability: these byte values are part of the on-disk schema and contribute to strong fingerprints.
        // The QuickBuild implementation locks the same numeric values; changing either side risks
        // semantic divergence in cross-system diagnostics or shared cache scenarios.
        Dictionary<ObservationType, byte> expected = new()
        {
            { ObservationType.FileContentRead, 1 },
            { ObservationType.DirectoryEnumeration, 2 },
            { ObservationType.ExistingProbe, 3 },
            { ObservationType.AbsentPathProbe, 4 },
        };

        foreach (KeyValuePair<ObservationType, byte> kvp in expected)
        {
            Assert.AreEqual(kvp.Value, (byte)kvp.Key, $"Byte value of {kvp.Key} changed; this would invalidate every PathSet on disk.");
        }
    }

    [TestMethod]
    public void ObservationTypePrecedenceOrdering()
    {
        // Lock the precedence order: FileContentRead > DirectoryEnumeration > ExistingProbe > AbsentPathProbe.
        // This is checked against the QuickBuild implementation; both systems must agree.
        ObservationType[] descendingPrecedence =
        {
            ObservationType.FileContentRead,
            ObservationType.DirectoryEnumeration,
            ObservationType.ExistingProbe,
            ObservationType.AbsentPathProbe,
        };

        // Pairwise: every left-of-right pair must yield the left value as Max (higher precedence wins).
        for (int i = 0; i < descendingPrecedence.Length; i++)
        {
            for (int j = i + 1; j < descendingPrecedence.Length; j++)
            {
                ObservationType higher = descendingPrecedence[i];
                ObservationType lower = descendingPrecedence[j];
                Assert.AreEqual(higher, ObservationTypePrecedence.Max(higher, lower), $"Max({higher}, {lower})");
                Assert.AreEqual(higher, ObservationTypePrecedence.Max(lower, higher), $"Max({lower}, {higher}) (commutative)");
            }
        }

        // Identity: Max(x, x) == x for all x.
        foreach (ObservationType t in descendingPrecedence)
        {
            Assert.AreEqual(t, ObservationTypePrecedence.Max(t, t));
        }
    }

    [TestMethod]
    public void EnumerationPatternNullForNonDirectoryEnumeration()
    {
        // Pattern is meaningful only for DirectoryEnumeration. The constructor normalizes anything else to null
        // so that semantically-equivalent observations compare equal regardless of pattern threading.
        Assert.IsNull(new ObservedPathEntry("p", ObservationType.FileContentRead, "*.cs").EnumerationPattern);
        Assert.IsNull(new ObservedPathEntry("p", ObservationType.ExistingProbe, "*.cs").EnumerationPattern);
        Assert.IsNull(new ObservedPathEntry("p", ObservationType.AbsentPathProbe, "*.cs").EnumerationPattern);
        Assert.AreEqual("*.cs", new ObservedPathEntry("p", ObservationType.DirectoryEnumeration, "*.cs").EnumerationPattern);
    }

    // -----------------------------------------------------------------------------------------
    // Schema: DirectoryEnumeration Members + WrittenMembers.
    // -----------------------------------------------------------------------------------------

    [TestMethod]
    public void EqualsIdenticalDirectoryEnumerationWithMembers()
    {
        var a = new ObservedPathEntry("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: null,
            members: new[] { "a.cs", "b.cs" },
            writtenMembers: new[] { "Foo.dll" });
        var b = new ObservedPathEntry("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: null,
            members: new[] { "a.cs", "b.cs" },
            writtenMembers: new[] { "Foo.dll" });

        Assert.IsTrue(a.Equals(b));
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void NotEqualWhenMembersDiffer()
    {
        var a = new ObservedPathEntry("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: null,
            members: new[] { "a.cs" },
            writtenMembers: null);
        var b = new ObservedPathEntry("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: null,
            members: new[] { "a.cs", "b.cs" },
            writtenMembers: null);

        Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void NotEqualWhenWrittenMembersDiffer()
    {
        var a = new ObservedPathEntry("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: null,
            members: new[] { "a.cs" },
            writtenMembers: new[] { "Foo.dll" });
        var b = new ObservedPathEntry("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: null,
            members: new[] { "a.cs" },
            writtenMembers: new[] { "Bar.dll" });

        Assert.IsFalse(a.Equals(b));
    }

    [TestMethod]
    public void MembersAndWrittenMembersOnlyApplyToDirectoryEnumeration()
    {
        // The constructor normalizes Members and WrittenMembers to null on non-DirEnum types,
        // mirroring EnumerationPattern's normalization. Ensures semantically-equivalent observations
        // compare equal regardless of whether a stray Members value was threaded through.
        var fcr = new ObservedPathEntry("p", ObservationType.FileContentRead, enumerationPattern: "*.cs",
            members: new[] { "should-be-ignored" },
            writtenMembers: new[] { "should-be-ignored" });

        Assert.IsNull(fcr.Members);
        Assert.IsNull(fcr.WrittenMembers);
        Assert.IsNull(fcr.EnumerationPattern);
    }

    [TestMethod]
    public void JsonRoundTripPreservesMembersAndWrittenMembers()
    {
        var original = new PathSet(new List<ObservedPathEntry>
        {
            new("dir/", ObservationType.DirectoryEnumeration, enumerationPattern: "*.cs",
                members: new[] { "a.cs", "b.cs" },
                writtenMembers: new[] { "Foo.dll", "Foo.pdb" }),
        });

        string json = JsonSerializer.Serialize(original, SourceGenerationContext.Default.PathSet);
        PathSet roundTripped = JsonSerializer.Deserialize(json, SourceGenerationContext.Default.PathSet)!;

        Assert.IsTrue(original.Equals(roundTripped),
            $"PathSet should round-trip through JSON serialization. Original entries: {original.Entries.Count}, deserialized entries: {roundTripped.Entries.Count}.");
    }
}
