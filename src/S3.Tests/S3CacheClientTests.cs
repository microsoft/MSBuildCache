// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3.Model;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.MSBuildCache.Caching;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.S3.Tests;

[TestClass]
public class S3CacheClientTests
{
    private static readonly byte[] WeakFingerprintBytes = [1, 2, 3, 4];

    private static readonly ContentHash PathSetHash = new(HashType.Vso0, new byte[33]);

    // Two full parts and a short trailing one.
    private const int MultipartContentLength = (int)(2 * S3CacheClientHarness.MinimumPartSizeBytes) + 1;

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    public async Task PlaceContentUsesRangedReadsAtMultipartThreshold(int offsetFromThreshold, bool expectRanged)
    {
        const long ThresholdBytes = 1024 * 1024;

        await using S3CacheClientHarness harness = new(multipartThresholdBytes: ThresholdBytes);
        byte[] content = S3CacheClientHarness.CreateContent((int)ThresholdBytes + offsetFromThreshold);
        ContentHash contentHash = harness.PutContentInBucket(content);
        string destination = harness.PathUnderRepoRoot("output.bin");

        await harness.PlaceContentAsync(contentHash, destination);

        Assert.HasCount(1, harness.S3.Reads);
        Assert.AreEqual(expectRanged, harness.S3.Reads[0].ByteRange is not null);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
    }

    [TestMethod]
    public async Task MultipartDownloadPartsCoverObject()
    {
        await using S3CacheClientHarness harness = new(multipartThresholdBytes: 1024, multipartPartSizeBytes: 1);
        byte[] content = S3CacheClientHarness.CreateContent(MultipartContentLength);
        ContentHash contentHash = harness.PutContentInBucket(content);
        string destination = harness.PathUnderRepoRoot("output.bin");

        await harness.PlaceContentAsync(contentHash, destination);

        List<ByteRange> ranges = harness.S3.Reads
            .Select(read => read.ByteRange)
            .OfType<ByteRange>()
            .OrderBy(range => range.Start)
            .ToList();

        Assert.HasCount(3, harness.S3.Reads);
        Assert.HasCount(3, ranges);
        Assert.AreEqual(0L, ranges[0].Start);
        Assert.AreEqual((long)content.Length - 1, ranges[^1].End);
        for (int i = 1; i < ranges.Count; i++)
        {
            Assert.AreEqual(ranges[i - 1].End + 1, ranges[i].Start);
        }

        Assert.AreEqual(S3CacheClientHarness.MinimumPartSizeBytes, ranges.Max(range => range.End - range.Start + 1));
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
    }

    [TestMethod]
    public async Task MisassembledMultipartDownloadIsRejected()
    {
        await using S3CacheClientHarness harness = new(multipartThresholdBytes: 1024, multipartPartSizeBytes: 1);
        byte[] content = S3CacheClientHarness.CreateContent(MultipartContentLength);
        ContentHash contentHash = harness.PutContentInBucket(content);
        string destination = harness.PathUnderRepoRoot("output.bin");

        // Serve every part from the start of the object.
        harness.S3.RewriteBody = (_, body) => content.AsSpan(0, body.Length).ToArray();

        CacheException exception = await Assert.ThrowsExactlyAsync<CacheException>(
            () => harness.PlaceContentAsync(contentHash, destination));

        StringAssert.Contains(exception.Message, "content hash", StringComparison.Ordinal);
        Assert.IsFalse(harness.LocalCas.Contains(contentHash));
        Assert.AreEqual(0, harness.LocalCas.PutFileCount);
        Assert.AreEqual(0, Directory.GetFiles(harness.RepoRoot).Length);
    }

