// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache.Hashing;

/// <summary>
/// This class provides hash values for file under source control.
/// </summary>
internal sealed class SourceControlFileHasher : IInputHasher
{
    private readonly ConcurrentDictionary<string, byte[]?> _cachedCalculatedHashes = new(StringComparer.OrdinalIgnoreCase);

    private readonly IContentHasher _contentHasher;

    private readonly PathNormalizer _pathNormalizer;

    private readonly IReadOnlyDictionary<string, byte[]> _fileHashes;

    public SourceControlFileHasher(
        IContentHasher contentHasher,
        PathNormalizer pathNormalizer,
        IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        _contentHasher = contentHasher;
        _pathNormalizer = pathNormalizer;
        _fileHashes = fileHashes;
    }

    /// <inheritdoc />
    public bool ContainsPath(string absolutePath) => _fileHashes.ContainsKey(absolutePath);

    /// <inheritdoc />
    public ValueTask<byte[]?> GetHashAsync(string absolutePath)
    {
        byte[]? hash = _cachedCalculatedHashes.GetOrAdd(
            absolutePath,
            path => _fileHashes.TryGetValue(path, out byte[]? contentHash)
                ? CalculateHashForFilePathAndContent(path, contentHash)
                : null);
        return new ValueTask<byte[]?>(hash);
    }

    // Filenames sometimes matter and impact build results, so consider the file path as well as the content for hashing.
    private byte[]? CalculateHashForFilePathAndContent(string filePath, byte[] contentHash)
    {
        string normalizedFilePath = _pathNormalizer.Normalize(filePath);
        return _contentHasher.CombineHashes(GetHashParts(normalizedFilePath, contentHash));

        static IEnumerable<byte[]> GetHashParts(string normalizedFilePath, byte[] contentHash)
        {
            yield return Encoding.UTF8.GetBytes(normalizedFilePath.ToUpperInvariant());
            yield return contentHash;
        }
    }
}
