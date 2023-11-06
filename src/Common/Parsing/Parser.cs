// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Graph;
using Microsoft.Build.Prediction;
using Microsoft.Build.Prediction.Predictors;

namespace Microsoft.MSBuildCache.Parsing;

internal record class ParserInfo(string NormalizedProjectFilePath, IReadOnlyList<PredictedInput> Inputs);

internal sealed class Parser
{
    private readonly PluginLoggerBase _logger;
    private readonly string _repoRoot;
    private readonly IReadOnlyDictionary<string, byte[]> _fileHashes;

    public Parser(
        PluginLoggerBase logger,
        string repoRoot,
        IReadOnlyDictionary<string, byte[]> fileHashes)
    {
        _logger = logger;
        _repoRoot = repoRoot;
        _fileHashes = fileHashes;
    }

    public IReadOnlyDictionary<ProjectGraphNode, ParserInfo> Parse(ProjectGraph graph)
    {
        var repoPathTree = new PathTree();
        Stopwatch stopwatch = Stopwatch.StartNew();
        int parallelism = Math.Max(2, Environment.ProcessorCount * 3 / 4);
        Parallel.ForEach(_fileHashes, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, pair => repoPathTree.AddFile(pair.Key));
        _logger.LogMessage($"PathTree filling of {_fileHashes.Count} entries took {stopwatch.ElapsedMilliseconds}ms at parallelism {parallelism}");

        var normalizedFileCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var predictionCollectorForProjects = new Dictionary<ProjectInstance, ProjectPredictionCollector>(graph.ProjectNodes.Count);
        foreach (ProjectGraphNode node in graph.ProjectNodes)
        {
            // Don't consider anything outside the repository.
            if (!node.ProjectInstance.FullPath.IsUnderDirectory(_repoRoot))
            {
                _logger.LogMessage($"Ignoring project outside the repository: {node.ProjectInstance.FullPath}");
                continue;
            }

            ProjectPredictionCollector predictionCollector = new(node, _repoRoot, repoPathTree, normalizedFileCache);
            predictionCollectorForProjects.Add(node.ProjectInstance, predictionCollector);
        }

        stopwatch.Restart();
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
            ProjectPredictionCollector predictionCollector = kvp.Value;
            parserInfoForNodes.Add(predictionCollector.Node, predictionCollector.ToParserInfo());
        }

        return parserInfoForNodes;
    }
}
