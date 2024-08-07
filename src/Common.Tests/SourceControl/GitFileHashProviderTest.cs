﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Utilities;
using Microsoft.MSBuildCache.SourceControl;
using Microsoft.MSBuildCache.Tests.Mocks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests.SourceControl;

[TestClass]
public class GitFileHashProviderTests
{
    private const string RepoRoot = @"X:\RepoRoot";

    private static readonly byte[] FakeHash = { 0, 1, 2, 3, 4 };

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable. Justification: Always set by MSTest
    public TestContext TestContext { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

    private static Task FakeHasher(List<string> filesToRehash, Dictionary<string, byte[]> fileHashes)
    {
        foreach (string file in filesToRehash)
        {
            fileHashes[file] = FakeHash;
        }

        return Task.CompletedTask;
    }

    [TestMethod]
    public async Task ParseGitLsFiles()
    {
        // This has two modified and one untracked files
        const string lsFilesOutput = @"bar/foo.txt 100644 f1845040ea7c5e2f51f68219f3c36ab3f14d0b39 0	Fakes.ProjTemplate 100644 64defffa713e6d923a2587b80da5770c53524f98 0	Fakes/Microsoft.TeamFoundation.VersionControl.Client.fakes 100644 05facf6babe5ca9a9557084f6a155c5649f7e416 0	GitNestedBranchErrorFromOutputTest.cs 100644 e25307225505549354124ea21470cf579101bf00 0	GitSourceControlTest.cs 100644 ee075fb0679a635d3c9acf121fbbb5f2137d4bd1 0	SDFileStreamTest.cs 100644 da7820c633d511273ef591b34f6497c738028bfd 0	SDHasherTest.cs 100644 cb77ed9737098aba0e0b5a29ae2fb33539ebda94 0	SDSourceControlTests.cs 100644 258592aa45eef6af99fd9ac9002eb403bbc8d661 0	SdHelperTest.cs 100644 5ca8de140ce3bd28cfadcb97a6d3620fa23d980a 0	SdHelperTest_TestData.txt 100644 35516909ae79850cc16581ad9b6f40537b445e25 0	SourceControlEnvironmentTest.cs 100644 8f2e9a74cdb797ac9c748868b4cd447db3c1f25f 0	SourceControlTest.csproj 100644 5f364b05c76746d18dc7df99553d27bdee80c1eb 0	TestData/SDFileStream/SampleSDFile 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0	TestData/SDHashes/Binary/356A192B7913B04C54574D18C28D46E6395428AB 100644 d71a9bfa07829b8c3eda93f7ee46466eb1fa9c81 0	TestData/SDHashes/Binary/3D486A02281782EB12DDB14A73DC11402FDE1AEB 100644 82af719446fed614cf54126671e26a6962a20c71 0	TestData/SDHashes/Binary/791F3D51A567CD53402381E66F1FCA15E4DFA5CA 100644 223a4598b6c1679f2eb986e871102a2140c1c43d 0	TestData/SDHashes/CText/A18D65F7C2D67F92D2204949DF47CC8B9E74ECC4 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0	TestData/SDHashes/CText/B6589FC6AB0DC82CF12099D1C2D40AB994E8410C 100644 9257a812ad6fcad36df464977a3999df81e051b4 0	TestData/SDHashes/CText/E0E2AFFAF627CE468F66396AB08A927D0D942D03 100644 223a4598b6c1679f2eb986e871102a2140c1c43d 0	TestData/SDHashes/Text/A18D65F7C2D67F92D2204949DF47CC8B9E74ECC4 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0	TestData/SDHashes/Text/B6589FC6AB0DC82CF12099D1C2D40AB994E8410C 100644 9257a812ad6fcad36df464977a3999df81e051b4 0	TestData/SDHashes/Text/E0E2AFFAF627CE468F66396AB08A927D0D942D03 100644 82af719446fed614cf54126671e26a6962a20c71 0	TestData/SDHashes/UBinary/015044A426D4925BC658F6D5E28458FBADC97430 100644 d71a9bfa07829b8c3eda93f7ee46466eb1fa9c81 0	TestData/SDHashes/UBinary/2BF948D5C19F59BECE037900C81653FF411EA32F 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0	TestData/SDHashes/UBinary/84A516841BA77A5B4648DE2CD0DFCB30EA46DBB4 100644 43471fb4dad5701062c1ef1b351801890fb6d84b 0	TestData/SDHashes/Unicode/D46FC5281A5CFC5FB645EA08DCCC45AAEF1A1F81 100644 186af661c48d2ead9525b02c2d5fc6aa0a2c9da4 0	TestData/SDHashes/Unicode/E850042BF7226FB85B7D98AA683FE779CD22C347 100644 82af719446fed614cf54126671e26a6962a20c71 0	TestData/SDHashes/XBinary/743E9170BA520F1A3010C1FB03DC60F8698D65F0 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0	TestData/SDHashes/XBinary/77DE68DAECD823BABBB58EDB1C8E14D7106E83BB 100644 d71a9bfa07829b8c3eda93f7ee46466eb1fa9c81 0	TestData/SDHashes/XBinary/8D601E323B2F5ECF91C58CA852F072E0A2B2D743 100644 9257a812ad6fcad36df464977a3999df81e051b4 0	TestData/SDHashes/XText/4D2829E334A5C75BD44F7AAB2B4569298109E9CA 100644 223a4598b6c1679f2eb986e871102a2140c1c43d 0	TestData/SDHashes/XText/8A2EFC7D51ABF8045EEFDED7F546FC1C0392834C 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 0	TestData/SDHashes/XText/DA4B9237BACCCDF19C0760CAB7AEC4A8359010B0 ";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        Dictionary<string, byte[]> hashes = await gitFileHashProvider.ParseGitLsFiles(RepoRoot, new StringReader(lsFilesOutput), FakeHasher);
        Assert.AreEqual(33, hashes.Count, "should be 33 files in this output");
        Assert.AreEqual(FakeHash, hashes[Path.Combine(RepoRoot, @"bar\foo.txt")]);
        Assert.AreEqual("8F2E9A74CDB797AC9C748868B4CD447DB3C1F25F", hashes[Path.Combine(RepoRoot, "SourceControlTest.csproj")].ToHex());
        // Make sure / -> \ happens
        Assert.AreEqual("5F364B05C76746D18DC7DF99553D27BDEE80C1EB", hashes[Path.Combine(RepoRoot, @"TestData\SDFileStream\SampleSDFile")].ToHex());

    }

    [TestMethod]
    public async Task ParseGitLsFilesRename()
    {
        // This is a rename. The original file is not shown.
        const string rename = "100644 c0696ccfe29ff80b7368599eb1bd1ac508d19089 0	barfoo.txt 100644 c75b3dcf2572caae098be44dc890c4980b0c69b8 0	foo.txt ";
        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        Dictionary<string, byte[]> hashes = await gitFileHashProvider.ParseGitLsFiles(Path.Combine(RepoRoot, @"submodule\path"), new StringReader(rename), FakeHasher);
        Assert.AreEqual(2, hashes.Count, "should be 2 files in this output");
        Assert.AreEqual("C0696CCFE29FF80B7368599EB1BD1AC508D19089", hashes[Path.Combine(RepoRoot, @"submodule\path\barfoo.txt")].ToHex());
    }

    /* Test at various levels of staging. foo is committed then added then modified. Bar is new added then modified.
    We should always get the hash for what is actually committed or added but if it's modified we'll see it repeated and nee to rehash
    >git ls-files -cmos --exclude-standard
    100644 296458e455e602fd6b8878877a71ee1c82b85e77 0       bar.txt
    100644 05e33066454fb2823990688888877b0c2856a56e 0       foo.txt
    100644 c0696ccfe29ff80b7368599eb1bd1ac508d19089 0       foobar.txt
    100644 296458e455e602fd6b8878877a71ee1c82b85e77 0       bar.txt
    100644 05e33066454fb2823990688888877b0c2856a56e 0       foo.txt
    100644 c0696ccfe29ff80b7368599eb1bd1ac508d19089 0       foobar.txt
    16:49:40 D:\src\blah
    >git status
    On branch master
    Changes to be committed:
    (use "git reset HEAD <file>..." to unstage)

            new file:   bar.txt
            modified:   foo.txt

    Changes not staged for commit:
    (use "git add <file>..." to update what will be committed)
    (use "git checkout -- <file>..." to discard changes in working directory)

            modified:   bar.txt
            modified:   foo.txt
            modified:   foobar.txt
    */
    [TestMethod]
    public async Task ParseGitLsFilesStaging()
    {
        const string stagingOutput = "100644 296458e455e602fd6b8878877a71ee1c82b85e77 0	bar.txt 100644 05e33066454fb2823990688888877b0c2856a56e 0	foo.txt 100644 c0696ccfe29ff80b7368599eb1bd1ac508d19089 0	foobar.txt 100644 296458e455e602fd6b8878877a71ee1c82b85e77 0	bar.txt 100644 05e33066454fb2823990688888877b0c2856a56e 0	foo.txt 100644 c0696ccfe29ff80b7368599eb1bd1ac508d19089 0	foobar.txt ";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        Dictionary<string, byte[]> hashes = await gitFileHashProvider.ParseGitLsFiles(RepoRoot, new StringReader(stagingOutput), FakeHasher);
        Assert.AreEqual(3, hashes.Count, "should be 3 files in this output");
        Assert.AreEqual(FakeHash, hashes[Path.Combine(RepoRoot, "foobar.txt")]);
        Assert.AreEqual(FakeHash, hashes[Path.Combine(RepoRoot, "foo.txt")]);
        Assert.AreEqual(FakeHash, hashes[Path.Combine(RepoRoot, "bar.txt")]);
    }

    [TestMethod]
    public async Task GitHashObject()
    {
        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);

        string testDir = CreateTestDirectory();
        Directory.CreateDirectory(Path.Combine(testDir, "bar"));

        string file1 = Path.Combine(testDir, "foo.txt");
        string file2 = Path.Combine(testDir, @"bar\baz.txt");

#if NETFRAMEWORK
        File.WriteAllText(file1, "ABC");
        File.WriteAllText(file2, "XYZ");
#else
        await File.WriteAllTextAsync(file1, "ABC");
        await File.WriteAllTextAsync(file2, "XYZ");
#endif

        Dictionary<string, byte[]> hashes = new(StringComparer.OrdinalIgnoreCase);
        await gitFileHashProvider.GitHashObjectAsync(testDir, [file1, file2], hashes, CancellationToken.None);

        Assert.AreEqual(2, hashes.Count);
        Assert.AreEqual("48B83B862EBC57BD3F7C34ED47262F4B402935AF", hashes[file1].ToHex());
        Assert.AreEqual("77BF25132DBE72C79B6AA40C648E4FF1B6E36770", hashes[file2].ToHex());
    }

