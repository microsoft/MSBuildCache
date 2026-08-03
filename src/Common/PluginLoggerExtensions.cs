// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;

namespace Microsoft.MSBuildCache;

internal static class PluginLoggerExtensions
{
    public static void LogWarningOrMessage(this PluginLoggerBase logger, string message, bool logAsMessage)
    {
        if (logAsMessage)
        {
            logger.LogMessage(message, MessageImportance.High);
        }
        else
        {
            logger.LogWarning(message);
        }
    }
}
