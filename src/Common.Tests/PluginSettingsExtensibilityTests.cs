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
public class PluginSettingsExtensibilityTests
{
    private const string RepoRoot = @"X:\Repo";

    private static readonly MockPluginSettings DefaultMockPluginSettings = new() { RepoRoot = RepoRoot };

    [TestMethod]
    public void EffectiveSettingsLogging()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        MockPluginLogger logger = new();
        _ = PluginSettings.Create<MockPluginSettings>(settings, logger, RepoRoot);

        Assert.HasCount(1, logger.LogEntries);

        // Ensure effective value of all properties are logged and that all properties are correctly defined.
        foreach (PropertyInfo property in typeof(MockPluginSettings).GetProperties())
        {
            // All properties are { get; init; }
            Assert.IsTrue(property.CanRead);
            Assert.IsTrue(property.GetSetMethod()!.ReturnParameter.GetRequiredCustomModifiers().Any(t => t.Name.Equals("IsExternalInit", StringComparison.Ordinal)));

            // RepoRoot isn't included in the logging.
            bool shouldBeLogged = !property.Name.Equals(nameof(PluginSettings.RepoRoot), StringComparison.Ordinal);
            if (shouldBeLogged)
            {
                AssertLogged(logger, PluginLogLevel.Message, $"{property.Name}:");
            }
            else
            {
                AssertNotLogged(logger, PluginLogLevel.Message, $"{property.Name}:");
            }
        }
    }

    [TestMethod]
    public void DefaultValue()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase);
        MockPluginLogger logger = new();
        MockPluginSettings pluginSettings = PluginSettings.Create<MockPluginSettings>(settings, logger, RepoRoot);

        Assert.AreEqual(DefaultMockPluginSettings.StringSetting, pluginSettings.StringSetting);

        Assert.AreEqual(DefaultMockPluginSettings.EnumSetting, pluginSettings.EnumSetting);

        Assert.AreEqual(DefaultMockPluginSettings.BoolSetting, pluginSettings.BoolSetting);
        Assert.AreEqual(DefaultMockPluginSettings.ByteSetting, pluginSettings.ByteSetting);
        Assert.AreEqual(DefaultMockPluginSettings.SbyteSetting, pluginSettings.SbyteSetting);
        Assert.AreEqual(DefaultMockPluginSettings.CharSetting, pluginSettings.CharSetting);
        Assert.AreEqual(DefaultMockPluginSettings.IntSetting, pluginSettings.IntSetting);
        Assert.AreEqual(DefaultMockPluginSettings.UintSetting, pluginSettings.UintSetting);
        Assert.AreEqual(DefaultMockPluginSettings.ShortSetting, pluginSettings.ShortSetting);
        Assert.AreEqual(DefaultMockPluginSettings.UshortSetting, pluginSettings.UshortSetting);
        Assert.AreEqual(DefaultMockPluginSettings.LongSetting, pluginSettings.LongSetting);
        Assert.AreEqual(DefaultMockPluginSettings.FloatSetting, pluginSettings.FloatSetting);
        Assert.AreEqual(DefaultMockPluginSettings.DoubleSetting, pluginSettings.DoubleSetting);
        Assert.AreEqual(DefaultMockPluginSettings.DecimalSetting, pluginSettings.DecimalSetting);

        Assert.AreEqual(DefaultMockPluginSettings.GlobSetting.ToString(), pluginSettings.GlobSetting.ToString());

        CollectionAssert.AreEqual(DefaultMockPluginSettings.ArraySetting, pluginSettings.ArraySetting);

        CollectionAssert.AreEqual(DefaultMockPluginSettings.ListSetting, pluginSettings.ListSetting);
        CollectionAssert.AreEqual(DefaultMockPluginSettings.IListSetting.ToList(), pluginSettings.IListSetting.ToList());
        CollectionAssert.AreEqual(DefaultMockPluginSettings.ICollectionSetting.ToList(), pluginSettings.ICollectionSetting.ToList());
        CollectionAssert.AreEqual(DefaultMockPluginSettings.IEnumerableSetting.ToList(), pluginSettings.IEnumerableSetting.ToList());
        CollectionAssert.AreEqual(DefaultMockPluginSettings.IReadOnlyListSetting.ToList(), pluginSettings.IReadOnlyListSetting.ToList());
        CollectionAssert.AreEqual(DefaultMockPluginSettings.IReadOnlyCollectionSetting.ToList(), pluginSettings.IReadOnlyCollectionSetting.ToList());

        CollectionAssert.AreEquivalent(DefaultMockPluginSettings.HashSetSetting.ToList(), pluginSettings.HashSetSetting.ToList());
        CollectionAssert.AreEquivalent(DefaultMockPluginSettings.ISetSetting.ToList(), pluginSettings.ISetSetting.ToList());

        AssertNotLogged(logger, PluginLogLevel.Warning, "has invalid value");
        AssertNotLogged(logger, PluginLogLevel.Warning, "has unsupported type");
    }

    [TestMethod]
    public void InvalidValues()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            { nameof(DefaultMockPluginSettings.EnumSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.BoolSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.ByteSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.SbyteSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.CharSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.IntSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.UintSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.ShortSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.UshortSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.LongSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.FloatSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.DoubleSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.DecimalSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.GlobSetting), "<<< InvalidValue >>>" },
            { nameof(DefaultMockPluginSettings.ArraySetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.ListSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.IListSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.ICollectionSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.IEnumerableSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.IReadOnlyListSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.IReadOnlyCollectionSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.HashSetSetting), "InvalidValue" },
            { nameof(DefaultMockPluginSettings.ISetSetting), "InvalidValue" },
        };
        MockPluginLogger logger = new();
        MockPluginSettings pluginSettings = PluginSettings.Create<MockPluginSettings>(settings, logger, RepoRoot);

        AssertInvalidValueHandled(nameof(MockPluginSettings.EnumSetting), pluginSettings => pluginSettings.EnumSetting);

        AssertInvalidValueHandled(nameof(MockPluginSettings.BoolSetting), pluginSettings => pluginSettings.BoolSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.ByteSetting), pluginSettings => pluginSettings.ByteSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.SbyteSetting), pluginSettings => pluginSettings.SbyteSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.CharSetting), pluginSettings => pluginSettings.CharSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.IntSetting), pluginSettings => pluginSettings.IntSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.UintSetting), pluginSettings => pluginSettings.UintSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.ShortSetting), pluginSettings => pluginSettings.ShortSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.UshortSetting), pluginSettings => pluginSettings.UshortSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.LongSetting), pluginSettings => pluginSettings.LongSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.FloatSetting), pluginSettings => pluginSettings.FloatSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.DoubleSetting), pluginSettings => pluginSettings.DoubleSetting);
        AssertInvalidValueHandled(nameof(MockPluginSettings.DecimalSetting), pluginSettings => pluginSettings.DecimalSetting);

        AssertInvalidValueHandled(nameof(MockPluginSettings.GlobSetting), pluginSettings => pluginSettings.GlobSetting.ToString());

        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.ArraySetting), pluginSettings => pluginSettings.ArraySetting);

        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.ListSetting), pluginSettings => pluginSettings.ListSetting);
        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.IListSetting), pluginSettings => pluginSettings.IListSetting.ToList());
        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.ICollectionSetting), pluginSettings => pluginSettings.ICollectionSetting.ToList());
        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.IEnumerableSetting), pluginSettings => pluginSettings.IEnumerableSetting.ToList());
        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.IReadOnlyListSetting), pluginSettings => pluginSettings.IReadOnlyListSetting.ToList());
        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.IReadOnlyCollectionSetting), pluginSettings => pluginSettings.IReadOnlyCollectionSetting.ToList());

        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.HashSetSetting), pluginSettings => pluginSettings.HashSetSetting.ToList());
        CollectionAssertInvalidValueHandled(nameof(MockPluginSettings.ISetSetting), pluginSettings => pluginSettings.ISetSetting.ToList());

        AssertNotLogged(logger, PluginLogLevel.Warning, "has unsupported type");

        void AssertInvalidValueHandled<T>(string settingName, Func<MockPluginSettings, T> valueAccessor)
        {
            AssertLogged(logger, PluginLogLevel.Warning, $"'{settingName}' has invalid value");
            Assert.AreEqual(valueAccessor(DefaultMockPluginSettings), valueAccessor(pluginSettings));
        }

        void CollectionAssertInvalidValueHandled<T>(string settingName, Func<MockPluginSettings, ICollection<T>> valueAccessor)
        {
            AssertLogged(logger, PluginLogLevel.Warning, $"'{settingName}' has invalid value");
            CollectionAssert.AreEqual(valueAccessor(DefaultMockPluginSettings).ToList(), valueAccessor(pluginSettings).ToList());
        }
    }

    [TestMethod]
    public void ExplicitValues()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            { nameof(DefaultMockPluginSettings.StringSetting), "B" },
            { nameof(DefaultMockPluginSettings.EnumSetting), nameof(MockEnum.C) },
            { nameof(DefaultMockPluginSettings.BoolSetting), bool.FalseString },
            { nameof(DefaultMockPluginSettings.ByteSetting), "2" },
            { nameof(DefaultMockPluginSettings.SbyteSetting), "2" },
            { nameof(DefaultMockPluginSettings.CharSetting), "B" },
            { nameof(DefaultMockPluginSettings.IntSetting), "2" },
            { nameof(DefaultMockPluginSettings.UintSetting), "2" },
            { nameof(DefaultMockPluginSettings.ShortSetting), "2" },
            { nameof(DefaultMockPluginSettings.UshortSetting), "2" },
            { nameof(DefaultMockPluginSettings.LongSetting), "2" },
            { nameof(DefaultMockPluginSettings.FloatSetting), "2.0" },
            { nameof(DefaultMockPluginSettings.DoubleSetting), "2.0" },
            { nameof(DefaultMockPluginSettings.DecimalSetting), "2.0" },
            { nameof(DefaultMockPluginSettings.GlobSetting), "b.*" },
            { nameof(DefaultMockPluginSettings.ArraySetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.ListSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.IListSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.ICollectionSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.IEnumerableSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.IReadOnlyListSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.IReadOnlyCollectionSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.HashSetSetting), "4; 5; 6" },
            { nameof(DefaultMockPluginSettings.ISetSetting), "4; 5; 6" },
        };
        MockPluginLogger logger = new();
        MockPluginSettings pluginSettings = PluginSettings.Create<MockPluginSettings>(settings, logger, RepoRoot);

        Assert.AreEqual("B", pluginSettings.StringSetting);

        Assert.AreEqual(MockEnum.C, pluginSettings.EnumSetting);

        Assert.IsFalse(pluginSettings.BoolSetting);
        Assert.AreEqual((byte)2, pluginSettings.ByteSetting);
        Assert.AreEqual((sbyte)2, pluginSettings.SbyteSetting);
        Assert.AreEqual('B', pluginSettings.CharSetting);
        Assert.AreEqual(2, pluginSettings.IntSetting);
        Assert.AreEqual(2u, pluginSettings.UintSetting);
        Assert.AreEqual((short)2, pluginSettings.ShortSetting);
        Assert.AreEqual((ushort)2, pluginSettings.UshortSetting);
        Assert.AreEqual(2L, pluginSettings.LongSetting);
        Assert.AreEqual(2.0f, pluginSettings.FloatSetting);
        Assert.AreEqual(2.0d, pluginSettings.DoubleSetting);
        Assert.AreEqual(2.0M, pluginSettings.DecimalSetting);

        Assert.AreEqual(@"X:\Repo\**\b.*", pluginSettings.GlobSetting.ToString());

        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.ArraySetting);

        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.ListSetting);
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.IListSetting.ToList());
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.ICollectionSetting.ToList());
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.IEnumerableSetting.ToList());
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.IReadOnlyListSetting.ToList());
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, pluginSettings.IReadOnlyCollectionSetting.ToList());

        CollectionAssert.AreEquivalent(new[] { 4, 5, 6 }, pluginSettings.HashSetSetting.ToList());
        CollectionAssert.AreEquivalent(new[] { 4, 5, 6 }, pluginSettings.ISetSetting.ToList());

        AssertNotLogged(logger, PluginLogLevel.Warning, "has invalid value");
        AssertNotLogged(logger, PluginLogLevel.Warning, "has unsupported type");
    }

    [TestMethod]
    public void UnsupportedTypeSetting()
    {
        Dictionary<string, string> settings = new(StringComparer.OrdinalIgnoreCase)
        {
            { nameof(MockPluginSettingsWithUnsupportedType.UnsupportedTypeSetting), "Baz" },
        };
        MockPluginLogger logger = new();
        MockPluginSettingsWithUnsupportedType pluginSettings = PluginSettings.Create<MockPluginSettingsWithUnsupportedType>(settings, logger, RepoRoot);

        AssertNotLogged(logger, PluginLogLevel.Warning, "has invalid value");
        AssertLogged(logger, PluginLogLevel.Warning, "has unsupported type");

        // Default value is preserved
        Assert.AreEqual(UnsupportedType.Default.Foo, pluginSettings.UnsupportedTypeSetting.Foo);
    }

    private static void AssertLogged(MockPluginLogger logger, PluginLogLevel logLevel, string partialMessage)
        => Assert.IsTrue(IsLogged(logger, logLevel, partialMessage), $"Did not find expected log message. LogLevel=[{logLevel}]; Message=[{partialMessage}]");

    private static void AssertNotLogged(MockPluginLogger logger, PluginLogLevel logLevel, string partialMessage)
        => Assert.IsFalse(IsLogged(logger, logLevel, partialMessage), $"Found unexpected log message. LogLevel=[{logLevel}]; Message=[{partialMessage}]");

    private static bool IsLogged(MockPluginLogger logger, PluginLogLevel logLevel, string partialMessage)
        =>
