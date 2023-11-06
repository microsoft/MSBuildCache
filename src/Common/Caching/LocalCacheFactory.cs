// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Cache.ContentStore.Distributed.NuCache;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Logging;
using BuildXL.Cache.ContentStore.Stores;
using BuildXL.Cache.MemoizationStore.Sessions;
using BuildXL.Cache.MemoizationStore.Stores;

namespace Microsoft.MSBuildCache.Caching;

public static class LocalCacheFactory
{
    public static LocalCache Create(ILogger logger, string cacheRootPath, uint localCacheSizeInMegabytes)
    {
        AbsolutePath rootPath = new(cacheRootPath);

        // Note: this only works in x64 processes, which this might not be...
        RocksDbMemoizationStoreConfiguration rocksDbMemoizationStoreConfiguration = new()
        {
            Database = new RocksDbContentLocationDatabaseConfiguration(rootPath / "RocksDbMemoizationStore")
            {
                CleanOnInitialize = false,
                GarbageCollectionInterval = TimeSpan.FromHours(1),
                OnFailureDeleteExistingStoreAndRetry = true,
            },
        };

        ContentStoreConfiguration contentStoreConfiguration = ContentStoreConfiguration.CreateWithMaxSizeQuotaMB(localCacheSizeInMegabytes);

        return LocalCache.CreateUnknownContentStoreInProcMemoizationStoreCache(
            logger,
            rootPath,
            rocksDbMemoizationStoreConfiguration,
            LocalCacheConfiguration.CreateServerDisabled(),
            new ConfigurationModel(contentStoreConfiguration),
            assumeCallerCreatesDirectoryForPlace: true);
    }
}
