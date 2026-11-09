// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Graph;

namespace Microsoft.MSBuildCache;

/// <summary>
/// Works around a race in MSBuild's project cache service.
/// In a graph build, <c>ProjectCacheService.InitializePluginsForGraph</c> initializes the plugin in the background with the graph,
/// while the scheduler already submits the first nodes. When a node's cache query initializes the plugin first, the plugin gets
/// no graph, one entry point per project of the solution (so the solution's ProjectDependencies edges are lost), and that node's
/// targets as the build's targets. The graph and target lists it computes then differ from the ones MSBuild builds with.
/// </summary>
internal static class GraphBuildRequestInitialization
{
    /// <summary>
    /// The targets a command-line graph build of a solution requests. The graph treats "Build" as each entry project's default targets.
    /// </summary>
    public static readonly string[] SolutionBuildTargets = ["Build"];

    private const string IsGraphBuildPropertyName = "IsGraphBuild";
    private const string SolutionPathPropertyName = "SolutionPath";
    private const string CurrentSolutionConfigurationContentsPropertyName = "CurrentSolutionConfigurationContents";

    /// <summary>
    /// Global properties that the solution graph sets again: the solution's own properties, the graph marker, and the project configuration
    /// (MSBuild drops the requesting project's Configuration and Platform and sets each entry point's from the solution configuration).
    /// </summary>
    private static readonly HashSet<string> DerivedGlobalProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        CurrentSolutionConfigurationContentsPropertyName,
        "BuildingSolutionFile",
        "SolutionDir",
        "SolutionExt",
        "SolutionFileName",
        "SolutionName",
        SolutionPathPropertyName,
        IsGraphBuildPropertyName,
        "Configuration",
        "Platform",
    };

    /// <summary>
    /// True when MSBuild initialized the plugin from a build request (no graph) although the build is a graph build.
    /// Graph nodes, and so the entry points MSBuild derives from their requests, carry IsGraphBuild=true. Visual Studio builds do not.
    /// </summary>
    public static bool IsGraphBuildInitializedFromBuildRequest(CacheContext context)
        => context.Graph is null
            && context.GraphEntryPoints is { Count: > 0 } entryPoints
            && entryPoints.All(entryPoint => IsTrue(entryPoint.GlobalProperties, IsGraphBuildPropertyName));

    /// <summary>
    /// Recreates the solution entry point of the graph build from the entry points MSBuild derived from a build request.
    /// Returns null, with the reason, when they do not come from a solution or the solution configuration cannot be found.
    /// </summary>
    public static ProjectGraphEntryPoint? CreateSolutionEntryPoint(IReadOnlyCollection<ProjectGraphEntryPoint> entryPoints, out string reason)
    {
        IDictionary<string, string>? globalProperties = entryPoints.FirstOrDefault().GlobalProperties;
        if (globalProperties is null
            || !globalProperties.TryGetValue(SolutionPathPropertyName, out string? escapedSolutionPath)
            || string.IsNullOrWhiteSpace(escapedSolutionPath)
            || !globalProperties.TryGetValue(CurrentSolutionConfigurationContentsPropertyName, out string? solutionConfigurationXml)
            || string.IsNullOrWhiteSpace(solutionConfigurationXml))
        {
            reason = "the entry points do not come from a solution";
            return null;
        }

        string solutionPath = ProjectCollection.Unescape(escapedSolutionPath);
        if (!File.Exists(solutionPath))
        {
            reason = $"the solution `{solutionPath}` does not exist";
            return null;
        }

        SolutionFile solution = SolutionFile.Parse(solutionPath);
        IReadOnlyDictionary<string, ProjectConfigurationMapping> mappings = ParseProjectConfigurations(solutionConfigurationXml);
        SolutionConfigurationInSolution? solutionConfiguration = FindSolutionConfiguration(solution, mappings);
        if (solutionConfiguration is null)
        {
            reason = $"no configuration of `{solutionPath}` maps its projects as the build does";
            return null;
        }

        Dictionary<string, string> solutionGlobalProperties = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> property in globalProperties)
        {
            if (!DerivedGlobalProperties.Contains(property.Key))
            {
                solutionGlobalProperties.Add(property.Key, property.Value);
            }
        }

        solutionGlobalProperties["Configuration"] = solutionConfiguration.ConfigurationName;
        solutionGlobalProperties["Platform"] = solutionConfiguration.PlatformName;

        reason = string.Empty;
        return new ProjectGraphEntryPoint(solutionPath, solutionGlobalProperties);
    }

    /// <summary>
    /// True when some node's target list holds exactly <paramref name="requestedTargets"/>: the targets of the build request that initialized the plugin.
    /// </summary>
    public static bool ContainsTargetList(IReadOnlyDictionary<ProjectGraphNode, System.Collections.Immutable.ImmutableList<string>> targetListPerNode, IReadOnlyCollection<string> requestedTargets)
    {
        HashSet<string> requested = new(requestedTargets, StringComparer.OrdinalIgnoreCase);
        return targetListPerNode.Values.Any(targetList => requested.SetEquals(targetList));
    }

    /// <summary>
    /// Reads the project configurations of a CurrentSolutionConfigurationContents value, keyed by project path.
    /// </summary>
    internal static IReadOnlyDictionary<string, ProjectConfigurationMapping> ParseProjectConfigurations(string solutionConfigurationXml)
    {
        XmlDocument document = new() { XmlResolver = null };
        using (XmlReader reader = XmlReader.Create(new StringReader(solutionConfigurationXml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document.Load(reader);
        }

        Dictionary<string, ProjectConfigurationMapping> mappings = new(StringComparer.OrdinalIgnoreCase);
        XmlNodeList? projectConfigurations = document.DocumentElement?.SelectNodes("ProjectConfiguration");
        if (projectConfigurations is null)
        {
            return mappings;
        }

        foreach (XmlElement projectConfiguration in projectConfigurations.OfType<XmlElement>())
        {
            string absolutePath = projectConfiguration.GetAttribute("AbsolutePath");
            if (string.IsNullOrEmpty(absolutePath))
            {
                continue;
            }

            string buildProjectInSolution = projectConfiguration.GetAttribute("BuildProjectInSolution");
            bool includeInBuild = !bool.TryParse(buildProjectInSolution, out bool parsed) || parsed;
            mappings[Path.GetFullPath(absolutePath)] = new ProjectConfigurationMapping(projectConfiguration.InnerText, includeInBuild);
        }

        return mappings;
    }

    /// <summary>
    /// Finds the solution configuration that maps every project as <paramref name="mappings"/> does.
    /// </summary>
    private static SolutionConfigurationInSolution? FindSolutionConfiguration(SolutionFile solution, IReadOnlyDictionary<string, ProjectConfigurationMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            return null;
        }

        Dictionary<string, ProjectInSolution> projectsByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectInSolution project in solution.ProjectsInOrder)
        {
            projectsByPath[Path.GetFullPath(project.AbsolutePath)] = project;
        }

        foreach (SolutionConfigurationInSolution solutionConfiguration in solution.SolutionConfigurations)
        {
            bool matches = true;
            foreach (KeyValuePair<string, ProjectConfigurationMapping> mapping in mappings)
            {
                if (!projectsByPath.TryGetValue(mapping.Key, out ProjectInSolution? project)
                    || !project.ProjectConfigurations.TryGetValue(solutionConfiguration.FullName, out ProjectConfigurationInSolution? projectConfiguration)
                    || !string.Equals(projectConfiguration.FullName, mapping.Value.ProjectConfiguration, StringComparison.OrdinalIgnoreCase)
                    || projectConfiguration.IncludeInBuild != mapping.Value.IncludeInBuild)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return solutionConfiguration;
            }
        }

        return null;
    }

    private static bool IsTrue(IDictionary<string, string>? properties, string name)
        => properties is not null
            && properties.TryGetValue(name, out string? value)
            && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal readonly record struct ProjectConfigurationMapping(string ProjectConfiguration, bool IncludeInBuild);
}
