// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Text.Json;

namespace Microsoft.MSBuildCache;

public static class SerializationHelper
{
    public static JsonWriterOptions WriterOptions { get; } = new JsonWriterOptions { Indented = true };

    public static JsonSerializerOptions SerializerOptions { get; } = CreateJsonSerializerOptions();

    public static async Task<T?> DeserializeAsync<T>(this Stream stream, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken);
        }
        catch (JsonException)
        {
            var message = $"Can't successfully deserialize a value of type {typeof(T)} from stream.";

            if (stream.CanSeek)
            {
                stream.Position = 0;

                using (var streamReader = new StreamReader(
                    stream,
                    Encoding.UTF8,
#if NETFRAMEWORK
                    detectEncodingFromByteOrderMarks: true,
                    bufferSize: 1024,
#endif
                    leaveOpen: true))
                {
                    string content = await streamReader.ReadToEndAsync(cancellationToken);

                    // Truncating the string to avoid a very long error message.
                    const int maxLength = 512;
                    if (content.Length > maxLength)
                    {
                        content = content.Substring(0, maxLength).Trim();
                    }

                    message = $"{message} Content: '{content}'";
                }
            }

            throw new InvalidOperationException(message);
        }
    }

    private static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
        };
        options.Converters.Add(new ContentHashJsonConverter());
        return options;
    }
}
