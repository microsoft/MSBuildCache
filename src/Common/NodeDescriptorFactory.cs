// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using Microsoft.Build.Execution;

namespace Microsoft.MSBuildCache;

internal sealed class NodeDescriptorFactory
{
    // There seems to be a bug in MSBuild where this global property gets added for the scheduler node.
    private const string MSBuildProjectInstancePrefix = "MSBuildProjectInstance";

    private readonly HashSet<string> _globalPropertiesToIgnore;

    public NodeDescriptorFactory(HashSet<string> globalPropertiesToIgnore)
    {
        _globalPropertiesToIgnore = globalPropertiesToIgnore;
    }

    public NodeDescriptor Create(ProjectInstance projectInstance) => Create(projectInstance.FullPath, projectInstance.GlobalProperties);

    public NodeDescriptor Create(string projectFullPath, IEnumerable<KeyValuePair<string, string>> globalProperties)
    {
        // Sort to ensure a consistent hash for equivalent sets of properties.
        // This allocates with the assumption that the hash code is used multiple times.
        SortedDictionary<string, string> filteredGlobalProperties = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kvp in globalProperties)
        {
            if (kvp.Key.StartsWith(MSBuildProjectInstancePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_globalPropertiesToIgnore.Contains(kvp.Key))
            {
                continue;
            }

            if (string.Equals(kvp.Key, "TargetFramework", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(kvp.Value))
            {
                continue;
            }

            filteredGlobalProperties.Add(kvp.Key, kvp.Value);
        }

        return new NodeDescriptor(projectFullPath, filteredGlobalProperties);
    }
}
