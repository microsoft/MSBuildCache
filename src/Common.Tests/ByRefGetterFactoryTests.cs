// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.MSBuildCache.FileAccess;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

/// <summary>
/// Covers binding against both shapes of MSBuild's <c>FileAccessData</c>: the older one that lacks
/// the enumeration fields and the newer one that carries them. The real struct comes from whichever
/// <c>Microsoft.Build.dll</c> the host supplies, so stand-in structs are used to exercise both sides
/// without needing two MSBuild installations.
/// </summary>
[TestClass]
public sealed class ByRefGetterFactoryTests
{
    private enum StandInAttributes : uint
    {
        None = 0,
        Directory = 0x10,
    }

    /// <summary>Mirrors the pre-18.10 FileAccessData: no enumeration fields.</summary>
    private struct OlderFileAccessData
    {
        private string _path;

        public OlderFileAccessData(string path) => _path = path;

        public string Path
        {
            readonly get => _path;
            private set => _path = value;
        }
    }

    /// <summary>Mirrors the newer FileAccessData, including the readonly-get/private-set shape.</summary>
    private struct NewerFileAccessData
    {
        private string _path;
        private string? _enumeratePattern;
        private StandInAttributes _openedAttributes;

        public NewerFileAccessData(string path, string? enumeratePattern, StandInAttributes openedAttributes)
        {
            _path = path;
            _enumeratePattern = enumeratePattern;
            _openedAttributes = openedAttributes;
        }

        public string Path
        {
            readonly get => _path;
            private set => _path = value;
        }

        public string? EnumeratePattern
        {
            readonly get => _enumeratePattern;
            private set => _enumeratePattern = value;
        }

        public StandInAttributes OpenedFileOrDirectoryAttributes
        {
            readonly get => _openedAttributes;
            private set => _openedAttributes = value;
        }
    }

    [TestMethod]
    public void ReturnsNullWhenPropertyIsAbsent()
    {
        Assert.IsNull(ByRefGetterFactory.TryCreate<OlderFileAccessData, string?>("EnumeratePattern"));
        Assert.IsNull(ByRefGetterFactory.TryCreate<OlderFileAccessData, StandInAttributes>("OpenedFileOrDirectoryAttributes"));
    }

    [TestMethod]
    public void ReturnsNullWhenPropertyTypeDiffers()
    {
        // Guards against silently binding to a property that was reshaped rather than added.
        Assert.IsNull(ByRefGetterFactory.TryCreate<NewerFileAccessData, int>("EnumeratePattern"));
    }

    [TestMethod]
    public void ReadsStringPropertyWithoutBoxing()
    {
        ByRefGetter<NewerFileAccessData, string?>? getter =
            ByRefGetterFactory.TryCreate<NewerFileAccessData, string?>("EnumeratePattern");
        Assert.IsNotNull(getter);

        NewerFileAccessData data = new(@"X:\dir", "*.cs", StandInAttributes.Directory);
        Assert.AreEqual("*.cs", getter(ref data));
    }

    [TestMethod]
    public void ReadsEnumProperty()
    {
        ByRefGetter<NewerFileAccessData, StandInAttributes>? getter =
            ByRefGetterFactory.TryCreate<NewerFileAccessData, StandInAttributes>("OpenedFileOrDirectoryAttributes");
        Assert.IsNotNull(getter);

        NewerFileAccessData data = new(@"X:\dir", "*.cs", StandInAttributes.Directory);
        Assert.AreEqual(StandInAttributes.Directory, getter(ref data));
    }

    [TestMethod]
    public void ReadsNullStringProperty()
    {
        ByRefGetter<NewerFileAccessData, string?>? getter =
            ByRefGetterFactory.TryCreate<NewerFileAccessData, string?>("EnumeratePattern");
        Assert.IsNotNull(getter);

        NewerFileAccessData data = new(@"X:\dir", enumeratePattern: null, StandInAttributes.None);
        Assert.IsNull(getter(ref data));
    }

    /// <summary>
    /// The getter is bound once and reused across every reported file access, so it must observe the
    /// instance it is handed rather than a snapshot captured at bind time.
    /// </summary>
    [TestMethod]
    public void BoundGetterIsReusableAcrossInstances()
    {
        ByRefGetter<NewerFileAccessData, string?>? getter =
            ByRefGetterFactory.TryCreate<NewerFileAccessData, string?>("EnumeratePattern");
        Assert.IsNotNull(getter);

        NewerFileAccessData first = new(@"X:\a", "*.cs", StandInAttributes.None);
        NewerFileAccessData second = new(@"X:\b", "*.dll", StandInAttributes.None);

        Assert.AreEqual("*.cs", getter(ref first));
        Assert.AreEqual("*.dll", getter(ref second));
    }
}
