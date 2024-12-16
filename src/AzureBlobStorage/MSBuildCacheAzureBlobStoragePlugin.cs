// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using BuildXL.Cache.BuildCacheResource.Helper;
using BuildXL.Cache.BuildCacheResource.Model;
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
using BuildXL.Cache.MemoizationStore.Stores;
using BuildXL.Utilities.Tracing;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.MSBuildCache.Caching;

namespace Microsoft.MSBuildCache.AzureBlobStorage;

public sealed class MSBuildCacheAzureBlobStoragePlugin : MSBuildCachePluginBase<AzureBlobStoragePluginSettings>
{
    // Note: These are not in PluginSettings as that's configured through item metadata and thus makes it into MSBuild logs. This is a secret so that's not desirable.
    //       Environment variables are also prone to leaking, so other authentication types are preferred when possible.
    private const string AzureBlobConnectionStringEnvVar = "MSBCACHE_CONNECTIONSTRING";
    private const string AzureBlobAccessTokenEnvVar = "MSBCACHE_ACCESSTOKEN";

    private readonly TokenCredential? _tokenCredential;

    // Although Azure Blob Storage is unrelated to Azure DevOps, Vso0 hashing is much faster than SHA256.
    protected override HashType HashType => HashType.Vso0;

    // Constructor used when MSBuild creates the plugin
    public MSBuildCacheAzureBlobStoragePlugin()
    {
    }

    public MSBuildCacheAzureBlobStoragePlugin(TokenCredential tokenCredential)
    {
        _tokenCredential = tokenCredential;
    }

    public MSBuildCacheAzureBlobStoragePlugin(string accessToken)
        : this(new StaticTokenCredential(accessToken))
    {
    }

    protected override async Task<ICacheClient> CreateCacheClientAsync(PluginLoggerBase logger, CancellationToken cancellationToken)
    {
        if (Settings == null
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

        // The cache universe and namespace are directly applied to the name of the container, so we need to sanitize and summarize with lowercase hash.
#pragma warning disable CA1308 // Azure Storage only supports lowercase
        string cacheUniverse = ContentHasher.GetContentHash(Encoding.UTF8.GetBytes(Settings.CacheUniverse)).ToShortString(includeHashType: false).ToLowerInvariant();
#pragma warning restore CA1308 // Azure Storage only supports lowercase

        logger.LogMessage($"Using cache universe '{Settings.CacheUniverse}' as '{cacheUniverse}'.");

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by TwoLevelCache
        ICache remoteCache = await CreateRemoteCacheAsync(new OperationContext(context, cancellationToken), Settings, cacheUniverse);
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
            GetFileRealizationMode,
            Settings.MaxConcurrentCacheContentOperations,
            Settings.AsyncCachePublishing,
            Settings.AsyncCacheMaterialization,
            Settings.SkipUnchangedOutputFiles);
    }

