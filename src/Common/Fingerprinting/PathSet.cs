// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache.Fingerprinting;

public sealed class PathSet : IEquatable<PathSet>
{
    public PathSet(IReadOnlyList<ObservedPathEntry> entries)
    {
        // Normalize null to empty. Deserializing a payload without an `Entries` property — a blob written
        // by a version predating this schema, or a truncated one — yields null here, and this type is used
        // as a dictionary key during cache lookup, so hashing happens before any caller can null-check it.
        // An empty set produces a null strong fingerprint, which skips the selector and misses, so
        // unrecognized data degrades to a cache miss rather than throwing mid-lookup.
        Entries = entries ?? Array.Empty<ObservedPathEntry>();
    }

    /// <summary>
    /// Gets the set of observations made during the build that were not predicted at planning time.
    /// </summary>
    /// <remarks>
    /// Entries are expected to be sorted by (Path OrdinalIgnoreCase, Type ascending, EnumerationPattern Ordinal)
    /// so that semantically equivalent PathSets serialize and compare identically.
    /// </remarks>
    public IReadOnlyList<ObservedPathEntry> Entries { get; }

    public bool Equals(PathSet? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        if (Entries.Count != other.Entries.Count)
        {
            return false;
        }

        for (int i = 0; i < Entries.Count; i++)
        {
            if (!Entries[i].Equals(other.Entries[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as PathSet);

    public override int GetHashCode()
    {
        var hashCode = default(HashCode);
        foreach (ObservedPathEntry entry in Entries)
        {
            hashCode.Add(entry);
        }

        return hashCode.ToHashCode();
    }
}
