// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Distributed.Blob;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Auth;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Cache.MemoizationStore.Distributed.Stores;
using BuildXL.Cache.MemoizationStore.Interfaces.Caches;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Sessions;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.SourceControl;

namespace Microsoft.MSBuildCache.AzureBlobStorage;

public sealed class MSBuildCacheAzureBlobStoragePlugin : MSBuildCachePluginBase<AzureBlobStoragePluginSettings>
{
    // Note: This is not in PluginSettings as that's configured through item metadata and thus makes it into MSBuild logs. This is a secret so that's not desirable.
    private const string AzureBlobConnectionStringEnvVar = "MSBUILDCACHE_CONNECTIONSTRING";

    // Although Azure Blob Storage is unrelated to Azure DevOps, Vso0 hashing is much faster than SHA256.
    protected override HashType HashType => HashType.Vso0;

    protected override async Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (Settings == null
            || NodeContextRepository == null
            || FingerprintFactory == null
            || ContentHasher == null
            || NugetPackageRoot == null)
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

        // We want our caches to be secure by default.  For Pipeline Caching, branches are isolated on the server-side.
        // For Blob L3, we need to isolate the cache namespace on the client-side.  We do this by using the branch name as the cache namespace.
        // Note: The build still has access to broad access to the underlying Storage account, so this is *not* a true security boundary,
        // but rather a best effort attempt.

        // The cache universe and namespace are directly applied to the name of the container, so we need to sanitize and summarize with hash.
        string @namespace = await Git.BranchNameAsync(logger, Settings.RepoRoot);
        string cacheContainer = $"{Settings.CacheUniverse}-{@namespace}";

#pragma warning disable CA1308 // Azure Storage only supports lowercase
        string cacheContainerHash = ContentHasher.GetContentHash(Encoding.UTF8.GetBytes(cacheContainer)).ToShortString(includeHashType: false).ToLowerInvariant();
#pragma warning restore CA1308 // Azure Storage only supports lowercase

        logger.LogMessage($"Using cache namespace '{cacheContainer}' as '{cacheContainerHash}'.");

        IAzureStorageCredentials credentials = CreateAzureStorageCredentials(Settings, cancellationToken);

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by TwoLevelCache
        ICache remoteCache = CreateRemoteCache(new OperationContext(context, cancellationToken), cacheContainerHash, Settings.RemoteCacheIsReadOnly, credentials);
#pragma warning restore CA2000 // Dispose objects before losing scope

        ICacheSession remoteCacheSession = await StartCacheSessionAsync(context, remoteCache, "remote");

        var twoLevelConfig = new TwoLevelCacheConfiguration
        {
            RemoteCacheIsReadOnly = Settings.RemoteCacheIsReadOnly,
            AlwaysUpdateFromRemote = true,
        };

        return new CasCacheClient(
            context,
            FingerprintFactory,
            localCache,
            localCacheSession,
            (remoteCache, remoteCacheSession, twoLevelConfig),
            ContentHasher,
            Settings.RepoRoot,
            NugetPackageRoot,
            NodeContextRepository,
            GetFileRealizationMode,
            Settings.MaxConcurrentCacheContentOperations,
            Settings.AsyncCachePublishing,
            Settings.AsyncCacheMaterialization);
    }

    private static IAzureStorageCredentials CreateAzureStorageCredentials(AzureBlobStoragePluginSettings settings, CancellationToken cancellationToken)
    {
        switch (settings.CredentialsType)
        {
            case AzureStorageCredentialsType.Interactive:
            {
                if (settings.BlobUri is null)
                {
                    throw new InvalidOperationException($"{nameof(AzureBlobStoragePluginSettings.BlobUri)} is required when using {nameof(AzureBlobStoragePluginSettings.CredentialsType)}={settings.CredentialsType}");
                }

                return new InteractiveClientStorageCredentials(settings.InteractiveAuthTokenDirectory, settings.BlobUri, cancellationToken);
            }
            case AzureStorageCredentialsType.ConnectionString:
            {
                string? connectionString = Environment.GetEnvironmentVariable(AzureBlobConnectionStringEnvVar);
                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new InvalidOperationException($"Required environment variable '{AzureBlobConnectionStringEnvVar}' not set");
                }

                return new SecretBasedAzureStorageCredentials(connectionString);
            }
            case AzureStorageCredentialsType.ManagedIdentity:
            {
                if (settings.BlobUri is null)
                {
                    throw new InvalidOperationException($"{nameof(AzureBlobStoragePluginSettings.BlobUri)} is required when using {nameof(AzureBlobStoragePluginSettings.CredentialsType)}={settings.CredentialsType}");
                }

                if (string.IsNullOrEmpty(settings.ManagedIdentityClientId))
                {
                    throw new InvalidOperationException($"{nameof(AzureBlobStoragePluginSettings.BlobUri)} is required when using {nameof(AzureBlobStoragePluginSettings.CredentialsType)}={settings.CredentialsType}");
                }

                return new ManagedIdentityAzureStorageCredentials(settings.ManagedIdentityClientId!, settings.BlobUri);
            }
            default:
            {
                throw new InvalidOperationException($"Unknown {nameof(AzureBlobStoragePluginSettings.CredentialsType)}: {settings.CredentialsType}");
            }
        }
    }

    private static ICache CreateRemoteCache(OperationContext context, string cacheUniverse, bool isReadOnly, IAzureStorageCredentials credentials)
    {
        BlobCacheStorageAccountName accountName = BlobCacheStorageAccountName.Parse(credentials.GetAccountName());
        AzureBlobStorageCacheFactory.Configuration cacheConfig = new(
            ShardingScheme: new ShardingScheme(ShardingAlgorithm.SingleShard, [accountName]),
            Universe: cacheUniverse,
            Namespace: "0",
            RetentionPolicyInDays: null,
            IsReadOnly: isReadOnly);
        return AzureBlobStorageCacheFactory.Create(context, cacheConfig, new StaticBlobCacheSecretsProvider(credentials)).Cache;
    }

    private static async Task<ICacheSession> StartCacheSessionAsync(Context context, ICache cache, string name)
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
