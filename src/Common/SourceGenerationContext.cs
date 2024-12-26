// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.MSBuildCache.Caching;
using Microsoft.MSBuildCache.Fingerprinting;

namespace Microsoft.MSBuildCache;

[JsonSourceGenerationOptions(WriteIndented = true, Converters = [typeof(ContentHashJsonConverter), typeof(SortedDictionaryConverter)])]
[JsonSerializable(typeof(NodeBuildResult))]
[JsonSerializable(typeof(PathSet))]
[JsonSerializable(typeof(LocalCacheStateFile))]
[JsonSerializable(typeof(IDictionary<string, ContentHash>))]
internal partial class SourceGenerationContext : JsonSerializerContext
{
}