#if NETFRAMEWORK
            logger.LogEntries.Any(entry => entry.LogLevel == logLevel && entry.Message.Contains(partialMessage));
#else
            logger.LogEntries.Any(entry => entry.LogLevel == logLevel && entry.Message.Contains(partialMessage, StringComparison.Ordinal));
#endif

    private enum MockEnum { A, B, C }

    /*
     * Ensure the following!
     * 1. This has all supported setting types.
     * 2. The default values for the setting are not the default values for the type (recommended, use "1" equivalent)
     * 3. When possible, tests above should use a 3rd value (recommended, use "2" equivalent)
     */
    private sealed class MockPluginSettings : PluginSettings
    {
        public string? StringSetting { get; init; } = "A";

        public MockEnum EnumSetting { get; init; } = MockEnum.B;

        public bool BoolSetting { get; init; } = true;

        public byte ByteSetting { get; init; } = 1;

        public sbyte SbyteSetting { get; init; } = 1;

        public char CharSetting { get; init; } = 'A';

        public int IntSetting { get; init; } = 1;

        public uint UintSetting { get; init; } = 1u;

        public short ShortSetting { get; init; } = 1;

        public ushort UshortSetting { get; init; } = 1;

        public long LongSetting { get; init; } = 1L;

        public float FloatSetting { get; init; } = 1.0f;

        public double DoubleSetting { get; init; } = 1.0d;

        public decimal DecimalSetting { get; init; } = 1.0M;

        public Glob GlobSetting { get; init; } = Glob.Parse("a.*");

        public int[] ArraySetting { get; init; } = [1, 2, 3];

        public List<int> ListSetting { get; init; } = [1, 2, 3];

        public IList<int> IListSetting { get; init; } = [1, 2, 3];

        public ICollection<int> ICollectionSetting { get; init; } = [1, 2, 3];

        public IEnumerable<int> IEnumerableSetting { get; init; } = [1, 2, 3];

        public IReadOnlyList<int> IReadOnlyListSetting { get; init; } = [1, 2, 3];

        public IReadOnlyCollection<int> IReadOnlyCollectionSetting { get; init; } = [1, 2, 3];

        public HashSet<int> HashSetSetting { get; init; } = [1, 2, 3];

        public ISet<int> ISetSetting { get; init; } = new HashSet<int> { 1, 2, 3 };
    }

    private sealed class UnsupportedType
    {
        public static UnsupportedType Default = new UnsupportedType { Foo = "Bar" };

        public string? Foo { get; set; }
    }

#pragma warning disable CA1812
    private sealed class MockPluginSettingsWithUnsupportedType : PluginSettings
    {
        public UnsupportedType UnsupportedTypeSetting { get; init; } = UnsupportedType.Default;
    }
#pragma warning restore CA1812
}
