// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MoreLinq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class NodeBuildResultTests
{
    private static readonly Dictionary<string, ContentHash> Outputs = new Dictionary<string, ContentHash> {
        {"Lib-link.write.1.tlog", ContentHash.Random()},
        {"Lib.command.1.tlog", ContentHash.Random()},
        {"logger.lastbuildstate", ContentHash.Random()},
    };

    [TestMethod]
    public void SortWorksConsistently()
    {
        List<string> names = Outputs.Keys.ToList();
        var baseline = new SortedSet<string>(names, StringComparer.OrdinalIgnoreCase);
        foreach (IList<string> permutation in names.Permutations())
        {
            var ordinal_sorted = new SortedSet<string>(permutation, StringComparer.OrdinalIgnoreCase);
            CollectionAssert.AreEqual(baseline, ordinal_sorted);
        }
    }

    [TestMethod]
    public void SortWorksConsistentlyAcrossJson()
    {
        List<string> names = Outputs.Keys.ToList();
        var expected = new SortedDictionary<string, ContentHash>(Outputs, StringComparer.OrdinalIgnoreCase);

        foreach (IList<string> permutation in names.Permutations())
        {
            var maybeMixed = new SortedDictionary<string, ContentHash>(
                permutation.ToDictionary(name => name, name => Outputs[name]));

            NodeBuildResult nodeBuildResult = new(
                maybeMixed,
                new SortedDictionary<string, string>(),
                new List<NodeTargetResult>(),
                DateTime.UtcNow,
                DateTime.UtcNow,
                null
            );

            string serialized = JsonSerializer.Serialize(nodeBuildResult, SourceGenerationContext.Default.NodeBuildResult);
            NodeBuildResult deserialized = JsonSerializer.Deserialize(serialized, SourceGenerationContext.Default.NodeBuildResult)!;

            CollectionAssert.AreEqual(expected.Keys, deserialized.Outputs.Keys, "\n" +
                "Permutation: " + string.Join(", ", permutation) + "\n" +
                "Serialized: " + serialized + "\n" +
                "Deserialized: " + string.Join(", ", deserialized.Outputs.Keys) + "\n" +
                "Expected: " + string.Join(", ", permutation) + "\n\n");
        }
    }
}