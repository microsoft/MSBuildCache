// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace Microsoft.MSBuildCache.S3.Tests;

internal sealed class FakeContentSession : IContentSession
{
    private readonly IContentHasher _hasher;
    private readonly ConcurrentDictionary<ContentHash, byte[]> _content = new();
    private int _putFileCount;

    public FakeContentSession(IContentHasher hasher) => _hasher = hasher;

    public string Name => nameof(FakeContentSession);

    public bool StartupCompleted => true;

    public bool StartupStarted => true;

    public bool ShutdownCompleted => true;

    public bool ShutdownStarted => true;

    public int PutFileCount => Volatile.Read(ref _putFileCount);

    public bool Contains(ContentHash contentHash) => _content.ContainsKey(contentHash);

    public void Add(ContentHash contentHash, byte[] bytes) => _content[contentHash] = bytes;

    public Task<BoolResult> StartupAsync(Context context) => Task.FromResult(BoolResult.Success);

    public Task<BoolResult> ShutdownAsync(Context context) => Task.FromResult(BoolResult.Success);

    public void Dispose()
    {
    }

    public Task<OpenStreamResult> OpenStreamAsync(
        Context context,
        ContentHash contentHash,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => Task.FromResult(
            _content.TryGetValue(contentHash, out byte[]? bytes)
                ? new OpenStreamResult(new MemoryStream(bytes, writable: false))
                : new OpenStreamResult(OpenStreamResult.ResultCode.ContentNotFound, $"No local content for {contentHash}."));

    public async Task<PlaceFileResult> PlaceFileAsync(
        Context context,
        ContentHash contentHash,
        AbsolutePath path,
        FileAccessMode accessMode,
        FileReplacementMode replacementMode,
        FileRealizationMode realizationMode,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        if (!_content.TryGetValue(contentHash, out byte[]? bytes))
        {
            return PlaceFileResult.ContentNotFound;
        }

        await File.WriteAllBytesAsync(path.Path, bytes, cancellationToken);
        return new PlaceFileResult(PlaceFileResult.ResultCode.PlacedWithCopy, bytes.Length, null, PlaceFileResult.Source.LocalCache);
    }

    public async Task<PutResult> PutFileAsync(
        Context context,
        ContentHash contentHash,
        AbsolutePath path,
        FileRealizationMode realizationMode,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        Interlocked.Increment(ref _putFileCount);

        byte[] bytes = await File.ReadAllBytesAsync(path.Path, cancellationToken);
        _content[contentHash] = bytes;

        if (realizationMode == FileRealizationMode.Move)
        {
            File.Delete(path.Path);
        }

        return new PutResult(contentHash, bytes.Length);
    }

    public async Task<PutResult> PutFileAsync(
        Context context,
        HashType hashType,
        AbsolutePath path,
        FileRealizationMode realizationMode,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path.Path, cancellationToken);
        return await PutFileAsync(context, _hasher.GetContentHash(bytes), path, realizationMode, cancellationToken, urgencyHint);
    }

    public async Task<PutResult> PutStreamAsync(
        Context context,
        HashType hashType,
        Stream stream,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        using MemoryStream buffer = new();
        await stream.CopyToAsync(buffer, cancellationToken);
        byte[] bytes = buffer.ToArray();

        ContentHash contentHash = _hasher.GetContentHash(bytes);
        _content[contentHash] = bytes;

        return new PutResult(contentHash, bytes.Length);
    }

    public Task<PutResult> PutStreamAsync(
        Context context,
        ContentHash contentHash,
        Stream stream,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => throw new NotSupportedException();

    public Task<PinResult> PinAsync(
        Context context,
        ContentHash contentHash,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => throw new NotSupportedException();

    public Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(
        Context context,
        IReadOnlyList<ContentHash> contentHashes,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => throw new NotSupportedException();

    public Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(
        Context context,
        IReadOnlyList<ContentHash> contentHashes,
        PinOperationConfiguration config)
        => throw new NotSupportedException();

    public Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceFileAsync(
        Context context,
        IReadOnlyList<ContentHashWithPath> hashesWithPaths,
        FileAccessMode accessMode,
        FileReplacementMode replacementMode,
        FileRealizationMode realizationMode,
        CancellationToken cancellationToken,
        UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => throw new NotSupportedException();
}

internal sealed class FakeCache : ICache
{
    public Guid Id { get; } = Guid.NewGuid();

    public bool StartupCompleted => true;

    public bool StartupStarted => true;

    public bool ShutdownCompleted => true;

    public bool ShutdownStarted => true;

    public Task<BoolResult> StartupAsync(Context context) => Task.FromResult(BoolResult.Success);

    public Task<BoolResult> ShutdownAsync(Context context) => Task.FromResult(BoolResult.Success);

    public Task<GetStatsResult> GetStatsAsync(Context context) => Task.FromResult(new GetStatsResult(new CounterSet()));

    public void Dispose()
    {
    }

    public CreateSessionResult<ICacheSession> CreateSession(Context context, string name, ImplicitPin implicitPin)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(Context context)
        => throw new NotSupportedException();
}
