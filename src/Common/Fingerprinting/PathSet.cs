// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache.Fingerprinting;

public sealed class PathSet : IEquatable<PathSet>
{
    public PathSet(IReadOnlyList<string> filesRead)
    {
        FilesRead = filesRead;
    }

    public IReadOnlyList<string> FilesRead { get; }

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

        if (FilesRead.Count != other.FilesRead.Count)
        {
            return false;
        }

        for (int i = 0; i < FilesRead.Count; i++)
        {
            if (!FilesRead[i].Equals(other.FilesRead[i], StringComparison.OrdinalIgnoreCase))
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
        foreach (string file in FilesRead)
        {
            hashCode.Add(file, StringComparer.OrdinalIgnoreCase);
        }

        return hashCode.ToHashCode();
    }
}