    private async Task<ICache> CreateRemoteCacheAsync(
        OperationContext context,
        AzureBlobStoragePluginSettings settings,
        string cacheUniverse)
    {
        List<BlobCacheStorageAccountName> accounts;
        IBlobCacheContainerSecretsProvider secretsProvider;
        string? connectionString;
        BuildCacheConfiguration? buildCacheConfiguration = await GetBuildCacheConfigurationAsync(context, settings);
        if (buildCacheConfiguration is not null)
        {
            accounts = new List<BlobCacheStorageAccountName>(buildCacheConfiguration!.Shards.Count);
            foreach (BuildCacheShard shard in buildCacheConfiguration.Shards)
            {
                accounts.Add(shard.GetAccountName());
            }

            secretsProvider = new AzureBuildCacheSecretsProvider(buildCacheConfiguration);
        }
        else if (!string.IsNullOrEmpty(connectionString = Environment.GetEnvironmentVariable(AzureBlobConnectionStringEnvVar)))
        {
            SecretBasedAzureStorageCredentials credentials = new(connectionString);
            BlobCacheStorageAccountName accountName = BlobCacheStorageAccountName.Parse(credentials.GetAccountName());
            accounts = [accountName];
            secretsProvider = new StaticBlobCacheSecretsProvider(credentials);
        }
        else if (settings.BlobUri is not null)
        {
            TokenCredential tokenCredential = GetTokenCredential(context, settings, settings.BlobUri.ToString());
            UriAzureStorageTokenCredential credentials = new UriAzureStorageTokenCredential(tokenCredential, settings.BlobUri);
            BlobCacheStorageAccountName accountName = BlobCacheStorageAccountName.Parse(credentials.GetAccountName());
            accounts = [accountName];
            secretsProvider = new StaticBlobCacheSecretsProvider(credentials);
        }
        else
        {
            throw new InvalidOperationException($"Either {nameof(AzureBlobStoragePluginSettings.BuildCacheConfigurationFile)}, {nameof(AzureBlobStoragePluginSettings.BuildCacheResourceId)}, or {nameof(AzureBlobStoragePluginSettings.BlobUri)} is required.");
        }

        ShardingScheme shardingScheme = new(
            accounts.Count == 1 ? ShardingAlgorithm.SingleShard : ShardingAlgorithm.JumpHash,
            accounts);

        AzureBlobStorageCacheFactory.Configuration cacheConfig = new(
            ShardingScheme: shardingScheme,
            Universe: cacheUniverse,
            Namespace: AzureBlobStorageCacheFactory.Configuration.DefaultNamespace,
            RetentionPolicyInDays: null,
            IsReadOnly: settings.RemoteCacheIsReadOnly)
        {
            BuildCacheConfiguration = buildCacheConfiguration,
            ContentHashListReplacementCheckBehavior = ContentHashListReplacementCheckBehavior.AllowPinElision,
        };

        return AzureBlobStorageCacheFactory.Create(context, cacheConfig, secretsProvider).Cache;
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

    private async Task<BuildCacheConfiguration?> GetBuildCacheConfigurationAsync(OperationContext context, AzureBlobStoragePluginSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.BuildCacheConfigurationFile))
        {
            string credentials = BlobCacheCredentialsHelper.ReadCredentials(new BuildXL.Cache.ContentStore.Interfaces.FileSystem.AbsolutePath(settings.BuildCacheConfigurationFile!), BlobCacheCredentialsHelper.FileEncryption.None);
            HostedPoolBuildCacheConfiguration hostedPoolBuildCacheConfiguration = BuildCacheResourceHelper.LoadFromString(credentials);

            if (!hostedPoolBuildCacheConfiguration.TrySelectBuildCache(hostedPoolActiveBuildCacheName: null, out BuildCacheConfiguration? buildCacheConfiguration))
            {
                throw new InvalidOperationException("No available caches");
            }

            return buildCacheConfiguration;
        }

        if (!string.IsNullOrEmpty(settings.BuildCacheResourceId))
        {
            TokenCredential tokenCredential = GetTokenCredential(context, settings, settings.BuildCacheResourceId!);
            return (await BuildCacheConfigurationProvider.TryGetBuildCacheConfigurationAsync(tokenCredential, settings.BuildCacheResourceId!, context.Token))
                .ThrowIfFailure()
                .Result;
        }

        return null;
    }

    private TokenCredential GetTokenCredential(OperationContext context, AzureBlobStoragePluginSettings settings, string tokenIdentifier)
    {
        string? accessToken = Environment.GetEnvironmentVariable(AzureBlobAccessTokenEnvVar);
        if (!string.IsNullOrEmpty(accessToken))
        {
            return new StaticTokenCredential(accessToken);
        }

        if (_tokenCredential is not null)
        {
            return _tokenCredential;
        }

        if (!string.IsNullOrEmpty(settings.ManagedIdentityClientId))
        {
            return new ManagedIdentityCredential(settings.ManagedIdentityClientId);
        }

        if (settings.AllowInteractiveAuth)
        {
            using StandardConsole console = new(colorize: false, animateTaskbar: false, supportsOverwriting: false, pathTranslator: null);
            ContentHash persistentTokenIdentifier = HashInfoLookup.GetContentHasher(HashType.SHA256).GetContentHash(Encoding.UTF8.GetBytes(tokenIdentifier));
            return new InteractiveClientTokenCredential(context, settings.InteractiveAuthTokenDirectory, persistentTokenIdentifier, console, context.Token);
        }

        throw new InvalidOperationException("Authentication parameters must be provided or interactive auth must be allowed.");
    }
}
