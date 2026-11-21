// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.MSBuildCache.Hashing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Fingerprint = Microsoft.MSBuildCache.Fingerprinting.Fingerprint;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class CasCacheClientTests
{
    private static readonly string RepoRoot = Path.Combine(Path.GetTempPath(), "MSBuildCacheTests", "repo");

    [TestMethod]
    public void NullContentHashListMeansSubmittedValueWasAccepted()
    {
        AddOrGetContentHashListResult result = new(default(ContentHashListWithDeterminism));

        Assert.AreEqual(AddNodeResult.Added, CasCacheClient.GetAddNodeResult(result));
    }

    [TestMethod]
    public void ReturnedContentHashListMeansAnotherValueWon()
    {
        ContentHashList contentHashList = new(Array.Empty<ContentHash>(), null);
        AddOrGetContentHashListResult result = new(new ContentHashListWithDeterminism(contentHashList, CacheDeterminism.None));

        Assert.AreEqual(AddNodeResult.AlreadyExists, CasCacheClient.GetAddNodeResult(result));
    }

    [TestMethod]
    public async Task AddNodeDoesNotUploadContentWhenRemoteCacheIsReadOnly()
    {
        (CasCacheClient cacheClient, RecordingCacheSession localSession, RecordingCacheSession remoteSession) = CreateCacheClient(remoteCacheIsReadOnly: true);

        await using (cacheClient)
        {
            await cacheClient.AddNodeInternalAsync(CreateNodeContext(), pathSet: null, CreateNodeBuildResult(), CancellationToken.None);
        }

        Assert.AreEqual(0, remoteSession.PinCallCount, "The remote session must not be pinned when the remote cache is read-only.");
        Assert.AreEqual(0, remoteSession.PutStreamCallCount, "Content must not be uploaded when the remote cache is read-only.");
        Assert.AreEqual(0, remoteSession.PutFileCallCount, "Content must not be uploaded when the remote cache is read-only.");

        // The node still needs to be written to the local cache.
        Assert.IsTrue(localSession.PutStreamCallCount > 0, "The node metadata must still be written to the local cache.");
        Assert.AreEqual(1, localSession.AddOrGetContentHashListCallCount, "The content hash list must still be added to the local cache.");
    }

    [TestMethod]
    public async Task AddNodeUploadsContentWhenRemoteCacheIsWritable()
    {
        (CasCacheClient cacheClient, RecordingCacheSession localSession, RecordingCacheSession remoteSession) = CreateCacheClient(remoteCacheIsReadOnly: false);

        await using (cacheClient)
        {
            await cacheClient.AddNodeInternalAsync(CreateNodeContext(), pathSet: null, CreateNodeBuildResult(), CancellationToken.None);
        }

        Assert.IsTrue(remoteSession.PinCallCount > 0, "The remote session should be pinned to determine what to upload.");
        Assert.IsTrue(remoteSession.PutStreamCallCount > 0, "Content should be uploaded when the remote cache is writable.");
        Assert.IsTrue(localSession.PutStreamCallCount > 0, "The node metadata should be written to the local cache.");
    }

    [TestMethod]
    public async Task AsyncPublicationRetainsPathSetUntilShutdownDrainsItsStrongFingerprint()
    {
        using IContentHasher hasher = HashInfoLookup.Find(HashType.Vso0).CreateContentHasher();
        GatedInputHasher inputHasher = new(hasher.Info.EmptyHash.ToHashByteArray());
        FingerprintFactory fingerprintFactory = new(
            hasher,
            inputHasher,
            new PluginSettings { RepoRoot = RepoRoot },
            new PathNormalizer(RepoRoot, Path.Combine(RepoRoot, "packages")));
        (CasCacheClient cacheClient, RecordingCacheSession localSession, _) = CreateCacheClient(
            remoteCacheIsReadOnly: true,
            enableAsyncPublishing: true,
            fingerprintFactory: new FixedFingerprintFactory(hasher, fingerprintFactory));

        await using (cacheClient)
        {
            // The helper returns only a weak reference after the public AddNodeAsync call has completed.
            WeakReference pathSetReference = await QueueNodeForAsyncPublicationAsync(cacheClient);
            try
            {
                Assert.AreSame(
                    inputHasher.Started,
                    await Task.WhenAny(inputHasher.Started, Task.Delay(TimeSpan.FromSeconds(30))),
                    "The queued publisher did not begin computing the strong fingerprint.");

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.IsTrue(pathSetReference.IsAlive, "The publisher must retain its PathSet while strong hashing is in flight.");

                Task shutdown = cacheClient.ShutdownAsync(CancellationToken.None);
                Assert.IsFalse(shutdown.IsCompleted, "Shutdown must wait for the queued publisher's strong fingerprint.");
                Assert.AreEqual(0, localSession.AddOrGetContentHashListCallCount);

                inputHasher.Release();
                await shutdown;

                Assert.AreEqual(1, inputHasher.HashCallCount);
                Assert.AreEqual(1, localSession.AddOrGetContentHashListCallCount);
                StrongFingerprint published = localSession.LastAddedStrongFingerprint;
                Fingerprint? expected = await fingerprintFactory.GetStrongFingerprintAsync(CreatePublicationPathSet());
                Assert.IsNotNull(expected);
                CollectionAssert.AreEqual(expected.Hash, published.Selector.Output);
                ContentHash expectedPathSetHash = hasher.GetContentHash(JsonSerializer.SerializeToUtf8Bytes(CreatePublicationPathSet(), SourceGenerationContext.Default.PathSet));
                Assert.AreEqual(expectedPathSetHash, published.Selector.ContentHash);
                Assert.IsFalse(localSession.ShutdownStarted, "Publication must finish before the cache session's lifetime ends.");
            }
            finally
            {
                inputHasher.Release();
                await cacheClient.ShutdownAsync(CancellationToken.None);
            }
        }
    }

    // Keep the caller's PathSet local out of the test's frame when forcing a collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<WeakReference> QueueNodeForAsyncPublicationAsync(CasCacheClient cacheClient)
    {
        PathSet pathSet = CreatePublicationPathSet();
        WeakReference reference = new(pathSet);
        return CompleteAsync(cacheClient.AddNodeAsync(CreateNodeContext(), pathSet, Array.Empty<string>(), _ => CreateNodeBuildResult(), CancellationToken.None), reference);

        static async Task<WeakReference> CompleteAsync(Task<NodeBuildResult> addNode, WeakReference reference)
        {
            await addNode;
            return reference;
        }
    }

    private static PathSet CreatePublicationPathSet()
        => new(new[] { new ObservedPathEntry("{RepoRoot}input.cs", ObservationType.FileContentRead) });

    private static (CasCacheClient CacheClient, RecordingCacheSession LocalSession, RecordingCacheSession RemoteSession) CreateCacheClient(
        bool remoteCacheIsReadOnly,
        bool enableAsyncPublishing = false,
        IFingerprintFactory? fingerprintFactory = null)
    {
#pragma warning disable CA2000 // Ownership of the hasher, caches, sessions and logger is transferred to the cache client, which disposes them.
        // Create a hasher rather than using HashInfoLookup.GetContentHasher, which returns a process-wide
        // instance that must not be disposed. CacheClient disposes the hasher it is given.
        IContentHasher hasher = HashInfoLookup.Find(HashType.Vso0).CreateContentHasher();

        // The local session reports content as present so nothing needs to be ingested from disk.
        RecordingCacheSession localSession = new("local", pinSucceeds: true);

        // The remote session reports content as missing, so a writable remote would upload it.
        RecordingCacheSession remoteSession = new("remote", pinSucceeds: false);

        RecordingCache localCache = new(localSession);
        RecordingCache remoteCache = new(remoteSession);

        TwoLevelCacheConfiguration twoLevelCacheConfiguration = new()
        {
            RemoteCacheIsReadOnly = remoteCacheIsReadOnly,
            AlwaysUpdateFromRemote = true,
        };

        CasCacheClient cacheClient = new(
            new Context(new Logger()),
            fingerprintFactory ?? new FixedFingerprintFactory(hasher),
            localCache,
            localSession,
            (remoteCache, remoteSession, twoLevelCacheConfiguration),
            hasher,
            RepoRoot,
            nugetPackageRoot: Path.Combine(RepoRoot, "packages"),
            getFileRealizationMode: _ => FileRealizationMode.Copy,
            maxConcurrentCacheContentOperations: 1,
            enableAsyncPublishing,
            enableAsyncMaterialization: false,
            skipUnchangedOutputFiles: false,
            touchOutputFiles: false);
#pragma warning restore CA2000

        return (cacheClient, localSession, remoteSession);
    }

    // ProjectInstance is only stored by NodeContext and is not used by the code under test, so this avoids
    // depending on MSBuild toolset resolution.
    private static NodeContext CreateNodeContext()
        => new(
            RepoRoot,
            projectInstance: null!,
            Array.Empty<NodeContext>(),
            "project.csproj",
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>(),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static NodeBuildResult CreateNodeBuildResult()
        => new(
            new SortedDictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase)
            {
                ["output.dll"] = ContentHash.Random(HashType.Vso0),
            },
            new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<NodeTargetResult>(),
            DateTime.UtcNow,
            DateTime.UtcNow,
            buildId: null);

    private sealed class FixedFingerprintFactory : IFingerprintFactory
    {
        private readonly Fingerprint _fingerprint;
        private readonly FingerprintFactory? _strongFingerprintFactory;

        public FixedFingerprintFactory(IContentHasher hasher, FingerprintFactory? strongFingerprintFactory = null)
        {
            _fingerprint = new Fingerprint(hasher.Info.EmptyHash.ToHashByteArray(), Array.Empty<FingerprintEntry>());
            _strongFingerprintFactory = strongFingerprintFactory;
        }

        public Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext) => Task.FromResult<Fingerprint?>(_fingerprint);

        public PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations) => null;

        public Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet)
            => _strongFingerprintFactory?.GetStrongFingerprintAsync(pathSet) ?? Task.FromResult<Fingerprint?>(_fingerprint);

        public bool MatchesCurrentState(PathSet? cachedPathSet) => true;
    }

    private sealed class GatedInputHasher : IInputHasher
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]?> _hash = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly byte[] _hashBytes;
        private int _hashCallCount;

        public GatedInputHasher(byte[] hashBytes) => _hashBytes = hashBytes;

        public Task Started => _started.Task;

        public int HashCallCount => Volatile.Read(ref _hashCallCount);

        public bool ContainsPath(string absolutePath) => true;

        public ValueTask<byte[]?> GetHashAsync(string absolutePath)
        {
            Interlocked.Increment(ref _hashCallCount);
            _started.TrySetResult(true);
            return new ValueTask<byte[]?>(_hash.Task);
        }

        public void Release() => _hash.TrySetResult(_hashBytes);
    }
}
