// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.Build.Graph;
using DotNet.Globbing;

namespace Microsoft.MSBuildCache.Fingerprinting;

public readonly record struct FingerprintEntry(byte[]? Hash, string Description);

public record Fingerprint(byte[] Hash, IReadOnlyList<FingerprintEntry> Entries);

public sealed class FingerprintFactory : IFingerprintFactory
{
    // Cache computed fingerprints which may be accessed multiple times. Note that this implies that fingerprint calculation
    // is based on data which is unchanging after it's computed. The weak fingerprints are based on dependencies' results, but
    // it's assumed that dependencies will always finish first.
    private readonly ConcurrentDictionary<NodeContext, Fingerprint?> _weakFingerprintCache = new();
    private readonly ConcurrentDictionary<PathSet, Fingerprint?> _strongFingerprintCache = new();

    private readonly ConcurrentDictionary<string, byte[]> _stringHashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IContentHasher _contentHasher;
    private readonly IInputHasher _inputHasher;
    private readonly INodeContextRepository _nodeContextRepository;
    private readonly List<FingerprintEntry> _pluginSettingsFingerprintEntries;
    private readonly PluginSettings _pluginSettings;
    private readonly PathNormalizer _pathNormalizer;

    public FingerprintFactory(
        IContentHasher contentHasher,
        IInputHasher inputHasher,
        INodeContextRepository nodeRepository,
        PluginSettings pluginSettings,
        PathNormalizer pathNormalizer)
    {
        _contentHasher = contentHasher;
        _inputHasher = inputHasher;
        _nodeContextRepository = nodeRepository;
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

    public Fingerprint? GetWeakFingerprint(NodeContext nodeContext)
        => _weakFingerprintCache.GetOrAdd(
            nodeContext,
            nodeContext =>
            {
                List<FingerprintEntry> entries = new(_pluginSettingsFingerprintEntries)
                {
                    // Add node information
                    CreateFingerprintEntry(nodeContext.NormalizedProjectFilePath)
                };

                foreach (KeyValuePair<string, string> property in nodeContext.GlobalProperties)
                {
                    entries.Add(CreateFingerprintEntry($"{property.Key}={property.Value}"));
                }

                // Add the target list since part of the cache result is the BuildResult which contains results per target.
                // Sort for consistent hash ordering
                string targetList = string.Join(", ", nodeContext.TargetNames.OrderBy(target => target, StringComparer.OrdinalIgnoreCase));
                entries.Add(CreateFingerprintEntry($"Targets: {targetList}"));

                // Add predicted inputs
                SortAndAddInputFileHashes(entries, nodeContext.Inputs);

                // Gather dependencies. Dependencies are sorted for a consistent hash ordering.
                SortedDictionary<string, NodeContext> dependencies = new(StringComparer.Ordinal);
                foreach (ProjectGraphNode dependencyNode in nodeContext.Node.ProjectReferences)
                {
                    if (!_nodeContextRepository.TryGetNodeContext(dependencyNode.ProjectInstance, out NodeContext? dependency))
                    {
                        return null;
                    }

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
                    foreach (KeyValuePair<string, (DateTime LastModified, ContentHash Hash)> dependencyOutput in dependency.BuildResult!.Outputs)
                    {
                        entries.Add(new FingerprintEntry(dependencyOutput.Value.Hash.ToHashByteArray(), $"Dependency Output: {dependency.Id} - {dependencyOutput.Key}"));
                    }
                }

                return CreateFingerprint(entries);
            });

    public PathSet? GetPathSet(NodeContext nodeContext, IEnumerable<string> observedInputs)
    {
        List<string> pathSetIncludedInputs = new();
        List<string> pathSetExcludedInputs = new();

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

            if (_inputHasher.ContainsPath(observedInput))
            {
                pathSetIncludedInputs.Add(observedInput);
            }
            else
            {
                pathSetExcludedInputs.Add(observedInput);
            }
        }

        // Sort the collections for consistent ordering
        pathSetIncludedInputs.Sort(StringComparer.OrdinalIgnoreCase);
        pathSetExcludedInputs.Sort(StringComparer.OrdinalIgnoreCase);

        // To help with debugging, dump the files which were included and excluded from the PathSet.
        File.WriteAllLines(Path.Combine(nodeContext.LogDirectory, "pathSetIncluded.txt"), pathSetIncludedInputs);
        File.WriteAllLines(Path.Combine(nodeContext.LogDirectory, "pathSetExcluded.txt"), pathSetExcludedInputs);

        // If the PathSet is effectively empty, return null instead.
        if (pathSetIncludedInputs.Count == 0)
        {
            return null;
        }

        return new PathSet(pathSetIncludedInputs);
    }

    public Fingerprint? GetStrongFingerprint(PathSet? pathSet)
        => pathSet == null
            ? null
            : _strongFingerprintCache.GetOrAdd(
                pathSet,
                pathSet =>
                {
                    if (pathSet?.FilesRead == null || pathSet.FilesRead.Count == 0)
                    {
                        return null;
                    }

                    List<FingerprintEntry> entries = new();
                    SortAndAddInputFileHashes(entries, pathSet.FilesRead);

                    if (entries.Count == 0)
                    {
                        return null;
                    }

                    return CreateFingerprint(entries);
                });

    private void SortAndAddInputFileHashes(List<FingerprintEntry> entries, IEnumerable<string> files)
    {
        List<string> filteredfiles = new();
        foreach (string file in files)
        {
            if (_inputHasher.ContainsPath(file) && !_pluginSettings.IgnoredInputPatterns.Any(pattern => pattern.IsMatch(file)))
            {
                filteredfiles.Add(file);
            }
        }

        if (filteredfiles.Count == 0)
        {
            return;
        }

        // Sort for consistent hash ordering
        filteredfiles.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string file in filteredfiles)
        {
            entries.Add(new FingerprintEntry(_inputHasher.GetHash(file), $"Input: {file}"));
        }

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
