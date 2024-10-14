// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using DotNet.Globbing;
using Microsoft.MSBuildCache.Hashing;

namespace Microsoft.MSBuildCache.Fingerprinting;

public readonly record struct FingerprintEntry(byte[]? Hash, string Description);

public record Fingerprint(byte[] Hash, IReadOnlyList<FingerprintEntry> Entries);

public sealed class FingerprintFactory : IFingerprintFactory
{
    // Cache computed fingerprints which may be accessed multiple times. Note that this implies that fingerprint calculation
    // is based on data which is unchanging after it's computed. The weak fingerprints are based on dependencies' results, but
    // it's assumed that dependencies will always finish first.
    private readonly ConcurrentDictionary<NodeContext, Task<Fingerprint?>> _weakFingerprintCache = new();
    private readonly ConcurrentDictionary<PathSet, Task<Fingerprint?>> _strongFingerprintCache = new();

    private readonly ConcurrentDictionary<string, byte[]> _stringHashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IContentHasher _contentHasher;
    private readonly IInputHasher _inputHasher;
    private readonly List<FingerprintEntry> _pluginSettingsFingerprintEntries;
    private readonly PluginSettings _pluginSettings;
    private readonly PathNormalizer _pathNormalizer;

    public FingerprintFactory(
        IContentHasher contentHasher,
        IInputHasher inputHasher,
        PluginSettings pluginSettings,
        PathNormalizer pathNormalizer)
    {
        _contentHasher = contentHasher;
        _inputHasher = inputHasher;
        _pluginSettings = pluginSettings;
        _pathNormalizer = pathNormalizer;

        _pluginSettingsFingerprintEntries = new List<FingerprintEntry>()
        {
            CreateFingerprintEntry($"NodeBuildResultVersion: {NodeBuildResult.CurrentVersion}"),
            CreateFingerprintEntry($"NodeTargetResultVersion: {NodeTargetResult.CurrentVersion}"),
            CreateFingerprintEntry($"CacheUniverse: {pluginSettings.CacheUniverse}"),
        };

        void AddSettingToFingerprint(IReadOnlyCollection<Glob>? patterns, string settingName)
        {
            if (patterns != null)
            {
                foreach (Glob glob in patterns)
                {
                    _pluginSettingsFingerprintEntries.Add(CreateFingerprintEntry($"{settingName}: {glob}"));
                }
            }
        }

        AddSettingToFingerprint(pluginSettings.IgnoredInputPatterns, nameof(pluginSettings.IgnoredInputPatterns));
        AddSettingToFingerprint(pluginSettings.IgnoredOutputPatterns, nameof(pluginSettings.IgnoredOutputPatterns));
        AddSettingToFingerprint(pluginSettings.IdenticalDuplicateOutputPatterns, nameof(pluginSettings.IdenticalDuplicateOutputPatterns));
        AddSettingToFingerprint(pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns, nameof(pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns));
        AddSettingToFingerprint(pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns, nameof(pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns));
        AddSettingToFingerprint(pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns, nameof(pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns));
    }

