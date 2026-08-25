// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.S3;

/// <summary>
/// The object key layout of the S3 cache. All keys live under
/// <c>{keyPrefix}/v{layoutVersion}-{hashType}/{universe}/</c>:
/// <list type="bullet">
///   <item><description><c>cas/{contentHash}</c> is content-addressed output files and PathSets.</description></item>
///   <item><description><c>selectors/{weakFingerprint}/{pathSetHash}/{strongFingerprintHex}</c> are empty objects, listed to discover the selectors of a weak fingerprint.</description></item>
///   <item><description><c>entries/{weakFingerprint}/{pathSetHash}/{strongFingerprintHex}</c> is the NodeBuildResult of a strong fingerprint, stored inline rather than by reference since it's never shared between entries.</description></item>
/// </list>
/// </summary>
internal sealed class S3ObjectKeys
{
    // Bumped when a change to the layout or to an object's payload makes existing objects unreadable, so that old and
    // new clients don't interpret each other's objects.
    private const int LayoutVersion = 1;

    private readonly string _rootPrefix;

    public S3ObjectKeys(string keyPrefix, HashType hashType, string universe)
    {
        string sanitizedPrefix = keyPrefix.Trim().Trim('/');
        if (string.IsNullOrEmpty(sanitizedPrefix))
        {
            sanitizedPrefix = "msbuildcache";
        }

        _rootPrefix = $"{sanitizedPrefix}/v{LayoutVersion}-{(int)hashType}/{universe}";
    }

    public string GetCas(ContentHash contentHash)
        => $"{_rootPrefix}/cas/{Escape(contentHash.Serialize())}";

    public string GetEntry(StrongFingerprint fingerprint)
        => $"{_rootPrefix}/entries/{GetSelectorPath(fingerprint.WeakFingerprint, fingerprint.Selector)}";

    public string GetSelector(StrongFingerprint fingerprint)
        => $"{_rootPrefix}/selectors/{GetSelectorPath(fingerprint.WeakFingerprint, fingerprint.Selector)}";

    public string GetSelectorPrefix(Fingerprint weakFingerprint)
        => $"{_rootPrefix}/selectors/{Escape(weakFingerprint.Serialize())}/";

    /// <summary>
    /// Reverses <see cref="GetSelector"/>. Returns false for any key not produced by this layout, as a listing can
    /// contain keys written by a different version of the plugin.
    /// </summary>
    public bool TryParseSelector(string key, Fingerprint weakFingerprint, out Selector selector)
    {
        selector = default;

        string prefix = GetSelectorPrefix(weakFingerprint);
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = key.Substring(prefix.Length).Split('/');
        if (parts.Length != 2 || !ContentHash.TryParse(Unescape(parts[0]), out ContentHash pathSetHash))
        {
            return false;
        }

        try
        {
            selector = new Selector(pathSetHash, HexUtilities.HexToBytes(parts[1]));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string GetSelectorPath(Fingerprint weakFingerprint, Selector selector)
        => $"{Escape(weakFingerprint.Serialize())}/{Escape(selector.ContentHash.Serialize())}/{HexUtilities.BytesToHex(selector.Output ?? Array.Empty<byte>())}";

    // Serialized content hashes and fingerprints separate the hash type with ':', which is legal in an S3 key but
    // awkward for tooling which maps keys onto file paths.
    private static string Escape(string value) => value.Replace(':', '~');

    private static string Unescape(string value) => value.Replace('~', ':');
}
