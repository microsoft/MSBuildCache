// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.Prediction.Predictors;

namespace Microsoft.MSBuildCache.Parsing;

internal record class ParserInfo(
    string ProjectFileRelativePath,
    IReadOnlyList<PredictedInput> Inputs,
    string? ReferenceAssemblyRelativePath);

internal sealed class Parser
{
    private readonly PluginLoggerBase _logger;
    private readonly string _repoRoot;

    public Parser(PluginLoggerBase logger, string repoRoot)
    {
        _logger = logger;
        _repoRoot = repoRoot;
    }

    public IReadOnlyDictionary<ProjectGraphNode, ParserInfo> Parse(ProjectGraph graph)
    {
        var predictionCollectorForProjects = new Dictionary<ProjectInstance, ProjectPredictionCollector>(graph.ProjectNodes.Count);
        foreach (ProjectGraphNode node in graph.ProjectNodes)
        {
            // Don't consider anything outside the repository.
            if (!node.ProjectInstance.FullPath.IsUnderDirectory(_repoRoot))
            {
                _logger.LogMessage($"Ignoring project outside the repository: {node.ProjectInstance.FullPath}");
                continue;
            }

            ProjectPredictionCollector predictionCollector = new(node);
            predictionCollectorForProjects.Add(node.ProjectInstance, predictionCollector);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        // AdditionalIncludeDirectoriesPredictor overpredicts and these can be covered by PathSet anyway.
        var projectPredictors = ProjectPredictors.AllProjectPredictors.Where(p => p is not AdditionalIncludeDirectoriesPredictor);
        var predictionExecutor = new ProjectGraphPredictionExecutor(ProjectPredictors.AllProjectGraphPredictors, projectPredictors);
        var compositePredictionCollector = new CompositeProjectPredictionCollector(_logger, predictionCollectorForProjects);
        predictionExecutor.PredictInputsAndOutputs(graph, compositePredictionCollector);
        _logger.LogMessage($"Executed project prediction on {graph.ProjectNodes.Count} nodes in {stopwatch.Elapsed.TotalSeconds:F2}s.");

        // Build the final collection to return
        var parserInfoForNodes = new Dictionary<ProjectGraphNode, ParserInfo>(predictionCollectorForProjects.Count);
        foreach (KeyValuePair<ProjectInstance, ProjectPredictionCollector> kvp in predictionCollectorForProjects)
        {
            ProjectInstance projectInstance = kvp.Key;
            ProjectPredictionCollector predictionCollector = kvp.Value;

            string projectFilePath = projectInstance.FullPath;
            string projectFileRelativePath = projectFilePath.MakePathRelativeTo(_repoRoot) ?? throw new InvalidOperationException($"Project \"{projectFilePath}\" is not under the repo root \"{_repoRoot}\"");

            string targetRefPath = projectInstance.GetPropertyValue("TargetRefPath");
            string? referenceAssemblyPath = !string.IsNullOrEmpty(targetRefPath) ? Path.Combine(projectInstance.Directory, targetRefPath) : null;

            ParserInfo parserInfo = new(projectFileRelativePath, predictionCollector.Inputs, referenceAssemblyPath);
            parserInfoForNodes.Add(predictionCollector.Node, parserInfo);
        }

        return parserInfoForNodes;
    }
}
