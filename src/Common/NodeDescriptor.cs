// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache;

public readonly struct NodeDescriptor : IEquatable<NodeDescriptor>
{
    private readonly string _projectFullPath;

    private readonly SortedDictionary<string, string> _filteredGlobalProperties;

    public NodeDescriptor(string projectFullPath, SortedDictionary<string, string> filteredGlobalProperties)
    {
        _projectFullPath = projectFullPath;
        _filteredGlobalProperties = filteredGlobalProperties;
    }

    /// <summary>
    /// Sorted by StringComparison.OrdinalIgnoreCase.
    /// </summary>
    public IReadOnlyDictionary<string, string> FilteredGlobalProperties => _filteredGlobalProperties;

    public bool Equals(NodeDescriptor other)
    {
        if (!_projectFullPath.Equals(other._projectFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_filteredGlobalProperties.Count != other._filteredGlobalProperties.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> kvp in _filteredGlobalProperties)
        {
            if (!other._filteredGlobalProperties.TryGetValue(kvp.Key, out string? otherValue))
            {
                return false;
            }

            if (!kvp.Value.Equals(otherValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is NodeDescriptor key && Equals(key);

    public static bool operator ==(NodeDescriptor left, NodeDescriptor right) => left.Equals(right);

    public static bool operator !=(NodeDescriptor left, NodeDescriptor right) => !(left == right);

    public override int GetHashCode()
    {
        HashCode hashCode = new();
        hashCode.Add(_projectFullPath, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> kvp in _filteredGlobalProperties)
        {
            hashCode.Add(kvp.Key, StringComparer.OrdinalIgnoreCase);
            hashCode.Add(kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        return hashCode.ToHashCode();
    }
}
