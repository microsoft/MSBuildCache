// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Execution;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.MSBuildCache;

public interface INodeContextRepository
{
    bool TryGetNodeContext(ProjectInstance projectInstance, [MaybeNullWhen(false)] out NodeContext nodeContext);
}
