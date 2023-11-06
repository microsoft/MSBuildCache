// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.MSBuildCache;

public sealed class NodeTargetResultTaskItem
{
    [JsonConstructor]
    public NodeTargetResultTaskItem(string itemSpec, IReadOnlyDictionary<string, string> metadata)
    {
        ItemSpec = itemSpec;
        Metadata = metadata;
    }

    public string ItemSpec { get; set; }

    public IReadOnlyDictionary<string, string> Metadata { get; set; }

    public static NodeTargetResultTaskItem FromTaskItem(ITaskItem taskItem, PathNormalizer pathNormalizer)
    {
        string itemSpec = pathNormalizer.Normalize(taskItem.ItemSpec);

        IDictionary clonedMetadata = taskItem.CloneCustomMetadata();
        Dictionary<string, string> metadata = new(clonedMetadata.Count);
        foreach (DictionaryEntry entry in clonedMetadata)
        {
            string key = (string)entry.Key;
            string value = (string)entry.Value!;

            value = pathNormalizer.Normalize(value);

            metadata.Add(key, value);
        }

        return new NodeTargetResultTaskItem(itemSpec, metadata);
    }

    public ITaskItem2 ToTaskItem(PathNormalizer pathNormalizer)
    {
        string itemSpec = pathNormalizer.Unnormalize(ItemSpec);

        Dictionary<string, string> metadata = new(Metadata.Count);
        foreach (KeyValuePair<string, string> kvp in Metadata)
        {
            string key = kvp.Key;
            string value = kvp.Value;

            value = pathNormalizer.Unnormalize(value);

            metadata.Add(key, value);
        }

        return new TaskItem(itemSpec, metadata);
    }
}
