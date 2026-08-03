// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
#if !NETFRAMEWORK
using System.Diagnostics;
using System.IO;
using System.Security;
#endif
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;
#if !NETFRAMEWORK
using Microsoft.VisualStudio.TestTools.UnitTesting;
#endif

namespace Microsoft.MSBuildCache.Tests;

public sealed class WarningPolicyTestPlugin : ProjectCachePluginBase
{
    internal const string DiagnosticMessage = "Allowlisted late file access diagnostic";

    public override Task BeginBuildAsync(CacheContext context, PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        PluginSettings settings = PluginSettings.Create<PluginSettings>(
            context.PluginSettings,
            logger,
            Environment.CurrentDirectory,
            supportsProbeAndEnumerationCapture: true);
        logger.LogWarningOrMessage(DiagnosticMessage, settings.LogAllowFileAccessAfterProjectFinishMatchesAsMessages);
        return Task.CompletedTask;
    }

    public override Task<CacheResult> GetCacheResultAsync(
        BuildRequestData buildRequest,
        PluginLoggerBase logger,
        CancellationToken cancellationToken)
        => Task.FromResult(CacheResult.IndicateNonCacheHit(CacheResultType.CacheNotApplicable));

    public override Task EndBuildAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

#if !NETFRAMEWORK
[TestClass]
public sealed class WarningPolicyTests
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Justification: Always set by MSTest.
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor.

    [TestMethod]
    public async Task DefaultDiagnosticIsVisibleAsWarning()
    {
        BuildInvocationResult result = await RunBuildAsync(logAsMessage: false, warnAsError: false, emitUnrelatedWarning: false);

        StringAssert.Contains(result.Output, "Build succeeded.", StringComparison.Ordinal);
        StringAssert.Contains(result.Output, WarningPolicyTestPlugin.DiagnosticMessage, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task WarnAsErrorEscalatesDefaultDiagnostic()
    {
        BuildInvocationResult result = await RunBuildAsync(logAsMessage: false, warnAsError: true, emitUnrelatedWarning: false);

        StringAssert.Contains(result.Output, "Build FAILED.", StringComparison.Ordinal);
        StringAssert.Contains(result.Output, WarningPolicyTestPlugin.DiagnosticMessage, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task MessageSettingAvoidsWarnAsErrorEscalation()
    {
        BuildInvocationResult result = await RunBuildAsync(logAsMessage: true, warnAsError: true, emitUnrelatedWarning: false);

        StringAssert.Contains(result.Output, "Build succeeded.", StringComparison.Ordinal);
        StringAssert.Contains(result.Output, WarningPolicyTestPlugin.DiagnosticMessage, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task MessageSettingDoesNotWeakenOtherWarnings()
    {
        BuildInvocationResult result = await RunBuildAsync(logAsMessage: true, warnAsError: true, emitUnrelatedWarning: true);

        StringAssert.Contains(result.Output, "Build FAILED.", StringComparison.Ordinal);
        StringAssert.Contains(result.Output, "Unrelated warning", StringComparison.Ordinal);
    }

    private async Task<BuildInvocationResult> RunBuildAsync(bool logAsMessage, bool warnAsError, bool emitUnrelatedWarning)
    {
        string testDirectory = Path.Combine(
            TestContext.TestResultsDirectory!,
            nameof(WarningPolicyTests),
            TestContext.TestName!,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);

        string pluginPath = SecurityElement.Escape(typeof(WarningPolicyTestPlugin).Assembly.Location)!;
        string commonTargetsPath = SecurityElement.Escape(FindCommonTargetsPath())!;
        string projectPath = Path.Combine(testDirectory, "test.proj");
        string projectContents =
            $"""
            <Project DefaultTargets="Build">
              <PropertyGroup>
                <MSBuildCacheAssembly>{pluginPath}</MSBuildCacheAssembly>
                <MSBuildCacheLogAllowFileAccessAfterProjectFinishMatchesAsMessages>{logAsMessage}</MSBuildCacheLogAllowFileAccessAfterProjectFinishMatchesAsMessages>
              </PropertyGroup>
              <Import Project="{commonTargetsPath}" />
              <Target Name="Build">
                <Warning Condition="'$(EmitUnrelatedWarning)' == 'true'" Code="UNRELATED001" Text="Unrelated warning" />
              </Target>
            </Project>
            """;
        await File.WriteAllTextAsync(projectPath, projectContents);

        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = testDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-graphBuild");
        startInfo.ArgumentList.Add("-maxCpuCount:1");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-verbosity:normal");
        if (warnAsError)
        {
            startInfo.ArgumentList.Add("-warnAsError");
        }

        if (emitUnrelatedWarning)
        {
            startInfo.ArgumentList.Add("-property:EmitUnrelatedWarning=true");
        }

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string output = await standardOutput + await standardError;
        return new BuildInvocationResult(output);
    }

    private static string FindCommonTargetsPath()
    {
        DirectoryInfo? directory = new FileInfo(typeof(WarningPolicyTestPlugin).Assembly.Location).Directory;
        while (directory != null)
        {
            string candidatePath = Path.Combine(directory.FullName, "src", "Common", "build", "Microsoft.MSBuildCache.Common.targets");
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find Microsoft.MSBuildCache.Common.targets.");
    }

    private readonly record struct BuildInvocationResult(string Output);
}
#endif
