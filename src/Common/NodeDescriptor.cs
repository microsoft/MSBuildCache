// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache;

public readonly struct NodeDescriptor : IEquatable<NodeDescriptor>
{
    private readonly string _projectFullPath;

    private readonly SortedDictionary<string, string> _globalProperties;

    public NodeDescriptor(string projectFullPath, SortedDictionary<string, string> globalProperties)
    {
        _projectFullPath = projectFullPath;
        _globalProperties = globalProperties;
    }

    /// <summary>
    /// Sorted by StringComparison.OrdinalIgnoreCase.
    /// </summary>
    public IReadOnlyDictionary<string, string> GlobalProperties => _globalProperties;

    public bool Equals(NodeDescriptor other)
    {
        if (!_projectFullPath.Equals(other._projectFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_globalProperties.Count != other._globalProperties.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> kvp in _globalProperties)
        {
            if (!other._globalProperties.TryGetValue(kvp.Key, out string? otherValue))
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

        foreach (KeyValuePair<string, string> kvp in _globalProperties)
        {
            hashCode.Add(kvp.Key, StringComparer.OrdinalIgnoreCase);
            hashCode.Add(kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        return hashCode.ToHashCode();
    }
}
