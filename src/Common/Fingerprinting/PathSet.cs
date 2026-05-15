// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache.Fingerprinting;

public sealed class PathSet : IEquatable<PathSet>
{
    public PathSet(IReadOnlyList<ObservedPathEntry> entries)
    {
        Entries = entries;
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
