// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.MSBuildCache.FileAccess;

internal sealed class FileAccesses
{
    public FileAccesses(IReadOnlyCollection<string> inputs, IReadOnlyCollection<string> outputs)
    {
        Inputs = inputs;
        Outputs = outputs;
    }

    public IReadOnlyCollection<string> Inputs { get; }

    public IReadOnlyCollection<string> Outputs { get; }
}
