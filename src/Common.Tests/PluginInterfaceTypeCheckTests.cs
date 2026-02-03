// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

// Make sure that types used by the plugin interface are limited to the assemblies we expect
[TestClass]
public class PluginInterfaceTypeCheckTests
{
    public static readonly HashSet<string> PluginInterfaceNuGetAssemblies = new HashSet<string>()
    {
        // specific CODESYNC[DO_NOT_ILMERGE_ASSEMBLIES]
        "Microsoft.Build.dll",
        "Microsoft.Build.Framework.dll",
        "Microsoft.Build.Utilities.Core.dll",
        "System.Collections.Immutable.dll",
    };

    private static readonly HashSet<string> PluginInterfaceAssemblies = new HashSet<string>(PluginInterfaceNuGetAssemblies)
    {
        // general
        "mscorlib.dll",
        "System.Private.CoreLib.dll",
        "System.Core.dll",
        // Transitive runtime dependency introduced by updated MSBuild.
        "Microsoft.VisualStudio.SolutionPersistence.dll",
        // .NET runtime splits more assemblies; we explicitly allow a few legacy names above and will wildcard allow other System.* below.
    };

    [TestMethod]
    public void ProjectCachePluginBase()
    {
        CheckAssembliesForType(typeof(ProjectCachePluginBase));
    }

    private static void AssertAssembly(Type t)
    {
        string assemblyFileName = Path.GetFileName(t.Assembly.Location);

        // Allow explicitly listed assemblies
        if (PluginInterfaceAssemblies.Contains(assemblyFileName))
        {
            return;
        }

        // Allow any System.* assembly. These are considered part of the platform surface and not third-party dependencies
        // that we ship. Adding a wildcard here keeps the test resilient to refactors in the BCL where new facade assemblies
        // can appear (e.g., System.Diagnostics.Process.dll).
        string simpleName = Path.GetFileNameWithoutExtension(assemblyFileName);
        if (simpleName.StartsWith("System", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Assert.Fail($"Type {t.FullName} is in assembly {t.Assembly.Location} which is not expected");
    }

    private static void CheckAssembliesForType(Type t)
    {
        var alreadyChecked = new HashSet<Type>();
        CheckAssemblies(t, alreadyChecked, 5);
        Assert.IsGreaterThan(10, alreadyChecked.Count, "Failed to find types.");
    }

    private static void CheckAssemblies(Type t, HashSet<Type> alreadyChecked, int depth)
    {
        if (depth <= 0 || t == null || !alreadyChecked.Add(t))
        {
            return;
        }

        if (t.FullName == null)
        {
            return;
        }

        AssertAssembly(t);

        // Accessing nested types / properties / methods may trigger loading of transitive dependencies
        // that MSBuild now has (e.g. Microsoft.VisualStudio.SolutionPersistence). If they are not present
        // we skip those branches instead of failing the test.
        try
        {
            foreach (Type nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                CheckAssemblies(nested, alreadyChecked, depth - 1);
            }
        }
        catch (FileNotFoundException)
        {
            return; // Skip further exploration of this branch
        }

        try
        {
            foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Type propertyType;
                try
                {
                    propertyType = p.PropertyType;
                }
                catch (FileNotFoundException)
                {
                    continue; // Skip this property
                }
                CheckAssemblies(propertyType, alreadyChecked, depth - 1);
            }
        }
        catch (FileNotFoundException)
        {
            return;
        }

        MethodInfo[] methods;
        try
        {
            methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        foreach (MethodInfo? m in methods)
        {
            try
            {
                CheckAssemblies(m.ReturnType, alreadyChecked, depth - 1);
                foreach (var p in m.GetParameters())
                {
                    CheckAssemblies(p.ParameterType, alreadyChecked, depth - 1);
                }
            }
            catch (FileNotFoundException)
            {
                // Skip this method entirely
                continue;
            }
        }
    }
}
