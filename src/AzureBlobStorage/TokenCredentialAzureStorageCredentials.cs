// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Azure.Core;
using BuildXL.Cache.ContentStore.Interfaces.Auth;

namespace Microsoft.MSBuildCache.AzureBlobStorage;

internal sealed class TokenCredentialAzureStorageCredentials : AzureStorageCredentialsBase
{
    public TokenCredentialAzureStorageCredentials(Uri blobUri, TokenCredential tokenCredential)
        : base(blobUri)
    {
        Credentials = tokenCredential;
    }

    protected override TokenCredential Credentials { get; }
}