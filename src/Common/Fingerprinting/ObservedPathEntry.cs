// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Microsoft.MSBuildCache.Fingerprinting;

/// <summary>
/// A single typed observation in a <see cref="PathSet"/>.
/// </summary>
/// <remarks>
/// Equality and hashing use case-insensitive comparison on <see cref="Path"/> (Windows-first), ordinal
/// comparison on <see cref="EnumerationPattern"/>, and case-insensitive ordered comparison on
/// <see cref="Members"/> and <see cref="WrittenMembers"/>.
/// </remarks>
public sealed class ObservedPathEntry : IEquatable<ObservedPathEntry>
{
    [JsonConstructor]
    public ObservedPathEntry(
        string path,
        ObservationType type,
        string? enumerationPattern = null,
        IReadOnlyList<string>? members = null,
        IReadOnlyList<string>? writtenMembers = null)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Type = type;

        // EnumerationPattern, Members, and WrittenMembers are meaningful only for DirectoryEnumeration
        // observations; normalize to null on other types so that semantically-equivalent observations
        // compare equal regardless of whether a caller happened to thread stray values through.
        bool isDirEnum = type == ObservationType.DirectoryEnumeration;
        EnumerationPattern = isDirEnum ? enumerationPattern : null;
        Members = isDirEnum ? members : null;
        WrittenMembers = isDirEnum ? writtenMembers : null;
    }

    /// <summary>The observed path. Normalization (e.g., repo/package-relative form) is the producer's responsibility.</summary>
    public string Path { get; }

    /// <summary>The kind of observation. Determines how the entry contributes to the strong fingerprint
    /// and how it is re-observed at cache lookup.</summary>
    public ObservationType Type { get; }

    /// <summary>
    /// Search pattern used for the enumeration (e.g., <c>*.cs</c>), or <c>null</c> for unfiltered enumerations
    /// and for non-<see cref="ObservationType.DirectoryEnumeration"/> entries.
    /// </summary>
    public string? EnumerationPattern { get; }

    /// <summary>
    /// For <see cref="ObservationType.DirectoryEnumeration"/> entries: the populate-time member-name list
    /// (leaf names, sorted <c>OrdinalIgnoreCase</c>) with self-outputs subtracted. This is the project's
    /// external view of the directory's membership — the part that can legitimately invalidate the cache
    /// when it changes. Hashed directly into the strong fingerprint, making the strong-FP deterministic
    /// from the entry without needing to touch the filesystem at compute time. Null for non-DirEnum
    /// entries and for entries where the directory was missing/inaccessible at populate time.
    /// </summary>
    public IReadOnlyList<string>? Members { get; }

    /// <summary>
    /// For <see cref="ObservationType.DirectoryEnumeration"/> entries: the populate-time self-output
    /// member-name list (leaf names, sorted <c>OrdinalIgnoreCase</c>) — the names that this project
    /// itself wrote into the directory during the populate build, identified via the <c>everWritten</c>
    /// set. At lookup time we subtract this from the re-enumerated current contents before comparing
    /// against <see cref="Members"/>, so cache hits succeed regardless of whether the previous build's
    /// outputs are still on disk (the deterministic-build assumption: the build would re-write the same
    /// names). Null for non-DirEnum entries.
    /// </summary>
    public IReadOnlyList<string>? WrittenMembers { get; }

    public bool Equals(ObservedPathEntry? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return Type == other.Type
            && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(EnumerationPattern, other.EnumerationPattern, StringComparison.Ordinal)
            && SequenceEqualsOIC(Members, other.Members)
            && SequenceEqualsOIC(WrittenMembers, other.WrittenMembers);
    }

    public override bool Equals(object? obj) => Equals(obj as ObservedPathEntry);

    public override int GetHashCode()
    {
        var hashCode = default(HashCode);
        hashCode.Add(Path, StringComparer.OrdinalIgnoreCase);
        hashCode.Add((byte)Type);
        if (EnumerationPattern is not null)
        {
            hashCode.Add(EnumerationPattern, StringComparer.Ordinal);
        }

        if (Members is not null)
        {
            foreach (string m in Members)
            {
                hashCode.Add(m, StringComparer.OrdinalIgnoreCase);
            }
        }

        if (WrittenMembers is not null)
        {
            foreach (string m in WrittenMembers)
            {
                hashCode.Add(m, StringComparer.OrdinalIgnoreCase);
            }
        }

        return hashCode.ToHashCode();
    }

    private static bool SequenceEqualsOIC(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
