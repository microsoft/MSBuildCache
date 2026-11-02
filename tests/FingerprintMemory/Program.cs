// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;

namespace FingerprintMemory;

internal static class Program
{
    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Developer-only diagnostic labels and CSV headings.")]
    private static int Main(string[] args)
    {
        if (args.Length is < 3 or > 4 ||
            !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int projects) || projects < 1 ||
            !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int members) || members < 1 ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out int batch) || batch < 1 ||
            (args.Length == 4 && args[3] != "--timing"))
        {
            Console.Error.WriteLine("Usage: FingerprintMemory <projects> <directory-members> <batch-size> [--timing]");
            return 1;
        }

        bool measureRetention = args.Length == 3;
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Vso0).CreateContentHasher();
        string root = Path.Combine(Path.GetTempPath(), "MSBuildCacheMemory");
        var factory = new FingerprintFactory(hasher, new NoopInputHasher(), new PluginSettings { RepoRoot = root }, new PathNormalizer(root, Path.Combine(root, "packages")));
        string[] names = Enumerable.Range(0, members).Select(i => $"file{i:D6}.dll").ToArray();
        using SHA256 checksum = SHA256.Create();
        using Process process = Process.GetCurrentProcess();

        Console.WriteLine($"Assembly: {typeof(FingerprintFactory).Assembly.Location}");
        Console.WriteLine($"Mode: {(measureRetention ? "retained heap (forced collections)" : "timing (no forced collections)")}");
        Console.WriteLine("CompletedProjects,ManagedBytes,PrivateBytes,ElapsedMilliseconds");
        var timer = Stopwatch.StartNew();
        for (int project = 0; project < projects; project++)
        {
            CalculateProject(factory, names, project, checksum);
            if ((project + 1) % batch == 0 || project + 1 == projects)
            {
                if (measureRetention)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                process.Refresh();
                Console.WriteLine(FormattableString.Invariant($"{project + 1},{GC.GetTotalMemory(false)},{process.PrivateMemorySize64},{timer.ElapsedMilliseconds}"));
            }
        }

        timer.Stop();
        checksum.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
#if NETFRAMEWORK
        string digest = BitConverter.ToString(checksum.Hash!).Replace("-", string.Empty);
#else
        string digest = Convert.ToHexString(checksum.Hash!);
#endif
        Console.WriteLine($"Fingerprint checksum: {digest}");
        GC.KeepAlive(factory);
        return 0;
    }

    // Return before collecting so the caller does not accidentally keep the last PathSet/result alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CalculateProject(FingerprintFactory factory, IReadOnlyList<string> members, int project, HashAlgorithm checksum)
    {
        var paths = new PathSet(new ObservedPathEntry[]
        {
            new(@"{RepoRoot}shared", ObservationType.DirectoryEnumeration, "*.dll", members),
            // Distinct observations prevent all projects sharing one structurally equal cache entry.
            new($"{{RepoRoot}}markers/project{project:D6}", ObservationType.AbsentPathProbe),
        });

        Fingerprint fingerprint = factory.GetStrongFingerprintAsync(paths).GetAwaiter().GetResult()!;
        checksum.TransformBlock(fingerprint.Hash, 0, fingerprint.Hash.Length, fingerprint.Hash, 0);
        // Publication and diagnostics request the same computation more than once.
        Fingerprint reused = factory.GetStrongFingerprintAsync(paths).GetAwaiter().GetResult()!;
        if (!ReferenceEquals(fingerprint, reused))
        {
            throw new InvalidOperationException("The same live PathSet should reuse its fingerprint.");
        }
    }

    private sealed class NoopInputHasher : IInputHasher
    {
        public bool ContainsPath(string absolutePath) => false;

        public ValueTask<byte[]?> GetHashAsync(string absolutePath) => new((byte[]?)null);
    }
}
