// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.MSBuildCache.AzureBlobStorage;

/// <summary>
/// Determines how to authenticate to Azure Storage.
/// </summary>
public enum AzureStorageCredentialsType
{
    /// <summary>
    /// Use interactive authentication.
    /// </summary>
    Interactive,

    /// <summary>
    /// Use a connection string to authenticate.
    /// </summary>
    /// <remarks>
    /// The "MSBCACHE_CONNECTIONSTRING" environment variable must contain the connection string to use.
    /// </remarks>
    ConnectionString,

    /// <summary>
    /// Use a managed identity to authenticate.
    /// </summary>
    ManagedIdentity,

    /// <summary>
    /// Use a token credential to authenticate.
    /// </summary>
    /// <remarks>
    /// The "MSBCACHE_ACCESSTOKEN" environment variable must contain the access token to use. Alternately if using the programmatic
    /// project cache API, a TokenCredential may be provided in the plugin's constructor.
    /// </remarks>
    TokenCredential,
}
