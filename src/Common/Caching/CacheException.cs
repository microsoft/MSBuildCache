// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace Microsoft.MSBuildCache.Caching;

public sealed class CacheException : Exception
{
    public CacheException()
    {
    }

    public CacheException(string message)
        : base(message)
    {
    }

    public CacheException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
