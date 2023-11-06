// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotNet.Globbing;
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
        _ = PluginSettings.Create<PluginSettings>(settings, logger, RepoRoot);

        Assert.AreEqual(1, logger.LogEntries.Count);

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

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(settings, NullPluginLogger.Instance, RepoRoot);

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

    private static IEnumerable<object[]> GlobTestData
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

    [TestMethod]
    [DynamicData(nameof(GlobTestData), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void IgnoredInputPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.IgnoredInputPatterns),
            testCase,
            pluginSettings => pluginSettings.IgnoredInputPatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestData), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void IgnoredOutputPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.IgnoredOutputPatterns),
            testCase,
            pluginSettings => pluginSettings.IgnoredOutputPatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestData), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
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
    [DynamicData(nameof(GlobTestData), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void AllowFileAccessAfterProjectFinishProcessPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns),
            testCase,
            pluginSettings => pluginSettings.AllowFileAccessAfterProjectFinishProcessPatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestData), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void AllowFileAccessAfterProjectFinishFilePatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.AllowFileAccessAfterProjectFinishFilePatterns),
            testCase,
            pluginSettings => pluginSettings.AllowFileAccessAfterProjectFinishFilePatterns);

    [TestMethod]
    [DynamicData(nameof(GlobTestData), DynamicDataDisplayName = nameof(GetTestCaseDisplayName))]
    public void AllowProcessCloseAfterProjectFinishProcessPatternsSetting(GlobTestCase testCase)
        => TestGlobListSetting(
            nameof(PluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns),
            testCase,
            pluginSettings => pluginSettings.AllowProcessCloseAfterProjectFinishProcessPatterns);

    [TestMethod]
    [DataRow(null, new string[] { }, null, DisplayName = "Null")]
    [DataRow("", new string[] { }, null, DisplayName = "Empty string")]
    [DataRow("A;B;C", new string[] { "A", "B", "C" }, null, DisplayName = "Basic values")]
    [DataRow(" ; A ;; ;;; B    ;\r\n\r\n;\r\nC;;;  ", new string[] { "A", "B", "C" }, null, DisplayName = "Whitespace and empty values")]
    public void GlobalPropertiesToIgnoreSetting(string? settingValue, string[] expectedValue, object _)
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        if (settingValue != null)
        {
            settings.Add(nameof(PluginSettings.GlobalPropertiesToIgnore), settingValue);
        }

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(settings, NullPluginLogger.Instance, RepoRoot);

        CollectionAssert.AreEqual(expectedValue, pluginSettings.GlobalPropertiesToIgnore.ToList());
    }

    private static void TestBoolSetting(string settingName, Func<PluginSettings, bool> valueAccessor)
        => TestBasicSetting(
            settingName,
            valueAccessor,
            testValues: new[] { false, true });

    private static void TestBasicSetting<T>(
        string settingName,
        Func<PluginSettings, T> valueAccessor,
        T[] testValues)
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

            PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(settings, NullPluginLogger.Instance, RepoRoot);

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

        PluginSettings pluginSettings = PluginSettings.Create<PluginSettings>(settings, NullPluginLogger.Instance, RepoRoot);

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
}
