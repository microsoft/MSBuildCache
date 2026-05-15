// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache.Fingerprinting;

/// <summary>
/// Predefined hash sentinels used by the type-aware strong fingerprint computation. Derived from constant
/// marker strings hashed through the configured <see cref="IContentHasher"/>, which guarantees correct
/// byte length for the active hash type and bit-for-bit reproduction across machines.
/// </summary>
internal static class WellKnownHashes
{
    // Marker strings: a namespaced prefix plus a role suffix. Constant across all consumers; never edit
    // these without also bumping the feature-flag value so old cache entries naturally invalidate.
    internal const string AbsentFileMarker = "Microsoft.MSBuildCache.Fingerprinting.AbsentFile";
    internal const string ZeroHashMarker = "Microsoft.MSBuildCache.Fingerprinting.Zero";

    /// <summary>
    /// Sentinel used to represent "this path did not exist" in a type-aware strong fingerprint payload.
    /// </summary>
    public static byte[] AbsentFileSentinel(IContentHasher hasher) => ComputeMarker(hasher, AbsentFileMarker);

    /// <summary>
    /// Sentinel used to represent "this is an existence-only observation; there is no content to hash".
    /// </summary>
    public static byte[] ZeroHash(IContentHasher hasher) => ComputeMarker(hasher, ZeroHashMarker);

    private static byte[] ComputeMarker(IContentHasher hasher, string marker)
    {
        ContentHash hash = hasher.GetContentHash(Encoding.UTF8.GetBytes(marker));
        return hash.ToHashByteArray();
    }
}
