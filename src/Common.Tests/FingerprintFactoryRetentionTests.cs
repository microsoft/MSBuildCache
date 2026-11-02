// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class FingerprintFactoryRetentionTests
{
    [TestMethod]
    public async Task SamePathSetReusesCompletedFingerprint()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var inputHasher = new CountingInputHasher(() => new ValueTask<byte[]?>(new byte[] { 1, 2, 3 }));
        FingerprintFactory factory = CreateFactory(hasher, inputHasher);
        PathSet pathSet = CreateFilePathSet();

        Fingerprint? first = await factory.GetStrongFingerprintAsync(pathSet);
        Fingerprint? second = await factory.GetStrongFingerprintAsync(pathSet);

        Assert.IsNotNull(first);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, inputHasher.CallCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SamePathSetConcurrentRequestsComputeOnce(bool asynchronousHash)
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        using var inputHasher = new GatedInputHasher(asynchronousHash);
        FingerprintFactory factory = CreateFactory(hasher, inputHasher);
        PathSet pathSet = CreateFilePathSet();
        Task<Fingerprint?>[] requests = StartConcurrentRequests(factory, pathSet);

        try
        {
            await AwaitSignalAsync(inputHasher.Entered);
            inputHasher.Release();
            Fingerprint?[] fingerprints = await Task.WhenAll(requests);
            Fingerprint? first = fingerprints[0];
            Assert.IsNotNull(first);
            foreach (Fingerprint? fingerprint in fingerprints)
            {
                Assert.AreSame(first, fingerprint);
            }

            Assert.AreSame(first, await factory.GetStrongFingerprintAsync(pathSet));
            Assert.AreEqual(1, inputHasher.CallCount, "Concurrent requests for one live key must execute the calculation once.");
        }
        finally
        {
            inputHasher.Release();
            await Task.WhenAll(requests);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EqualDistinctPathSetsKeepHashesAndTheirOwnDescriptions(bool differentCasing)
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var inputHasher = new CountingInputHasher(() => new ValueTask<byte[]?>(new byte[] { 1, 2, 3 }));
        FingerprintFactory factory = CreateFactory(hasher, inputHasher);
        PathSet firstPathSet = CreateMixedPathSet(lowerCase: false);
        PathSet secondPathSet = CreateMixedPathSet(lowerCase: differentCasing);
        Assert.AreNotSame(firstPathSet, secondPathSet);
        Assert.IsTrue(firstPathSet.Equals(secondPathSet));

        Fingerprint? first = await factory.GetStrongFingerprintAsync(firstPathSet);
        Fingerprint? second = await factory.GetStrongFingerprintAsync(secondPathSet);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        CollectionAssert.AreEqual(first.Hash, second.Hash);
        Assert.AreEqual(first.Entries.Count, second.Entries.Count);
        for (int i = 0; i < first.Entries.Count; i++)
        {
            CollectionAssert.AreEqual(first.Entries[i].Hash, second.Entries[i].Hash);
        }

        AssertDescriptions(first, lowerCase: false);
        AssertDescriptions(second, lowerCase: differentCasing);
        Assert.AreSame(first, await factory.GetStrongFingerprintAsync(firstPathSet));
        Assert.AreSame(second, await factory.GetStrongFingerprintAsync(secondPathSet));
        Assert.AreEqual(2, inputHasher.CallCount, "Equal objects have independent memoized calculations.");
    }

    [TestMethod]
    [DoNotParallelize]
    public void StrongFingerprintIsRetainedOnlyWhilePathSetIsReachable()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        FingerprintFactory factory = CreateFactory(hasher, new CountingInputHasher(() => new ValueTask<byte[]?>((byte[]?)null)));
        LifetimeReferences references = CreateAndReleasePathSet(factory);

        try
        {
            for (int attempt = 0; attempt < 5 && AnyTargetIsAlive(references); attempt++)
            {
                CollectFully();
            }

            Assert.IsFalse(IsAlive(references.PathSet), "The factory must not retain a completed strong-fingerprint key.");
            Assert.IsFalse(IsAlive(references.Fingerprint), "The factory must release a fingerprint after its key becomes unreachable.");
            Assert.IsFalse(IsAlive(references.Entries), "The factory must release the expanded fingerprint entries after their key becomes unreachable.");
        }
        finally
        {
            GC.KeepAlive(factory);
        }
    }

    [TestMethod]
    public async Task NullEmptyAndUnhashablePathSetsStillReturnNull()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var inputHasher = new CountingInputHasher(() => new ValueTask<byte[]?>((byte[]?)null));
        FingerprintFactory factory = CreateFactory(hasher, inputHasher);

        Assert.IsNull(await factory.GetStrongFingerprintAsync(null));
        Assert.IsNull(await factory.GetStrongFingerprintAsync(new PathSet(Array.Empty<ObservedPathEntry>())));
        Assert.AreEqual(0, inputHasher.CallCount);

        PathSet unhashablePathSet = CreateFilePathSet();
        Assert.IsNull(await factory.GetStrongFingerprintAsync(unhashablePathSet));
        Assert.IsNull(await factory.GetStrongFingerprintAsync(unhashablePathSet));
        Assert.AreEqual(1, inputHasher.CallCount, "A null result is memoized while its key is alive.");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task FailedOrCanceledInputHashRemainsMemoized(bool canceled, bool synchronousThrow)
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Murmur).CreateContentHasher();
        var inputHasher = new CountingInputHasher(() => synchronousThrow
            ? throw new InvalidOperationException("Input hashing failed.")
            : canceled
                ? new ValueTask<byte[]?>(Task.FromCanceled<byte[]?>(new CancellationToken(canceled: true)))
                : new ValueTask<byte[]?>(Task.FromException<byte[]?>(new InvalidOperationException("Input hashing failed."))));
        FingerprintFactory factory = CreateFactory(hasher, inputHasher);
        PathSet pathSet = CreateFilePathSet();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (canceled)
            {
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => factory.GetStrongFingerprintAsync(pathSet));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => factory.GetStrongFingerprintAsync(pathSet));
            }
        }

        Assert.AreEqual(1, inputHasher.CallCount, "A live key retains its original failure or cancellation without retrying.");
    }

    private static FingerprintFactory CreateFactory(IContentHasher hasher, IInputHasher inputHasher)
        => new(hasher, inputHasher, new TestPluginSettings { RepoRoot = @"X:\Repo" }, new PathNormalizer(@"X:\Repo", @"X:\Nuget"));

    private static PathSet CreateFilePathSet()
        => new(new[] { new ObservedPathEntry(@"{RepoRoot}File.cs", ObservationType.FileContentRead) });

    private static PathSet CreateMixedPathSet(bool lowerCase)
    {
        string directory = lowerCase ? "directory" : "Directory";
        string file = lowerCase ? "file.cs" : "File.cs";
        string member = new((lowerCase ? "member.cs" : "Member.cs").ToCharArray());
        string missing = lowerCase ? "missing" : "Missing";
        return new PathSet(new[]
        {
            new ObservedPathEntry($"{{RepoRoot}}{directory}", ObservationType.DirectoryEnumeration, "*.cs", new[] { member }),
            new ObservedPathEntry($"{{RepoRoot}}{file}", ObservationType.FileContentRead),
            new ObservedPathEntry($"{{RepoRoot}}{missing}", ObservationType.AbsentPathProbe),
        });
    }

    private static void AssertDescriptions(Fingerprint fingerprint, bool lowerCase)
    {
        string directory = lowerCase ? "directory" : "Directory";
        string file = lowerCase ? "file.cs" : "File.cs";
        string member = lowerCase ? "member.cs" : "Member.cs";
        string missing = lowerCase ? "missing" : "Missing";
        Assert.AreEqual(4, fingerprint.Entries.Count);
        Assert.AreEqual($"DirectoryEnumeration: {{RepoRoot}}{directory} (*.cs)", fingerprint.Entries[0].Description);
        Assert.AreEqual($"DirectoryEnumeration: {{RepoRoot}}{directory} (*.cs) - {member}", fingerprint.Entries[1].Description);
        Assert.AreEqual($"FileContentRead: {{RepoRoot}}{file}", fingerprint.Entries[2].Description);
        Assert.AreEqual($"AbsentPathProbe: {{RepoRoot}}{missing}", fingerprint.Entries[3].Description);
    }

    private static Task<Fingerprint?>[] StartConcurrentRequests(FingerprintFactory factory, PathSet pathSet)
    {
        const int requestCount = 8;
        using var ready = new CountdownEvent(requestCount);
        using var started = new CountdownEvent(requestCount);
        using var start = new ManualResetEventSlim();
        var requests = new Task<Fingerprint?>[requestCount];
        for (int i = 0; i < requests.Length; i++)
        {
            // Dedicated threads keep the synchronous hash gate independent of thread-pool injection.
            requests[i] = Task.Factory.StartNew(
                () =>
                {
                    ready.Signal();
                    Assert.IsTrue(start.Wait(TimeSpan.FromSeconds(30)), "Concurrent request start gate timed out.");
                    started.Signal();
                    return factory.GetStrongFingerprintAsync(pathSet);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();
        }

        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(30)), "Concurrent request threads did not become ready.");
        }
        finally
        {
            start.Set();
        }

        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)), "Concurrent requests did not reach the factory.");
        return requests;
    }

    private static async Task AwaitSignalAsync(Task signal)
    {
        Task completed = await Task.WhenAny(signal, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.AreSame(signal, completed, "Input hashing did not reach its gate.");
        await signal;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeReferences CreateAndReleasePathSet(FingerprintFactory factory)
    {
        // Use only the strong cache: weak-fingerprint diagnostics have a separate, intentional lifetime.
        var pathSet = new PathSet(new[]
        {
            new ObservedPathEntry($"{{RepoRoot}}retention-{Guid.NewGuid():N}", ObservationType.ExistingProbe),
        });
        LifetimeReferences references = CaptureWeakReferences(factory, pathSet);
        CollectFully();
        Assert.IsTrue(IsAlive(references.Fingerprint), "A live key must retain its completed memoized fingerprint.");
        Assert.IsTrue(IsAlive(references.Entries), "A live key must retain its fingerprint diagnostics.");
        GC.KeepAlive(pathSet);
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeReferences CaptureWeakReferences(FingerprintFactory factory, PathSet pathSet)
    {
        Task<Fingerprint?> task = factory.GetStrongFingerprintAsync(pathSet);
        Assert.AreEqual(TaskStatus.RanToCompletion, task.Status, "Probe-only fingerprinting must complete synchronously.");
        Fingerprint? fingerprint = task.GetAwaiter().GetResult();
        Assert.IsNotNull(fingerprint);
        return new LifetimeReferences(
            new WeakReference<PathSet>(pathSet),
            new WeakReference<Fingerprint>(fingerprint),
            // The string-hash cache retains descriptions; the expanded entry list must still be collectible.
            new WeakReference<IReadOnlyList<FingerprintEntry>>(fingerprint.Entries));
    }

    private static bool AnyTargetIsAlive(LifetimeReferences references)
        => IsAlive(references.PathSet) || IsAlive(references.Fingerprint) || IsAlive(references.Entries);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference)
        where T : class
        => reference.TryGetTarget(out _);

    private static void CollectFully()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
    }

    private sealed record LifetimeReferences(
        WeakReference<PathSet> PathSet,
        WeakReference<Fingerprint> Fingerprint,
        WeakReference<IReadOnlyList<FingerprintEntry>> Entries);

    private sealed class TestPluginSettings : PluginSettings
    {
    }

    private sealed class CountingInputHasher(Func<ValueTask<byte[]?>> getHash) : IInputHasher
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public bool ContainsPath(string absolutePath) => true;

        public ValueTask<byte[]?> GetHashAsync(string absolutePath)
        {
            Interlocked.Increment(ref _callCount);
            return getHash();
        }
    }

    private sealed class GatedInputHasher(bool asynchronousHash) : IInputHasher, IDisposable
    {
        private readonly ManualResetEventSlim _releaseGate = new();
        private readonly TaskCompletionSource<bool> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]?> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task Entered => _entered.Task;

        public bool ContainsPath(string absolutePath) => true;

        public ValueTask<byte[]?> GetHashAsync(string absolutePath)
        {
            Interlocked.Increment(ref _callCount);
            _entered.TrySetResult(true);
            if (asynchronousHash)
            {
                return new ValueTask<byte[]?>(_released.Task);
            }

            Assert.IsTrue(_releaseGate.Wait(TimeSpan.FromSeconds(30)), "Synchronous input-hash gate timed out.");
            return new ValueTask<byte[]?>(new byte[] { 1, 2, 3 });
        }

        public void Release()
        {
            _releaseGate.Set();
            _released.TrySetResult(new byte[] { 1, 2, 3 });
        }

        public void Dispose() => _releaseGate.Dispose();
    }
}
