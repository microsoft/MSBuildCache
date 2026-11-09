// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.FileSystem;
using Microsoft.Build.Graph;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public sealed class GraphBuildRequestInitializationTests
{
    // B depends on A only through the solution's ProjectDependencies, as in solutions that declare build dependencies between projects.
    private const string Solution = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "A\A.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "B\B.csproj", "{22222222-2222-2222-2222-222222222222}"
        	ProjectSection(ProjectDependencies) = postProject
        		{11111111-1111-1111-1111-111111111111} = {11111111-1111-1111-1111-111111111111}
        	EndProjectSection
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|AnyCPU
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|AnyCPU
        		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|AnyCPU
        		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|AnyCPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|AnyCPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|AnyCPU
        		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|AnyCPU
        		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|AnyCPU
        	EndGlobalSection
        EndGlobal
        """;

    // A project referenced for "Build" gets "GetInfo", like the SDK's GetTargetFrameworks/GetNativeManifest protocol.
    private const string Project = """
        <Project DefaultTargets="Build">
          <ItemGroup>
            <ProjectReferenceTargets Include="Build" Targets="GetInfo" />
          </ItemGroup>
          <Target Name="Build" />
          <Target Name="GetInfo" />
        </Project>
        """;

    // MSBuild's ProjectCacheService drops these from the requesting configuration before deriving entry points.
    private static readonly HashSet<string> ProjectSpecificPropertyNames = new(StringComparer.OrdinalIgnoreCase) { "TargetFramework", "Configuration", "Platform", "TargetPlatform", "OutputType" };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RebuildsTheSolutionGraphWhenTheBuildRequestWinsTheRace()
    {
        string solutionPath = CreateSolution();
        Dictionary<string, string> commandLineProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Configuration"] = "Release",
            ["Platform"] = "Any CPU",
            ["CI_DEPLOYMENT"] = "true",
        };

        // The graph MSBuild builds with.
        ProjectGraph buildGraph = new(new ProjectGraphEntryPoint(solutionPath, commandLineProperties));
        IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> buildTargetLists = buildGraph.GetTargetLists(["Build"]);
        ProjectGraphNode buildA = GetNode(buildGraph, "A");
        CollectionAssert.AreEquivalent(new[] { "Build", "GetInfo" }, buildTargetLists[buildA].ToArray());

        // The race: A's cache query initializes the plugin, with entry points derived from A's request and A's targets.
        CacheContext context = new(
            new Dictionary<string, string>(),
            new NoFileSystem(),
            buildTargetLists[buildA],
            graph: null,
            graphEntryPoints: CreateEntryPointsLikeMSBuild(buildA));
        Assert.IsTrue(GraphBuildRequestInitialization.IsGraphBuildInitializedFromBuildRequest(context));

        MockPluginLogger logger = new();
        (ProjectGraph graph, IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> targetLists) = MSBuildCachePluginBase.GetProjectGraph(context, logger);

        ProjectGraphNode a = GetNode(graph, "A");
        ProjectGraphNode b = GetNode(graph, "B");
        CollectionAssert.Contains(b.ProjectReferences.ToList(), a, "The solution's ProjectDependencies edge is back.");
        CollectionAssert.AreEquivalent(new[] { "Build", "GetInfo" }, targetLists[a].ToArray());
        CollectionAssert.AreEquivalent(new[] { "Build" }, targetLists[b].ToArray());
        CollectionAssert.AreEquivalent(
            buildA.ProjectInstance.GlobalProperties.ToArray(),
            a.ProjectInstance.GlobalProperties.ToArray(),
            "Same global properties as MSBuild's node, so same node ids.");
        Assert.IsTrue(logger.LogEntries.Any(entry => entry.Message.Contains("Rebuilt the graph of", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void KeepsTheEntryPointGraphWhenTheRequestTargetsAreNotTheSolutionBuilds()
    {
        string solutionPath = CreateSolution();
        ProjectGraph buildGraph = new(new ProjectGraphEntryPoint(solutionPath, new Dictionary<string, string> { ["Configuration"] = "Debug" }));
        ProjectGraphNode buildA = GetNode(buildGraph, "A");

        // A build of another target (e.g. -t:GetInfo): the solution graph for "Build" does not explain the request.
        CacheContext context = new(
            new Dictionary<string, string>(),
            new NoFileSystem(),
            ["Unknown"],
            graph: null,
            graphEntryPoints: CreateEntryPointsLikeMSBuild(buildA));

        MockPluginLogger logger = new();
        (ProjectGraph graph, IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> targetLists) = MSBuildCachePluginBase.GetProjectGraph(context, logger);

        Assert.IsEmpty(GetNode(graph, "B").ProjectReferences, "Entry points graph: no solution edge.");
        CollectionAssert.AreEquivalent(new[] { "Unknown" }, targetLists[GetNode(graph, "A")].ToArray());
        Assert.IsTrue(logger.LogEntries.Any(entry => entry.Message.Contains("it is not the graph being built", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void IgnoresEntryPointsOutsideAGraphBuild()
    {
        // Visual Studio: entry points without IsGraphBuild.
        CacheContext context = new(
            new Dictionary<string, string>(),
            new NoFileSystem(),
            ["Build"],
            graph: null,
            graphEntryPoints: [new ProjectGraphEntryPoint(@"X:\Repo\A.csproj", new Dictionary<string, string> { ["Configuration"] = "Debug" })]);

        Assert.IsFalse(GraphBuildRequestInitialization.IsGraphBuildInitializedFromBuildRequest(context));
    }

    [TestMethod]
    public void ReportsEntryPointsThatDoNotComeFromASolution()
    {
        ProjectGraphEntryPoint? entryPoint = GraphBuildRequestInitialization.CreateSolutionEntryPoint(
            [new ProjectGraphEntryPoint(@"X:\Repo\A.csproj", new Dictionary<string, string> { ["IsGraphBuild"] = "true" })],
            out string reason);

        Assert.IsNull(entryPoint);
        Assert.AreEqual("the entry points do not come from a solution", reason);
    }

    private string CreateSolution()
    {
        string root = Path.Combine(TestContext.TestRunDirectory ?? Path.GetTempPath(), nameof(GraphBuildRequestInitializationTests), Guid.NewGuid().ToString("N"));
        foreach (string name in new[] { "A", "B" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, name + ".csproj"), Project);
        }

        string solutionPath = Path.Combine(root, "Test.sln");
        File.WriteAllText(solutionPath, Solution);
        return solutionPath;
    }

    private static ProjectGraphNode GetNode(ProjectGraph graph, string name)
        => graph.ProjectNodes.Single(node => string.Equals(Path.GetFileNameWithoutExtension(node.ProjectInstance.FullPath), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Mirrors ProjectCacheService.GetGraphEntryPoints: one entry point per project of CurrentSolutionConfigurationContents,
    /// with the requesting node's global properties, its project-specific ones replaced by each project's configuration.
    /// </summary>
    private static List<ProjectGraphEntryPoint> CreateEntryPointsLikeMSBuild(ProjectGraphNode requestingNode)
    {
        Dictionary<string, string> template = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> property in requestingNode.ProjectInstance.GlobalProperties)
        {
            if (!ProjectSpecificPropertyNames.Contains(property.Key))
            {
                template.Add(property.Key, property.Value);
            }
        }

        List<ProjectGraphEntryPoint> entryPoints = new();
        foreach (KeyValuePair<string, GraphBuildRequestInitialization.ProjectConfigurationMapping> mapping
            in GraphBuildRequestInitialization.ParseProjectConfigurations(template["CurrentSolutionConfigurationContents"]))
        {
            string[] configurationAndPlatform = mapping.Value.ProjectConfiguration.Split('|');
            Dictionary<string, string> globalProperties = new(template, StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = configurationAndPlatform[0],
                ["Platform"] = configurationAndPlatform[1],
            };
            entryPoints.Add(new ProjectGraphEntryPoint(mapping.Key, globalProperties));
        }

        return entryPoints;
    }

    private sealed class NoFileSystem : MSBuildFileSystemBase
    {
    }
}
