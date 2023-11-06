// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;

namespace Microsoft.MSBuildCache.Parsing;

internal sealed class PredictedInput
{
    private readonly List<string> _predictorNames = new(1);

    public PredictedInput(string relativePath)
    {
        RelativePath = relativePath;
    }

    public string RelativePath { get; }

    public IReadOnlyList<string> PredictorNames => _predictorNames;

    public void AddPredictorName(string predictorName)
    {
        // Only need to lock on add, not get.
        // Parsing populates this collection and it's only read for diagnostic purposes after.
        lock (_predictorNames)
        {
            // Iterate instead of using a HashSet as this is expected to be a very small collection.
            foreach (string existingPredictorName in _predictorNames)
            {
                if (predictorName.Equals(existingPredictorName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _predictorNames.Add(predictorName);
        }
    }
}
