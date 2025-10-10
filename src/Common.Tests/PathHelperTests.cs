// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class PathHelperTests
{
    [TestMethod]
    [DataRow(@"X:\A\B\C", @"X:\A", @"B\C")]
    // Lots of .. and .
    [DataRow(@"X:\Z\..\A\B\.\C", @"X:\Y\..\D\..\A\.\.", @"B\C")]
    // Equal paths
    [DataRow(@"X:\A\B\C", @"X:\A\B\C", @"")]
    // Drive root
    [DataRow(@"X:\A", @"X:\", @"A")]
    // Not relative to the base
    [DataRow(@"X:\D\E\F", @"X:\A\B\D", null)]
    // Different drives
    [DataRow(@"X:\A", @"Y:\", null)]
    // Trailing slashes
    [DataRow(@"X:\A\B\C\", @"X:\A\", @"B\C\")]
    [DataRow(@"X:\A\B\C\", @"X:\A", @"B\C\")]
    [DataRow(@"X:\A\B\C", @"X:\A\", @"B\C")]
    public void MakePathRelative(string path, string basePath, string? expectedResult)
        => Assert.AreEqual(expectedResult, path.MakePathRelativeTo(basePath));

    [TestMethod]
    [DataRow(@"X:\A\B\C\file.txt", @"X:\A", true)]
    // Lots of .. and .
    [DataRow(@"X:\Z\..\A\B\.\C\file.txt", @"X:\Y\..\D\..\A\.\.", true)]
    // Drive root
    [DataRow(@"X:\A\file.txt", @"X:\", true)]
    // Not under the dir
    [DataRow(@"X:\D\E\F\file.txt", @"X:\A\B\D", false)]
    // Different drives
    [DataRow(@"X:\A", @"Y:\", false)]
    // Trailing slash
    [DataRow(@"X:\A\B\C\file.txt", @"X:\A\B\C\", true)]
    public void IsUnderDirectory(string filePath, string directoryPath, bool expectedResult)
        => Assert.AreEqual(expectedResult, filePath.IsUnderDirectory(directoryPath));
}
