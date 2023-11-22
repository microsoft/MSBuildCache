// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;
using System.Text.Json;

namespace Microsoft.MSBuildCache;

internal static class SerializationHelper
{
    public static JsonWriterOptions WriterOptions { get; } = new JsonWriterOptions { Indented = true };

    public static JsonSerializerOptions SerializerOptions { get; } = CreateJsonSerializerOptions();

    internal static string Serialize<T>(this T value) => JsonSerializer.Serialize(value, SerializerOptions);

    internal static Task<T?> DeserializeAsync<T>(this Stream stream, CancellationToken cancellationToken = default) where T : class =>
        DeserializeAsync<T>(stream, SerializerOptions, cancellationToken);

    internal static T? Deserialize<T>(this string str) where T : class =>
        Deserialize<T>(str, SerializerOptions);

    internal static T? Deserialize<T>(this string str, JsonSerializerOptions? options = null)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(str, options);
        }
        catch (JsonException)
        {
            Throw<T>(str);
            throw new InvalidOperationException(); // Unreachable
        }
    }

    internal static async Task<T?> DeserializeAsync<T>(this Stream stream, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, options, cancellationToken);
        }
        catch (JsonException)
        {
            string? content = null;
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
                    content = await streamReader.ReadToEndAsync(cancellationToken);
                }
            }

            Throw<T>(content);
            throw new InvalidOperationException(); // Unreachable
        }
    }

    private static void Throw<T>(string? fullString)
    {
        string message = $"Can't successfully deserialize a value of type {typeof(T)} from stream.";
        if (fullString != null)
        {
            // Truncating the string to avoid a very long error message.
            const int maxLength = 512;
            if (fullString.Length > maxLength)
            {
                fullString = fullString.Substring(0, maxLength).Trim();
            }

            message = $"{message} Content: '{fullString}'";
        }

        throw new InvalidOperationException(message);
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
