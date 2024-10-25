// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Azure.Core;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Microsoft.MSBuildCache.AzureBlobStorage;

internal sealed class StaticTokenCredential : TokenCredential
{
    private readonly string _accessToken;

    public StaticTokenCredential(string accessToken)
    {
        _accessToken = accessToken;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        // The token is static and we don't know the expiry, so just say it's a day from now.
        => new AccessToken(_accessToken, DateTimeOffset.Now.AddDays(1));

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
}
