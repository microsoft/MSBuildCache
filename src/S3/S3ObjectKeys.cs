// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.MemoizationStore.Interfaces.Sessions;
using Fingerprint = BuildXL.Cache.MemoizationStore.Interfaces.Sessions.Fingerprint;

namespace Microsoft.MSBuildCache.S3;

// {prefix}/{cas|entries|selectors}/v{version}-{hashType}/{universe}/...
// The kind comes first so a lifecycle rule can match it by prefix.
internal sealed class S3ObjectKeys
{
    // Bump when existing objects become unreadable by the new layout.
    private const int LayoutVersion = 1;

    private readonly string _keyPrefix;
    private readonly string _scope;

    public S3ObjectKeys(string keyPrefix, HashType hashType, string universe)
    {
        string sanitizedPrefix = keyPrefix.Trim().Trim('/');
        if (string.IsNullOrEmpty(sanitizedPrefix))
        {
            sanitizedPrefix = "msbuildcache";
        }

        _keyPrefix = sanitizedPrefix;
        _scope = $"v{LayoutVersion}-{(int)hashType}/{universe}";
    }

    public string GetCas(ContentHash contentHash)
        => $"{KindPrefix("cas")}{Escape(contentHash.Serialize())}";

    public string GetEntry(StrongFingerprint fingerprint)
        => $"{KindPrefix("entries")}{GetSelectorPath(fingerprint.WeakFingerprint, fingerprint.Selector)}";

    public string GetSelector(StrongFingerprint fingerprint)
        => $"{KindPrefix("selectors")}{GetSelectorPath(fingerprint.WeakFingerprint, fingerprint.Selector)}";

    public string GetSelectorPrefix(Fingerprint weakFingerprint)
        => $"{KindPrefix("selectors")}{Escape(weakFingerprint.Serialize())}/";

    private string KindPrefix(string kind) => $"{_keyPrefix}/{kind}/{_scope}/";

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

    // Serialized hashes contain ':', which is valid in a key but not in a file path for tools which mirror buckets to disk.
    private static string Escape(string value) => value.Replace(':', '~');

    private static string Unescape(string value) => value.Replace('~', ':');
}
