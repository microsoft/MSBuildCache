// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache.Hashing;

internal static class HashingExtensions
{
    private static readonly byte[] HashCharacterBuffer = { (byte)'#' };

    /// <summary>
    /// Combines hashes into a single hash.
    /// </summary>
    public static byte[]? CombineHashes(this IContentHasher contentHasher, IEnumerable<byte[]?> hashes)
    {
        using (HasherToken hasherToken = contentHasher.CreateToken())
        {
            HashAlgorithm hasher = hasherToken.Hasher;
            int totalBytes = 0;
            foreach (byte[]? hash in hashes)
            {
                if (hash == null || hash.Length == 0)
                {
                    continue;
                }

                hasher.TransformBlock(HashCharacterBuffer, 0, 1, HashCharacterBuffer, 0);
                hasher.TransformBlock(hash, 0, hash.Length, hash, 0);
                totalBytes += 1 + hash.Length;
            }

            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            if (totalBytes > 0)
            {
                return hasher.Hash;
            }
            else
            {
                return null;
            }
        }
    }

    public static async Task<ContentHash> GetFileHashAsync(this IContentHasher contentHasher, string filePath)
    {
        using FileStream fileStream = new(
            filePath,
            FileMode.Open,
            System.IO.FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096, // Copied from FileStream's DefaultBufferSize
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await contentHasher.GetContentHashAsync(fileStream);
    }
}
