using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class NodeTargetResultTaskItemTests
{
    private const string RepoRoot = @"X:\Repo";

    private const string NugetPackageRoot = @"X:\Nuget";

    private static readonly PathNormalizer PathNormalizer = new(RepoRoot, NugetPackageRoot);

    [TestMethod]
    public void FromTaskItem()
    {
        TaskItem taskItem = new(
            RepoRoot + @"\src\HelloWorld\bin\x64\Release\HelloWorld.dll",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "TargetFrameworkIdentifier", ".NETStandard" },
                { "TargetPlatformMoniker", "Windows,Version=7.0" },
                { "CopyUpToDateMarker", RepoRoot + @"\src\HelloWorld\bin\x64\Release\HelloWorld.csproj.CopyComplete" },
                { "TargetPlatformIdentifier", "Windows" },
                { "TargetFrameworkVersion", "2.0" },
                { "ReferenceAssembly", RepoRoot + @"\src\HelloWorld\bin\x64\Release\ref\HelloWorld.dll" },
            });

        NodeTargetResultTaskItem nodeTargetResultTaskItem = NodeTargetResultTaskItem.FromTaskItem(taskItem, PathNormalizer);

        Assert.IsNotNull(nodeTargetResultTaskItem);
        Assert.AreEqual(@"{RepoRoot}src\HelloWorld\bin\x64\Release\HelloWorld.dll", nodeTargetResultTaskItem.ItemSpec);
        Assert.HasCount(taskItem.CloneCustomMetadata().Count, nodeTargetResultTaskItem.Metadata);
        Assert.AreEqual(".NETStandard", nodeTargetResultTaskItem.Metadata["TargetFrameworkIdentifier"]);
        Assert.AreEqual("Windows,Version=7.0", nodeTargetResultTaskItem.Metadata["TargetPlatformMoniker"]);
        Assert.AreEqual(@"{RepoRoot}src\HelloWorld\bin\x64\Release\HelloWorld.csproj.CopyComplete", nodeTargetResultTaskItem.Metadata["CopyUpToDateMarker"]);
        Assert.AreEqual("Windows", nodeTargetResultTaskItem.Metadata["TargetPlatformIdentifier"]);
        Assert.AreEqual("2.0", nodeTargetResultTaskItem.Metadata["TargetFrameworkVersion"]);
        Assert.AreEqual(@"{RepoRoot}src\HelloWorld\bin\x64\Release\ref\HelloWorld.dll", nodeTargetResultTaskItem.Metadata["ReferenceAssembly"]);
    }

    [TestMethod]
    public void ToTaskItem()
    {
        NodeTargetResultTaskItem nodeTargetResultTaskItem = new(
            @"{RepoRoot}src\HelloWorld\bin\x64\Release\HelloWorld.dll",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "TargetFrameworkIdentifier", ".NETStandard" },
                { "TargetPlatformMoniker", "Windows,Version=7.0" },
                { "CopyUpToDateMarker", @"{RepoRoot}src\HelloWorld\bin\x64\Release\HelloWorld.csproj.CopyComplete" },
                { "TargetPlatformIdentifier", "Windows" },
                { "TargetFrameworkVersion", "2.0" },
                { "ReferenceAssembly", @"{RepoRoot}src\HelloWorld\bin\x64\Release\ref\HelloWorld.dll" },
            });

        ITaskItem2 taskItem = nodeTargetResultTaskItem.ToTaskItem(PathNormalizer);

        Assert.IsNotNull(taskItem);
        Assert.AreEqual(RepoRoot + @"\src\HelloWorld\bin\x64\Release\HelloWorld.dll", taskItem.ItemSpec);
        Assert.HasCount(nodeTargetResultTaskItem.Metadata.Count, taskItem.CloneCustomMetadata());
        Assert.AreEqual(".NETStandard", taskItem.GetMetadata("TargetFrameworkIdentifier"));
        Assert.AreEqual("Windows,Version=7.0", taskItem.GetMetadata("TargetPlatformMoniker"));
        Assert.AreEqual(RepoRoot + @"\src\HelloWorld\bin\x64\Release\HelloWorld.csproj.CopyComplete", taskItem.GetMetadata("CopyUpToDateMarker"));
        Assert.AreEqual("Windows", taskItem.GetMetadata("TargetPlatformIdentifier"));
        Assert.AreEqual("2.0", taskItem.GetMetadata("TargetFrameworkVersion"));
        Assert.AreEqual(RepoRoot + @"\src\HelloWorld\bin\x64\Release\ref\HelloWorld.dll", taskItem.GetMetadata("ReferenceAssembly"));
    }
}
