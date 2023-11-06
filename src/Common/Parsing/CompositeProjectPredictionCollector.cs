// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Prediction;

namespace Microsoft.MSBuildCache.Parsing;

internal sealed class CompositeProjectPredictionCollector : IProjectPredictionCollector
{
    private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

    private readonly PluginLoggerBase _logger;
    private readonly Dictionary<ProjectInstance, ProjectPredictionCollector> _collectorByProjectInstance;

    public CompositeProjectPredictionCollector(PluginLoggerBase logger, Dictionary<ProjectInstance, ProjectPredictionCollector> collectorByProjectInstance)
    {
        _logger = logger;
        _collectorByProjectInstance = collectorByProjectInstance;
    }

    public void AddInputFile(string path, ProjectInstance projectInstance, string predictorName)
    {
        if (path.IndexOfAny(InvalidPathChars) != -1)
        {
            _logger.LogMessage($"Ignoring input file with invalid path '{path}'. Predictor: {predictorName}. Project: {projectInstance.FullPath}");
            return;
        }

        GetProjectCollector(projectInstance)?.AddInputFile(path, projectInstance, predictorName);
    }

    public void AddInputDirectory(string path, ProjectInstance projectInstance, string predictorName)
    {
        if (path.IndexOfAny(InvalidPathChars) != -1)
        {
            _logger.LogMessage($"Ignoring input directory with invalid path '{path}'. Predictor: {predictorName}. Project: {projectInstance.FullPath}");
            return;
        }

        GetProjectCollector(projectInstance)?.AddInputDirectory(path, projectInstance, predictorName);
    }

    public void AddOutputFile(string path, ProjectInstance projectInstance, string predictorName)
    {
        if (path.IndexOfAny(InvalidPathChars) != -1)
        {
            _logger.LogMessage($"Ignoring output file with invalid path '{path}'. Predictor: {predictorName}. Project: {projectInstance.FullPath}");
            return;
        }

        GetProjectCollector(projectInstance)?.AddOutputFile(path, projectInstance, predictorName);
    }

    public void AddOutputDirectory(string path, ProjectInstance projectInstance, string predictorName)
    {
        if (path.IndexOfAny(InvalidPathChars) != -1)
        {
            _logger.LogMessage($"Ignoring output directory with invalid path '{path}'. Predictor: {predictorName}. Project: {projectInstance.FullPath}");
            return;
        }

        GetProjectCollector(projectInstance)?.AddOutputDirectory(path, projectInstance, predictorName);
    }
    private ProjectPredictionCollector? GetProjectCollector(ProjectInstance projectInstance)
        => _collectorByProjectInstance.TryGetValue(projectInstance, out ProjectPredictionCollector? collector) ? collector : null;
}
