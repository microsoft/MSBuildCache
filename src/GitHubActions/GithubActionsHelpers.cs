// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache.GitHubActions;

internal static class GithubActionsHelpers
{
    private static readonly bool IsGitHubActions = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
    private static readonly string? Token = Environment.GetEnvironmentVariable("ACTIONS_RUNTIME_TOKEN");
    private static readonly string? ActionsCacheUrl = Environment.GetEnvironmentVariable("ACTIONS_CACHE_URL");
    private static readonly string? RunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
    private static readonly string? RunAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");

    public static bool IsGithubActionsEnvironment() => IsGitHubActions;

    public static string GetActionsCacheUrl()
    {
        EnsureGithubActionsEnvironment();
        return ActionsCacheUrl!;
    }

    public static string GetToken()
    {
        EnsureGithubActionsEnvironment();
        return Token!;
    }

    public static string GetBuildId()
    {
        EnsureGithubActionsEnvironment();
        return $"{RunId}_{RunAttempt}";
    }

    private static void EnsureGithubActionsEnvironment()
    {
        if (!IsGithubActionsEnvironment())
        {
            throw new InvalidOperationException("Not running in a GitHub Actions environment");
        }
    }
}
