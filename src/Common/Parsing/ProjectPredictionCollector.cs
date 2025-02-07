// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;

namespace Microsoft.MSBuildCache.Parsing;

internal sealed class ProjectPredictionCollector : IProjectPredictionCollector
{
    private static readonly char[] DirectorySeparatorChars = { Path.DirectorySeparatorChar };
    private readonly string _projectDirectory;

    // A cache of paths (relative or absolute) to their absolute form.
    // This is scoped per build file since the paths are relative to different directories.
    private readonly Dictionary<string, string> _absolutePathFileCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, PredictedInput> _inputs = new(StringComparer.OrdinalIgnoreCase);

    public ProjectPredictionCollector(ProjectGraphNode node)
    {
        Node = node;
        _projectDirectory = Path.GetDirectoryName(node.ProjectInstance.FullPath)!;
    }

    public ProjectGraphNode Node { get; }

    public IReadOnlyList<PredictedInput> Inputs
    {
        get
        {
            var inputs = new PredictedInput[_inputs.Count];
            int inputIndex = 0;
            foreach (KeyValuePair<string, PredictedInput> kvp in _inputs)
            {
                inputs[inputIndex++] = kvp.Value;
            }

            return inputs;
        }
    }

    public void AddInputFile(string path, ProjectInstance projectInstance, string predictorName)
    {
        string absolutePath = GetFullPath(path);
        AddInput(absolutePath, predictorName);
    }

    public void AddInputDirectory(string path, ProjectInstance projectInstance, string predictorName)
    {
        string absoluteDirPath = GetFullPath(path);
        if (!Directory.Exists(absoluteDirPath))
        {
            // Ignore inputs to output directories which don't exist
            return;
        }

        foreach (string filePath in Directory.EnumerateFiles(absoluteDirPath, "*", SearchOption.TopDirectoryOnly))
        {
            AddInputFile(filePath, projectInstance, predictorName);
        }
    }

    public void AddOutputFile(string path, ProjectInstance projectInstance, string predictorName)
    {
        // No need to track these
    }

    public void AddOutputDirectory(string path, ProjectInstance projectInstance, string predictorName)
    {
        // No need to track these
    }

    private void AddInput(string absolutePath, string predictorName)
    {
        PredictedInput input = _inputs.GetOrAdd(absolutePath, static path => new PredictedInput(path));
        input.AddPredictorName(predictorName);
    }

    /// <summary>
    /// Gets the full path of a path which may be absolute or project-relative.
    /// </summary>
    /// <param name="path">An absolute or project-relative file path</param>
    /// <returns>The absolute file path.</returns>
    private string GetFullPath(string path)
    {
        if (!_absolutePathFileCache.TryGetValue(path, out string? absolutePath))
        {
            absolutePath = path;

            // If the path contains forward slash, normalize it with backward slash
            // Note that Replace returns the same string instance if there was no replacement, so there is no need for a Contains call to avoid allocating.
            absolutePath = absolutePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            // Make the path absolute
            if (!Path.IsPathRooted(absolutePath))
            {
                absolutePath = Path.Combine(_projectDirectory, absolutePath);
            }

            // Remove any \.\ or \..\ stuff
            absolutePath = Path.GetFullPath(absolutePath);

            // Always trim trailing slashes
            absolutePath = absolutePath.TrimEnd(DirectorySeparatorChars);

            _absolutePathFileCache.Add(path, absolutePath);
        }

        return absolutePath;
    }
}
