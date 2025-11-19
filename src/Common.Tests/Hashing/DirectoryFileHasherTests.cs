// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.Hashing;

[TestClass]
public class DirectoryFileHasherTests
{
    private static readonly IContentHasher ContentHasher = HashInfoLookup.Find(HashType.MD5).CreateContentHasher();

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    [TestMethod]
    [DataRow(@"X:\Dir\Foo\1.0.0\lib\Foo.dll", true)]
    [DataRow(@"x:\dIR\Foo\1.0.0\lib\Foo.dll", true)]
    [DataRow(@"x:\OtherDir\foo.txt", false)]
    public void ContainsPath(string path, bool expectedResult)
    {
        const string directory = @"X:\Dir";
        DirectoryFileHasher hasher = new(directory, ContentHasher);
        Assert.AreEqual(expectedResult, hasher.ContainsPath(path));
    }

    [TestMethod]
    [DataRow(@"Dir\Foo\1.0.0\lib\Foo.dll", true)]
    [DataRow(@"dIR\Foo\2.0.0\lib\Foo.dll", true)]
    [DataRow(@"OtherDir\foo.txt", false)]
    public async Task ComputeHash(string relativePath, bool expectedToHaveHash)
    {
        string baseDir = CreateTestDirectory();
        string directory = Path.Combine(baseDir, "Dir");

        DirectoryFileHasher hasher = new(directory, ContentHasher);

        string absolutePath = Path.Combine(baseDir, relativePath);
        string fileContent = absolutePath; // Just use the file name itself as the content.
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
#if NETFRAMEWORK
        File.WriteAllText(absolutePath, fileContent);
#else
        await File.WriteAllTextAsync(absolutePath, fileContent);
#endif

        byte[]? hash = await hasher.GetHashAsync(absolutePath);
        if (expectedToHaveHash)
        {
            byte[] expectedHash = ContentHasher.GetContentHash(Encoding.Default.GetBytes(fileContent)).ToHashByteArray();
            CollectionAssert.AreEqual(expectedHash, hash);
        }
        else
        {
            Assert.IsNull(hash);
        }
    }

    private string CreateTestDirectory()
    {
        string testDirectory = Path.Combine(TestContext.TestRunDirectory!, nameof(DirectoryFileHasherTests), TestContext.TestName!);
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }
}
