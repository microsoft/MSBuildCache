// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif
using Microsoft.Build.Execution;

namespace Microsoft.MSBuildCache;

public sealed class NodeDescriptorFactory
{
    // There seems to be a bug in MSBuild where this global property gets added for the scheduler node.
    private const string MSBuildProjectInstancePrefix = "MSBuildProjectInstance";

    private readonly HashSet<string> _globalPropertiesToIgnore;

    public NodeDescriptorFactory(HashSet<string> globalPropertiesToIgnore)
    {
        _globalPropertiesToIgnore = globalPropertiesToIgnore;
    }

    public NodeDescriptor Create(ProjectInstance projectInstance) => Create(projectInstance.FullPath, new ReadOnlyDictionaryAdapter<string, string>(projectInstance.GlobalProperties));

    public NodeDescriptor Create(string projectFullPath, IReadOnlyDictionary<string, string> globalProperties)
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

    // Adapts IDictionary to IReadOnlyDictionary
    private readonly struct ReadOnlyDictionaryAdapter<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    {
        private readonly IDictionary<TKey, TValue> _dictionary;

        public ReadOnlyDictionaryAdapter(IDictionary<TKey, TValue> dictionary)
        {
            _dictionary = dictionary;
        }

        public TValue this[TKey key] => _dictionary[key];

        public IEnumerable<TKey> Keys => _dictionary.Keys;

        public IEnumerable<TValue> Values => _dictionary.Values;

        public int Count => _dictionary.Count;

        public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

        public bool TryGetValue(
            TKey key,
#if NET5_0_OR_GREATER
            [MaybeNullWhen(false)]
#endif
            out TValue value) => _dictionary.TryGetValue(key, out value);

        IEnumerator IEnumerable.GetEnumerator() => _dictionary.GetEnumerator();
    }
}
