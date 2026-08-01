// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Experimental.FileAccess;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class ObservationFilterTests
{
    [TestMethod]
    [DataRow(0u, ObservationType.ExistingProbe, DisplayName = "success")]
    [DataRow(2u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_FILE_NOT_FOUND")]
    [DataRow(3u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_PATH_NOT_FOUND")]
    [DataRow(53u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_BAD_NETPATH")]
    [DataRow(123u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_INVALID_NAME")]
    [DataRow(5u, ObservationType.ExistingProbe, DisplayName = "ERROR_ACCESS_DENIED is transient, not absence")]
    [DataRow(32u, ObservationType.ExistingProbe, DisplayName = "ERROR_SHARING_VIOLATION is transient, not absence")]
    [DataRow(1224u, ObservationType.ExistingProbe, DisplayName = "unrecognized error is not absence")]
    public void ProbeErrorClassification(uint error, ObservationType expected)
    {
        Assert.AreEqual(
            expected,
            FileAccessRepository.ClassifyObservation(RequestedAccess.Probe, error),
            "Only definitive not-found codes may classify as absent. Mapping a transient failure such as "
            + "ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED to AbsentPathProbe would let machine flakiness "
            + "change the PathSet and lose cache hits.");
    }

    /// <summary>
    /// <see cref="RequestedAccess.Enumerate"/> is reported against the directory being enumerated, but
    /// <see cref="RequestedAccess.EnumerationProbe"/> is reported against each matched child. Recording the
    /// latter as a directory enumeration would key the entry on a file path, and since lookup-time
    /// validation requires the path to still be a directory, such an entry could never re-validate — every
    /// build using <c>FindFirstFileEx</c>/<c>FindNextFile</c> enumeration would permanently miss.
    /// </summary>
    [TestMethod]
    public void EnumerationProbeIsAProbeNotADirectoryEnumeration()
    {
        Assert.AreEqual(
            ObservationType.DirectoryEnumeration,
            FileAccessRepository.ClassifyObservation(RequestedAccess.Enumerate, 0),
            "Enumerate is reported on the directory itself and carries the search pattern.");

        Assert.AreEqual(
            ObservationType.ExistingProbe,
            FileAccessRepository.ClassifyObservation(RequestedAccess.EnumerationProbe, 0),
            "EnumerationProbe is reported per matched child, so it is an existence probe on that child.");
    }

    /// <summary>
    /// <see cref="RequestedAccess"/> is a flags enum, and content access must keep flowing to the normal
    /// file-table handling rather than being short-circuited into an observation.
    /// </summary>
    [TestMethod]
    [DataRow(RequestedAccess.Read, DisplayName = "Read")]
    [DataRow(RequestedAccess.Write, DisplayName = "Write")]
    [DataRow(RequestedAccess.ReadWrite, DisplayName = "ReadWrite")]
    [DataRow(RequestedAccess.Read | RequestedAccess.Probe, DisplayName = "Read combined with Probe")]
    [DataRow(RequestedAccess.None, DisplayName = "None")]
    public void ContentAccessIsNotAnObservation(RequestedAccess requestedAccess)
    {
        Assert.IsNull(
            FileAccessRepository.ClassifyObservation(requestedAccess, 0),
            "Accesses carrying Read or Write are content accesses and must reach the file table.");
    }

    [TestMethod]
    public void TrimTrailingSeparatorRemovesBackslash()
    {
        Assert.AreEqual(@"X:\foo\bar", FileAccessRepository.TrimTrailingSeparator(@"X:\foo\bar\"));
    }

    [TestMethod]
    public void TrimTrailingSeparatorRemovesForwardSlash()
    {
        Assert.AreEqual("X:/foo/bar", FileAccessRepository.TrimTrailingSeparator("X:/foo/bar/"));
    }

    [TestMethod]
    public void TrimTrailingSeparatorNoOpWhenAbsent()
    {
        Assert.AreEqual(@"X:\foo\bar", FileAccessRepository.TrimTrailingSeparator(@"X:\foo\bar"));
    }

    [TestMethod]
    public void TrimTrailingSeparatorEmpty()
    {
        Assert.AreEqual(string.Empty, FileAccessRepository.TrimTrailingSeparator(string.Empty));
    }

    [TestMethod]
    public void BuildEverWrittenOrAncestorSetIncludesAllAncestors()
    {
        HashSet<string> result = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>
        {
            @"X:\Repo\bin\Debug\net9.0\TestProject.dll",
        });

        // Must include the file itself plus every ancestor up to drive root.
        Assert.IsTrue(result.Contains(@"X:\Repo\bin\Debug\net9.0\TestProject.dll"));
        Assert.IsTrue(result.Contains(@"X:\Repo\bin\Debug\net9.0"));
        Assert.IsTrue(result.Contains(@"X:\Repo\bin\Debug"));
        Assert.IsTrue(result.Contains(@"X:\Repo\bin"));
        Assert.IsTrue(result.Contains(@"X:\Repo"));
        Assert.IsTrue(result.Contains(@"X:\"));
    }

    [TestMethod]
    public void BuildEverWrittenOrAncestorSetDeduplicatesSharedAncestors()
    {
        HashSet<string> result = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>
        {
            @"X:\Repo\bin\Debug\net9.0\TestProject.dll",
            @"X:\Repo\bin\Debug\net9.0\TestProject.pdb",
        });

        // Two paths share most of the ancestor chain — they shouldn't double-count.
        // Specifically, the early-exit when an ancestor is already in the set should kick in for
        // the second path at "X:\Repo\bin\Debug\net9.0".
        int net9Count = result.Count(p => string.Equals(p, @"X:\Repo\bin\Debug\net9.0", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, net9Count, "Shared ancestor must appear exactly once.");
    }

    [TestMethod]
    public void BuildEverWrittenOrAncestorSetCaseInsensitive()
    {
        HashSet<string> result = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>
        {
            @"X:\Repo\BIN\Debug\TestProject.dll",
            @"X:\repo\bin\debug\Other.dll",
        });

        // Case-insensitive comparison: both files contribute their ancestor chains, but the chains
        // share the same logical entries (just different casings).
        // Both files plus shared ancestor chain @ "X:\Repo\BIN\Debug" + "X:\Repo\BIN" + "X:\Repo" + "X:\"
        // First write's ancestors get added with their casing; second write's ancestors are deduped via
        // OrdinalIgnoreCase.
        Assert.AreEqual(6, result.Count);
    }

    [TestMethod]
    public void BuildEverWrittenOrAncestorSetTrimsTrailingSeparator()
    {
        // Caller-supplied paths may have a trailing separator (e.g., directory writes recorded as
        // "X:\Repo\bin\Debug\net9.0\"). BuildEverWrittenOrAncestorSet must trim once on entry so the
        // ancestor walk does not produce an off-by-one parent of the same logical directory.
        HashSet<string> result = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>
        {
            @"X:\Repo\bin\Debug\net9.0\",
        });

        Assert.IsTrue(result.Contains(@"X:\Repo\bin\Debug\net9.0"));
        Assert.IsFalse(result.Contains(@"X:\Repo\bin\Debug\net9.0\"));
        Assert.IsTrue(result.Contains(@"X:\Repo\bin\Debug"));
        Assert.IsTrue(result.Contains(@"X:\Repo\bin"));
        Assert.IsTrue(result.Contains(@"X:\Repo"));
        Assert.IsTrue(result.Contains(@"X:\"));
    }

    [TestMethod]
    public void BuildEverWrittenOrAncestorSetEmptyInput()
    {
        HashSet<string> result = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>());
        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void SelfOutputProbeIsExcluded()
    {
        HashSet<string> everWrittenOrAncestor = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>
        {
            @"X:\Repo\staging\Generated.dll",
        });
        HashSet<string> everWritten = new(StringComparer.OrdinalIgnoreCase)
        {
            @"X:\Repo\staging\Generated.dll",
        };

        var observation = new ObservedAccess(@"X:\Repo\staging", ObservationType.ExistingProbe);

        Assert.IsTrue(FileAccessRepository.ShouldExcludeSelfOutputObservation(observation, everWritten, everWrittenOrAncestor));
    }

    [TestMethod]
    public void SelfOutputDirectoryEnumerationIsRetainedForPartitioning()
    {
        HashSet<string> everWrittenOrAncestor = FileAccessRepository.BuildEverWrittenOrAncestorSet(new List<string>
        {
            @"X:\Repo\staging\Generated.dll",
        });
        HashSet<string> everWritten = new(StringComparer.OrdinalIgnoreCase)
        {
            @"X:\Repo\staging\Generated.dll",
        };

        var observation = new ObservedAccess(@"X:\Repo\staging", ObservationType.DirectoryEnumeration);

        Assert.IsFalse(
            FileAccessRepository.ShouldExcludeSelfOutputObservation(observation, everWritten, everWrittenOrAncestor),
            "Directory enumerations must reach PartitionDirectoryMembers so external members remain fingerprint dependencies.");
    }

    [TestMethod]
    public void SelfCreatedDirectoryEnumerationIsExcluded()
    {
        var writtenPaths = new List<string>
        {
            @"X:\Repo\obj\generated",
            @"X:\Repo\obj\generated\Generated.gen",
        };
        HashSet<string> everWrittenOrAncestor = FileAccessRepository.BuildEverWrittenOrAncestorSet(writtenPaths);
        HashSet<string> everWritten = new(writtenPaths, StringComparer.OrdinalIgnoreCase);

        var observation = new ObservedAccess(@"X:\Repo\obj\generated", ObservationType.DirectoryEnumeration);

        Assert.IsTrue(
            FileAccessRepository.ShouldExcludeSelfOutputObservation(observation, everWritten, everWrittenOrAncestor),
            "A directory created by the project has no pre-build membership state to validate at lookup.");
    }
}
