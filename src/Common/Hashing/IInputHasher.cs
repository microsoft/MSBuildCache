// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.MSBuildCache.Hashing;

public interface IInputHasher
{
    /// <summary>
    /// Check if a file path is in the repository.
    /// </summary>
    bool ContainsPath(string relativePath);

    /// <summary>
    /// Return a hash value for the path.
    /// </summary>
    /// <returns>
    /// Returns null if no matching files are found.
    /// </returns>
    byte[]? GetHash(string relativePath);
}
