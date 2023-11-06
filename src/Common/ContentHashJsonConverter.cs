// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache;

internal sealed class ContentHashJsonConverter : JsonConverter<ContentHash>
{
    public override ContentHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new ContentHash(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ContentHash value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Serialize());
}
