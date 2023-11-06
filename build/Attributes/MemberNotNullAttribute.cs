// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETFRAMEWORK || NETSTANDARD
namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(string member) => Members = new[] { member };

        public MemberNotNullAttribute(params string[] members) => Members = members;

        public string[] Members { get; }
    }
}
#endif