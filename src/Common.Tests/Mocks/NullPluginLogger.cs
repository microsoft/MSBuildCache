// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;

namespace Microsoft.MSBuildCache.Tests.Mocks;

internal sealed class NullPluginLogger : PluginLoggerBase
{
    private NullPluginLogger() : base()
    {
    }

    public static NullPluginLogger Instance { get; } = new NullPluginLogger();

    public override bool HasLoggedErrors
    {
        get => false;
        protected set { }
    }

    public override void LogError(string error)
    {
    }

    public override void LogMessage(string message, MessageImportance? messageImportance = null)
    {
    }

    public override void LogWarning(string warning)
    {
    }
}
