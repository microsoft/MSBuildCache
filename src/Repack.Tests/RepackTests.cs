// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.MSBuildCache.AzureBlobStorage;
using Microsoft.MSBuildCache.AzurePipelines;
using Microsoft.MSBuildCache.Tests;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Repack.Tests;

// Check to make sure that the assemblies that defined the MSBuild/Plugin interface are not merged
[TestClass]
public class RepackTests
{
    private static readonly Type[] TypesToCheck =
    {
        typeof(MSBuildCacheAzureBlobStoragePlugin),
        typeof(MSBuildCacheAzurePipelinesPlugin),
        typeof(MSBuildCacheLocalPlugin),
        typeof(SharedCompilation.ResolveFileAccesses),
    };

    [TestMethod]
    public void PluginInterfaceAssembliesNotMerged()
    {
        foreach (Type type in TypesToCheck)
        {
            Dictionary<string, AssemblyName> references = type.Assembly
                .GetReferencedAssemblies()
                .Where(a => a.Name is not null)
                .ToDictionary(
                    a => a.Name!,
                    a => a
                );

            // Check to make sure that each of the interface assemblies are still actually referenced
            foreach (string expectedRefFileName in PluginInterfaceTypeCheckTests.PluginInterfaceNuGetAssemblies)
            {
                string expectedRef = Path.GetFileNameWithoutExtension(expectedRefFileName);
                Assert.IsNotNull(references.FirstOrDefault(a => a.Value.FullName.IndexOf(expectedRef, StringComparison.Ordinal) > 0));
            }
        }
    }
}
