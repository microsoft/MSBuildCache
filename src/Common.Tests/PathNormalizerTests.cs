// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class PathNormalizerTests
{
    /// <summary>
    /// Repo-rooted paths are normalized after <see cref="PathNormalizer.Normalize"/>.
    /// </summary>
    [TestMethod]
    public void IsNormalizedRepoRoot()
    {
        var pn = new PathNormalizer(@"X:\Repo", @"X:\Nuget");
        string normalized = pn.Normalize(@"X:\Repo\src\foo.cs");

        Assert.IsTrue(PathNormalizer.IsNormalized(normalized), $"Expected '{normalized}' to be normalized (under repo root).");
    }

    /// <summary>
    /// NuGet-rooted paths are normalized after <see cref="PathNormalizer.Normalize"/>.
    /// </summary>
    [TestMethod]
    public void IsNormalizedNugetRoot()
    {
        var pn = new PathNormalizer(@"X:\Repo", @"X:\Nuget");
        string normalized = pn.Normalize(@"X:\Nuget\some.package\1.0.0\lib\foo.dll");

        Assert.IsTrue(PathNormalizer.IsNormalized(normalized), $"Expected '{normalized}' to be normalized (under NuGet root).");
    }

    /// <summary>
    /// System paths (drives outside repo+NuGet) are not normalized. This is the rule that filters
    /// .NET Framework breadcrumb stores, NGEN caches, MSBuild SDK installs, etc.
    /// </summary>
    [TestMethod]
    public void IsNormalizedSystemPathFails()
    {
        var pn = new PathNormalizer(@"X:\Repo", @"X:\Nuget");
        string normalized = pn.Normalize(@"C:\ProgramData\Microsoft\NetFramework\BreadcrumbStore\some.breadcrumb");

        Assert.IsFalse(PathNormalizer.IsNormalized(normalized), $"Expected '{normalized}' to NOT be normalized (system path).");
    }

    /// <summary>
    /// Same drive as the repo but outside the repo root is still not normalized.
    /// </summary>
    [TestMethod]
    public void IsNormalizedSameDriveOutsideRepoFails()
    {
        var pn = new PathNormalizer(@"X:\Repo", @"X:\Nuget");
        string normalized = pn.Normalize(@"X:\OtherProject\foo.cs");

        Assert.IsFalse(PathNormalizer.IsNormalized(normalized), $"Expected '{normalized}' to NOT be normalized (sibling of repo root).");
    }

    /// <summary>
    /// Round-trip: Normalize then Unnormalize returns the original path (or an OS-equivalent form).
    /// Pinned because the scope check depends on Normalize producing a placeholder prefix for in-scope paths.
    /// </summary>
    [TestMethod]
    public void NormalizeUnnormalizeRoundTrip()
    {
        var pn = new PathNormalizer(@"X:\Repo", @"X:\Nuget");
        string original = @"X:\Repo\src\Program.cs";

        string normalized = pn.Normalize(original);
        string roundTripped = pn.Unnormalize(normalized);

        Assert.AreEqual(original, roundTripped);
        Assert.IsTrue(normalized.StartsWith("{RepoRoot}", System.StringComparison.Ordinal));
    }
}
