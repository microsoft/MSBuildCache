// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache;

public static class HexUtilities
{
    private static readonly ushort[] HexToNybble =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,  // Character codes 0-9.
        0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100,  // Character codes ":;<=>?@"
        10, 11, 12, 13, 14, 15,  // Character codes A-F
        0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100,  // G-P
        0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100, 0x100,  // Q-Z
        0x100, 0x100, 0x100, 0x100, 0x100, 0x100,  // Character codes "[\]^_`"
        10, 11, 12, 13, 14, 15,  // Character codes a-f
    };

    public static byte[] HexToBytes(string? hex)
        => hex == null
            ? Array.Empty<byte>()
            : HexToBytes(hex.AsSpan());

    /// <summary>
    /// Parses hexadecimal strings the form '1234abcd' or '0x9876fedb' into
    /// an array of bytes.
    /// </summary>
    public static byte[] HexToBytes(ReadOnlySpan<char> hex)
    {
        hex = hex.Trim();

        ReadOnlySpan<char> cur = hex;
        if (cur.StartsWith("0x".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            cur = cur.Slice(2);
        }

        byte[] result = new byte[cur.Length / 2];
        int index = 0;

        try
        {
            while (!cur.IsEmpty)
            {
                int b = (HexToNybble[cur[0] - '0'] << 4) | HexToNybble[cur[1] - '0'];
                if (b < 256)
                {
                    result[index++] = (byte)b;
                }
                else
                {
                    throw new ArgumentException($"Invalid hex string {new string(hex.ToArray())}", nameof(hex));
                }

                cur = cur.Slice(2);
            }
        }
        catch (IndexOutOfRangeException)
        {
            throw new ArgumentException($"Invalid hex string {new string(hex.ToArray())}", nameof(hex));
        }

        return result;
    }

    public static byte[] Base64ToBytes(string base64Hash)
    {
        byte[] hashBytes = Convert.FromBase64String(base64Hash);
        return hashBytes;
    }
}
