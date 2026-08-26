// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.MemoizationStore.Interfaces.Results;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace Microsoft.MSBuildCache.Tests;

/// <summary>
/// An <see cref="ICacheSession"/> which records the calls made against it so tests can assert which
/// operations a cache client did, or did not, perform. Members which the tests do not exercise throw.
/// </summary>
internal sealed class RecordingCacheSession : ICacheSession
{
    private readonly bool _pinSucceeds;

    /// <param name="name">The session name.</param>
    /// <param name="pinSucceeds">
    /// When true, pins report content as already present, so a caller has nothing to upload.
    /// When false, pins report content as missing, so a caller will try to upload it.
    /// </param>
    public RecordingCacheSession(string name, bool pinSucceeds)
    {
        Name = name;
        _pinSucceeds = pinSucceeds;
    }

    public string Name { get; }

    public int PinCallCount { get; private set; }

    public int PutStreamCallCount { get; private set; }

    public int PutFileCallCount { get; private set; }

    public int AddOrGetContentHashListCallCount { get; private set; }

    public bool StartupCompleted => true;

    public bool StartupStarted => true;

    public bool ShutdownCompleted { get; private set; }

    public bool ShutdownStarted { get; private set; }

    public Task<BoolResult> StartupAsync(Context context) => Task.FromResult(BoolResult.Success);

    public Task<BoolResult> ShutdownAsync(Context context)
    {
        ShutdownStarted = true;
        ShutdownCompleted = true;
        return Task.FromResult(BoolResult.Success);
    }

    public void Dispose()
    {
    }

    /* Content session */

    public Task<PinResult> PinAsync(Context context, ContentHash contentHash, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        PinCallCount++;
        return Task.FromResult(_pinSucceeds ? PinResult.Success : PinResult.ContentNotFound);
    }

    public Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(Context context, IReadOnlyList<ContentHash> contentHashes, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        PinCallCount++;
        PinResult result = _pinSucceeds ? PinResult.Success : PinResult.ContentNotFound;
        return Task.FromResult(contentHashes.Select((_, index) => Task.FromResult(new Indexed<PinResult>(result, index))));
    }

    public Task<IEnumerable<Task<Indexed<PinResult>>>> PinAsync(Context context, IReadOnlyList<ContentHash> contentHashes, PinOperationConfiguration config)
        => PinAsync(context, contentHashes, CancellationToken.None);

    public Task<OpenStreamResult> OpenStreamAsync(Context context, ContentHash contentHash, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => Task.FromResult(new OpenStreamResult(new MemoryStream(Array.Empty<byte>())));

    public Task<PutResult> PutStreamAsync(Context context, HashType hashType, Stream stream, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        PutStreamCallCount++;
        return Task.FromResult(new PutResult(ContentHash.Random(hashType), contentSize: 0));
    }

    public Task<PutResult> PutStreamAsync(Context context, ContentHash contentHash, Stream stream, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        PutStreamCallCount++;
        return Task.FromResult(new PutResult(contentHash, contentSize: 0));
    }

    public Task<PutResult> PutFileAsync(Context context, HashType hashType, AbsolutePath path, FileRealizationMode realizationMode, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        PutFileCallCount++;
        return Task.FromResult(new PutResult(ContentHash.Random(hashType), contentSize: 0));
    }

    public Task<PutResult> PutFileAsync(Context context, ContentHash contentHash, AbsolutePath path, FileRealizationMode realizationMode, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        PutFileCallCount++;
        return Task.FromResult(new PutResult(contentHash, contentSize: 0));
    }

    public Task<PlaceFileResult> PlaceFileAsync(Context context, ContentHash contentHash, AbsolutePath path, FileAccessMode accessMode, FileReplacementMode replacementMode, FileRealizationMode realizationMode, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => throw new NotSupportedException();

    public Task<IEnumerable<Task<Indexed<PlaceFileResult>>>> PlaceFileAsync(Context context, IReadOnlyList<ContentHashWithPath> hashesWithPaths, FileAccessMode accessMode, FileReplacementMode replacementMode, FileRealizationMode realizationMode, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => throw new NotSupportedException();

    /* Memoization session */

    public async IAsyncEnumerable<GetSelectorResult> GetSelectors(Context context, Fingerprint weakFingerprint, [EnumeratorCancellation] CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<GetContentHashListResult> GetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => Task.FromResult(new GetContentHashListResult(default(ContentHashListWithDeterminism)));

    public Task<AddOrGetContentHashListResult> AddOrGetContentHashListAsync(Context context, StrongFingerprint strongFingerprint, ContentHashListWithDeterminism contentHashListWithDeterminism, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
    {
        AddOrGetContentHashListCallCount++;
        return Task.FromResult(new AddOrGetContentHashListResult(default(ContentHashListWithDeterminism)));
    }

    public Task<BoolResult> IncorporateStrongFingerprintsAsync(Context context, IEnumerable<Task<StrongFingerprint>> strongFingerprints, CancellationToken cts, UrgencyHint urgencyHint = UrgencyHint.Nominal)
        => Task.FromResult(BoolResult.Success);
}
