// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Sessions;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Caching;

namespace Microsoft.MSBuildCache.GitHubActions;

public sealed class MSBuildCacheGitHubActionsPlugin : MSBuildCachePluginBase
{
    protected override HashType HashType => HashType.SHA256;

    protected override async Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (Settings == null
            || NodeContextRepository == null
            || FingerprintFactory == null)
        {
            throw new InvalidOperationException();
        }

        FileLog fileLog = new(Path.Combine(Settings.LogDirectory, "CacheClient.log"));
#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed using Context.Logger.Dispose in the cache client implementation.
        Logger cacheLogger = new(fileLog);
#pragma warning restore CA2000 // Dispose objects before losing scope
        Context context = new(cacheLogger);

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by TwoLevelCache
        LocalCache localCache = LocalCacheFactory.Create(cacheLogger, Settings.LocalCacheRootPath, Settings.LocalCacheSizeInMegabytes);
#pragma warning restore CA2000 // Dispose objects before losing scope

        ICacheSession localCacheSession = await StartCacheSessionAsync(context, localCache, "local");

        return new GithubActionsCacheClient(
            context,
            FingerprintFactory,
            HashType,
            localCacheSession,
            cacheLogger,
            Settings.CacheUniverse,
            new AbsolutePath(Settings.RepoRoot),
            NodeContextRepository,
            GetFileRealizationMode,
            Settings.MaxConcurrentCacheContentOperations,
            Settings.RemoteCacheIsReadOnly,
            Settings.AsyncCachePublishing,
            Settings.AsyncCacheMaterialization);
    }

    private static async Task<ICacheSession> StartCacheSessionAsync(Context context, LocalCache cache, string name)
    {
        await cache.StartupAsync(context).ThrowIfFailure();
        CreateSessionResult<ICacheSession> cacheSessionResult = cache
            .CreateSession(context, name, ImplicitPin.PutAndGet)
            .ThrowIfFailure();
        ICacheSession session = cacheSessionResult.Session!;

        (await session.StartupAsync(context)).ThrowIfFailure();

        return session;
    }


}
