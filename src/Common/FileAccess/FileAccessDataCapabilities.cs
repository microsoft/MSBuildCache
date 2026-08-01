// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Reflection;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Experimental.FileAccess;

namespace Microsoft.MSBuildCache.FileAccess;

/// <summary>
/// Detects whether the running MSBuild reports the file-access fields that probe and enumeration
/// fingerprinting depends on.
/// </summary>
/// <remarks>
/// <para>
/// <c>EnumeratePattern</c> was added to <see cref="FileAccessData"/> after the MSBuild version
/// MSBuildCache compiles against, so it is read reflectively rather than by a direct property access
/// that would not compile — and, if the compile reference were raised, would throw on every older host.
/// </para>
/// <para>
/// Without it a filtered enumeration such as <c>*.cs</c> is recorded as if it were unfiltered, so any
/// unrelated file appearing in the directory invalidates the entry and the common case essentially
/// never hits. That is why its absence disables the feature outright rather than degrading it.
/// </para>
/// </remarks>
internal static class FileAccessDataCapabilities
{
    private static readonly ByRefGetter<FileAccessData, string?>? EnumeratePatternGetter =
        ByRefGetterFactory.TryCreate<FileAccessData, string?>("EnumeratePattern");

    /// <summary>
    /// Whether the running MSBuild reports the enumeration pattern. Used to force
    /// <c>EnableProbeAndEnumerationFingerprinting</c> off on hosts that cannot support it.
    /// </summary>
    public static bool IsSupported { get; } = EnumeratePatternGetter is not null;

    /// <summary>
    /// The running MSBuild's file version, for diagnostics only, or <c>null</c> if it cannot be
    /// determined.
    /// </summary>
    /// <remarks>
    /// Deliberately not used to decide <see cref="IsSupported"/>. The version that first carries the
    /// field is not knowable while it is unreleased, and a version comparison that guessed high would
    /// enable the feature on a host that cannot actually report patterns. Probing for the field tests
    /// the exact thing the feature depends on, and stays correct if the field is ever serviced back to
    /// an older branch.
    /// </remarks>
    public static string? MSBuildVersion { get; } =
        typeof(ProjectCollection).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

    /// <summary>
    /// The search pattern for a directory enumeration, or <c>null</c> for an unfiltered enumeration,
    /// a non-enumeration access, or a host that does not report it.
    /// </summary>
    public static string? GetEnumeratePattern(ref FileAccessData fileAccessData)
        => EnumeratePatternGetter is null ? null : EnumeratePatternGetter(ref fileAccessData);
}
