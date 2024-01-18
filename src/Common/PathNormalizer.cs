// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

namespace Microsoft.MSBuildCache;

/// <summary>
/// Normalizes paths to be portable across machines.
/// </summary>
/// <remarks>
/// "Normalizing" a path will replace well-known base paths with placeholders. This enables the base paths to be different across machines
/// and thus make the paths portable, at least for the well-known base paths.
/// </remarks>
public sealed class PathNormalizer
{
    private const string RepoRootPlaceholder = "{RepoRoot}";
    private const string NugetPackageRootPlaceholder = "{NugetPackageRoot}";

    private readonly string _repoRoot;

    private readonly string _nugetPackageRoot;

    public PathNormalizer(string repoRoot, string nugetPackageRoot)
    {
        _repoRoot = EnsureTrailingSlash(Path.GetFullPath(repoRoot));
        _nugetPackageRoot = EnsureTrailingSlash(Path.GetFullPath(nugetPackageRoot));

        static string EnsureTrailingSlash(string path) => path[path.Length - 1] == '\\' ? path : (path + '\\');
    }

    public string Normalize(string path)
        => path
            .Replace(_repoRoot, RepoRootPlaceholder, StringComparison.OrdinalIgnoreCase)
            .Replace(_nugetPackageRoot, NugetPackageRootPlaceholder, StringComparison.OrdinalIgnoreCase);

    public string Unnormalize(string normalized)
        => normalized
            .Replace(RepoRootPlaceholder, _repoRoot, StringComparison.Ordinal)
            .Replace(NugetPackageRootPlaceholder, _nugetPackageRoot, StringComparison.Ordinal);
}
