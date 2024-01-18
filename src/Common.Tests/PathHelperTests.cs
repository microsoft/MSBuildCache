// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class PathHelperTests
{
    [DataTestMethod]
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
    // Long paths
    [DataRow(
        @"\\?\D:\a\_work\1\s\src\modules\FileLocksmith\FileLocksmithUI\obj\x64\Release\generated\CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator\PowerToys.FileLocksmithUI.ViewModels.MainViewModel.RestartElevated.g.cs",
        @"\\?\D:\a\_work\1\s\",
        @"src\modules\FileLocksmith\FileLocksmithUI\obj\x64\Release\generated\CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator\PowerToys.FileLocksmithUI.ViewModels.MainViewModel.RestartElevated.g.cs")]
    [DataRow(
        @"\\?\D:\a\_work\1\s\src\modules\FileLocksmith\FileLocksmithUI\obj\x64\Release\generated\CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator\PowerToys.FileLocksmithUI.ViewModels.MainViewModel.RestartElevated.g.cs",
        @"D:\a\_work\1\s\",
        @"src\modules\FileLocksmith\FileLocksmithUI\obj\x64\Release\generated\CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator\PowerToys.FileLocksmithUI.ViewModels.MainViewModel.RestartElevated.g.cs")]
    public void MakePathRelative(string path, string basePath, string? expectedResult)
    {
        Assert.AreEqual(expectedResult, path.MakePathRelativeTo(basePath));
        Assert.AreEqual(
            expectedResult == null ? null : new RelativePath(expectedResult),
            new AbsolutePath(path).MakePathRelativeTo(new AbsolutePath(basePath)));
    }

    [DataTestMethod]
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
