// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using DotNet.Globbing;
using Microsoft.Build.Construction;
using Microsoft.Build.Execution;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class FingerprintFactoryCompatibilityTests
{
    private const string RepoRoot = @"X:\Repo";
    private const string PackageRoot = @"X:\Packages";

    // Captured from the original factory before its retention implementation was changed. The
    // snapshots cover each ordered entry's description and nullable hash as well as the final hash.
    [TestMethod]
    [DataRow(
        HashType.Murmur,
        "BB7A01CC02068EF800000000000000000000000000000000000000000000000000",
        "E6D9647B17B032BDD08FB88799DF968E0C27F7D7DD162BCE0D7E4863D73AB24D",
        "BACA6AB00398175200000000000000000000000000000000000000000000000000",
        "39F533AA20BB9F53F35FCF0F3614BB9D809DFBA1EF48E92A77B64A1E73351254")]
    [DataRow(
        HashType.Vso0,
        "EDFFA15338BC12D14BFA9F82C6B84B69E006B393A8A499868339D43E44E6535300",
        "609A1CAB3304320AC3B831FC9AC8B0D131C46A0B98F09F63BF15FDECBFE5CCB4",
        "023825593B4225F603DB80618D00868EB7059F4CB02EF199EB56545740198E4B00",
        "CBB1E5CEDB083BE5EF56C63B5E38342DEE25D8AEEAF709ECAB7B9CE33D0FD1CC")]
    [DataRow(
        HashType.SHA256,
        "DF0A861DF742BC6A52AC4699FBC879934205FEB31744386CCF6B3ADFE8CED877",
        "F9C5E17AF64119D4459656BC251C1306E181765B6C6F3EECFE0A304FE07DA3D2",
        "5FA927720652C784E01A4EA22E8DD7A9DDA0E7DB87AE963C1BC4A5363927FC69",
        "02FDBDEF4703F1ABF65DF02FF054A21DC5684314BE88754F916E8D355C1DEFDA")]
    public async Task FingerprintsMatchOriginalFactory(
        HashType hashType,
        string weakHash,
        string weakEntriesSnapshot,
        string strongHash,
        string strongEntriesSnapshot)
    {
        using IContentHasher contentHasher = HashInfoLookup.Find(hashType).CreateContentHasher();
        var inputHasher = new FixedInputHasher(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"X:\Repo\src\Alpha.cs"] = Hash(contentHasher, "alpha source"),
            [@"X:\Packages\Tool\build.props"] = Hash(contentHasher, "package input"),
            [@"X:\Repo\src\Observed.cs"] = Hash(contentHasher, "observed source"),
        });
        var settings = new PluginSettings
        {
            RepoRoot = RepoRoot,
            CacheUniverse = @"compatibility X:\Repo\universe",
            IgnoreDotNetSdkPatchVersion = true,
            IgnoredInputPatterns = new[] { Glob.Parse(@"X:\Repo\obj\**") },
            IgnoredOutputPatterns = new[] { Glob.Parse("**/*.tmp") },
            IdenticalDuplicateOutputPatterns = new[] { Glob.Parse("**/*.dll") },
            AllowFileAccessAfterProjectFinishFilePatterns = new[] { Glob.Parse("**/*.log") },
            AllowFileAccessAfterProjectFinishProcessPatterns = new[] { Glob.Parse("**/compiler.exe") },
            AllowProcessCloseAfterProjectFinishProcessPatterns = new[] { Glob.Parse("**/worker.exe") },
        };
        var factory = new FingerprintFactory(contentHasher, inputHasher, settings, new PathNormalizer(RepoRoot, PackageRoot));

        NodeContext dependencyZ = CreateDependency(contentHasher, @"src\Z.csproj", "z");
        NodeContext dependencyA = CreateDependency(contentHasher, @"src\A.csproj", "a");
        var project = new ProjectInstance(ProjectRootElement.Create());
        project.SetProperty("VCToolsVersion", "14.42.34433");
        project.SetProperty("NETCoreSdkVersion", "9.0.203");
        var node = new NodeContext(
            RepoRoot,
            project,
            new[] { dependencyZ, dependencyA },
            @"src\Main.nativeproj",
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Platform"] = "x64",
                ["Configuration"] = "Release",
                ["SourceRoot"] = @"x:\REPO\src",
            },
            new[] { @"x:\PACKAGES\Tool\build.props", @"X:\Repo\src\Missing.cs", @"x:\REPO\src\Alpha.cs", @"X:\Repo\obj\ignored.cs" },
            referenceAssemblyRelativePath: null,
            new HashSet<string>(new[] { "Rebuild", "Build" }, StringComparer.OrdinalIgnoreCase));

        var pathSet = new PathSet(new ObservedPathEntry[]
        {
            new(@"{NugetPackageRoot}Tool\build.props", ObservationType.FileContentRead),
            new(@"{RepoRoot}dirs\Absent", ObservationType.DirectoryEnumeration),
            new(@"{RepoRoot}dirs\Empty", ObservationType.DirectoryEnumeration, members: Array.Empty<string>()),
            new(@"{RepoRoot}dirs\Members", ObservationType.DirectoryEnumeration, "*.cs", new[] { "Alpha.cs", "beta.cs" }, new[] { "generated.cs" }),
            new(@"{RepoRoot}dirs\Members", ObservationType.DirectoryEnumeration, "*.dll", new[] { "library.dll" }),
            new(@"{RepoRoot}obj\ignored.cs", ObservationType.FileContentRead),
            new(@"{RepoRoot}probes\Absent", ObservationType.AbsentPathProbe),
            new(@"{RepoRoot}probes\Existing", ObservationType.ExistingProbe),
            new(@"{RepoRoot}src\Missing.cs", ObservationType.FileContentRead),
            new(@"{RepoRoot}src\Observed.cs", ObservationType.FileContentRead),
        });

        Fingerprint? weak = await factory.GetWeakFingerprintAsync(node);
        Fingerprint? strong = await factory.GetStrongFingerprintAsync(pathSet);
        Assert.IsNotNull(weak);
        Assert.IsNotNull(strong);

        // Preserve the snapshots as constants, never by regenerating the original algorithm in tests.
        Assert.AreEqual(
            string.Join("\n", weakHash, weakEntriesSnapshot, strongHash, strongEntriesSnapshot),
            string.Join("\n", Hex(weak.Hash), Snapshot(weak), Hex(strong.Hash), Snapshot(strong)),
            $"Original {hashType} weak hash / entries snapshot / strong hash / entries snapshot:\n{Hex(weak.Hash)}\n{Snapshot(weak)}\n{Hex(strong.Hash)}\n{Snapshot(strong)}\nWeak entries:\n{Descriptions(weak)}\nStrong entries:\n{Descriptions(strong)}");
    }

    private static NodeContext CreateDependency(IContentHasher contentHasher, string path, string label)
    {
        var dependency = new NodeContext(
            RepoRoot,
            new ProjectInstance(ProjectRootElement.Create()),
            Array.Empty<NodeContext>(),
            path,
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            referenceAssemblyRelativePath: null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        dependency.SetBuildResult(new NodeBuildResult(
            new SortedDictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase)
            {
                [$@"bin\{label}.pdb"] = contentHasher.GetContentHash(Encoding.UTF8.GetBytes($"{label} symbols")),
                [$@"bin\{label}.dll"] = contentHasher.GetContentHash(Encoding.UTF8.GetBytes($"{label} assembly")),
            },
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<NodeTargetResult>(),
            DateTime.MinValue,
            DateTime.MinValue,
            buildId: null));
        return dependency;
    }

    private static byte[] Hash(IContentHasher contentHasher, string value)
        => contentHasher.GetContentHash(Encoding.UTF8.GetBytes(value)).ToHashByteArray();

    private static string Snapshot(Fingerprint fingerprint)
    {
        string snapshot = string.Join("\n", fingerprint.Entries.Select(entry => $"{entry.Description}\t{(entry.Hash is null ? "<null>" : Hex(entry.Hash))}"));
#if NETFRAMEWORK
        using SHA256 hasher = SHA256.Create();
        return Hex(hasher.ComputeHash(Encoding.UTF8.GetBytes(snapshot)));
#else
        return Hex(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot)));
#endif
    }

    private static string Descriptions(Fingerprint fingerprint)
        => string.Join("\n", fingerprint.Entries.Select(entry => entry.Description));

    private static string Hex(byte[] bytes)
#if NETFRAMEWORK
        => BitConverter.ToString(bytes).Replace("-", string.Empty, StringComparison.Ordinal);
#else
        => Convert.ToHexString(bytes);
#endif

    private sealed class FixedInputHasher : IInputHasher
    {
        private readonly IReadOnlyDictionary<string, byte[]> _hashes;

        public FixedInputHasher(IReadOnlyDictionary<string, byte[]> hashes) => _hashes = hashes;

        public bool ContainsPath(string absolutePath) => _hashes.ContainsKey(absolutePath);

        public ValueTask<byte[]?> GetHashAsync(string absolutePath)
            => new(_hashes.TryGetValue(absolutePath, out byte[]? hash) ? hash : null);
    }
}
