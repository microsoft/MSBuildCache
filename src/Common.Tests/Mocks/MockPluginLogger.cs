// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Experimental.ProjectCache;
using Microsoft.Build.Framework;

namespace Microsoft.MSBuildCache.Tests.Mocks;

internal enum PluginLogLevel { Message, Warning, Error };

internal readonly record struct PluginLogEntry(PluginLogLevel LogLevel, string Message);

internal sealed class MockPluginLogger : PluginLoggerBase
{
    private readonly List<PluginLogEntry> _logEntries = new();

    public IReadOnlyList<PluginLogEntry> LogEntries => _logEntries;

    public override bool HasLoggedErrors
    {
        get => _logEntries.Any(entry => entry.LogLevel == PluginLogLevel.Error);
        protected set { }
    }

    public override void LogError(string error) => _logEntries.Add(new PluginLogEntry(PluginLogLevel.Error, error));

    public override void LogMessage(string message, MessageImportance? messageImportance = null) => _logEntries.Add(new PluginLogEntry(PluginLogLevel.Message, message));

    public override void LogWarning(string warning) => _logEntries.Add(new PluginLogEntry(PluginLogLevel.Warning, warning));
}
