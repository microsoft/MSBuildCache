// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
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

    private static (CasCacheClient CacheClient, RecordingCacheSession LocalSession, RecordingCacheSession RemoteSession) CreateCacheClient(bool remoteCacheIsReadOnly)
    {
        IContentHasher hasher = HashInfoLookup.GetContentHasher(HashType.Vso0);

        // The local session reports content as present so nothing needs to be ingested from disk.
        RecordingCacheSession localSession = new("local", pinSucceeds: true);

        // The remote session reports content as missing, so a writable remote would upload it.
        RecordingCacheSession remoteSession = new("remote", pinSucceeds: false);

#pragma warning disable CA2000 // Ownership of the caches, sessions and logger is transferred to the cache client, which disposes them.
        RecordingCache localCache = new(localSession);
        RecordingCache remoteCache = new(remoteSession);

        TwoLevelCacheConfiguration twoLevelCacheConfiguration = new()
        {
            RemoteCacheIsReadOnly = remoteCacheIsReadOnly,
            AlwaysUpdateFromRemote = true,
        };

        CasCacheClient cacheClient = new(
            new Context(new Logger()),
            new FixedFingerprintFactory(hasher),
            localCache,
            localSession,
            (remoteCache, remoteSession, twoLevelCacheConfiguration),
            hasher,
            RepoRoot,
            nugetPackageRoot: Path.Combine(RepoRoot, "packages"),
            getFileRealizationMode: _ => FileRealizationMode.Copy,
            maxConcurrentCacheContentOperations: 1,
            enableAsyncPublishing: false,
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

        public FixedFingerprintFactory(IContentHasher hasher)
            => _fingerprint = new Fingerprint(hasher.Info.EmptyHash.ToHashByteArray(), Array.Empty<FingerprintEntry>());

        public Task<Fingerprint?> GetWeakFingerprintAsync(NodeContext nodeContext) => Task.FromResult<Fingerprint?>(_fingerprint);

        public PathSet? GetPathSet(NodeContext nodeContext, IReadOnlyCollection<ObservedAccess> observations) => null;

        public Task<Fingerprint?> GetStrongFingerprintAsync(PathSet? pathSet) => Task.FromResult<Fingerprint?>(_fingerprint);

        public bool MatchesCurrentState(PathSet? cachedPathSet) => true;
    }
}
