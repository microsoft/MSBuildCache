// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK
#pragma warning disable IDE0060 // Remove unused parameter. This is intentional to match the missing APIs

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.MSBuildCache;

/// <summary>
/// Instead of adding #ifs multiple places, implement APIs missing from .NET Framework here.
/// </summary>
public static class NetFrameworkPolyfills
{
    public static Task<Stream> GetStreamAsync(this HttpClient @this, string? requestUri, CancellationToken cancellationToken)
        => @this.GetStreamAsync(requestUri);

    public static Task<Stream> ReadAsStreamAsync(this HttpContent @this, CancellationToken cancellationToken)
        => @this.ReadAsStreamAsync();

    public static Task<string> ReadToEndAsync(this StreamReader @this, CancellationToken cancellationToken)
        => @this.ReadToEndAsync();

    public static bool Contains(this string @this, char value)
        => @this.IndexOf(value) >= 0;

    public static string Replace(this string @this, string oldValue, string? newValue, StringComparison comparisonType)
    {
        if (comparisonType == StringComparison.Ordinal)
        {
            return @this.Replace(oldValue, newValue);
        }

        StringBuilder sb = new StringBuilder();

        int previousIndex = 0;
        int index = @this.IndexOf(oldValue, comparisonType);
        while (index != -1)
        {
            sb.Append(@this.Substring(previousIndex, index - previousIndex));
            sb.Append(newValue);
            index += oldValue.Length;

            previousIndex = index;
            index = @this.IndexOf(oldValue, index, comparisonType);
        }

        sb.Append(@this.Substring(previousIndex));

        return sb.ToString();
    }

    public static bool EndsWith(this string @this, char value)
    {
        int thisLen = @this.Length;
        return thisLen != 0 && @this[thisLen - 1] == value;
    }
}

#pragma warning restore IDE0060 // Remove unused parameter
#endif
