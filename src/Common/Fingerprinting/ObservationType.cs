// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.MSBuildCache.Fingerprinting;

/// <summary>
/// Classifies how a path was observed during a build, so that the strong fingerprint can be re-derived
/// from the right kind of filesystem evidence (content hash, directory membership, or just existence).
/// </summary>
/// <remarks>
/// Byte values are part of the on-disk schema and must remain stable. The numeric ordering also defines
/// type-precedence — lower value wins when the same path is observed multiple times.
/// </remarks>
#pragma warning disable CA1008 // Enums should have zero value. Schema-stable enum; 0 has no defined observation semantics and adding a synthetic None would waste a byte value in a deliberately-compact schema.
#pragma warning disable CA1028 // Enum storage should be Int32. Schema-stable enum serialized into PathSet and contributing to the strong fingerprint; the byte width is intentional and must match the QuickBuild implementation.
public enum ObservationType : byte
#pragma warning restore CA1028
#pragma warning restore CA1008
{
    /// <summary>The file's content was read; cache depends on the content hash.</summary>
    FileContentRead = 1,

    /// <summary>A directory was enumerated; cache depends on the (filtered) member list.</summary>
    DirectoryEnumeration = 2,

    /// <summary>A path was probed and found to exist; cache depends on its existence
    /// (but not on file content or directory membership).</summary>
    ExistingProbe = 3,

    /// <summary>A path was probed but did not exist; cache depends on its absence.
    /// This is the keystone of correct clean→dirty→miss caching: if the path later appears, the strong
    /// fingerprint differs and the cache correctly misses.</summary>
    AbsentPathProbe = 4,
}
