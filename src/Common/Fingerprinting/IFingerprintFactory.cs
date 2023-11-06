// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;

namespace Microsoft.MSBuildCache.Fingerprinting;

public interface IFingerprintFactory
{
    Fingerprint? GetWeakFingerprint(NodeContext nodeContext);

    PathSet? GetPathSet(NodeContext nodeContext, IEnumerable<string> observedInputs);

    Fingerprint? GetStrongFingerprint(PathSet? pathSet);
}
