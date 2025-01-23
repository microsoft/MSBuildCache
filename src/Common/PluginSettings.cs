// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DotNet.Globbing;
using Microsoft.Build.Experimental.ProjectCache;

namespace Microsoft.MSBuildCache;

public class PluginSettings
{
    private static readonly char[] ValueSeparator = [';'];

    private static readonly char[] DirectorySeparators = ['\\', '/'];

    private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

    private static readonly GlobOptions GlobOptions = new() { Evaluation = { CaseInsensitive = true } };

    private enum SettingParseResult { Success, InvalidValue, UnsupportedType };

    private static readonly Dictionary<Type, Func<string, object?>> PrimitiveParsers = new()
    {
        { typeof(bool), s => ParseOrNull<bool>(s, bool.TryParse) },
        { typeof(byte), s => ParseOrNull<byte>(s, byte.TryParse) },
        { typeof(sbyte), s => ParseOrNull<sbyte>(s, sbyte.TryParse) },
        { typeof(char), s => ParseOrNull<char>(s, char.TryParse) },
        { typeof(int), s => ParseOrNull<int>(s, int.TryParse) },
        { typeof(uint), s => ParseOrNull<uint>(s, uint.TryParse) },
        { typeof(short), s => ParseOrNull<short>(s, short.TryParse) },
        { typeof(ushort), s => ParseOrNull<ushort>(s, ushort.TryParse) },
        { typeof(long), s => ParseOrNull<long>(s, long.TryParse) },
        { typeof(float), s => ParseOrNull<float>(s, float.TryParse) },
        { typeof(double), s => ParseOrNull<double>(s, double.TryParse) },
        { typeof(decimal), s => ParseOrNull<decimal>(s, decimal.TryParse) },
    };

    private delegate bool TryParseFunc<T>(string value, out T result);

    private static T? ParseOrNull<T>(string value, TryParseFunc<T> tryParseFunc)
        where T : struct
        => tryParseFunc(value, out T result) ? result : null;

    private static readonly HashSet<string> ListTypeNames = GetAssignableCollectionTypeNames(typeof(List<>));

    private static readonly HashSet<string> HashSetTypeNames = GetAssignableCollectionTypeNames(typeof(HashSet<>));

    private string _logDirectory = "MSBuildCacheLogs";
    private string? _localCacheRootPath;

    public required string RepoRoot { get; init; }

    /// <summary>
    /// Base directory to use for logging. If a relative path, it's assumed relative to the repo root.
    /// </summary>
    public string LogDirectory
    {
        get => Path.Combine(RepoRoot, _logDirectory);
        init => _logDirectory = value;
    }

    public string CacheUniverse { get; init; } = "default";

    public int MaxConcurrentCacheContentOperations { get; init; } = 64;

    /// <summary>
    /// Base directory to use for the local cache. If null, the default value is the "MSBuildCache" folder in the root of the drive the repo root is on.
    /// </summary>
    public string LocalCacheRootPath
    {
        get => string.IsNullOrEmpty(_localCacheRootPath)
                ? Path.Combine(Path.GetPathRoot(RepoRoot)!, "MSBuildCache")
                : _localCacheRootPath!;
        init => _localCacheRootPath = value;
    }

    public uint LocalCacheSizeInMegabytes { get; init; } = 102400; // 100GB

    public IReadOnlyCollection<Glob> IgnoredInputPatterns { get; init; } = Array.Empty<Glob>();

    public IReadOnlyCollection<Glob> IgnoredOutputPatterns { get; init; } = Array.Empty<Glob>();

    public IReadOnlyCollection<Glob> IdenticalDuplicateOutputPatterns { get; init; } = Array.Empty<Glob>();

    public bool RemoteCacheIsReadOnly { get; init; }

    public bool AsyncCachePublishing { get; init; } = true;

    public bool AsyncCacheMaterialization { get; init; } = true;

    public IReadOnlyCollection<Glob> AllowFileAccessAfterProjectFinishProcessPatterns { get; init; } = Array.Empty<Glob>();

    public IReadOnlyCollection<Glob> AllowFileAccessAfterProjectFinishFilePatterns { get; init; } = Array.Empty<Glob>();

    public IReadOnlyCollection<Glob> AllowProcessCloseAfterProjectFinishProcessPatterns { get; init; } = Array.Empty<Glob>();

    public IReadOnlyList<string> GlobalPropertiesToIgnore { get; init; } = Array.Empty<string>();

    public bool GetResultsForUnqueriedDependencies { get; init; }

    public IReadOnlyList<string> TargetsToIgnore { get; init; } = Array.Empty<string>();

    public bool IgnoreDotNetSdkPatchVersion { get; init; }

    public bool SkipUnchangedOutputFiles { get; init; }

