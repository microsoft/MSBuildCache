// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

namespace Microsoft.MSBuildCache;

public sealed class PathNormalizer
{
    // To assist portability of these results, on serialization replace various well-known absolute path roots
    // with a placeholder and on deserialization replace the placeholder with the repo root. That way the producing
    // and consuming builds don't need the repo root to have these roots be the exact same path.
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
