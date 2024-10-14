﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Execution;

namespace Microsoft.MSBuildCache;

public sealed class NodeContext
{
    private static readonly byte[] PropertyHashDelimiter = new byte[] { 0x01 };
    private static readonly byte[] PropertyValueHashDelimiter = new byte[] { 0x02 };

    private readonly string _logDirectory;
    private bool _logDirectoryCreated;

    public NodeContext(
        string baseLogDirectory,
        ProjectInstance projectInstance,
        IReadOnlyList<NodeContext> dependencies,
        string projectFileRelativePath,
        IReadOnlyDictionary<string, string> filteredGlobalProperties,
        IReadOnlyList<string> inputs,
        HashSet<string> targetNames)
    {
        Id = GenerateId(projectFileRelativePath, filteredGlobalProperties);
        _logDirectory = Path.Combine(baseLogDirectory, Id);
        ProjectInstance = projectInstance;
        Dependencies = dependencies;
        ProjectFileRelativePath = projectFileRelativePath;
        FilteredGlobalProperties = filteredGlobalProperties;
        Inputs = inputs;
        TargetNames = targetNames;
    }

    public string Id { get; }

    public string LogDirectory
    {
        get
        {
            // If something is accessing it, assume it wants to use it, so create the directory.
            if (!_logDirectoryCreated)
            {
                Directory.CreateDirectory(_logDirectory);
                _logDirectoryCreated = true;
            }

            return _logDirectory;
        }
    }

    public ProjectInstance ProjectInstance { get; }

    public IReadOnlyList<NodeContext> Dependencies { get; }

    public string ProjectFileRelativePath { get; }

    public IReadOnlyDictionary<string, string> FilteredGlobalProperties { get; }

    public IReadOnlyList<string> Inputs { get; }

    public HashSet<string> TargetNames { get; }

    public DateTime? StartTimeUtc { get; private set; }

    public void SetStartTime() => StartTimeUtc = DateTime.UtcNow;

    public DateTime? EndTimeUtc { get; private set; }

    public void SetEndTime() => EndTimeUtc = DateTime.UtcNow;

    public NodeBuildResult? BuildResult { get; private set; }

    public void SetBuildResult(NodeBuildResult buildResult)
    {
        if (BuildResult != null)
        {
            throw new InvalidOperationException("Build result already set");
        }

        BuildResult = buildResult;
    }

    /// <summary>
    /// Generate a stable Id which we can use for sorting and comparison purposes across builds.
    /// </summary>
    private static string GenerateId(string projectFileRelativePath, IReadOnlyDictionary<string, string> filteredGlobalProperties)
    {
        // In practice, the dictionary we're given is SortedDictionary<string, string>, so try casting.
        if (filteredGlobalProperties is not SortedDictionary<string, string> sortedProperties)
        {
            sortedProperties = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kvp in filteredGlobalProperties)
            {
                sortedProperties.Add(kvp.Key, kvp.Value);
            }
        }

#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms. This is not used for crypto.
        using MD5 hasher = MD5.Create();
#pragma warning restore CA5351 // Do Not Use Broken Cryptographic Algorithms

        foreach (KeyValuePair<string, string> kvp in sortedProperties)
        {
            AddCaseInsensitiveStringToHash(hasher, kvp.Key);
            AddBytesToHash(hasher, PropertyValueHashDelimiter);
            AddCaseInsensitiveStringToHash(hasher, kvp.Value);
            AddBytesToHash(hasher, PropertyHashDelimiter);

            static void AddCaseInsensitiveStringToHash(MD5 hasher, string str) => AddBytesToHash(hasher, Encoding.UTF8.GetBytes(str.ToUpperInvariant()));
            static void AddBytesToHash(MD5 hasher, byte[] bytes) => hasher.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] hash = hasher.Hash!;

        string id = $"{projectFileRelativePath}_{Convert.ToBase64String(hash)}";

        // Avoid casing issues
        id = id.ToUpperInvariant();

        // Ensure the id is path-friendly
        id = id
            .Replace('\\', '_')
            .Replace('/', '_')
            .Replace('+', '.')
            .TrimEnd('=');

        return id;
    }
}
