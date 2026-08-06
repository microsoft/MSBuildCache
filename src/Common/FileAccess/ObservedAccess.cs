// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using Microsoft.MSBuildCache.Fingerprinting;

namespace Microsoft.MSBuildCache.FileAccess;

/// <summary>
/// A single typed sandbox observation that contributes (subject to filtering) to the strong
/// fingerprint as an <see cref="ObservedPathEntry"/>. Created by <see cref="FileAccessRepository"/> as
/// probe and enumeration events arrive from MSBuild; consumed by <see cref="FingerprintFactory"/>'s
/// <c>GetPathSet</c>.
/// </summary>
public sealed record ObservedAccess(
    string Path,
    ObservationType Type,
    string? EnumerationPattern = null,
    IReadOnlyList<string>? Members = null,
    IReadOnlyList<string>? WrittenMembers = null);
