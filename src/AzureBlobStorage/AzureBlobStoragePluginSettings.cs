// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache.AzureBlobStorage;

public class AzureBlobStoragePluginSettings : PluginSettings
{
    public Uri? BlobUri { get; init; }

    public string? ManagedIdentityClientId { get; init; }

    public bool AllowInteractiveAuth { get; set; }

    public string InteractiveAuthTokenDirectory { get; init; } = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\MSBuildCache\AuthTokenCache");

    public string? BuildCacheConfigurationFile { get; init; }

    public string? BuildCacheResourceId { get; init; }
}
