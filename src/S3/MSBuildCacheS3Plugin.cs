// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Logging;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using BuildXL.Cache.MemoizationStore.Sessions;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;
using Microsoft.MSBuildCache.Caching;

namespace Microsoft.MSBuildCache.S3;

public sealed class MSBuildCacheS3Plugin : MSBuildCachePluginBase<S3PluginSettings>
{
    // Note: Credentials are not in PluginSettings as that's configured through item metadata and thus makes it into
    //       MSBuild logs. Instead they come from the plugin's constructor or the default AWS credential chain, which
    //       covers the AWS_* environment variables, the shared credentials file, and instance and task roles.
    private readonly AWSCredentials? _credentials;

    // Although S3 is unrelated to Azure DevOps, Vso0 hashing is much faster than SHA256.
    protected override HashType HashType => HashType.Vso0;

    // Constructor used when MSBuild creates the plugin
    public MSBuildCacheS3Plugin()
    {
    }

    public MSBuildCacheS3Plugin(AWSCredentials credentials)
    {
        _credentials = credentials;
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

        if (string.IsNullOrWhiteSpace(Settings.BucketName))
        {
            throw new InvalidOperationException($"{nameof(S3PluginSettings.BucketName)} is required. Set the MSBuildCacheS3BucketName MSBuild property.");
        }

        // The cache universe becomes part of the object key, so summarize it with a lowercase hash.
#pragma warning disable CA1308 // S3 object keys are conventionally lowercase
        string cacheUniverse = ContentHasher.GetContentHash(Encoding.UTF8.GetBytes(Settings.CacheUniverse)).ToShortString(includeHashType: false).ToLowerInvariant();
#pragma warning restore CA1308 // S3 object keys are conventionally lowercase

        logger.LogMessage(
            $"Using S3 bucket '{Settings.BucketName}'{(Settings.ServiceUrl is null ? null : $" at '{Settings.ServiceUrl}'")} with cache universe '{Settings.CacheUniverse}' as '{cacheUniverse}'.",
            MessageImportance.Normal);

        FileLog fileLog = new(Path.Combine(Settings.LogDirectory, "CacheClient.log"));
#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed using Context.Logger.Dispose in the cache client implementation.
        Logger cacheLogger = new(fileLog);
#pragma warning restore CA2000 // Dispose objects before losing scope
        Context context = new(cacheLogger);

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by S3CacheClient
        LocalCache localCache = LocalCacheFactory.Create(cacheLogger, Settings.LocalCacheRootPath, Settings.LocalCacheSizeInMegabytes);
#pragma warning restore CA2000 // Dispose objects before losing scope

        ICacheSession localCacheSession = await StartCacheSessionAsync(context, localCache, "local");

#pragma warning disable CA2000 // Dispose objects before losing scope. Expected to be disposed by S3CacheClient
        IAmazonS3 s3Client = CreateS3Client(Settings);
#pragma warning restore CA2000 // Dispose objects before losing scope

        return new S3CacheClient(
            context,
            FingerprintFactory,
            ContentHasher,
            localCache,
            localCacheSession,
            s3Client,
            Settings.BucketName!,
            Settings.KeyPrefix,
            cacheUniverse,
            Settings.RepoRoot,
            NugetPackageRoot,
            GetFileRealizationMode,
            Settings.MaxConcurrentCacheContentOperations,
            Settings.RemoteCacheIsReadOnly,
            Settings.AsyncCachePublishing,
            Settings.AsyncCacheMaterialization,
            Settings.SkipUnchangedOutputFiles,
            Settings.TouchOutputFiles,
            Settings.MultipartThresholdBytes,
            Settings.MultipartPartSizeBytes,
            Settings.MaxConcurrentPartsPerObject);
    }

    private AmazonS3Client CreateS3Client(S3PluginSettings settings)
    {
        AmazonS3Config config = new()
        {
            Timeout = TimeSpan.FromMinutes(5),
            MaxErrorRetry = 5,
            ForcePathStyle = settings.ForcePathStyle,
        };

#if NETFRAMEWORK
        // Size this client's connection pool for the cache operation gate. Note that changing
        // ServicePointManager.DefaultConnectionLimit instead would affect unrelated MSBuild tasks.
        config.ConnectionLimit = Math.Max(settings.MaxConcurrentCacheContentOperations, settings.MaxConcurrentPartsPerObject);
#endif

        if (settings.ServiceUrl is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }
        else
        {
            config.ServiceURL = settings.ServiceUrl.AbsoluteUri;
            config.AuthenticationRegion = settings.Region;
        }

        return new AmazonS3Client(_credentials ?? FallbackCredentialsFactory.GetCredentials(), config);
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