    [TestMethod]
    public async Task MultipartDownloadRespectsMaxConcurrentParts()
    {
        const int MaxConcurrentParts = 2;

        await using S3CacheClientHarness harness = new(
            multipartThresholdBytes: 1024,
            multipartPartSizeBytes: 1,
            maxConcurrentPartsPerObject: MaxConcurrentParts,
            maxConcurrentCacheContentOperations: 32);
        byte[] content = S3CacheClientHarness.CreateContent(MultipartContentLength);
        ContentHash contentHash = harness.PutContentInBucket(content);
        string destination = harness.PathUnderRepoRoot("output.bin");

        ConcurrencyProbe probe = new();
        harness.S3.BeforeRead = _ => probe.OccupyAsync(TimeSpan.FromMilliseconds(100));

        await harness.PlaceContentAsync(contentHash, destination);

        Assert.HasCount(3, harness.S3.Reads);
        Assert.AreEqual(
            MaxConcurrentParts,
            probe.Peak);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(destination));
    }

    [TestMethod]
    public async Task MissingEntryIsNotRetried()
    {
        await using S3CacheClientHarness harness = new();

        object? entry = await harness.GetCacheEntryAsync(CreateStrongFingerprint());

        Assert.IsNull(entry);
        Assert.HasCount(1, harness.S3.Reads);
    }

    [TestMethod]
    public async Task UnreadableEntryIsCacheMiss()
    {
        await using S3CacheClientHarness harness = new();
        StrongFingerprint fingerprint = CreateStrongFingerprint();
        harness.S3.Put(harness.Keys.GetEntry(fingerprint), S3CacheClientHarness.CreateContent(64));
        harness.S3.BeforeRead = _ => throw new IOException("The connection was closed.");

        object? entry = await harness.GetCacheEntryAsync(fingerprint);

        Assert.IsNull(entry);
        Assert.HasCount(3, harness.S3.Reads);
    }

    [TestMethod]
    public async Task CacheEntryReturnsNodeBuildResult()
    {
        await using S3CacheClientHarness harness = new();
        StrongFingerprint fingerprint = CreateStrongFingerprint();
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(512);
        harness.S3.Put(harness.Keys.GetEntry(fingerprint), nodeBuildResult);

        object? entry = await harness.GetCacheEntryAsync(fingerprint);

        Assert.IsNotNull(entry);
        CollectionAssert.AreEqual(nodeBuildResult, await harness.GetNodeBuildResultAsync(entry));
    }

    [TestMethod]
    public async Task MismatchedContentIsNotAddedToLocalCache()
    {
        await using S3CacheClientHarness harness = new();
        ContentHash expectedContentHash = harness.HashOf(S3CacheClientHarness.CreateContent(4096));
        harness.S3.Put(harness.Keys.GetCas(expectedContentHash), S3CacheClientHarness.CreateContent(2048));
        string destination = harness.PathUnderRepoRoot("output.bin");

        CacheException exception = await Assert.ThrowsExactlyAsync<CacheException>(
            () => harness.PlaceContentAsync(expectedContentHash, destination));

        StringAssert.Contains(exception.Message, "content hash", StringComparison.Ordinal);
        Assert.IsFalse(harness.LocalCas.Contains(expectedContentHash));
        Assert.AreEqual(0, harness.LocalCas.PutFileCount);
        Assert.AreEqual(0, Directory.GetFiles(harness.RepoRoot).Length);
    }

    [TestMethod]
    public async Task PlaceFilesFailsForMissingContent()
    {
        await using S3CacheClientHarness harness = new();
        StrongFingerprint fingerprint = CreateStrongFingerprint();
        harness.S3.Put(harness.Keys.GetEntry(fingerprint), S3CacheClientHarness.CreateContent(64));
        object? entry = await harness.GetCacheEntryAsync(fingerprint);
        Assert.IsNotNull(entry);

        ContentHash missingContentHash = harness.HashOf(S3CacheClientHarness.CreateContent(128));
        string destination = harness.PathUnderRepoRoot("output.bin");

        CacheException exception = await Assert.ThrowsExactlyAsync<CacheException>(
            () => harness.PlaceFilesAsync(entry, new Dictionary<string, ContentHash> { [destination] = missingContentHash }));

        StringAssert.Contains(exception.Message, "Failed to materialize", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, S3CacheClientHarness.BucketName, StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, harness.Keys.GetCas(missingContentHash), StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OpenStreamPrefersLocalCache()
    {
        await using S3CacheClientHarness harness = new();
        byte[] bytes = S3CacheClientHarness.CreateContent(256);
        ContentHash contentHash = harness.HashOf(bytes);
        harness.LocalCas.Add(contentHash, bytes);

        OpenStreamResult result = await harness.OpenStreamAsync(contentHash);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(bytes, await ReadAllBytesAsync(result));
        Assert.HasCount(0, harness.S3.Reads);
    }

    [TestMethod]
    public async Task OpenStreamAddsRemoteContentToLocalCache()
    {
        await using S3CacheClientHarness harness = new();
        byte[] bytes = S3CacheClientHarness.CreateContent(256);
        ContentHash contentHash = harness.PutContentInBucket(bytes);

        OpenStreamResult result = await harness.OpenStreamAsync(contentHash);

        Assert.IsTrue(result.Succeeded);
        CollectionAssert.AreEqual(bytes, await ReadAllBytesAsync(result));
        Assert.IsTrue(harness.LocalCas.Contains(contentHash));
    }

    [TestMethod]
    public async Task OpenStreamFailsForMissingContent()
    {
        await using S3CacheClientHarness harness = new();
        ContentHash contentHash = harness.HashOf(S3CacheClientHarness.CreateContent(256));

        OpenStreamResult result = await harness.OpenStreamAsync(contentHash);

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task OpenStreamRejectsMismatchedContent()
    {
        await using S3CacheClientHarness harness = new();
        ContentHash expectedContentHash = harness.HashOf(S3CacheClientHarness.CreateContent(256));
        harness.S3.Put(harness.Keys.GetCas(expectedContentHash), S3CacheClientHarness.CreateContent(128));

        await Assert.ThrowsExactlyAsync<CacheException>(() => harness.OpenStreamAsync(expectedContentHash));

        Assert.IsFalse(harness.LocalCas.Contains(expectedContentHash));
    }

    [TestMethod]
    public async Task AddNodeSkipsWhenRemoteCacheIsReadOnly()
    {
        await using S3CacheClientHarness harness = new(remoteCacheIsReadOnly: true);
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        AddNodeResult result = await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash>(),
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        Assert.AreEqual(AddNodeResult.Skipped, result);
        Assert.HasCount(0, harness.S3.WrittenKeys);
        Assert.AreEqual(0, harness.S3.MetadataReadCount);
    }

    [TestMethod]
    public async Task AddNodeWritesSelectorAfterEntry()
    {
        await using S3CacheClientHarness harness = new();
        StrongFingerprint fingerprint = CreateStrongFingerprint();
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        AddNodeResult result = await harness.AddNodeAsync(
            fingerprint,
            new Dictionary<string, ContentHash>(),
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        Assert.AreEqual(AddNodeResult.Added, result);

        string entryKey = harness.Keys.GetEntry(fingerprint);
        string selectorKey = harness.Keys.GetSelector(fingerprint);
        CollectionAssert.AreEqual(
            new[] { entryKey, selectorKey },
            harness.S3.WrittenKeys.ToArray());
        CollectionAssert.AreEqual(nodeBuildResult, harness.S3.Read(entryKey));
        Assert.AreEqual(0, harness.S3.Read(selectorKey).Length);
    }

    [TestMethod]
    public async Task AddNodeRewritesExistingEntryAndSelector()
    {
        await using S3CacheClientHarness harness = new();
        StrongFingerprint fingerprint = CreateStrongFingerprint();
        harness.S3.Put(harness.Keys.GetEntry(fingerprint), S3CacheClientHarness.CreateContent(64));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(128);

        AddNodeResult result = await harness.AddNodeAsync(
            fingerprint,
            new Dictionary<string, ContentHash>(),
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        Assert.AreEqual(AddNodeResult.AlreadyExists, result);

        CollectionAssert.AreEqual(
            new[] { harness.Keys.GetEntry(fingerprint), harness.Keys.GetSelector(fingerprint) },
            harness.S3.WrittenKeys.ToArray());
    }

    [TestMethod]
    public async Task AddNodeRenewsExistingContent()
    {
        await using S3CacheClientHarness harness = new();
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.PutContentInBucket(output);
        string casKey = harness.Keys.GetCas(outputHash);
        harness.S3.Backdate(casKey, TimeSpan.FromDays(10));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [harness.PathUnderRepoRoot("output.bin")] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        CollectionAssert.Contains(harness.S3.RenewedKeys.ToArray(), casKey);
        CollectionAssert.DoesNotContain(harness.S3.WrittenKeys.ToArray(), casKey);
        CollectionAssert.AreEqual(output, harness.S3.Read(casKey));
    }

    [TestMethod]
    public async Task AddNodeRenewsAnExistingPathSet()
    {
        await using S3CacheClientHarness harness = new();
        byte[] pathSet = S3CacheClientHarness.CreateContent(256);
        ContentHash pathSetHash = harness.PutContentInBucket(pathSet);
        string casKey = harness.Keys.GetCas(pathSetHash);
        harness.S3.Backdate(casKey, TimeSpan.FromDays(10));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash>(),
            (harness.HashOf(nodeBuildResult), nodeBuildResult),
            (pathSetHash, pathSet));

        CollectionAssert.Contains(harness.S3.RenewedKeys.ToArray(), casKey);
        CollectionAssert.DoesNotContain(harness.S3.WrittenKeys.ToArray(), casKey);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(8, true)]
    public async Task AddNodeRenewsContentAtTheConfiguredAge(int ageInDays, bool expectRenewal)
    {
        await using S3CacheClientHarness harness = new(contentRenewalAgeDays: 7);
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.PutContentInBucket(output);
        string casKey = harness.Keys.GetCas(outputHash);
        harness.S3.Backdate(casKey, TimeSpan.FromDays(ageInDays));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [harness.PathUnderRepoRoot("output.bin")] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        Assert.AreEqual(expectRenewal, harness.S3.RenewedKeys.Contains(casKey));
    }

    [TestMethod]
    public async Task AddNodeUploadsWhenRenewalFails()
    {
        await using S3CacheClientHarness harness = new();
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.PutContentInBucket(output);
        string outputPath = harness.PathUnderRepoRoot("output.bin");
        await File.WriteAllBytesAsync(outputPath, output);
        harness.S3.BeforeCopy = _ => throw FakeAmazonS3Client.NoSuchKey(harness.Keys.GetCas(outputHash));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [outputPath] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        CollectionAssert.Contains(harness.S3.WrittenKeys.ToArray(), harness.Keys.GetCas(outputHash));
        CollectionAssert.AreEqual(output, harness.S3.Read(harness.Keys.GetCas(outputHash)));
    }

    [TestMethod]
    public async Task AddNodeUploadsContentTooLargeToCopy()
    {
        await using S3CacheClientHarness harness = new();
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.PutContentInBucket(output);
        string outputPath = harness.PathUnderRepoRoot("output.bin");
        await File.WriteAllBytesAsync(outputPath, output);
        string casKey = harness.Keys.GetCas(outputHash);
        harness.S3.OverrideContentLength(casKey, 5L * 1024 * 1024 * 1024);
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [outputPath] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        Assert.HasCount(0, harness.S3.Copies);
        CollectionAssert.Contains(harness.S3.WrittenKeys.ToArray(), casKey);
    }

    [TestMethod]
    public async Task AddNodeRenewsContentWithAFutureTimestamp()
    {
        await using S3CacheClientHarness harness = new(contentRenewalAgeDays: 7);
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.PutContentInBucket(output);
        string casKey = harness.Keys.GetCas(outputHash);
        harness.S3.Backdate(casKey, TimeSpan.FromHours(-2));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [harness.PathUnderRepoRoot("output.bin")] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        CollectionAssert.Contains(harness.S3.RenewedKeys.ToArray(), casKey);
    }

    [TestMethod]
    public async Task AddNodeSkipsExistingContent()
    {
        await using S3CacheClientHarness harness = new();
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.PutContentInBucket(output);
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        // The output is neither on disk nor in the local cache.
        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [harness.PathUnderRepoRoot("output.bin")] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        CollectionAssert.DoesNotContain(harness.S3.WrittenKeys.ToArray(), harness.Keys.GetCas(outputHash));
    }

    [TestMethod]
    public async Task AddNodeUploadsOutputFromDisk()
    {
        await using S3CacheClientHarness harness = new();
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.HashOf(output);
        string outputPath = harness.PathUnderRepoRoot("output.bin");
        await File.WriteAllBytesAsync(outputPath, output);
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash> { [outputPath] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        CollectionAssert.AreEqual(output, harness.S3.Read(harness.Keys.GetCas(outputHash)));
    }

    [TestMethod]
    public async Task AddNodeFailsWhenOutputChangedAfterHashing()
    {
        await using S3CacheClientHarness harness = new();
        byte[] original = S3CacheClientHarness.CreateContent(4096);
        ContentHash originalHash = harness.HashOf(original);
        string outputPath = harness.PathUnderRepoRoot("output.bin");
        await File.WriteAllBytesAsync(outputPath, S3CacheClientHarness.CreateContent(4097));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        CacheException exception = await Assert.ThrowsExactlyAsync<CacheException>(
            () => harness.AddNodeAsync(
                CreateStrongFingerprint(),
                new Dictionary<string, ContentHash> { [outputPath] = originalHash },
                (harness.HashOf(nodeBuildResult), nodeBuildResult)));

        StringAssert.Contains(exception.Message, "changed", StringComparison.Ordinal);
        CollectionAssert.DoesNotContain(harness.S3.WrittenKeys.ToArray(), harness.Keys.GetCas(originalHash));
    }

    [TestMethod]
    public async Task AddNodeAddsPathSetToLocalCache()
    {
        await using S3CacheClientHarness harness = new();
        byte[] pathSet = S3CacheClientHarness.CreateContent(256);
        ContentHash pathSetHash = harness.HashOf(pathSet);
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(),
            new Dictionary<string, ContentHash>(),
            (harness.HashOf(nodeBuildResult), nodeBuildResult),
            (pathSetHash, pathSet));

        CollectionAssert.AreEqual(pathSet, harness.S3.Read(harness.Keys.GetCas(pathSetHash)));
        Assert.IsTrue(harness.LocalCas.Contains(pathSetHash));
    }

    [TestMethod]
    public async Task AddNodeFailsForMissingContent()
    {
        await using S3CacheClientHarness harness = new();
        ContentHash outputHash = harness.HashOf(S3CacheClientHarness.CreateContent(4096));
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        CacheException exception = await Assert.ThrowsExactlyAsync<CacheException>(
            () => harness.AddNodeAsync(
                CreateStrongFingerprint(),
                new Dictionary<string, ContentHash> { [harness.PathUnderRepoRoot("output.bin")] = outputHash },
                (harness.HashOf(nodeBuildResult), nodeBuildResult)));

        StringAssert.Contains(exception.Message, "does not exist", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AddNodeRetriesFailedUpload()
    {
        await using S3CacheClientHarness harness = new();
        byte[] output = S3CacheClientHarness.CreateContent(4096);
        ContentHash outputHash = harness.HashOf(output);
        string firstPath = harness.PathUnderRepoRoot("first.bin");
        string secondPath = harness.PathUnderRepoRoot("second.bin");
        byte[] nodeBuildResult = S3CacheClientHarness.CreateContent(64);

        await Assert.ThrowsExactlyAsync<CacheException>(
            () => harness.AddNodeAsync(
                CreateStrongFingerprint(1),
                new Dictionary<string, ContentHash> { [firstPath] = outputHash },
                (harness.HashOf(nodeBuildResult), nodeBuildResult)));

        await File.WriteAllBytesAsync(secondPath, output);

        await harness.AddNodeAsync(
            CreateStrongFingerprint(2),
            new Dictionary<string, ContentHash> { [secondPath] = outputHash },
            (harness.HashOf(nodeBuildResult), nodeBuildResult));

        CollectionAssert.AreEqual(output, harness.S3.Read(harness.Keys.GetCas(outputHash)));
    }

    [TestMethod]
    public async Task GetSelectorsSkipsUnrecognizedKeys()
    {
        await using S3CacheClientHarness harness = new();
        Fingerprint weakFingerprint = new(WeakFingerprintBytes);
        StrongFingerprint first = CreateStrongFingerprint(1);
        StrongFingerprint second = CreateStrongFingerprint(2);
        harness.S3.Put(harness.Keys.GetSelector(first), []);
        harness.S3.Put(harness.Keys.GetSelector(second), []);

        harness.S3.Put($"{harness.Keys.GetSelectorPrefix(weakFingerprint)}written-by-something-else", []);

        List<Selector> selectors = await CollectAsync(harness.GetSelectors(weakFingerprint));

        CollectionAssert.AreEquivalent(new[] { first.Selector, second.Selector }, selectors);
    }

    [TestMethod]
    public async Task GetSelectorsFollowsContinuationTokens()
    {
        await using S3CacheClientHarness harness = new();
        harness.S3.MaxKeysPerPage = 1;
        Fingerprint weakFingerprint = new(WeakFingerprintBytes);
        StrongFingerprint[] fingerprints = [CreateStrongFingerprint(1), CreateStrongFingerprint(2), CreateStrongFingerprint(3)];
        foreach (StrongFingerprint fingerprint in fingerprints)
        {
            harness.S3.Put(harness.Keys.GetSelector(fingerprint), []);
        }

        List<Selector> selectors = await CollectAsync(harness.GetSelectors(weakFingerprint));

        CollectionAssert.AreEquivalent(fingerprints.Select(fingerprint => fingerprint.Selector).ToArray(), selectors);
    }

    [TestMethod]
    public async Task GetSelectorsIsEmptyWhenListingFails()
    {
        await using S3CacheClientHarness harness = new();
        Fingerprint weakFingerprint = new(WeakFingerprintBytes);
        harness.S3.Put(harness.Keys.GetSelector(CreateStrongFingerprint()), []);
        harness.S3.BeforeList = _ => throw new IOException("The connection was closed.");

        List<Selector> selectors = await CollectAsync(harness.GetSelectors(weakFingerprint));

        Assert.HasCount(0, selectors);
    }

    private static StrongFingerprint CreateStrongFingerprint(byte output = 42)
        => new(new Fingerprint(WeakFingerprintBytes), new Selector(PathSetHash, [output]));

    private static async Task<List<Selector>> CollectAsync(IAsyncEnumerable<Selector> selectors)
    {
        List<Selector> collected = [];
        await foreach (Selector selector in selectors)
        {
            collected.Add(selector);
        }

        return collected;
    }

    private static async Task<byte[]> ReadAllBytesAsync(OpenStreamResult result)
    {
        using Stream stream = result.Stream!;
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class ConcurrencyProbe
    {
        private int _inFlight;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public async Task OccupyAsync(TimeSpan duration)
        {
            int inFlight = Interlocked.Increment(ref _inFlight);

            int peak = Volatile.Read(ref _peak);
            while (inFlight > peak)
            {
                int previous = Interlocked.CompareExchange(ref _peak, inFlight, peak);
                if (previous == peak)
                {
                    break;
                }

                peak = previous;
            }

            await Task.Delay(duration);
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
