// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.MSBuildCache.AzureBlobStorage;
using Microsoft.MSBuildCache.AzurePipelines;
using Microsoft.MSBuildCache.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Repack.Tests;

// Check to make sure that the assemblies that defined the MSBuild/Plugin interface are not merged
[TestClass]
public class RepackTests
{
    [TestMethod]
    [DataRow(typeof(MSBuildCacheAzureBlobStoragePlugin))]
    [DataRow(typeof(MSBuildCacheAzurePipelinesPlugin))]
    [DataRow(typeof(MSBuildCacheLocalPlugin))]
    [DataRow(typeof(SharedCompilation.ResolveFileAccesses))]
    public void PluginInterfaceAssembliesNotMerged(Type typeToCheck)
    {
#if DEBUG
        // Using Assert.Inconclusive instead of removing the entire test for visibility that the test exists.
        Assert.Inconclusive("This test only applies to Release builds.");
#endif

        HashSet<string> references = typeToCheck.Assembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null)
            .Select(reference => reference.Name!)
            .ToHashSet(StringComparer.Ordinal);

        // Check to make sure that each of the interface assemblies are still actually referenced
        foreach (string expectedRefFileName in PluginInterfaceTypeCheckTests.PluginInterfaceNuGetAssemblies)
        {
            string expectedRef = Path.GetFileNameWithoutExtension(expectedRefFileName);
            Assert.Contains(expectedRef, references);
        }
    }
}
