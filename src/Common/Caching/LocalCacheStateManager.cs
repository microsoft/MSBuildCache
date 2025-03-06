// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Tracing;

namespace Microsoft.MSBuildCache.Caching;

internal sealed record LocalCacheStateEntry(string Hash, long LastWriteTime, long FileSize);

internal sealed record LocalCacheStateFile(Dictionary<string, LocalCacheStateEntry> Files);

internal sealed class LocalCacheStateManager
{
    private const string CacheStateDirName = ".msbuildcache";

    private readonly Tracer _tracer = new(nameof(LocalCacheStateManager));
    private readonly string _repoRoot;
    private readonly string _cacheStateDir;

    public LocalCacheStateManager(string repoRoot)
    {
        _repoRoot = repoRoot;
        _cacheStateDir = Path.Combine(repoRoot, CacheStateDirName);
    }

    internal async Task WriteStateFileAsync(
        NodeContext nodeContext,
        NodeBuildResult nodeBuildResult)
    {
        Dictionary<string, LocalCacheStateEntry> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, ContentHash> kvp in nodeBuildResult.Outputs)
        {
            string relativeFilePath = kvp.Key;
            ContentHash contentHash = kvp.Value;
            FileInfo fileInfo = new(Path.Combine(_repoRoot, relativeFilePath));
            files[relativeFilePath] = new LocalCacheStateEntry(contentHash.ToShortString(), fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length);
        }

        Directory.CreateDirectory(_cacheStateDir);

        string stateFilePath = Path.Combine(_cacheStateDir, nodeContext.Id + ".json");
        LocalCacheStateFile stateFile = new(files);

        using FileStream fileStream = File.Create(stateFilePath);
        await JsonSerializer.SerializeAsync(fileStream, stateFile, SourceGenerationContext.Default.LocalCacheStateFile);
    }

    internal async Task<List<KeyValuePair<string, ContentHash>>> GetOutOfDateFilesAsync(
        Context context,
        NodeContext nodeContext,
        NodeBuildResult nodeBuildResult)
    {
        string stateFilePath = Path.Combine(_repoRoot, CacheStateDirName, nodeContext.Id + ".json");

        LocalCacheStateFile? depFile = null;
        if (File.Exists(stateFilePath))
        {
            try
            {
                using FileStream fileStream = File.OpenRead(stateFilePath);
                depFile = await JsonSerializer.DeserializeAsync(fileStream, SourceGenerationContext.Default.LocalCacheStateFile);
            }
            catch (JsonException ex)
            {
                _tracer.Debug(context, $"Error reading local cache state for node {nodeContext.Id}. {ex.Message}");

                File.Delete(stateFilePath);
                depFile = null;
            }
        }
        else
        {
            _tracer.Debug(context, $"Local cache state for build target {nodeContext.Id} did not exist.");
        }

        if (depFile == null)
        {
            _tracer.Debug(context, "Considering all output files out of date.");
        }

        List<KeyValuePair<string, ContentHash>> outOfDateFiles = new(nodeBuildResult.Outputs.Count);
        foreach (KeyValuePair<string, ContentHash> kvp in nodeBuildResult.Outputs)
        {
            string relativeFilePath = kvp.Key;
            ContentHash contentHash = kvp.Value;
            if (!IsFileUpToDate(context, depFile, relativeFilePath, contentHash))
            {
                outOfDateFiles.Add(kvp);
            }
        }

        return outOfDateFiles;
    }

    private bool IsFileUpToDate(Context context, LocalCacheStateFile? depFile, string relativeFilePath, ContentHash expectedHash)
    {
        if (depFile == null)
        {
            return false;
        }

        if (!depFile.Files.TryGetValue(relativeFilePath, out LocalCacheStateEntry? cachedInfo))
        {
            _tracer.Debug(context, $"File {relativeFilePath} was out of date. It was missing from the state file.");
            return false;
        }

        if (!expectedHash.ToShortString().Equals(cachedInfo.Hash, StringComparison.OrdinalIgnoreCase))
        {
            _tracer.Debug(context, $"File {relativeFilePath} was out of date. The hash did not match.");
            return false;
        }

        FileInfo fileInfo = new(Path.Combine(_repoRoot, relativeFilePath));
        if (!fileInfo.Exists)
        {
            _tracer.Debug(context, $"File {relativeFilePath} was out of date. The file does not exist.");
            return false;
        }

        if (cachedInfo.LastWriteTime != fileInfo.LastWriteTimeUtc.Ticks)
        {
            _tracer.Debug(context, $"File {relativeFilePath} was out of date. The timestamp did not match.");
            return false;
        }

        if (cachedInfo.FileSize != fileInfo.Length)
        {
            _tracer.Debug(context, $"File {relativeFilePath} was out of date. The file size did not match.");
            return false;
        }

        _tracer.Debug(context, $"Skipping unchanged file: {relativeFilePath}");
        return true;
    }
}