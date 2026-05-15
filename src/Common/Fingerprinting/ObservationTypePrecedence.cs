// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.MSBuildCache.Fingerprinting;

/// <summary>
/// Helpers for working with <see cref="ObservationType"/> precedence when the same path is observed
/// multiple times in a single build.
/// </summary>
/// <remarks>
/// Precedence order: <c>FileContentRead &gt; DirectoryEnumeration &gt; ExistingProbe &gt; AbsentPathProbe</c>
/// — the most specific observation wins. Use this helper rather than relying on the underlying byte
/// values so precedence remains decoupled from the schema.
/// </remarks>
internal static class ObservationTypePrecedence
{
    /// <summary>Returns whichever of <paramref name="a"/> and <paramref name="b"/> has higher precedence.</summary>
    public static ObservationType Max(ObservationType a, ObservationType b)
        => Rank(a) <= Rank(b) ? a : b;

    /// <summary>
    /// Returns the precedence rank of an observation type. Lower rank = higher precedence
    /// (matches the byte value, but callers must not rely on that — go through this helper).
    /// </summary>
    public static int Rank(ObservationType type)
        => type switch
        {
            ObservationType.FileContentRead => 1,
            ObservationType.DirectoryEnumeration => 2,
            ObservationType.ExistingProbe => 3,
            ObservationType.AbsentPathProbe => 4,
            _ => int.MaxValue, // Unknown type — treat as lowest precedence (will lose every fold).
        };
}
