// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache.S3;

public class S3PluginSettings : PluginSettings
{
    public string? BucketName { get; init; }

    /// <summary>
    /// The AWS region. Also used as the signing region when <see cref="ServiceUrl"/> is set.
    /// </summary>
    public string Region { get; init; } = "us-east-1";

    /// <summary>
    /// The service url of an S3-compatible store such as MinIO. If null, the AWS endpoint for the region is used.
    /// </summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>
    /// Whether to use path-style addressing. Most S3-compatible stores require this.
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>
    /// The key prefix under which all cache objects are stored.
    /// </summary>
    public string KeyPrefix { get; init; } = "msbuildcache";

    /// <summary>
    /// Objects at or above this size are transferred as multiple parts in parallel instead of as a single request.
    /// </summary>
    /// <remarks>
    /// Build outputs are heavily skewed towards small files, but the few large ones dominate the time to materialize a
    /// cache hit with a cold local cache.
    /// </remarks>
    public long MultipartThresholdBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// The part size for multipart transfers. Values below S3's 5 MB minimum are raised to it.
    /// </summary>
    /// <remarks>
    /// This also bounds how much of an object is re-transferred when a request fails, as a part is retried on its own
    /// rather than restarting the whole object.
    /// </remarks>
    public long MultipartPartSizeBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// How many parts of a single object are transferred concurrently. Ranged requests across all multipart transfers
    /// are additionally bounded by <see cref="PluginSettings.MaxConcurrentCacheContentOperations"/>.
    /// </summary>
    public int MaxConcurrentPartsPerObject { get; init; } = 8;
}
