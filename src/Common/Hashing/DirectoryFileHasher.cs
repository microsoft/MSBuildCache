// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache.Hashing;

/// <summary>
/// Represents a hasher implementation which hashes files under a particular directory.
/// </summary>
internal sealed class DirectoryFileHasher : IInputHasher
{
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _cachedHashes = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;

    private readonly IContentHasher _contentHasher;

    public DirectoryFileHasher(string directory, IContentHasher contentHasher)
    {
        _directory = directory;
        _contentHasher = contentHasher;
    }

    public bool ContainsPath(string absolutePath) => absolutePath.IsUnderDirectory(_directory);

    public async ValueTask<byte[]?> GetHashAsync(string absolutePath)
    {
        if (!ContainsPath(absolutePath))
        {
            return null;
        }

        // Hashing is expensive so wrap it in a Lazy so that it happens *exactly* once.
        Lazy<Task<byte[]?>> lazyTask = _cachedHashes.GetOrAdd(
            absolutePath,
            path => new Lazy<Task<byte[]?>>(
                async () =>
                {
                    if (!File.Exists(path))
                    {
                        return null;
                    }

#if NETFRAMEWORK
                    if (path.IndexOf("microsoft.msbuildcache.", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return null;
                    }
#endif

                    ContentHash contentHash = await _contentHasher.GetFileHashAsync(path);
                    return contentHash.ToHashByteArray();
                }));

        return await lazyTask.Value;
    }
}