    public static T Create<T>(
        IReadOnlyDictionary<string, string> settings,
        PluginLoggerBase logger,
        string repoRoot)
        where T : PluginSettings
    {
        T? pluginSettings = Activator.CreateInstance<T>();

        StringBuilder logMessage = new();
        logMessage.AppendLine("Effective plugin settings:");

        PropertyInfo[] properties = typeof(T).GetProperties();
        foreach (PropertyInfo property in properties)
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            string propertyName = property.Name;

            // Special-case RepoRoot since it's not a setting but provided separately.
            if (propertyName.Equals(nameof(RepoRoot), StringComparison.Ordinal))
            {
                MethodInfo setMethod = property.GetSetMethod(nonPublic: true)!;
                _ = setMethod.Invoke(pluginSettings, new[] { repoRoot });
                continue;
            }

            object? settingValue = null;
            if (settings.TryGetValue(propertyName, out string? rawSettingValue))
            {
                SettingParseResult parseResult = TryParseSettingValue(property.PropertyType, rawSettingValue, repoRoot, out settingValue);
                switch (parseResult)
                {
                    case SettingParseResult.Success:
                    {
                        if (settingValue != null)
                        {
                            // We already checked CanWrite, should not be null
                            MethodInfo setMethod = property.GetSetMethod(nonPublic: true)!;
                            _ = setMethod.Invoke(pluginSettings, new[] { settingValue });
                        }

                        break;
                    }
                    case SettingParseResult.InvalidValue:
                    {
                        logger.LogWarning($"Setting '{propertyName}' has invalid value '{rawSettingValue}'. Ignoring and using the default.");
                        break;
                    }
                    case SettingParseResult.UnsupportedType:
                    {
                        logger.LogWarning($"Setting '{propertyName}' has unsupported type '{property.PropertyType}'. Ignoring.");
                        break;
                    }
                }
            }

            // If there was no user-configured value or it was invalid, use the default
            if (settingValue == null)
            {
                // We already checked CanRead, should not be null
                MethodInfo getMethod = property.GetGetMethod(nonPublic: true)!;
                settingValue = getMethod.Invoke(pluginSettings, Array.Empty<object>());
            }

            logMessage.Append(propertyName);
            logMessage.Append(": ");
            AppendFormattedSettingValue(logMessage, settingValue);
            logMessage.AppendLine();
        }

        logger.LogMessage(logMessage.ToString());

