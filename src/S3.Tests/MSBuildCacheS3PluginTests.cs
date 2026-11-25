// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Reflection;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.MSBuildCache.S3.Tests;

[TestClass]
public class MSBuildCacheS3PluginTests
{
    private const string RepoRoot = @"X:\Repo";
    private const string Region = "eu-central-1";
    private const string ServiceUrl = "http://minio.example:9000";

    [TestMethod]
    public async Task ServiceUrlUsesRegionForSigning()
    {
        AmazonS3Config config = await CreateConfigAsync(
            new S3PluginSettings
            {
                RepoRoot = RepoRoot,
                Region = Region,
                ServiceUrl = new Uri(ServiceUrl),
            });

        Assert.AreEqual($"{ServiceUrl}/", config.ServiceURL);
        Assert.AreEqual(Region, config.AuthenticationRegion);
    }

    [TestMethod]
    public async Task RegionSelectsEndpointWithoutServiceUrl()
    {
        AmazonS3Config config = await CreateConfigAsync(new S3PluginSettings { RepoRoot = RepoRoot, Region = Region });

        Assert.IsNull(config.ServiceURL);
        Assert.IsNotNull(config.RegionEndpoint);
        Assert.AreEqual(Region, config.RegionEndpoint.SystemName);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ForcePathStyle(bool forcePathStyle)
    {
        AmazonS3Config config = await CreateConfigAsync(
            new S3PluginSettings
            {
                RepoRoot = RepoRoot,
                ServiceUrl = new Uri(ServiceUrl),
                ForcePathStyle = forcePathStyle,
            });

        Assert.AreEqual(forcePathStyle, config.ForcePathStyle);
    }

    private static async Task<AmazonS3Config> CreateConfigAsync(S3PluginSettings settings)
    {
        // Explicit credentials so that the default AWS credential chain isn't consulted.
        await using MSBuildCacheS3Plugin plugin = new(new BasicAWSCredentials("fake-access-key", "fake-secret-key"));

        MethodInfo createS3Client = typeof(MSBuildCacheS3Plugin).GetMethod("CreateS3Client", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using AmazonS3Client client = (AmazonS3Client)createS3Client.Invoke(plugin, [settings])!;
        return (AmazonS3Config)client.Config;
    }
}
