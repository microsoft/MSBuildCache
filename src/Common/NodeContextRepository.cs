// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Build.Execution;

namespace Microsoft.MSBuildCache;

internal sealed class NodeContextRepository : INodeContextRepository
{
    private readonly IReadOnlyDictionary<NodeDescriptor, NodeContext> _nodeContexts;

    private readonly NodeDescriptorFactory _nodeDescriptorFactory;

    public NodeContextRepository(
        IReadOnlyDictionary<NodeDescriptor, NodeContext> nodeContexts,
        NodeDescriptorFactory nodeDescriptorFactory)
    {
        _nodeContexts = nodeContexts;
        _nodeDescriptorFactory = nodeDescriptorFactory;
    }

    public bool TryGetNodeContext(ProjectInstance projectInstance, [MaybeNullWhen(false)] out NodeContext nodeContext)
        => TryGetNodeContext(_nodeDescriptorFactory.Create(projectInstance), out nodeContext);

    internal bool TryGetNodeContext(NodeDescriptor descriptor, [MaybeNullWhen(false)] out NodeContext nodeContext)
        => _nodeContexts.TryGetValue(descriptor, out nodeContext);
}
