// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.MSBuildCache.FileAccess;

internal sealed record FileAccesses(
    IReadOnlyCollection<ObservedAccess> Observations,
    IReadOnlyCollection<string> Outputs);
