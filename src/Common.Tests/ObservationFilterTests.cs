// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Build.Experimental.FileAccess;
using Microsoft.MSBuildCache.FileAccess;
using Microsoft.MSBuildCache.Fingerprinting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.Tests;

[TestClass]
public class ObservationFilterTests
{
    [TestMethod]
    [DataRow(0u, ObservationType.ExistingProbe, DisplayName = "success")]
    [DataRow(2u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_FILE_NOT_FOUND")]
    [DataRow(3u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_PATH_NOT_FOUND")]
    [DataRow(53u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_BAD_NETPATH")]
    [DataRow(123u, ObservationType.AbsentPathProbe, DisplayName = "ERROR_INVALID_NAME")]
    [DataRow(5u, ObservationType.ExistingProbe, DisplayName = "ERROR_ACCESS_DENIED is transient, not absence")]
    [DataRow(32u, ObservationType.ExistingProbe, DisplayName = "ERROR_SHARING_VIOLATION is transient, not absence")]
    [DataRow(1224u, ObservationType.ExistingProbe, DisplayName = "unrecognized error is not absence")]
    public void ProbeErrorClassification(uint error, ObservationType expected)
    {
        Assert.AreEqual(
            expected,
            FileAccessRepository.ClassifyObservation(RequestedAccess.Probe, error),
            "Only definitive not-found codes may classify as absent. Mapping a transient failure such as "
            + "ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED to AbsentPathProbe would let machine flakiness "
            + "change the PathSet and lose cache hits.");
    }

    /// <summary>
    /// <see cref="RequestedAccess.Enumerate"/> is reported against the directory being enumerated, but
    /// <see cref="RequestedAccess.EnumerationProbe"/> is reported against each matched child. Recording the
    /// latter as a directory enumeration would key the entry on a file path, and since lookup-time
    /// validation requires the path to still be a directory, such an entry could never re-validate — every
    /// build using <c>FindFirstFileEx</c>/<c>FindNextFile</c> enumeration would permanently miss.
    /// </summary>
    [TestMethod]
    public void EnumerationProbeIsAProbeNotADirectoryEnumeration()
    {
        Assert.AreEqual(
            ObservationType.DirectoryEnumeration,
            FileAccessRepository.ClassifyObservation(RequestedAccess.Enumerate, 0),
            "Enumerate is reported on the directory itself and carries the search pattern.");

        Assert.AreEqual(
            ObservationType.ExistingProbe,
            FileAccessRepository.ClassifyObservation(RequestedAccess.EnumerationProbe, 0),
            "EnumerationProbe is reported per matched child, so it is an existence probe on that child.");
    }

    /// <summary>
    /// <see cref="RequestedAccess"/> is a flags enum, and content access must keep flowing to the normal
    /// file-table handling rather than being short-circuited into an observation.
    /// </summary>
    [TestMethod]
    [DataRow(RequestedAccess.Read, DisplayName = "Read")]
    [DataRow(RequestedAccess.Write, DisplayName = "Write")]
    [DataRow(RequestedAccess.ReadWrite, DisplayName = "ReadWrite")]
    [DataRow(RequestedAccess.Read | RequestedAccess.Probe, DisplayName = "Read combined with Probe")]
    [DataRow(RequestedAccess.None, DisplayName = "None")]
    public void ContentAccessIsNotAnObservation(RequestedAccess requestedAccess)
    {
        Assert.IsNull(
            FileAccessRepository.ClassifyObservation(requestedAccess, 0),
            "Accesses carrying Read or Write are content accesses and must reach the file table.");
    }
}