    [TestMethod]
    public void ParseGitSubModuleStatus()
    {
        // This had two modified and one untracked files
        const string statusOutput = " 73261c727719cf6c820925ae3fd03ccfa76d48f2 private/subDll2 (heads/dev/submodule_support)\n"
            + " a72c4c0e5e4c3106cfa130b697f5d8149c526495 private/subDll2/shared (heads/dev/submodule_support)\n";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        List<string> modules = gitFileHashProvider.ParseGitSubmoduleStatus(statusOutput);

        Assert.AreEqual(2, modules.Count, "should be 2 modules in this output");
        Assert.IsTrue(modules.Contains(@"private\subDll2"));
        Assert.IsTrue(modules.Contains(@"private\subDll2\shared"));
    }

    [TestMethod]
    public void ParseGitSubModuleStatusState()
    {
        // This test exposes the 4 states of submodule states
        const string statusOutput = "+f1845040ea7c5e2f51f68219f3c36ab3f14d0b39 private/subDll2 (heads/dev/submodule_support)\n"
            + " a72c4c0e5e4c3106cfa130b697f5d8149c526495 private/subDll2/shared (heads/dev/submodule_support)\n"
            + "-e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 private/subDll1 (heads/dev/submodule/uninitialized)\n"
            + "U64defffa713e6d923a2587b80da5770c53524f98 private/subDll1/shared (heads/dev/submodule/conflicts)\n";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        List<string> modules = gitFileHashProvider.ParseGitSubmoduleStatus(statusOutput);

        Assert.AreEqual(3, modules.Count, "should be 3 modules in this output");
        Assert.IsTrue(modules.Contains(@"private\subDll2"));
        Assert.IsTrue(modules.Contains(@"private\subDll2\shared"));
        Assert.IsTrue(modules.Contains(@"private\subDll1\shared"));
    }

