// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;

namespace Microsoft.MSBuildCache;

public static class PathHelper
{
    public static string? MakePathRelativeTo(this string path, string basePath)
    {
        ReadOnlySpan<char> pathSpan = Path.GetFullPath(path).AsSpan();
        ReadOnlySpan<char> basePathSpan = Path.GetFullPath(basePath).AsSpan();

        basePathSpan = basePathSpan.TrimEnd(Path.DirectorySeparatorChar);

        if (pathSpan.StartsWith(basePathSpan, StringComparison.OrdinalIgnoreCase))
        {
            // Relative path.
            if (basePathSpan.Length == pathSpan.Length)
            {
                return string.Empty;
            }
            else if (pathSpan[basePathSpan.Length] == '\\')
            {
                return new string(pathSpan.Slice(basePathSpan.Length + 1).ToArray());
            }
        }

        return null;
    }

    public static bool IsUnderDirectory(this string filePath, string directoryPath)
    {
        filePath = Path.GetFullPath(filePath);
        directoryPath = Path.GetFullPath(directoryPath);

        if (!filePath.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (directoryPath[directoryPath.Length - 1] == Path.DirectorySeparatorChar)
        {
            return true;
        }
        else
        {
            return filePath.Length > directoryPath.Length
                && filePath[directoryPath.Length] == Path.DirectorySeparatorChar;
        }
    }

    /// <summary>
    /// File paths returned by Detours have some prefixes that need to be removed:
    /// \\?\ - removes the file name limit of 260 chars. It makes it 32735 (+ a null terminator)
    /// \??\ - this is a native Win32 FS path WinNt32
    /// </summary>
    internal static ReadOnlySpan<char> RemoveLongPathPrefixes(ReadOnlySpan<char> absolutePath)
    {
        ReadOnlySpan<char> pattern1 = @"\\?\".AsSpan();
        ReadOnlySpan<char> pattern2 = @"\??\".AsSpan();

        if (absolutePath.StartsWith(pattern1, StringComparison.OrdinalIgnoreCase))
        {
            return absolutePath.Slice(pattern1.Length);
        }

        if (absolutePath.StartsWith(pattern2, StringComparison.OrdinalIgnoreCase))
        {
            return absolutePath.Slice(pattern2.Length);
        }

        return absolutePath;
    }
}
