// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache.SourceControl;

/// <summary>
/// Specific Source Control HashException thrown when the hash generation fails.
/// </summary>
public class SourceControlHashException : Exception
{
    public SourceControlHashException()
    {
    }

    public SourceControlHashException(string message)
        : base(FormatMessage(message))
    {
    }

    public SourceControlHashException(string message, Exception inner)
        : base(FormatMessage(message), inner)
    {
    }

    private static string FormatMessage(string message) => $"Hash generation failed with message: {message}";
}
