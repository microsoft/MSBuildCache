// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Reflection;

namespace Microsoft.MSBuildCache.FileAccess;

/// <summary>
/// Reads a property from a struct without boxing it. Instance members on a value type take their
/// receiver by reference, so an open-instance delegate bound to a struct property getter must too.
/// </summary>
internal delegate TResult ByRefGetter<TStruct, TResult>(ref TStruct instance)
    where TStruct : struct;

/// <summary>
/// Binds open-instance getters for properties that may not exist on the running assembly.
/// </summary>
/// <remarks>
/// MSBuildCache compiles against a reference assembly but runs against whatever <c>Microsoft.Build.dll</c>
/// the host MSBuild supplies, so properties added in newer MSBuild versions cannot be called directly —
/// they would not compile against the reference assembly, and would throw
/// <see cref="MissingMethodException"/> on older hosts if they did. Binding a delegate once and reusing
/// it keeps the per-access cost to roughly a virtual call, which matters because file-access reporting
/// is a very hot path.
/// </remarks>
internal static class ByRefGetterFactory
{
    /// <summary>
    /// Binds a getter for <paramref name="propertyName"/>, or returns <c>null</c> if the property does
    /// not exist or does not have type <typeparamref name="TResult"/> — i.e. the running assembly
    /// predates the property.
    /// </summary>
    public static ByRefGetter<TStruct, TResult>? TryCreate<TStruct, TResult>(string propertyName)
        where TStruct : struct
    {
        PropertyInfo? property = typeof(TStruct).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(TResult))
        {
            return null;
        }

        MethodInfo? getter = property.GetGetMethod(nonPublic: false);
        if (getter is null)
        {
            return null;
        }

        try
        {
#if NET9_0_OR_GREATER
            return getter.CreateDelegate<ByRefGetter<TStruct, TResult>>();
#else
            return (ByRefGetter<TStruct, TResult>)getter.CreateDelegate(typeof(ByRefGetter<TStruct, TResult>));
#endif
        }
        catch (ArgumentException)
        {
            // The property exists but its getter does not have the expected shape. Treat it as absent
            // rather than failing the build.
            return null;
        }
    }
}
