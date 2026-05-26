// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class ObservationFilterTests
{
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

        Assert.IsFalse(result.Contains(@"X:\Repo\bin\Debug\net9.0"), "Ancestor walk should skip the off-by-one parent of the trimmed input.");
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
}
