// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class PathSetTests
{
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
}
