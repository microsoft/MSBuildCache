// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Threading.Tasks;

namespace Microsoft.MSBuildCache.Fingerprinting;

public interface IFingerprintFactory
{
    Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext);

    PathSet? GetPathSet(NodeContext nodeContext, IEnumerable<string> observedInputs);

    Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet);
}
