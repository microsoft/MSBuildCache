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
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Hashing;
using NuGet.Versioning;

namespace Microsoft.MSBuildCache.Fingerprinting;

public readonly record struct FingerprintEntry(byte[]? Hash, string Description);

public record Fingerprint(byte[] Hash, IReadOnlyList<FingerprintEntry> Entries);

public sealed class FingerprintFactory : IFingerprintFactory
{
    // Case-insensitive to match Windows filesystem semantics (NtQueryDirectoryFile / FindFirstFileEx).
    // Used for re-enumeration at lookup time against populate-time captured patterns.
    private static readonly GlobOptions CaseInsensitiveGlobOptions = new() { Evaluation = { CaseInsensitive = true } };

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

    // Sentinels computed once per factory instance from the configured content hasher; constant across
    // instances using the same HashType.
    internal byte[] AbsentFileSentinel { get; }
    internal byte[] ZeroHash { get; }

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

        byte[] ComputeWellKnownHash(string value) => contentHasher.GetContentHash(Encoding.UTF8.GetBytes(value)).ToHashByteArray();

        AbsentFileSentinel = ComputeWellKnownHash("Microsoft.MSBuildCache.Fingerprinting.AbsentFile");
        ZeroHash = ComputeWellKnownHash("Microsoft.MSBuildCache.Fingerprinting.Zero");

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

        _pluginSettingsFingerprintEntries.Add(
            CreateFingerprintEntry($"{nameof(pluginSettings.EnableProbeAndEnumerationFingerprinting)}: {pluginSettings.EnableProbeAndEnumerationFingerprinting}"));
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
                // This only applies to projects using MSVC, but the Developer Command Prompt and Visual Studio set it as an environment variable,
                // making GetPropertyValue always return a value. To avoid projects which don't use MSVC from getting cache misses when the version
                // changes, only apply this to vcxproj files. It's feasible but rare for other projects to use MSVC, so this is a compromise for better
                // cache hits.
                if (nodeContext.ProjectFileRelativePath.EndsWith(".vcxproj", StringComparison.OrdinalIgnoreCase)
                    || nodeContext.ProjectFileRelativePath.EndsWith(".nativeproj", StringComparison.OrdinalIgnoreCase))
                {
                    string vcToolsVersion = nodeContext.ProjectInstance.GetPropertyValue("VCToolsVersion");
                    if (!string.IsNullOrEmpty(vcToolsVersion))
                    {
                        entries.Add(CreateFingerprintEntry($"VCToolsVersion: {vcToolsVersion}"));
                    }
                }

