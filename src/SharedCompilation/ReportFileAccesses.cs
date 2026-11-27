// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using static Microsoft.MSBuildCache.SharedCompilation.VBCSCompilerReporter;

namespace Microsoft.MSBuildCache.SharedCompilation;

/// <summary>
/// When shared compilation is on, this task reports the file accesses of a compiler invocation
/// whose command line is not available, given its inputs and outputs explicitly.
/// For instance, the Csc task of the SDK target CoreGenerateSatelliteAssemblies does not return its command line arguments.
/// </summary>
public sealed class ReportFileAccesses : Task
{
    public ITaskItem[] Inputs { get; set; } = Array.Empty<ITaskItem>();

    public ITaskItem[] Outputs { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The project file, whose directory resolves relative paths.
    /// </summary>
    [Required]
    public string? ProjectFile { get; set; }

    public override bool Execute()
    {
        if (string.IsNullOrWhiteSpace(ProjectFile))
        {
            throw new ArgumentException($"{nameof(ProjectFile)} cannot be null or whitespace.");
        }

        AccessRegistrar accessRegistrar = new(Path.GetDirectoryName(Path.GetFullPath(ProjectFile))!, ((IBuildEngine10)BuildEngine).EngineServices);
        accessRegistrar.RegisterInputs(Inputs.Select(item => item.ItemSpec));
        foreach (ITaskItem output in Outputs)
        {
            accessRegistrar.RegisterOutput(output.ItemSpec);
        }

        return true;
    }
}