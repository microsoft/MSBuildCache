// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotNet.Globbing;
using Microsoft.Build.Evaluation;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public sealed class PluginSettingsTests
{
    private const string RepoRoot = @"X:\Repo";

    private static readonly PluginSettings DefaultPluginSettings = new() { RepoRoot = RepoRoot };

    [TestMethod]
    public void EffectiveSettingsLogging()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        MockPluginLogger logger = new();

        // Pin the capability so this stays a test of settings logging; without it the probe-and-enumeration
        // clamp would log a second entry whenever the test host's MSBuild predates the required fields.
        _ = PluginSettings.Create<PluginSettings>(settings, logger, RepoRoot, supportsProbeAndEnumerationCapture: true);

        Assert.HasCount(1, logger.LogEntries);

        PluginLogEntry effectiveSettingsLogEntry = logger.LogEntries[0];
        Assert.AreEqual(PluginLogLevel.Message, effectiveSettingsLogEntry.LogLevel);

        // Ensure effective value of all properties are logged and that all properties are correctly defined.
        string effectiveSettingsLogMessage = effectiveSettingsLogEntry.Message;
        foreach (PropertyInfo property in typeof(PluginSettings).GetProperties())
        {
            // All properties are { get; init; }
            Assert.IsTrue(property.CanRead);
            Assert.IsTrue(property.GetSetMethod()!.ReturnParameter.GetRequiredCustomModifiers().Any(t => t.Name.Equals("IsExternalInit", StringComparison.Ordinal)));

            // RepoRoot isn't included in the logging.
            bool isLogged = !property.Name.Equals(nameof(PluginSettings.RepoRoot), StringComparison.Ordinal);
#if NETFRAMEWORK
            Assert.AreEqual(isLogged, effectiveSettingsLogMessage.Contains($"{property.Name}:"));
#else
            Assert.AreEqual(isLogged, effectiveSettingsLogMessage.Contains($"{property.Name}:", StringComparison.Ordinal));
#endif
        }
    }

    [TestMethod]
    [DataRow(null, RepoRoot + @"\MSBuildCacheLogs", DisplayName = "Null")]
    [DataRow("", RepoRoot + @"\MSBuildCacheLogs", DisplayName = "Empty string")]
    [DataRow(@"Logs\Directory", RepoRoot + @"\Logs\Directory", DisplayName = "Relative path")]
    [DataRow(@"X:\Logs", @"X:\Logs", DisplayName = "Absolute path")]
    public void LogDirectorySetting(string? logDirectorySetting, string expectedLogDirectory)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        if (logDirectorySetting != null)
        {
            settings.Add(nameof(PluginSettings.LogDirectory), logDirectorySetting);
        }

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            NullPluginLogger.Instance,
            RepoRoot,
            supportsProbeAndEnumerationCapture: true);

        Assert.AreEqual(expectedLogDirectory, pluginSettings.LogDirectory);
    }

    [TestMethod]
    public void CacheUniverseSetting()
        => TestBasicSetting(
            nameof(PluginSettings.CacheUniverse),
            pluginSettings => pluginSettings.CacheUniverse,
            new[] { "A", "B", "C" });

    [TestMethod]
    public void MaxConcurrentCacheContentOperationsSetting()
        => TestBasicSetting(
            nameof(PluginSettings.MaxConcurrentCacheContentOperations),
            pluginSettings => pluginSettings.MaxConcurrentCacheContentOperations,
            new[] { 123, 456, 789 });

    [TestMethod]
    public void LocalCacheRootPathSetting()
        => TestBasicSetting(
            nameof(PluginSettings.LocalCacheRootPath),
            pluginSettings => pluginSettings.LocalCacheRootPath,
            new[] { @"X:\A", @"X:\B", @"X:\C" });

    [TestMethod]
    public void LocalCacheSizeInMegabytesSetting()
        => TestBasicSetting(
            nameof(PluginSettings.LocalCacheSizeInMegabytes),
            pluginSettings => pluginSettings.LocalCacheSizeInMegabytes,
            new[] { 123u, 456u, 789u });

    // =========================================================================================
    // EnableProbeAndEnumerationFingerprinting capability clamp.
    //
    // Probe and enumeration observations are only sound when the running MSBuild reports the
    // enumeration pattern. On an older MSBuild the setting is forced to false so that the property
    // itself is always the single source of truth — including for the weak fingerprint, which is what
    // keeps caches from being shared between hosts that do and don't capture these observations.
    // =========================================================================================

    [TestMethod]
    [DataRow(true, DisplayName = "explicitly requested")]
    [DataRow(false, DisplayName = "left at default")]
    public void ProbeAndEnumerationFingerprintingForcedOffWithoutCapability(bool requestExplicitly)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        if (requestExplicitly)
        {
            settings[nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting)] = "true";
        }

        MockPluginLogger logger = new();
        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            logger,
            RepoRoot,
            supportsProbeAndEnumerationCapture: false);

        Assert.IsFalse(
            pluginSettings.EnableProbeAndEnumerationFingerprinting,
            "The setting must be forced off when the host MSBuild cannot report the required file access fields, "
            + "even when the user explicitly asked for it.");

        Assert.IsTrue(
            logger.LogEntries.Any(entry => entry.Message.Contains(
                nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting), StringComparison.Ordinal)
                && entry.Message.Contains("forced to false", StringComparison.Ordinal)),
            "Forcing the setting off must be logged so the cache-behavior change is diagnosable.");

        // Naming the running version is what makes the message actionable — otherwise a user is told the
        // feature is off but not what they are on or that upgrading would fix it.
        if (FileAccessDataCapabilities.MSBuildVersion is string msbuildVersion)
        {
            Assert.IsTrue(
                logger.LogEntries.Any(entry => entry.Message.Contains(msbuildVersion, StringComparison.Ordinal)),
                $"The message must name the running MSBuild version ('{msbuildVersion}').");
        }
    }

    [TestMethod]
    public void MSBuildVersionUsesAssemblyInformationalVersion()
    {
        string? expected = typeof(ProjectCollection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.AreEqual(expected, FileAccessDataCapabilities.MSBuildVersion);
    }

    [TestMethod]
    public void ProbeAndEnumerationFingerprintingHonoredWithCapability()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        MockPluginLogger logger = new();
        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            logger,
            RepoRoot,
            supportsProbeAndEnumerationCapture: true);

        Assert.IsTrue(pluginSettings.EnableProbeAndEnumerationFingerprinting);
    }

    /// <summary>
    /// The clamp must not resurrect the feature for a user who explicitly turned it off on a capable host.
    /// </summary>
    [TestMethod]
    public void ProbeAndEnumerationFingerprintingRespectsExplicitOptOutWithCapability()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(PluginSettings.EnableProbeAndEnumerationFingerprinting)] = "false",
        };

        MockPluginLogger logger = new();
        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            logger,
            RepoRoot,
            supportsProbeAndEnumerationCapture: true);

        Assert.IsFalse(pluginSettings.EnableProbeAndEnumerationFingerprinting);
    }

    [TestMethod]
    [DynamicData(nameof(GlobTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void IgnoredInputPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.IgnoredInputPatterns),
            testCase,
            pluginSettings => pluginSettings.IgnoredInputPatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void IgnoredOutputPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.IgnoredOutputPatterns),
            testCase,
            pluginSettings => pluginSettings.IgnoredOutputPatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void IdenticalDuplicateOutputPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.IdenticalDuplicateOutputPatterns),
            testCase,
            pluginSettings => pluginSettings.IdenticalDuplicateOutputPatterns);

    [TestMethod]
    public void RemoteCacheIsReadOnlySetting()
        => TestBoolSetting(nameof(PluginSettings.RemoteCacheIsReadOnly), pluginSettings => pluginSettings.RemoteCacheIsReadOnly);

    [TestMethod]
    public void AsyncCachePublishingSetting()
        => TestBoolSetting(nameof(PluginSettings.AsyncCachePublishing), pluginSettings => pluginSettings.AsyncCachePublishing);

    [TestMethod]
    public void AsyncCacheMaterializationSetting()
        => TestBoolSetting(nameof(PluginSettings.AsyncCacheMaterialization), pluginSettings => pluginSettings.AsyncCacheMaterialization);

    [TestMethod]
    [DynamicData(nameof(GlobTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void AllowFileAccessAfterProjectFinishProcessPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns),
            testCase,
            pluginSettings => pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void AllowFileAccessAfterProjectFinishFilePatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.AllowFileAccessAfterProjectFinishFilePatterns),
            testCase,
            pluginSettings => pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns);

    [TestMethod]
    public void AllowFileAccessAfterProjectFinishFilePatternsSupportsMachineLocalCategories()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(PluginSettings.AllowFileAccessAfterProjectFinishFilePatterns)] =
                @"\**\ApplicationInsights.config;" +
                @"C:\Users\Test\AppData\Local\Microsoft\VSApplicationInsights\**;" +
                @"C:\Users\Test\AppData\Local\Microsoft\Windows\INetCache\**;" +
                @"C:\Windows\**",
        };

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            NullPluginLogger.Instance,
            RepoRoot,
            supportsProbeAndEnumerationCapture: true);

        IReadOnlyCollection<Glob> patterns = pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns;
        Assert.IsTrue(patterns.Any(pattern => pattern.IsMatch(@"C:\Program Files\Telemetry\ApplicationInsights.config")));
        Assert.IsTrue(patterns.Any(pattern => pattern.IsMatch(@"C:\Users\Test\AppData\Local\Microsoft\VSApplicationInsights\config.json")));
        Assert.IsTrue(patterns.Any(pattern => pattern.IsMatch(@"C:\Users\Test\AppData\Local\Microsoft\Windows\INetCache\IE\ABC\dyntelconfig[2].cache")));
        Assert.IsTrue(patterns.Any(pattern => pattern.IsMatch(@"C:\Windows\System32\ci.dll")));
        Assert.IsFalse(patterns.Any(pattern => pattern.IsMatch(@"X:\Repo\src\Program.cs")));
    }

    [TestMethod]
    [DynamicData(nameof(GlobTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void AllowProcessCloseAfterProjectFinishProcessPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns),
            testCase,
            pluginSettings => pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns);

    [TestMethod]
    [DynamicData(nameof(StringListTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void GlobalPropertiesToIgnoreSetting(StringListTestCase testCase)
        => TestStringListSetting(nameof(PluginSettings.GlobalPropertiesToIgnore), testCase, pluginSettings => pluginSettings.GlobalPropertiesToIgnore);

    [TestMethod]
    public void GetResultsForUnqueriedDependenciesSetting()
        => TestBoolSetting(nameof(PluginSettings.GetResultsForUnqueriedDependencies), pluginSettings => pluginSettings.GetResultsForUnqueriedDependencies);

    [TestMethod]
    [DynamicData(nameof(StringListTestCases), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void TargetsToIgnoreSetting(StringListTestCase testCase)
        => TestStringListSetting(nameof(PluginSettings.TargetsToIgnore), testCase, pluginSettings => pluginSettings.TargetsToIgnore);

    [TestMethod]
    public void IgnoreDotNetSdkPatchVersionSetting()
        => TestBoolSetting(nameof(PluginSettings.IgnoreDotNetSdkPatchVersion), pluginSettings => pluginSettings.IgnoreDotNetSdkPatchVersion);

    private static void TestBoolSetting(string settingName, Func<PluginSettings, bool> valueAccessor)
        => TestBasicSetting(
            settingName,
            valueAccessor,
            testValues: [false, true]);

    private static void TestBasicSetting<T>(
        string settingName,
        Func<PluginSettings, T> valueAccessor,
        ReadOnlySpan<T> testValues)
    {
        T defaultValue = valueAccessor(DefaultPluginSettings);

        TestBasicSettingValue(null, defaultValue);
        TestBasicSettingValue(string.Empty, defaultValue);
        TestBasicSettingValue(defaultValue?.ToString(), defaultValue);

        foreach (T testValue in testValues)
        {
            TestBasicSettingValue(testValue?.ToString(), testValue);
        }

        void TestBasicSettingValue(string? settingValue, T expectedValue)
        {
            Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
            if (settingValue != null)
            {
                settings.Add(settingName, settingValue);
            }

            PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
                settings,
                NullPluginLogger.Instance,
                RepoRoot,
                supportsProbeAndEnumerationCapture: true);

            Assert.AreEqual(expectedValue, valueAccessor(pluginSettings));
        }
    }

    private static void TestGlobListSetting(
        string settingName,
        GlobTestCase testCase,
        Func<PluginSettings, IReadOnlyCollection<Glob>> valueAccessor)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            { settingName, testCase.Glob },
        };

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            NullPluginLogger.Instance,
            RepoRoot,
            supportsProbeAndEnumerationCapture: true);

        foreach (string path in testCase.ExpectedMatching)
        {
            Assert.IsTrue(MatchesGlobs(path), $"Path did not match any patterns: {path}");
        }

        foreach (string path in testCase.ExpectedNotMatching)
        {
            Assert.IsFalse(MatchesGlobs(path), $"Path matched pattern unexpectedly: {path}");
        }

        bool MatchesGlobs(string path)
        {
            foreach (Glob glob in valueAccessor(pluginSettings))
            {
                if (glob.IsMatch(path))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static void TestStringListSetting(
        string settingName,
        StringListTestCase testCase,
        Func<PluginSettings, IReadOnlyCollection<string>> valueAccessor)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        if (testCase.SettingValue != null)
        {
            settings.Add(settingName, testCase.SettingValue);
        }

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(
            settings,
            NullPluginLogger.Instance,
            RepoRoot,
            supportsProbeAndEnumerationCapture: true);

        CollectionAssert.AreEqual(testCase.ExpectedValues.ToList(), valueAccessor(pluginSettings).ToList());
    }

    public static IEnumerable<object[]> GlobTestCases
    {
        get
        {
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "File pattern",
                    Glob = "*.txt",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\foo\bar.txt",
                        $@"{RepoRoot}\foo\bar\baz.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.xml",
                        $@"{RepoRoot}\foo\bar.xml",
                        $@"{RepoRoot}\foo\bar\baz.xml",
                    },
                }
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Relative subdir pattern",
                    Glob = @"a\b\c\*.txt",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo.xml",
                        $@"{RepoRoot}\a\b\c\foo\bar.txt",
                    },
                }
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Absolute subdir pattern",
                    Glob = $@"{RepoRoot}\a\b\c\*.txt",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo.xml",
                        $@"{RepoRoot}\a\b\c\foo\bar.txt",
                    },
                }
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Relative subdir recursive pattern",
                    Glob = @"a\b\c\**\*.txt",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar\baz.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo.xml",
                    },
                }
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Absolute subdir recursive pattern",
                    Glob = $@"{RepoRoot}\a\b\c\**\*.txt",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar\baz.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo.xml",
                    },
                }
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Relative subdir open recursive pattern",
                    Glob = @"a\b\c\**",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar\baz.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\x\a\b\c\foo.txt",
                    },
                }
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Absolute subdir open recursive pattern",
                    Glob = $@"{RepoRoot}\a\b\c\**",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar.txt",
                        $@"{RepoRoot}\a\b\c\foo\bar\baz.txt",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\foo.txt",
                        $@"{RepoRoot}\x\a\b\c\foo.txt",
                    }
                },
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Whitespace and empty values",
                    Glob = "  ; *.a  ;; *.b;  ;*.c;;;",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\b\c\foo.a",
                        $@"{RepoRoot}\a\b\c\foo\bar.b",
                        $@"{RepoRoot}\a\b\c\foo\bar\baz.c",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\x.txt",
                        $@"{RepoRoot}\x\a\b\c\y.txt",
                    }
                },
            };
            yield return new object[]
            {
                new GlobTestCase
                {
                    DisplayName = "Absolute path outside of repo",
                    Glob = @"\**\vctip.exe",
                    ExpectedMatching = new[]
                    {
                        $@"{RepoRoot}\a\vctip.exe",
                        $@"C:\Program Files\vctip.exe",
                        $@"Z:\Program Files\vctip.exe",
                    },
                    ExpectedNotMatching = new[]
                    {
                        $@"{RepoRoot}\x.txt",
                        $@"{RepoRoot}\x\a\b\c\y.txt",
                        $@"C:\Program Files\x.txt",
                        $@"Z:\Program Files\x.txt",
                    }
                },
            };
        }
    }

    public static IEnumerable<object[]> StringListTestCases
    {
        get
        {
            yield return new object[]
            {
                new StringListTestCase
                {
                    DisplayName = "Null",
                    SettingValue = null,
                    ExpectedValues = [],
                }
            };
            yield return new object[]
            {
                new StringListTestCase
                {
                    DisplayName = "Empty string",
                    SettingValue = string.Empty,
                    ExpectedValues = [],
                }
            };
            yield return new object[]
            {
                new StringListTestCase
                {
                    DisplayName = "Basic values",
                    SettingValue = "A;B;C",
                    ExpectedValues = [ "A", "B", "C" ],
                }
            };
            yield return new object[]
            {
                new StringListTestCase
                {
                    DisplayName = "Whitespace and empty values",
                    SettingValue = " ; A ;; ;;; B    ;\r\n\r\n;\r\nC;;;  ",
                    ExpectedValues = [ "A", "B", "C" ],
                }
            };
        }
    }

#pragma warning disable IDE0060 // Remove unused parameter
    public static string GetTestCaseDisplayName(MethodInfo methodInfo, object[] data) => ((TestCaseBase)data[0]).DisplayName;
#pragma warning restore IDE0060 // Remove unused parameter

    public abstract class TestCaseBase
    {
        public required string DisplayName { get; init; }
    }

    public sealed class GlobTestCase : TestCaseBase
    {
        public required string Glob { get; init; }

        public required IReadOnlyList<string> ExpectedMatching { get; init; }

        public required IReadOnlyList<string> ExpectedNotMatching { get; init; }
    }

    public sealed class StringListTestCase : TestCaseBase
    {
        public required string? SettingValue { get; init; }

        public required IReadOnlyList<string> ExpectedValues { get; init; }
    }
}