    [TestMethod]
    public void ParseGitSubModuleStatusEmpty()
    {
        // This case includes extra line and extra spaces; the current CloudBuild version of git.exe adds an additional linefeed for
        // readability, so we want to make sure this case is covered and allows for future changes of whitespaces
        const string statusOutput = "\n";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        List<string> modules = gitFileHashProvider.ParseGitSubmoduleStatus(statusOutput);

        Assert.AreEqual(0, modules.Count, "should be no modules in this output");
    }

    [TestMethod]
    public void ParseGitSubModuleStatusExtraSpacesTest()
    {
        // This case includes extra line and extra spaces; the current CloudBuild version of git.exe adds an additional linefeed for
        // readability, so we want to make sure this case is covered and allows for future changes of whitespaces
        const string statusOutput = "\n f1845040ea7c5e2f51f68219f3c36ab3f14d0b39  private/subDll2\t (heads/dev/submodule_support)\n\n";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        List<string> modules = gitFileHashProvider.ParseGitSubmoduleStatus(statusOutput);

        Assert.AreEqual(1, modules.Count, "should be 1 modules in this output");
    }

    [TestMethod]
    public void ParseGitSubModuleStatusNegativeTests()
    {
        // This case has an invalidly formatted response from git, and ensures that "unrecognized" entries are properly ignored
        const string statusOutput = " f1845040ea7c5e2f51f68219f3c36ab3f14d0b39 private/subDll2\n";

        GitFileHashProvider gitFileHashProvider = new(NullPluginLogger.Instance);
        List<string> modules = gitFileHashProvider.ParseGitSubmoduleStatus(statusOutput);

        Assert.AreEqual(0, modules.Count, "should be 0 modules in this output");
    }

    private string CreateTestDirectory()
    {
        string testDir = Path.Combine(TestContext.TestRunDirectory!, TestContext.TestName!);
        if (Directory.Exists(testDir))
        {
            Directory.Delete(testDir, recursive: true);
        }

        Directory.CreateDirectory(testDir);

        return testDir;
    }
}
