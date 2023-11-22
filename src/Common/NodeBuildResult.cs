// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;
using Microsoft.Build.Execution;
using Microsoft.Build.Experimental.ProjectCache;

namespace Microsoft.MSBuildCache;

public record struct OutputInfo(DateTime LastModified, ContentHash Hash);

public sealed class NodeBuildResult
{
    public const uint CurrentVersion = 1;

    [JsonConstructor]
    public NodeBuildResult(
        SortedDictionary<string, OutputInfo> outputs,
        IReadOnlyList<NodeTargetResult> targetResults,
        DateTime startTimeUtc,
        DateTime endTimeUtc,
        string? buildId)
    {
        Outputs = outputs;
        TargetResults = targetResults;
        StartTimeUtc = startTimeUtc;
        EndTimeUtc = endTimeUtc;
        BuildId = buildId;
    }

    // Use a sorted dictionary so the JSON output is deterministically sorted and easier to compare build-to-build.
    [JsonConverter(typeof(SortedDictionaryConverter))]
    public SortedDictionary<string, OutputInfo> Outputs { get; }

    public IReadOnlyList<NodeTargetResult> TargetResults { get; }

    public DateTime StartTimeUtc { get; }

    public DateTime EndTimeUtc { get; }

    public string? BuildId { get; }

    public static NodeBuildResult FromBuildResult(SortedDictionary<string, OutputInfo> outputs, BuildResult buildResult, DateTime creationTimeUtc, DateTime endTimeUtc, string? buildId, PathNormalizer pathNormalizer)
    {
        List<NodeTargetResult> targetResults = new(buildResult.ResultsByTarget.Count);
        foreach (KeyValuePair<string, TargetResult> kvp in buildResult.ResultsByTarget)
        {
            targetResults.Add(NodeTargetResult.FromTargetResult(kvp.Key, kvp.Value, pathNormalizer));
        }

        return new NodeBuildResult(outputs, targetResults, creationTimeUtc, endTimeUtc, buildId);
    }

    public CacheResult ToCacheResult(PathNormalizer pathNormalizer)
    {
        List<PluginTargetResult> targetResults = new(TargetResults.Count);
        foreach (NodeTargetResult targetResult in TargetResults)
        {
            targetResults.Add(targetResult.ToPluginTargetResult(pathNormalizer));
        }

        return CacheResult.IndicateCacheHit(targetResults);
    }

    private sealed class SortedDictionaryConverter : JsonConverter<SortedDictionary<string, OutputInfo>>
    {
        public override SortedDictionary<string, OutputInfo>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var infoConverter = (JsonConverter<OutputInfo>)options.GetConverter(typeof(OutputInfo));
            var outputs = new SortedDictionary<string, OutputInfo>(StringComparer.OrdinalIgnoreCase);
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

                OutputInfo? info = infoConverter.Read(ref reader, typeof(OutputInfo), options);
                if (info == null)
                {
                    throw new JsonException($"Property value for '{propertyName}' could not be parsed.");
                }

                outputs.Add(propertyName, info.Value);
            }

            return outputs;
        }

        public override void Write(Utf8JsonWriter writer, SortedDictionary<string, OutputInfo> value, JsonSerializerOptions options)
        {
            var defaultConverter = (JsonConverter<SortedDictionary<string, OutputInfo>>)
                options.GetConverter(typeof(SortedDictionary<string, OutputInfo>));
            defaultConverter.Write(writer, value, options);
        }
    }
}