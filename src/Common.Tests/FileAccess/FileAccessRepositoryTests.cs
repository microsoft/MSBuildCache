// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.FileAccess;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.FileAccess;

[TestClass]
public sealed class FileAccessRepositoryTests
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Justification: Always set by MSTest.
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor.

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FilePatternMatchUsesConfiguredLogLevel(bool logAsMessage)
    {
        string testDirectory = CreateTestDirectory();
        string lateAccessPath = Path.Combine(testDirectory, "late-access.txt");
        PluginSettings settings = CreateSettings(
            testDirectory,
            new Dictionary<string, string>
            {
                [nameof(PluginSettings.AllowFileAccessAfterProjectFinishFilePatterns)] = lateAccessPath,
                [nameof(PluginSettings.LogAllowFileAccessAfterProjectFinishMatchesAsMessages)] = logAsMessage.ToString(),
            });

        MockPluginLogger logger = new();
        using FileAccessRepository repository = new(logger, settings);
        NodeContext nodeContext = CreateNodeContext(testDirectory);

        _ = repository.FinishProject(nodeContext);
        repository.AddFileAccess(nodeContext, CreateFileAccess(lateAccessPath));

        Assert.HasCount(1, logger.LogEntries);
        Assert.AreEqual(logAsMessage ? PluginLogLevel.Message : PluginLogLevel.Warning, logger.LogEntries[0].LogLevel);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProcessPatternMatchUsesConfiguredLogLevel(bool logAsMessage)
    {
        string testDirectory = CreateTestDirectory();
        string processPath = Path.Combine(testDirectory, "detached.exe");
        PluginSettings settings = CreateSettings(
            testDirectory,
            new Dictionary<string, string>
            {
                [nameof(PluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns)] = processPath,
                [nameof(PluginSettings.LogAllowFileAccessAfterProjectFinishMatchesAsMessages)] = logAsMessage.ToString(),
            });

        MockPluginLogger logger = new();
        using FileAccessRepository repository = new(logger, settings);
        NodeContext nodeContext = CreateNodeContext(testDirectory);

        repository.AddFileAccess(nodeContext, CreateProcessAccess(processPath));
        _ = repository.FinishProject(nodeContext);
        repository.AddFileAccess(nodeContext, CreateFileAccess(Path.Combine(testDirectory, "other.txt")));

        Assert.HasCount(1, logger.LogEntries);
        Assert.AreEqual(logAsMessage ? PluginLogLevel.Message : PluginLogLevel.Warning, logger.LogEntries[0].LogLevel);
    }

    private string CreateTestDirectory()
    {
        string testDirectory = Path.Combine(
            TestContext.TestResultsDirectory!,
            nameof(FileAccessRepositoryTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }

    private static PluginSettings CreateSettings(string repoRoot, Dictionary<string, string> settings)
        => PluginSettings.Create<PluginSettings>(
            settings,
            NullPluginLogger.Instance,
            repoRoot,
            supportsProbeAndEnumerationCapture: true);

    private static NodeContext CreateNodeContext(string testDirectory)
    {
        string projectPath = Path.Combine(testDirectory, "p.proj");
        File.WriteAllText(projectPath, "<Project />");
        ProjectInstance projectInstance = new(projectPath);

        return new NodeContext(
            testDirectory,
            projectInstance,
            Array.Empty<NodeContext>(),
            "p",
            new Dictionary<string, string>(),
            Array.Empty<string>(),
            referenceAssemblyRelativePath: null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static FileAccessData CreateFileAccess(string path)
        => new(
            ReportedFileOperation.CreateFile,
            RequestedAccess.Read,
            processId: 123,
            id: 0,
            correlationId: 0,
            error: 1,
            DesiredAccess.GENERIC_READ,
            FlagsAndAttributes.FILE_ATTRIBUTE_NORMAL,
            path,
            processArgs: null,
            isAnAugmentedFileAccess: false);

    private static FileAccessData CreateProcessAccess(string path)
        => new(
            ReportedFileOperation.Process,
            RequestedAccess.None,
            processId: 123,
            id: 0,
            correlationId: 0,
            error: 0,
            desiredAccess: 0,
            flagsAndAttributes: 0,
            path,
            processArgs: null,
            isAnAugmentedFileAccess: false);
}
