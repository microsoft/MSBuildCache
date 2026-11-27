// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.MSBuildCache.FileAccess;

/// <param name="Observations">The observations contributing to the fingerprint.</param>
/// <param name="Outputs">The files written by the project.</param>
/// <param name="ExistingProbes">The paths probed and found existing, and not written by the project.
/// Captured whether or not probes contribute to the fingerprint.</param>
internal sealed record FileAccesses(
    IReadOnlyCollection<ObservedAccess> Observations,
    IReadOnlyCollection<string> Outputs,
    IReadOnlyCollection<string> ExistingProbes);