                // If the .NET SDK changes, the node should rebuild.
                string dotnetSdkVersion = nodeContext.ProjectInstance.GetPropertyValue("NETCoreSdkVersion");
                if (!string.IsNullOrEmpty(dotnetSdkVersion))
                {
                    if (_pluginSettings.IgnoreDotNetSdkPatchVersion
                        && NuGetVersion.TryParse(dotnetSdkVersion, out NuGetVersion? parsedDotnetSdkVersion))
                    {
                        // The "feature band" indicates the feature set and is aligned with the Visual Studio version. It's "C00" in a version like A.B.CDD
                        // Extract it by removing the last two digits of the patch number, eg 123 -> 1.
                        int featureBand = parsedDotnetSdkVersion.Patch / 100;

                        // Eg: "8.0.404" -> "8.0.4XX", or "9.0.100-rc.2.24474.11" -> "9.0.100"
                        entries.Add(CreateFingerprintEntry($"DotnetSdkVersion: {parsedDotnetSdkVersion.Major}.{parsedDotnetSdkVersion.Minor}.{featureBand}XX"));
                    }
                    else
                    {
                        entries.Add(CreateFingerprintEntry($"DotnetSdkVersion: {dotnetSdkVersion}"));
                    }
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

    public PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations)
    {
        HashSet<string> predictedInputsSet = new(StringComparer.OrdinalIgnoreCase);
        foreach (string input in nodeContext.Inputs)
        {
            predictedInputsSet.Add(input);
        }

        (List<ObservedPathEntry> included, List<string> excluded) = FilterObservations(
            observations,
            predictedInputsSet,
            _inputHasher,
            _pathNormalizer,
            _pluginSettings.IgnoredInputPatterns);

        // Build the typed entry list. Extracted to a testable static helper.
        List<ObservedPathEntry> sortedEntries = FoldPathSetEntries(
            included,
            enableProbeAndEnumeration: _pluginSettings.EnableProbeAndEnumerationFingerprinting);

        // To help with debugging, dump the files which were included and excluded from the PathSet.
        // When probe/enumeration fingerprinting is active, include the type column so the log is diagnosable.
        File.WriteAllLines(
            Path.Combine(nodeContext.LogDirectory, "pathSetIncluded.txt"),
            sortedEntries.Select(e => e.Type == ObservationType.FileContentRead
                ? e.Path
                : (e.EnumerationPattern is null
                    ? $"[{e.Type}] {e.Path}"
                    : $"[{e.Type} {e.EnumerationPattern}] {e.Path}")));
        excluded.Sort(StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(Path.Combine(nodeContext.LogDirectory, "pathSetExcluded.txt"), excluded);

        // If the PathSet is effectively empty, return null instead.
        if (sortedEntries.Count == 0)
        {
            return null;
        }

        return new PathSet(sortedEntries);
    }

    /// <summary>
    /// Filters and normalizes sandbox observations into <see cref="ObservedPathEntry"/> records suitable for
    /// inclusion in a <see cref="PathSet"/>. Drops:
    /// <list type="bullet">
    ///   <item><description>Predicted inputs (already covered by the weak fingerprint).</description></item>
    ///   <item><description>Observations whose absolute path matches one of <paramref name="ignoredInputPatterns"/>.</description></item>
    ///   <item><description>Probe/enumeration observations outside any known root (see <see cref="PathNormalizer.IsNormalized"/>).</description></item>
    /// </list>
    /// For <c>FileContentRead</c> observations, paths the input hasher cannot hash are returned in the
    /// <c>Excluded</c> list (for debug logging) rather than the included list.
    /// </summary>
    internal static (List<ObservedPathEntry> Included, List<string> Excluded) FilterObservations(
        IReadOnlyCollection<ObservedAccess> observations,
        HashSet<string> predictedInputs,
        IInputHasher inputHasher,
        PathNormalizer pathNormalizer,
        IReadOnlyCollection<Glob> ignoredInputPatterns)
    {
        List<ObservedPathEntry> included = new();
        List<string> excluded = new();

        foreach (ObservedAccess observation in observations)
        {
            // Predicted inputs are already covered by the weak fingerprint; skip.
            if (predictedInputs.Contains(observation.Path))
            {
                continue;
            }

            // Drop observations whose path matches a configured ignore pattern. Applied before normalization
            // so the pattern matches against the absolute path (the form users author).
            if (MatchesAny(observation.Path, ignoredInputPatterns))
            {
                continue;
            }

            string normalizedPath = pathNormalizer.Normalize(observation.Path);

            if (observation.Type == ObservationType.FileContentRead)
            {
                // FCR contributes only if the hasher can hash this path; otherwise it just goes to a debug log.
                if (inputHasher.ContainsPath(observation.Path))
                {
                    included.Add(new ObservedPathEntry(normalizedPath, ObservationType.FileContentRead));
                }
                else
                {
                    excluded.Add(normalizedPath);
                }
            }
            else if (PathNormalizer.IsNormalized(normalizedPath))
            {
                // Probe/enum contributes only if it's under a known root.
                included.Add(new ObservedPathEntry(normalizedPath, observation.Type, observation.EnumerationPattern, observation.Members, observation.WrittenMembers));
            }
        }

        return (included, excluded);
    }

    private static bool MatchesAny(string path, IReadOnlyCollection<Glob> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        foreach (Glob pattern in patterns)
        {
            if (pattern.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Folds observations into a canonical, sorted list of <see cref="ObservedPathEntry"/>. For multiple
    /// observations of the same path:
    /// <list type="bullet">
    ///   <item><description>The highest-precedence type wins (per <see cref="ObservationTypePrecedence"/>).</description></item>
    ///   <item><description><c>DirectoryEnumeration</c> entries with distinct <c>EnumerationPattern</c>s are kept as separate entries.</description></item>
    /// </list>
    /// When <paramref name="enableProbeAndEnumeration"/> is <c>false</c>, only <c>FileContentRead</c>
    /// observations contribute. Returns entries sorted by
    /// <c>(Path OrdinalIgnoreCase, Type ascending, Pattern Ordinal)</c>.
    /// </summary>
    internal static List<ObservedPathEntry> FoldPathSetEntries(
        IReadOnlyCollection<ObservedPathEntry> observations,
        bool enableProbeAndEnumeration)
    {
        Dictionary<string, List<ObservedPathEntry>> normalizedPathToEntries = new(StringComparer.OrdinalIgnoreCase);

        foreach (ObservedPathEntry observation in observations)
        {
            if (!enableProbeAndEnumeration && observation.Type != ObservationType.FileContentRead)
            {
                continue;
            }

            if (!normalizedPathToEntries.TryGetValue(observation.Path, out List<ObservedPathEntry>? existingEntries))
            {
                normalizedPathToEntries[observation.Path] = new List<ObservedPathEntry> { observation };
                continue;
            }

            // All entries for a given path share the same highest-precedence Type by construction.
            ObservationType currentType = existingEntries[0].Type;
            ObservationType winning = ObservationTypePrecedence.Max(currentType, observation.Type);

            if (winning != currentType)
            {
                existingEntries.Clear();
                existingEntries.Add(observation);
            }
            else if (currentType == ObservationType.DirectoryEnumeration
                  && observation.Type == ObservationType.DirectoryEnumeration)
            {
                bool patternAlreadyPresent = false;
                foreach (ObservedPathEntry e in existingEntries)
                {
                    if (string.Equals(e.EnumerationPattern, observation.EnumerationPattern, StringComparison.Ordinal))
                    {
                        patternAlreadyPresent = true;
                        break;
                    }
                }

                if (!patternAlreadyPresent)
                {
                    existingEntries.Add(observation);
                }
            }
            // else: new observation has same-or-lower precedence and is not a new DirEnum pattern — drop.
        }

        return normalizedPathToEntries.Values
            .SelectMany(list => list)
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => (byte)e.Type)
            .ThenBy(e => e.EnumerationPattern, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet)
        => pathSet == null
            ? null
            : await _strongFingerprintCache.GetOrAdd(
                pathSet,
                async pathSet =>
                {
                    if (pathSet?.Entries == null || pathSet.Entries.Count == 0)
                    {
                        return null;
                    }

                    List<FingerprintEntry> entries = new();
                    await SortAndAddPathSetEntriesAsync(entries, pathSet.Entries, pathsAreNormalized: true);

                    if (entries.Count == 0)
                    {
                        return null;
                    }

                    return CreateFingerprint(entries);
                });

    /// <summary>
    /// Returns true if every non-FCR observation in <paramref name="cachedPathSet"/> still matches the
    /// current filesystem state. Probes verify presence/absence; directory enumerations verify the effective
    /// member list (after subtracting <c>WrittenMembers</c>) still matches <c>Members</c>. <c>FileContentRead</c>
    /// entries are not checked here — their content is validated implicitly by
    /// <see cref="GetStrongFingerprintAsync"/>, which hashes the current file contents.
    /// </summary>
    /// <remarks>
    /// This is a necessary-but-not-sufficient precondition for a cache hit: if it returns false, the strong
    /// fingerprint is guaranteed to differ and the caller can skip the FP computation. If it returns true,
    /// the caller must still compute the strong fingerprint and compare against the cached selector's FP
    /// to detect FCR content changes.
    /// </remarks>
    public bool MatchesCurrentState(PathSet? cachedPathSet)
    {
        if (cachedPathSet?.Entries == null)
        {
            return true;
        }

        foreach (ObservedPathEntry cached in cachedPathSet.Entries)
        {
            if (cached.Type == ObservationType.FileContentRead)
            {
                continue;
            }

            string absolutePath = _pathNormalizer.Unnormalize(cached.Path);
            bool fileExists = File.Exists(absolutePath);
            bool dirExists = !fileExists && Directory.Exists(absolutePath);

            switch (cached.Type)
            {
                case ObservationType.ExistingProbe:
                    if (!fileExists && !dirExists)
                    {
                        return false;
                    }
                    break;

                case ObservationType.AbsentPathProbe:
                    if (fileExists || dirExists)
                    {
                        return false;
                    }
                    break;

                case ObservationType.DirectoryEnumeration:
                    if (!dirExists)
                    {
                        return false;
                    }

                    // Re-enumerate, subtract cached.WrittenMembers, and compare against cached.Members.
                    // Subtracting self-outputs makes the comparison robust to whether the previous build's
                    // outputs are still on disk — the load-bearing trick for hitting on incremental rebuilds.
                    IReadOnlyList<string>? effective = EnumerateAndSubtract(absolutePath, cached.EnumerationPattern, cached.WrittenMembers);
                    if (effective is null)
                    {
                        // IO failure mid-enumeration — couldn't observe; force MISS to avoid a false hit
                        // against a populate-time empty observation.
                        return false;
                    }

                    if (!SequenceEqualsOIC(effective, cached.Members))
                    {
                        return false;
                    }
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Enumerates the directory, applies the optional <paramref name="enumerationPattern"/> filter, subtracts
    /// <paramref name="writtenMembersToSubtract"/> (case-insensitive leaf names), and returns the result
    /// sorted <c>OrdinalIgnoreCase</c>. Returns <c>null</c> on <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> — distinct from an empty list, which means the directory
    /// was observed and found empty. The caller uses null to force a cache MISS.
    /// </summary>
    internal static IReadOnlyList<string>? EnumerateAndSubtract(string absoluteDirectoryPath, string? enumerationPattern, IReadOnlyList<string>? writtenMembersToSubtract)
    {
        Glob? patternGlob = string.IsNullOrEmpty(enumerationPattern)
            ? null
            : Glob.Parse(enumerationPattern, CaseInsensitiveGlobOptions);

        HashSet<string> subtract = writtenMembersToSubtract is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(writtenMembersToSubtract, StringComparer.OrdinalIgnoreCase);

        List<string> effective = new();
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(absoluteDirectoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string leaf = Path.GetFileName(entry);
                if (patternGlob != null && !patternGlob.IsMatch(leaf))
                {
                    continue;
                }
                if (subtract.Contains(leaf))
                {
                    continue;
                }
                effective.Add(leaf);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        effective.Sort(StringComparer.OrdinalIgnoreCase);
        return effective;
    }

    private static bool SequenceEqualsOIC(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a is null || b is null)
        {
            // Treat null and empty as equal for re-observation purposes — both mean "no external members".
            return (a is null ? 0 : a.Count) == (b is null ? 0 : b.Count);
        }
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private async Task SortAndAddPathSetEntriesAsync(List<FingerprintEntry> entries, IReadOnlyList<ObservedPathEntry> pathSetEntries, bool pathsAreNormalized)
    {
        // PathSet.Entries are sorted by (Path OrdinalIgnoreCase, Type ascending, Pattern Ordinal) per the
        // PathSet contract — GetPathSet sorts at populate, deserialization preserves order at lookup.

        // Pre-compute file content hashes in parallel for FileContentRead entries — they're typically the
        // most expensive payloads.
        Dictionary<string, byte[]?> fileContentHashes = new(StringComparer.OrdinalIgnoreCase);
        List<Task<(string Path, byte[]? Hash)>> pendingHashes = new();
        foreach (ObservedPathEntry entry in pathSetEntries)
        {
            if (entry.Type != ObservationType.FileContentRead)
            {
                continue;
            }

            string absoluteFilePath = pathsAreNormalized ? _pathNormalizer.Unnormalize(entry.Path) : entry.Path;
            if (_pluginSettings.IgnoredInputPatterns.Count > 0
                && _pluginSettings.IgnoredInputPatterns.Any(pattern => pattern.IsMatch(absoluteFilePath)))
            {
                continue;
            }

            ValueTask<byte[]?> hashTask = _inputHasher.GetHashAsync(absoluteFilePath);
            if (hashTask.IsCompletedSuccessfully)
            {
                fileContentHashes[entry.Path] = hashTask.Result;
            }
            else
            {
                pendingHashes.Add(WrapAsync(entry.Path, hashTask.AsTask()));
            }

            static async Task<(string Path, byte[]? Hash)> WrapAsync(string path, Task<byte[]?> task) => (path, await task);
        }

        if (pendingHashes.Count > 0)
        {
            foreach ((string path, byte[]? hash) in await Task.WhenAll(pendingHashes))
            {
                fileContentHashes[path] = hash;
            }
        }

        // Emit entries — exactly one FingerprintEntry per ObservedPathEntry, with the
        // description encoding the observation's identity (type + path + any pattern) and the payload
        // encoding its value (content hash for FCR, member hash for DirEnum; probes have no value beyond
        // their identity, so the description hash alone is the entry hash).
        foreach (ObservedPathEntry entry in pathSetEntries)
        {
            string normalizedPath = pathsAreNormalized ? entry.Path : _pathNormalizer.Normalize(entry.Path);

            switch (entry.Type)
            {
                case ObservationType.FileContentRead:
                {
                    byte[]? contentHash = fileContentHashes.TryGetValue(entry.Path, out byte[]? h) ? h : null;
                    if (contentHash is null)
                    {
                        // The configured IInputHasher returned no hash for this path (e.g., out-of-scope or
                        // excluded). The path is intentionally not part of the fingerprint — skip emitting
                        // any entry so neither its content nor its identity contributes to the strong FP.
                        break;
                    }

                    entries.Add(CreateFingerprintEntry($"FileContentRead: {normalizedPath}", contentHash));
                    break;
                }

                case ObservationType.DirectoryEnumeration:
                {
                    string description = string.IsNullOrEmpty(entry.EnumerationPattern)
                        ? $"DirectoryEnumeration: {normalizedPath}"
                        : $"DirectoryEnumeration: {normalizedPath} ({entry.EnumerationPattern})";
                    byte[] memberHash = ComputeDirectoryMemberHash(entry);

                    // Header entry: payload encodes the entry's state (absent / empty / member-hash for
                    // non-empty), so the strong FP differentiates all three.
                    entries.Add(CreateFingerprintEntry(description, memberHash));

                    // Per-member entries: redundant for correctness (the header already encodes membership
                    // via ComputeDirectoryMemberHash), but they make fingerprint dumps diff-friendly — a
                    // reviewer can see exactly which member was added or removed between builds. The payload
                    // reuses the precomputed member hash so the call shape stays uniform with the header.
                    if (entry.Members is not null)
                    {
                        foreach (string member in entry.Members)
                        {
                            entries.Add(CreateFingerprintEntry($"{description} - {member}", memberHash));
                        }
                    }
                    break;
                }

                case ObservationType.ExistingProbe:
                {
                    entries.Add(CreateFingerprintEntry($"ExistingProbe: {normalizedPath}"));
                    break;
                }

                case ObservationType.AbsentPathProbe:
                {
                    entries.Add(CreateFingerprintEntry($"AbsentPathProbe: {normalizedPath}"));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Computes the strong-fingerprint payload for a DirectoryEnumeration entry — a deterministic hash of
    /// <see cref="ObservedPathEntry.Members"/> (no filesystem access). Returns
    /// <see cref="AbsentFileSentinel"/> when Members is null (directory absent/inaccessible at populate)
    /// and <see cref="ZeroHash"/> when Members is empty.
    /// </summary>
    private byte[] ComputeDirectoryMemberHash(ObservedPathEntry entry)
    {
        if (entry.Members is null)
        {
            return AbsentFileSentinel;
        }

        if (entry.Members.Count == 0)
        {
            return ZeroHash;
        }

        var sb = new StringBuilder();
        foreach (string name in entry.Members)
        {
            // Uppercase to mirror CreateFingerprintEntry's case-insensitive normalization; null separator
            // disambiguates entries (so "ab" + "c" can't collide with "a" + "bc").
            sb.Append(name.ToUpperInvariant()).Append('\0');
        }

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return _contentHasher.GetContentHash(bytes).ToHashByteArray();
    }

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

    /// <summary>
    /// Like <see cref="CreateFingerprintEntry(string)"/> but combines an additional payload (e.g., a file
    /// content hash) into the entry's hash. The description-derived hash carries the entry's identity (path,
    /// type); the payload carries its value (content / membership). The resulting entry's hash depends on
    /// both — change either and the fingerprint differs.
    /// </summary>
    private FingerprintEntry CreateFingerprintEntry(string info, byte[] payload)
    {
        FingerprintEntry descriptionEntry = CreateFingerprintEntry(info);
        byte[] combined = _contentHasher.CombineHashes(new[] { descriptionEntry.Hash, payload })!;
        return new FingerprintEntry(combined, info);
    }
}
