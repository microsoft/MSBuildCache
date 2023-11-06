// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;

namespace Microsoft.MSBuildCache;

public sealed class NodeTargetResult
{
    public const uint CurrentVersion = 0;

    [JsonConstructor]
    public NodeTargetResult(string targetName, IReadOnlyList<NodeTargetResultTaskItem> taskItems)
    {
        TargetName = targetName;
        TaskItems = taskItems;
    }

    public string TargetName { get; }

    public IReadOnlyList<NodeTargetResultTaskItem> TaskItems { get; }

    public static NodeTargetResult FromTargetResult(string targetName, TargetResult targetResult, PathNormalizer pathNormalizer)
    {
        List<NodeTargetResultTaskItem> taskItems = new(targetResult.Items.Length);
        foreach (ITaskItem taskItem in targetResult.Items)
        {
            taskItems.Add(NodeTargetResultTaskItem.FromTaskItem(taskItem, pathNormalizer));
        }

        return new NodeTargetResult(targetName, taskItems);
    }

    public PluginTargetResult ToPluginTargetResult(PathNormalizer pathNormalizer)
    {
        List<ITaskItem2> taskItems = new(TaskItems.Count);
        foreach (NodeTargetResultTaskItem item in TaskItems)
        {
            taskItems.Add(item.ToTaskItem(pathNormalizer));
        }

        // We only cache successful results.
        return new PluginTargetResult(TargetName, taskItems, BuildResultCode.Success);
    }
}
