// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache.S3;

public class S3PluginSettings : PluginSettings
{
    public string? BucketName { get; init; }

    public string Region { get; init; } = "us-east-1";

    public Uri? ServiceUrl { get; init; }

    public bool ForcePathStyle { get; init; }

    public string KeyPrefix { get; init; } = "msbuildcache";

    public long MultipartThresholdBytes { get; init; } = 32 * 1024 * 1024;

    public long MultipartPartSizeBytes { get; init; } = 8 * 1024 * 1024;

    public int MaxConcurrentPartsPerObject { get; init; } = 8;

    // Reused content older than this is rewritten in place. Zero renews on every reuse.
    public double ContentRenewalAgeDays { get; init; }
}
