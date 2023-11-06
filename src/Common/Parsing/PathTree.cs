// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.MSBuildCache.Hashing;

namespace Microsoft.MSBuildCache.Parsing;

/// <summary>
/// Data structure to represent one segment of the path towards any file whose hash value
/// is used to determine target hash value. It is used by <see cref="InputHasher"/> class
/// to store and efficiently look up hash values of folders or files matching wildcards.
/// </summary>
internal sealed class PathTree
{
    private static readonly char[] FilePathSplitter = { '\\', '/' };

    private readonly Lazy<ConcurrentDictionary<string, PathTree>> _folders = new Lazy<ConcurrentDictionary<string, PathTree>>(() => new ConcurrentDictionary<string, PathTree>(StringComparer.OrdinalIgnoreCase));
    private readonly Lazy<ConcurrentDictionary<string, bool>> _files = new Lazy<ConcurrentDictionary<string, bool>>(() => new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    private readonly ReadOnlyMemory<char> _pathTilNodeWithTrailingSeparator;

    public PathTree()
        : this(ReadOnlyMemory<char>.Empty)
    {
    }

    private PathTree(ReadOnlyMemory<char> pathTillNode)
    {
        _pathTilNodeWithTrailingSeparator = pathTillNode;
    }

    public void AddFile(string pathRelativeToNode)
    {
        PathTree node = this;
        int index = -1;
        while (index < pathRelativeToNode.Length)
        {
            int prevIndex = index;
            index = pathRelativeToNode.IndexOfAny(FilePathSplitter, index + 1);
            if (index < 0)
            {
                // Last segment, the file name.
                node._files.Value[pathRelativeToNode.Substring(prevIndex + 1)] = true;
                break;
            }

            int folderNameLen = index - prevIndex - 1;
            if (folderNameLen == 0)
            {
                continue;
            }

            string folderName = pathRelativeToNode.Substring(prevIndex + 1, folderNameLen);
            ReadOnlyMemory<char> pathThusFarWithTrailingSeparator = pathRelativeToNode.AsMemory(0, index + 1);
            node = node._folders.Value.GetOrAdd(folderName, _ => new PathTree(pathThusFarWithTrailingSeparator));
        }
    }

    public IEnumerable<string> EnumerateFiles(string relativePath, SearchOption fileSearchOption)
    {
        PathTree? node = LocateNode(relativePath.AsSpan());
        if (node == null)
        {
            return Enumerable.Empty<string>();
        }

        return node.EnumerateFiles(fileSearchOption);
    }

    private IEnumerable<string> EnumerateFiles(SearchOption searchOption)
    {
        foreach (KeyValuePair<string, bool> file in _files.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            yield return _pathTilNodeWithTrailingSeparator + file.Key;
        }

        if (searchOption == SearchOption.AllDirectories)
        {
            foreach (KeyValuePair<string, PathTree> folder in _folders.Value.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string file in folder.Value.EnumerateFiles(searchOption))
                {
                    yield return file;
                }
            }
        }
    }

    private PathTree? LocateNode(ReadOnlySpan<char> folderPath)
    {
        PathTree? currentNode = this;
        while (folderPath.Length > 0)
        {
            int nextDirectorySeparator = folderPath.IndexOfAny(FilePathSplitter);
            ReadOnlySpan<char> subFolder;
            if (nextDirectorySeparator == -1)
            {
                // Use the rest of the path
                subFolder = folderPath;
                folderPath = ReadOnlySpan<char>.Empty;
            }
            else
            {
                subFolder = folderPath.Slice(0, nextDirectorySeparator);
                folderPath = folderPath.Slice(nextDirectorySeparator + 1);
            }

            if (subFolder.Length == 0)
            {
                continue;
            }

            if (!currentNode._folders.Value.TryGetValue(subFolder.ToString(), out currentNode))
            {
                // The folder did not exist
                return null;
            }
        }

        return currentNode;
    }
}
