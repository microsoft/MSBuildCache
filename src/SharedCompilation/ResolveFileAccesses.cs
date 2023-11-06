// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using static Microsoft.MSBuildCache.SharedCompilation.VBCSCompilerReporter;

namespace Microsoft.MSBuildCache.SharedCompilation;

/// <summary>
/// When shared compilation is on, this task is used to resolve file accesses
/// by passing in the command line arguments returned by a Csc or Vbc task to
/// the <see cref="VBCSCompilerReporter"/>, which parses
/// them and reports file accesses.
/// </summary>
public sealed class ResolveFileAccesses : Task
{
    private string? _language;

    [Required]
    public string? Language
    {
        get => _language;
        set
        {
            if (LanguageNames.CSharp.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                _language = LanguageNames.CSharp;
            }
            else if (LanguageNames.VisualBasic.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                _language = LanguageNames.VisualBasic;
            }
            else
            {
                // F# is disallowed.
                throw new ArgumentException("Language must be either C# or Visual Basic.");
            }
        }
    }

    [Required]
    public ITaskItem[]? CommandLineArguments { get; set; }

    [Required]
    public string? ProjectFile { get; set; }

    public override bool Execute()
    {
        if (CommandLineArguments == null)
        {
            throw new ArgumentNullException(nameof(CommandLineArguments));
        }

        if (Language == null)
        {
            throw new ArgumentNullException(nameof(Language));
        }

        if (string.IsNullOrWhiteSpace(ProjectFile))
        {
            throw new ArgumentException($"{nameof(ProjectFile)} cannot be null or whitespace.");
        }

        ResolveFileAccesses(
            Language,
            CommandLineArguments.Select(item => item.ItemSpec).ToArray(),
            ProjectFile!,
            ((IBuildEngine10)BuildEngine).EngineServices);
        return true;
    }
}