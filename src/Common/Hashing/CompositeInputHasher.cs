// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Tasks;

namespace Microsoft.MSBuildCache.Hashing;

/// <summary>
/// Represents a hasher which combines multiple hasher implementations together.
/// </summary>
internal sealed class CompositeInputHasher : IInputHasher
{
    private readonly IInputHasher[] _inputHashers;

    public CompositeInputHasher(IInputHasher[] inputHashers)
    {
        _inputHashers = inputHashers;
    }

    public bool ContainsPath(string absolutePath)
    {
        foreach (IInputHasher inputHasher in _inputHashers)
        {
            if (inputHasher.ContainsPath(absolutePath))
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<byte[]?> GetHashAsync(string absolutePath)
    {
        foreach (IInputHasher inputHasher in _inputHashers)
        {
            byte[]? hash = await inputHasher.GetHashAsync(absolutePath);
            if (hash != null)
            {
                return hash;
            }
        }

        return null;
    }
}
