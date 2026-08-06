// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using BuildXL.Native.IO;
using DotNet.Globbing;

namespace Microsoft.MSBuildCache.Fingerprinting;

/// <summary>
/// Enumerates directory member names with the exact Win32 search pattern reported by the sandbox.
/// </summary>
internal static class DirectoryEnumerationReader
{
    /// <summary>
    /// Returns the matching, non-ignored leaf names, or <c>null</c> when the directory could not be
    /// enumerated.
    /// </summary>
    /// <remarks>
    /// A general-purpose glob is not equivalent to Win32 matching: for example, Win32 <c>*.*</c> also
    /// matches extensionless names. BuildXL's native filesystem layer preserves those semantics,
    /// including DOS_STAR, DOS_QM, and DOS_DOT patterns, on both target frameworks.
    /// </remarks>
    public static IReadOnlyList<string>? EnumerateLeafNames(
        string absoluteDirectoryPath,
        string? enumerationPattern,
        IReadOnlyCollection<Glob>? ignoredInputPatterns = null)
    {
        var members = new List<string>();
        string searchPattern = string.IsNullOrEmpty(enumerationPattern) ? "*" : enumerationPattern!;

        EnumerateDirectoryResult result = FileUtilities.EnumerateDirectoryEntries(
            absoluteDirectoryPath,
            recursive: false,
            searchPattern,
            (_, name, _) =>
            {
                string absoluteMemberPath = Path.Combine(absoluteDirectoryPath, name);
                if (ignoredInputPatterns is null || !MatchesAny(absoluteMemberPath, ignoredInputPatterns))
                {
                    members.Add(name);
                }
            });

        return result.Succeeded ? members : null;
    }

    private static bool MatchesAny(string path, IReadOnlyCollection<Glob> patterns)
    {
        foreach (Glob pattern in patterns)
        {
            if (pattern.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }
}
