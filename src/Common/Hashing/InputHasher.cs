// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache.Hashing;

/// <summary>
/// This class provides hash values for file, patterns with wildcards.
/// </summary>
internal sealed class InputHasher : IInputHasher
{
    private readonly ConcurrentDictionary<string, byte[]?> _cachedCalculatedHashes = new(StringComparer.OrdinalIgnoreCase);

    private readonly IContentHasher _contentHasher;

    private readonly IReadOnlyDictionary<string, byte[]> _fileHashes;

    public InputHasher(
        IContentHasher contentHasher,
        IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        _contentHasher = contentHasher;
        _fileHashes = fileHashes;
    }

    /// <inheritdoc />
    public bool ContainsPath(string relativePath)
        => _fileHashes.ContainsKey(relativePath); // Note: Wildcarded paths are not handled.

    /// <inheritdoc />
    public byte[]? GetHash(string relativePath)
        => _cachedCalculatedHashes.GetOrAdd(
            relativePath,
            path => _fileHashes.TryGetValue(path, out byte[]? contentHash)
                ? CalculateHashForFilePathAndContent(path, contentHash)
                : null);

    private byte[]? CalculateHashForFilePathAndContent(string filePath, byte[] contentHash)
    {
        return _contentHasher.CombineHashes(GetHashParts(filePath, contentHash));

        static IEnumerable<byte[]> GetHashParts(string filePath, byte[] contentHash)
        {
            yield return Encoding.UTF8.GetBytes(filePath.ToUpperInvariant());
            yield return contentHash;
        }
    }
}
