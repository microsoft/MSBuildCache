// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Hashing;

[TestClass]
public class OutputHasherTests
{
    private static readonly IContentHasher ContentHasher = HashInfoLookup.Find(HashType.MD5).CreateContentHasher();

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    [TestMethod]
    public async Task ComputeHash()
    {
        await using OutputHasher hasher = new(ContentHasher);

        string dir = CreateTestDirectory();
        string file = Path.Combine(dir, "file.txt");
#if NETFRAMEWORK
        File.WriteAllText(file, "someContent");
#else
        await File.WriteAllTextAsync(file, "someContent");
#endif

        ContentHash hash = await hasher.ComputeHashAsync(file, CancellationToken.None);
        ContentHash expectedHash = ContentHasher.GetContentHash(Encoding.Default.GetBytes("someContent"));
        Assert.AreEqual(expectedHash, hash);
    }

    [TestMethod]
    public async Task ComputeHashFileNotFound()
    {
        await using OutputHasher hasher = new(ContentHasher);

        string dir = CreateTestDirectory();
        string file = Path.Combine(dir, "file.txt");

        // Ensure exceptions are propagated correctly.
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () => await hasher.ComputeHashAsync(file, CancellationToken.None));
    }

    [TestMethod]
    public async Task StressTest()
    {
        const int NumFiles = 1000;
        const int NumFileLines = 100;

        await using OutputHasher hasher = new(ContentHasher);

        string dir = CreateTestDirectory();

        Dictionary<string, string> files = Enumerable.Range(0, NumFiles)
            .Select(i => Path.Combine(dir, $"{i}.txt"))
            .ToDictionary(
                filePath => filePath,
                filePath =>
                {
                    StringBuilder sb = new();
                    for (int i = 0; i < NumFileLines; i++)
                    {
                        sb.AppendLine(filePath);
                    }

                    return sb.ToString();
                });

        // Write all files to disk
        Parallel.ForEach(files, file => File.WriteAllText(file.Key, file.Value));

        // Hash them all at the same time
        ConcurrentDictionary<string, Task<ContentHash>> tasks = new();
        Parallel.ForEach(files, file => tasks.TryAdd(file.Key, hasher.ComputeHashAsync(file.Key, CancellationToken.None)));

        await Task.WhenAll(tasks.Values);

        // Ensure hashing was actually correct
        Parallel.ForEach(files, file =>
        {
            ContentHash expectedHash = ContentHasher.GetContentHash(Encoding.Default.GetBytes(file.Value));
            Assert.AreEqual(expectedHash, tasks[file.Key].Result, $"Hash did not match: {file.Key}");
        });
    }

    private string CreateTestDirectory()
    {
        string testDirectory = Path.Combine(TestContext.TestRunDirectory!, nameof(OutputHasherTests), TestContext.TestName!);
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }
}
