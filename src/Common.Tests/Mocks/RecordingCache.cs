// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.UtilitiesCore;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;

namespace Microsoft.MSBuildCache.Tests;

/// <summary>
/// A minimal <see cref="ICache"/> which hands out a fixed <see cref="RecordingCacheSession"/>.
/// Members which the tests do not exercise throw.
/// </summary>
internal sealed class RecordingCache : ICache
{
    private readonly RecordingCacheSession _session;

    public RecordingCache(RecordingCacheSession session) => _session = session;

    public Guid Id { get; } = Guid.NewGuid();

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

    public CreateSessionResult<ICacheSession> CreateSession(Context context, string name, ImplicitPin implicitPin)
        => new(_session);

    public Task<GetStatsResult> GetStatsAsync(Context context) => Task.FromResult(new GetStatsResult(new CounterSet()));

    public IAsyncEnumerable<StructResult<StrongFingerprint>> EnumerateStrongFingerprints(Context context) => throw new NotSupportedException();
}