        return pluginSettings;
    }

    private static void AppendFormattedSettingValue(StringBuilder sb, object? settingValue)
    {
        if (settingValue is IEnumerable enumerable and not string)
        {
            foreach (object obj in enumerable)
            {
                AppendFormattedSettingValue(sb, obj);
                sb.Append(';');
            }

            // Cut off the last ';'
            sb.Length--;
        }
        else
        {
            sb.Append(settingValue?.ToString() ?? "(null)");
        }
    }

    private static SettingParseResult TryParseSettingValue(
        Type type,
        string rawSettingValue,
        string repoRoot,
        out object? settingValue)
    {
        rawSettingValue = rawSettingValue.Trim();
        if (string.IsNullOrEmpty(rawSettingValue))
        {
            settingValue = null;
            return SettingParseResult.Success;
        }

        if (type == typeof(string))
        {
            settingValue = rawSettingValue;
            return SettingParseResult.Success;
        }

        if (type.IsEnum)
        {
#if NETFRAMEWORK
            try
            {
                settingValue = Enum.Parse(type, rawSettingValue);
                return SettingParseResult.Success;
            }
            catch (ArgumentException)
            {
                settingValue = null;
                return SettingParseResult.InvalidValue;
            }
#else
            return Enum.TryParse(type, rawSettingValue, out settingValue)
                ? SettingParseResult.Success
                : SettingParseResult.InvalidValue;
#endif
        }

        if (PrimitiveParsers.TryGetValue(type, out Func<string, object?>? parser))
        {
            settingValue = parser(rawSettingValue);
            return settingValue == null ? SettingParseResult.InvalidValue : SettingParseResult.Success;
        }

        if (type == typeof(Uri))
        {
            if (Uri.TryCreate(rawSettingValue, UriKind.Absolute, out Uri? uri))
            {
                settingValue = uri;
                return SettingParseResult.Success;
            }
            else
            {
                settingValue = null;
                return SettingParseResult.InvalidValue;
            }
        }

        if (type == typeof(Glob))
        {
            string globSpec = rawSettingValue;

            if (globSpec.IndexOfAny(InvalidPathChars) != -1)
            {
                settingValue = null;
                return SettingParseResult.InvalidValue;
            }

            int firstDirSeparator = globSpec.IndexOfAny(DirectorySeparators);
            if (firstDirSeparator == -1)
            {
                // This looks like a match on the file name, so prepend a recursive match under the repo.
                globSpec = Path.Combine(repoRoot, "**", globSpec);
            }
            else if (firstDirSeparator == 0)
            {
                // A leading slash means the glob is *not* to be rooted under the repo.
                // e.g. `\**\vctip.exe` becomes `**\vctip.exe`
                globSpec = globSpec.Substring(1);
            }
            else if (!Path.IsPathRooted(globSpec))
            {
                // Root the path if needed.
                globSpec = Path.Combine(repoRoot, globSpec);
            }

            try
            {
                settingValue = Glob.Parse(globSpec, GlobOptions);
            }
            catch (Exception)
            {
                settingValue = null;
                return SettingParseResult.InvalidValue;
                throw;
            }

            return SettingParseResult.Success;
        }

        if (type.IsArray)
        {
            Type? elementType = type.GetElementType();
            if (elementType != null)
            {
                SettingParseResult collectionParseResult = TryParseCollectionSetting(elementType, rawSettingValue, repoRoot, out object[]? elementValues);
                if (collectionParseResult != SettingParseResult.Success
                    || elementValues == null
                    || elementValues.Length == 0)
                {
                    settingValue = null;
                    return collectionParseResult;
                }

                Array arr = Array.CreateInstance(elementType, elementValues.Length);
                elementValues.CopyTo(arr, 0);
                settingValue = arr;

                return SettingParseResult.Success;
            }
        }

        // Handle collection types. Only certain collection types are supported.
        if (typeof(IEnumerable).IsAssignableFrom(type) && type.IsGenericType)
        {
            Type[] genericTypeArguments = type.GenericTypeArguments;
            if (genericTypeArguments.Length == 1)
            {
                Type? elementType = genericTypeArguments[0];
                if (elementType != null)
                {
                    SettingParseResult collectionParseResult = TryParseCollectionSetting(elementType, rawSettingValue, repoRoot, out object[]? elementValues);
                    if (collectionParseResult != SettingParseResult.Success
                        || elementValues == null
                        || elementValues.Length == 0)
                    {
                        settingValue = null;
                        return collectionParseResult;
                    }

                    string genericTypeName = type.GetGenericTypeDefinition().Name;
                    MethodInfo? addMethod;

                    if (ListTypeNames.Contains(genericTypeName))
                    {
                        Type listType = typeof(List<>).MakeGenericType(elementType);

                        // new List<T>(elementValues.Length)
                        settingValue = listType
                            .GetConstructor(new Type[] { typeof(int) })!
                            .Invoke(new object[] { elementValues.Length })!;

                        // List<T>.Add(T item)
                        addMethod = listType.GetMethod("Add", new[] { elementType })!;
                    }
                    else if (HashSetTypeNames.Contains(genericTypeName))
                    {
                        Type hashSetType = typeof(HashSet<>).MakeGenericType(elementType);

                        // new HashSet<T>(elementValues.Length)
                        settingValue = hashSetType
                            .GetConstructor(new Type[] { typeof(int) })!
                            .Invoke(new object[] { elementValues.Length })!;

                        // HashSet<T>.Add(T item)
                        addMethod = hashSetType.GetMethod("Add", new[] { elementType })!;
                    }
                    else
                    {
                        settingValue = null;
                        addMethod = null;
                    }

                    if (settingValue != null && addMethod != null)
                    {
                        foreach (object elementValue in elementValues)
                        {
                            addMethod.Invoke(settingValue, new[] { elementValue });
                        }
                    }

                    return SettingParseResult.Success;
                }
            }
        }

        settingValue = null;
        return SettingParseResult.UnsupportedType;
    }

    private static SettingParseResult TryParseCollectionSetting(
        Type elementType,
        string rawSettingValue,
        string repoRoot,
        out object[]? values)
    {
        string[] rawValues = rawSettingValue.Split(
            ValueSeparator,
            StringSplitOptions.RemoveEmptyEntries
#if !NETFRAMEWORK
            | StringSplitOptions.TrimEntries
#endif
        );

        if (rawValues.Length == 0)
        {
            values = Array.Empty<object>();
            return SettingParseResult.Success;
        }

        values = new object[rawValues.Length];
        int valueIdx = 0;
        for (int i = 0; i < rawValues.Length; i++)
        {
            string rawValue = rawValues[i];
            SettingParseResult parseResult = TryParseSettingValue(elementType, rawValue, repoRoot, out object? elementValue);
            if (parseResult != SettingParseResult.Success)
            {
                values = null;
                return parseResult;
            }

            if (elementValue != null)
            {
                values[valueIdx++] = elementValue;
            }
        }

        Array.Resize(ref values, valueIdx);
        return SettingParseResult.Success;
    }

    /// <summary>
    /// Gets a set of all compatible type names for a given type.
    /// </summary>
    /// <remarks>
    /// We cannot use <see cref="Type"/> as generic types see to have fake/stub generic interfaces which don't match the runtime types actually used.
    /// </remarks>
    private static HashSet<string> GetAssignableCollectionTypeNames(Type type)
    {
        HashSet<string> typeNames = new();

        // Add self
        typeNames.Add(type.Name);

        // Add all interfaces which are generic types
        foreach (Type t in type.GetInterfaces())
        {
            if (t.IsGenericType)
            {
                typeNames.Add(t.Name);
            }
        }

        return typeNames;
    }
}