    public async Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext)
        => await _weakFingerprintCache.GetOrAdd(
            nodeContext,
            async nodeContext =>
            {
                List<FingerprintEntry> entries = new(_pluginSettingsFingerprintEntries)
                {
                    // Add node information
                    CreateFingerprintEntry(nodeContext.ProjectFileRelativePath)
                };

                foreach (KeyValuePair<string, string> property in nodeContext.FilteredGlobalProperties)
                {
                    entries.Add(CreateFingerprintEntry($"{property.Key}={property.Value}"));
                }

                // Add the target list since part of the cache result is the BuildResult which contains results per target.
                // Sort for consistent hash ordering
                string targetList = string.Join(", ", nodeContext.TargetNames.OrderBy(target => target, StringComparer.OrdinalIgnoreCase));
                entries.Add(CreateFingerprintEntry($"Targets: {targetList}"));

                // If the VC toolchain changes, the node should rebuild.
                string vcToolsVersion = nodeContext.ProjectInstance.GetPropertyValue("VCToolsVersion");
                if (!string.IsNullOrEmpty(vcToolsVersion))
                {
                    entries.Add(CreateFingerprintEntry($"VCToolsVersion: {vcToolsVersion}"));
                }

                // If the .NET SDK changes, the node should rebuild.
                string dotnetSdkVersion = nodeContext.ProjectInstance.GetPropertyValue("NETCoreSdkVersion");
                if (!string.IsNullOrEmpty(dotnetSdkVersion))
                {
                    entries.Add(CreateFingerprintEntry($"DotnetSdkVersion: {dotnetSdkVersion}"));
                }

                // Add predicted inputs
                await SortAndAddInputFileHashesAsync(entries, nodeContext.Inputs, pathsAreNormalized: false);

                // Gather dependencies. Dependencies are sorted for a consistent hash ordering.
                SortedDictionary<string, NodeContext> dependencies = new(StringComparer.Ordinal);
                foreach (NodeContext dependency in nodeContext.Dependencies)
                {
                    if (dependency.BuildResult == null)
                    {
                        // The dependency has not been built, or at least not successfully
                        return null;
                    }

                    dependencies.Add(dependency.Id, dependency);
                }

                // Add dependency outputs
                foreach (KeyValuePair<string, NodeContext> kvp in dependencies)
                {
                    NodeContext dependency = kvp.Value;

                    // Sort outputs for consistent hash ordering
                    foreach (KeyValuePair<string, ContentHash> dependencyOutput in dependency.BuildResult!.Outputs)
                    {
                        entries.Add(new FingerprintEntry(dependencyOutput.Value.ToHashByteArray(), $"Dependency Output: {dependency.Id} - {dependencyOutput.Key}"));
                    }
                }

                return CreateFingerprint(entries);
            });

    public PathSet? GetPathSet(NodeContext nodeContext, IEnumerable<string> observedInputs)
    {
        List<string> pathSetIncludedNormalizedInputs = new();
        List<string> pathSetExcludedNormalizedInputs = new();

        HashSet<string> predictedInputsSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (string input in nodeContext.Inputs)
        {
            predictedInputsSet.Add(input);
        }

        // As an optimization, only include non-predicted inputs. If a predicted input changes, the weak fingerprint
        // will not match and so the associated PathSets will never be used.
        foreach (string observedInput in observedInputs)
        {
            if (predictedInputsSet.Contains(observedInput))
            {
                continue;
            }

            string normalizedInputPath = _pathNormalizer.Normalize(observedInput);
            if (_inputHasher.ContainsPath(observedInput))
            {
                pathSetIncludedNormalizedInputs.Add(normalizedInputPath);
            }
            else
            {
                pathSetExcludedNormalizedInputs.Add(normalizedInputPath);
            }
        }

        // Sort the collections for consistent ordering
        pathSetIncludedNormalizedInputs.Sort(StringComparer.OrdinalIgnoreCase);
        pathSetExcludedNormalizedInputs.Sort(StringComparer.OrdinalIgnoreCase);

        // To help with debugging, dump the files which were included and excluded from the PathSet.
        File.WriteAllLines(Path.Combine(nodeContext.LogDirectory, "pathSetIncluded.txt"), pathSetIncludedNormalizedInputs);
        File.WriteAllLines(Path.Combine(nodeContext.LogDirectory, "pathSetExcluded.txt"), pathSetExcludedNormalizedInputs);

        // If the PathSet is effectively empty, return null instead.
        if (pathSetIncludedNormalizedInputs.Count == 0)
        {
            return null;
        }

        return new PathSet(pathSetIncludedNormalizedInputs);
    }

    public async Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet)
        => pathSet == null
            ? null
            : await _strongFingerprintCache.GetOrAdd(
                pathSet,
                async pathSet =>
                {
                    if (pathSet?.FilesRead == null || pathSet.FilesRead.Count == 0)
                    {
                        return null;
                    }

                    List<FingerprintEntry> entries = new();
                    await SortAndAddInputFileHashesAsync(entries, pathSet.FilesRead, pathsAreNormalized: true);

                    if (entries.Count == 0)
                    {
                        return null;
                    }

                    return CreateFingerprint(entries);
                });

    private async Task SortAndAddInputFileHashesAsync(List<FingerprintEntry> entries, IReadOnlyList<string> files, bool pathsAreNormalized)
    {
        // Sort for consistent hash ordering
        SortedDictionary<string, byte[]?> filteredNormalizedFiles = new(StringComparer.OrdinalIgnoreCase);
        List<Task<(string, byte[]?)>> pendingHashingTasks = new();
        foreach (string file in files)
        {
            string absoluteFilePath = pathsAreNormalized ? _pathNormalizer.Unnormalize(file) : file;
            if (_pluginSettings.IgnoredInputPatterns.Count == 0
                || !_pluginSettings.IgnoredInputPatterns.Any(pattern => pattern.IsMatch(absoluteFilePath)))
            {
                string normalizedFilePath = pathsAreNormalized ? file : _pathNormalizer.Normalize(file);

                ValueTask<byte[]?> hashTask = _inputHasher.GetHashAsync(absoluteFilePath);

                // If the hashing task is synchronous or already complete, add it directly.
                // Otherwise, stash the task for later.
                if (hashTask.IsCompletedSuccessfully)
                {
                    byte[]? hash = hashTask.Result;
                    filteredNormalizedFiles.Add(normalizedFilePath, hash);
                }
                else
                {
                    pendingHashingTasks.Add(WrapHashingTask(normalizedFilePath, hashTask.AsTask()));
                }
            }
        }

        if (pendingHashingTasks.Count > 0)
        {
            // Wait for each of the hashing tasks to complete and then add them to the collection
            foreach ((string normalizedFilePath, byte[]? hash) in await Task.WhenAll(pendingHashingTasks))
            {
                filteredNormalizedFiles.Add(normalizedFilePath, hash);
            }
        }

        foreach (KeyValuePair<string, byte[]?> kvp in filteredNormalizedFiles)
        {
            entries.Add(new FingerprintEntry(kvp.Value, $"Input: {kvp.Key}"));
        }

        static async Task<(string, byte[]?)> WrapHashingTask(string normalizedFilePath, Task<byte[]?> hashTask) => (normalizedFilePath, await hashTask);
    }

    private Fingerprint? CreateFingerprint(List<FingerprintEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        // We know there's always at least one entry, so the content hasher will always produce a hash.
        byte[] hash = _contentHasher.CombineHashes(entries.Select(entry => entry.Hash))!;

        return new Fingerprint(hash, entries);
    }

    private FingerprintEntry CreateFingerprintEntry(string info)
    {
        // Replace absolute paths with a placeholder
        info = _pathNormalizer.Normalize(info);

        return new FingerprintEntry(
            _stringHashCache.GetOrAdd(
                info,
                str =>
                {
                    // Normalize case before hashing. We expect these strings to be case-insensitive
                    str = str.ToUpperInvariant();

                    byte[] bytes = Encoding.UTF8.GetBytes(str);
                    ContentHash hash = _contentHasher.GetContentHash(bytes);
                    return hash.ToHashByteArray();
                }),
            info);
    }
}
