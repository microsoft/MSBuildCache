// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using Microsoft.Build.Execution;

namespace Microsoft.MSBuildCache;

internal static class ProjectInstanceExtensions
{
    /// <summary>
    /// Parses a property that may contain a bool, returning the default value if the property is not
    /// set, or returns false if the property is set to 'false', otherwise returning true.
    /// </summary>
    public static bool GetBoolPropertyValue(this ProjectInstance projectInstance, string propName, bool defaultValue = false)
    {
        string prop = projectInstance.GetPropertyValue(propName);
        if (string.IsNullOrWhiteSpace(prop))
        {
            return defaultValue;
        }

        if (string.Equals(prop, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static bool IsPlatformNegotiationBuild(this ProjectInstance projectInstance)
    {
        // the fact that there is zero global properties here allows us to determine this was an evaluation done for the sole use of determining a referenced project's compatible platforms. We know that this is a build for this use
        // because "IsGraphBuild" will be set to true in the global properties of all other project instances.
        return projectInstance.IsPlatformNegotiationEnabled() && projectInstance.GlobalProperties.Count == 0;
    }

    public static bool IsPlatformNegotiationEnabled(this ProjectInstance projectInstance)
    {
        // Determine whether or not the platform negotiation is turned on in msbuild
        return !string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue("EnableDynamicPlatformResolution"));
    }

    public static bool IsInnerBuild(this ProjectInstance projectInstance)
    {
        // This follows the logic of MSBuild's ProjectInterpretation.GetProjectType.
        // See: https://github.com/microsoft/msbuild/blob/master/src/Build/Graph/ProjectInterpretation.cs
        return !projectInstance.IsPlatformNegotiationBuild() && !string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue(projectInstance.GetPropertyValue("InnerBuildProperty"))) && !string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue(projectInstance.GetPropertyValue("InnerBuildPropertyValues")));
    }

    public static bool IsOuterBuild(this ProjectInstance projectInstance)
    {
        // This follows the logic of MSBuild's ProjectInterpretation.GetProjectType.
        // See: https://github.com/microsoft/msbuild/blob/master/src/Build/Graph/ProjectInterpretation.cs
        return !projectInstance.IsPlatformNegotiationBuild() && !projectInstance.IsInnerBuild() && !string.IsNullOrWhiteSpace(projectInstance.GetPropertyValue(projectInstance.GetPropertyValue("InnerBuildPropertyValues")));
    }
}
