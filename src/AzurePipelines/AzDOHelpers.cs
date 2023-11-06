// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace Microsoft.MSBuildCache.AzurePipelines;

internal static class AzDOHelpers
{
    private static readonly string? AccessToken = Environment.GetEnvironmentVariable("SYSTEM_ACCESSTOKEN");

    private static readonly string? CollectionUri = Environment.GetEnvironmentVariable("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI");

    public static Guid SessionGuid => VssClientHttpRequestSettings.Default.SessionId;

    public static VssBasicCredential GetCredentials()
    {
        EnsureAzureDevopsEnvironment();
        if (AccessToken == null)
        {
            throw new InvalidOperationException("Access token is not available. See: https://learn.microsoft.com/en-us/azure/devops/pipelines/build/variables?view=azure-devops&tabs=yaml#systemaccesstoken");
        }

        return new VssBasicCredential("user", AccessToken);
    }

    private static Uri GetTfsUrl()
    {
        EnsureAzureDevopsEnvironment();
        return new Uri(CollectionUri!);
    }

    public static bool IsRunningInPipeline() => CollectionUri != null;

    public static Uri GetServiceUriFromEnv(string service)
    {
        Uri tfs = GetTfsUrl();
        return new Uri(
            tfs.AbsoluteUri
                .Replace("https://dev.azure.com", $"https://{service}.dev.azure.com", StringComparison.OrdinalIgnoreCase)
                .Replace(".visualstudio.com", $".{service}.visualstudio.com", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static void EnsureAzureDevopsEnvironment()
    {
        if (!IsRunningInPipeline())
        {
            throw new InvalidOperationException("Not running in an Azure DevOps environment");
        }
    }
}
