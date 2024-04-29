// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache;

internal sealed class SortedDictionaryConverter : JsonConverter<SortedDictionary<string, ContentHash>>
{
    public override SortedDictionary<string, ContentHash>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var contentHashConverter = (JsonConverter<ContentHash>)options.GetConverter(typeof(ContentHash));
        var outputs = new SortedDictionary<string, ContentHash>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Unexpected token: {reader.TokenType}");
            }

            string propertyName = reader.GetString()!;
            if (!reader.Read())
            {
                throw new JsonException($"Property name '{propertyName}' does not have a value.");
            }

            ContentHash? contentHash = contentHashConverter.Read(ref reader, typeof(ContentHash), options);
            if (contentHash == null)
            {
                throw new JsonException($"Property value for '{propertyName}' could not be parsed.");
            }

            outputs.Add(propertyName, contentHash.Value);
        }

        return outputs;
    }

    public override void Write(Utf8JsonWriter writer, SortedDictionary<string, ContentHash> value, JsonSerializerOptions options)
    {
        var defaultConverter = (JsonConverter<IDictionary<string, ContentHash>>)options.GetConverter(typeof(IDictionary<string, ContentHash>));
        defaultConverter.Write(writer, value, options);
    }
}
