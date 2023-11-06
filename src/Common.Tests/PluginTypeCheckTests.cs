// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class PluginTypeCheckTests
{
    private static readonly HashSet<string> PluginAssemblies = new HashSet<string>()
    {
        // specific CODESYNC[DO_NOT_ILMERGE_ASSEMBLIES]
        "Microsoft.Build.dll",
        "Microsoft.Build.Framework.dll",
        "Microsoft.Build.Utilities.Core.dll",
        "System.Collections.Immutable.dll",
    };

    [TestMethod]
    public void PluginAssembliesNotMerged()
    {
        Dictionary<string, AssemblyName> references = typeof(MSBuildCachePluginBase).Assembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null)
            .ToDictionary(
                a => a.Name!,
                a => a
            );

        foreach (string expectedRefFileName in PluginAssemblies)
        {
            string expectedRef = Path.GetFileNameWithoutExtension(expectedRefFileName);
            Assert.IsNotNull(references.FirstOrDefault(a => a.Value.FullName.IndexOf(expectedRef, StringComparison.Ordinal) > 0));
        }
    }

    [TestMethod]
    public void ProjectCachePluginBase()
    {
        CheckAssembliesForType(typeof(ProjectCachePluginBase));
    }

    private static readonly HashSet<string> ExpectedAssemblies = new HashSet<string>(PluginAssemblies)
    {
        // general
        "mscorlib.dll",
        "System.Private.CoreLib.dll",
        "System.Core.dll",
    };

    private static void AssertAssembly(Type t)
    {
        Assert.IsTrue(ExpectedAssemblies.Contains(Path.GetFileName(t.Assembly.Location)),
            $"Type {t.FullName} is in assembly {t.Assembly.Location} which is not expected");
    }

    private static void CheckAssembliesForType(Type t)
    {
        var alreadyChecked = new HashSet<Type>();
        CheckAssemblies(t, alreadyChecked, 5);
        Assert.IsTrue(alreadyChecked.Count > 10, "Failed to find types.");
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

        // Console.WriteLine($"Checking {t.FullName}");

        AssertAssembly(t);
        foreach (Type nested in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            CheckAssemblies(nested, alreadyChecked, depth - 1);
        }

        foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            CheckAssemblies(p.PropertyType, alreadyChecked, depth - 1);
        }

        foreach (MethodInfo? m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            CheckAssemblies(m.ReturnType, alreadyChecked, depth - 1);
            foreach (var p in m.GetParameters())
            {
                CheckAssemblies(p.ParameterType, alreadyChecked, depth - 1);
            }
        }
    }
}
