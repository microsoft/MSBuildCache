// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.AzurePipelines.Tests;

[TestClass]
public class SelectorPublicationTests
{
    [TestMethod]
    public async Task ConcurrentPublicationsMergeTheConflictWinner()
    {
        Selector existingSelector = CreateSelector(1);
        Selector firstSelector = CreateSelector(2);
        Selector secondSelector = CreateSelector(3);
        Fingerprint weakFingerprint = new("01");
        const string Universe = "universe";
        string latestKey = PipelineCachingCacheClient.ComputeSelectorsReadKey(Universe, weakFingerprint);
        PublicationState initial = new("initial", new[] { existingSelector });
        ConcurrentDictionary<string, PublicationState> entries = new();
        ConcurrentQueue<string> conflictQueries = new();
        PublicationState? firstWinner = null;
        PublicationState? finalWinner = null;
        string? finalWriteKey = null;
        int initialAttempts = 0;
        TaskCompletionSource<bool> bothInitialAttemptsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> firstWinnerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<PublicationState?> QueryAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key == latestKey)
            {
                return Task.FromResult<PublicationState?>(initial);
            }

            entries.TryGetValue(key, out PublicationState? entry);
            return Task.FromResult(entry);
        }

        Task<PublicationState> GetConflictWinnerAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            conflictQueries.Enqueue(key);
            Assert.IsTrue(entries.TryGetValue(key, out PublicationState? entry));
            return Task.FromResult(entry);
        }

        async Task<bool> TryPublishAsync(
            PublicationState? predecessor,
            IReadOnlyCollection<Selector> selectors,
            string key,
            CancellationToken cancellationToken)
        {
            Assert.IsNotNull(predecessor);

            if (predecessor.Id == initial.Id)
            {
                if (Interlocked.Increment(ref initialAttempts) == 2)
                {
                    bothInitialAttemptsStarted.SetResult(true);
                }

                await bothInitialAttemptsStarted.Task;

                if (selectors.Contains(firstSelector))
                {
                    firstWinner = new PublicationState("first", selectors);
                    Assert.IsTrue(entries.TryAdd(key, firstWinner));
                    firstWinnerReady.SetResult(true);
                    return true;
                }

                await firstWinnerReady.Task;
                return false;
            }

            Assert.AreEqual(firstWinner!.Id, predecessor.Id);
            finalWinner = new PublicationState("final", selectors);
            finalWriteKey = key;
            Assert.IsTrue(entries.TryAdd(key, finalWinner));
            return true;
        }

        Task<bool> firstPublication = SelectorPublication.AddAsync(
            firstSelector,
            latestKey,
            QueryAsync,
            GetConflictWinnerAsync,
            (state, _) => Task.FromResult<IReadOnlyCollection<Selector>>(state!.Selectors),
            state => PipelineCachingCacheClient.ComputeSelectorsWriteKey(Universe, weakFingerprint, state?.Id),
            TryPublishAsync,
            CancellationToken.None);
        Task<bool> secondPublication = SelectorPublication.AddAsync(
            secondSelector,
            latestKey,
            QueryAsync,
            GetConflictWinnerAsync,
            (state, _) => Task.FromResult<IReadOnlyCollection<Selector>>(state!.Selectors),
            state => PipelineCachingCacheClient.ComputeSelectorsWriteKey(Universe, weakFingerprint, state?.Id),
            TryPublishAsync,
            CancellationToken.None);

        Assert.IsTrue(await firstPublication);
        Assert.IsTrue(await secondPublication);
        CollectionAssert.AreEquivalent(
            new[] { existingSelector, firstSelector, secondSelector },
            finalWinner!.Selectors.ToArray());
        string initialWriteKey = PipelineCachingCacheClient.ComputeSelectorsWriteKey(Universe, weakFingerprint, initial.Id);
        CollectionAssert.AreEqual(new[] { initialWriteKey }, conflictQueries.ToArray());
        Assert.AreEqual(
            PipelineCachingCacheClient.ComputeSelectorsWriteKey(Universe, weakFingerprint, firstWinner!.Id),
            finalWriteKey);
        StringAssert.StartsWith(latestKey, "selector6|", StringComparison.Ordinal);
    }

    [TestMethod]
    public void OutputKeyIncludesSelectorOutput()
    {
        Selector firstSelector = CreateSelector(1);
        Selector secondSelector = CreateSelector(2);
        Fingerprint weakFingerprint = new("01");
        StrongFingerprint first = new(weakFingerprint, firstSelector);
        StrongFingerprint second = new(weakFingerprint, secondSelector);

        string firstKey = PipelineCachingCacheClient.ComputeOutputKey("universe", first, forWrite: false, writeId: 0);
        string secondKey = PipelineCachingCacheClient.ComputeOutputKey("universe", second, forWrite: false, writeId: 0);

        Assert.AreNotEqual(firstKey, secondKey);
        StringAssert.StartsWith(firstKey, "outputs6|", StringComparison.Ordinal);
        StringAssert.Contains(firstKey, "|01|", StringComparison.Ordinal);
        StringAssert.Contains(secondKey, "|02|", StringComparison.Ordinal);
    }

    private static Selector CreateSelector(byte output)
    {
        byte[] hashBytes = Enumerable.Repeat((byte)0x5a, 32).ToArray();
        return new Selector(new ContentHash(HashType.SHA256, hashBytes), new[] { output });
    }

    private sealed class PublicationState
    {
        public PublicationState(string id, IEnumerable<Selector> selectors)
        {
            Id = id;
            Selectors = new HashSet<Selector>(selectors);
        }

        public string Id { get; }

        public HashSet<Selector> Selectors { get; }
    }
}
