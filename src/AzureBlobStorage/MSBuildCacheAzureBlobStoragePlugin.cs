// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
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

        IAzureStorageCredentials credentials = CreateAzureStorageCredentials(context, Settings, cancellationToken);

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by TwoLevelCache
        ICache remoteCache = CreateRemoteCache(new OperationContext(context, cancellationToken), cacheUniverse, Settings.RemoteCacheIsReadOnly, credentials);
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
            Settings.AsyncCacheMaterialization);
    }

    private IAzureStorageCredentials CreateAzureStorageCredentials(Context context, AzureBlobStoragePluginSettings settings, CancellationToken cancellationToken)
    {
        switch (settings.CredentialsType)
        {
            case AzureStorageCredentialsType.Interactive:
            {
                if (settings.BlobUri is null)
                {
                    throw new InvalidOperationException($"{nameof(AzureBlobStoragePluginSettings.BlobUri)} is required when using {nameof(AzureBlobStoragePluginSettings.CredentialsType)}={settings.CredentialsType}");
                }

                using StandardConsole console = new(colorize: false, animateTaskbar: false, supportsOverwriting: false, pathTranslator: null);
                InteractiveClientTokenCredential tokenCredential = new(context, settings.InteractiveAuthTokenDirectory, GetHashForTokenIdentifier(settings.BlobUri), console, cancellationToken);
                return new UriAzureStorageTokenCredential(tokenCredential, settings.BlobUri);
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
                    throw new InvalidOperationException($"{nameof(AzureBlobStoragePluginSettings.ManagedIdentityClientId)} is required when using {nameof(AzureBlobStoragePluginSettings.CredentialsType)}={settings.CredentialsType}");
                }

                return new ManagedIdentityAzureStorageCredentials(settings.ManagedIdentityClientId!, settings.BlobUri);
            }
            case AzureStorageCredentialsType.TokenCredential:
            {
                if (settings.BlobUri is null)
                {
                    throw new InvalidOperationException($"{nameof(AzureBlobStoragePluginSettings.BlobUri)} is required when using {nameof(AzureBlobStoragePluginSettings.CredentialsType)}={settings.CredentialsType}");
                }

                // Allow the environment variable to supersede the constuctor-provided value.
                string? accessToken = Environment.GetEnvironmentVariable(AzureBlobAccessTokenEnvVar);
                TokenCredential? tokenCredential = !string.IsNullOrEmpty(accessToken)
                        ? new StaticTokenCredential(accessToken)
                        : _tokenCredential;
                if (tokenCredential is null)
                {
                    throw new InvalidOperationException($"Required environment variable '{AzureBlobAccessTokenEnvVar}' not set");
                }

                return new TokenCredentialAzureStorageCredentials(settings.BlobUri, tokenCredential);
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
            Namespace: AzureBlobStorageCacheFactory.Configuration.DefaultNamespace,
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

    private static ContentHash GetHashForTokenIdentifier(Uri uri) => GetHashForTokenIdentifier(uri.ToString());

    private static ContentHash GetHashForTokenIdentifier(string identifier) => HashInfoLookup.GetContentHasher(HashType.SHA256).GetContentHash(Encoding.UTF8.GetBytes(identifier));
}
