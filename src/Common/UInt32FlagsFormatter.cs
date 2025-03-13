// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
#if DEBUG
using System.Diagnostics;
#endif
using System.IO;

namespace Microsoft.MSBuildCache;

/// <summary>
/// Fast flags enum formatter that separates bits with '|'
/// </summary>
internal static class UInt32FlagsFormatter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly string[] Names = GetNames();

#if DEBUG
    static UInt32FlagsFormatter()
    {
        Debug.Assert(typeof(TEnum).IsEnum);
        Debug.Assert(typeof(TEnum).GetEnumUnderlyingType() == typeof(uint));
    }
#endif

    private static string[] GetNames()
    {
        var names = new string[32];
        int k = 1;
        for (int j = 0; j < 32; j++, k <<= 1)
        {
            names[j] = unchecked(((TEnum)(object)(uint)k).ToString());
        }

        return names;
    }

    public static void Write(StreamWriter writer, uint value)
    {
        bool first = true;
        uint currentFlag = 1;
        for (int j = 0; j < 32; j++, currentFlag <<= 1)
        {
            if ((value & currentFlag) != 0)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    writer.Write('|');
                }

                writer.Write(Names[j]);
            }
        }

        if (first)
        {
            writer.Write(0);
        }
    }
}
