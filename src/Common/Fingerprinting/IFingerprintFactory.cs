// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.MSBuildCache.FileAccess;

namespace Microsoft.MSBuildCache.Fingerprinting;

public interface IFingerprintFactory
{
    Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext);

    PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations);

    Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet);

    bool MatchesCurrentState(PathSet? cachedPathSet);
}
