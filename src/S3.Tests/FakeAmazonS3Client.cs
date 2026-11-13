// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Microsoft.MSBuildCache.S3.Tests;

// An in-memory bucket. Operations which aren't overridden go to an unused port and fail quickly.
internal sealed class FakeAmazonS3Client : AmazonS3Client
{
    public const string ServiceUrl = "http://127.0.0.1:9/";

    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<GetObjectRequest> _reads = new();
    private readonly ConcurrentQueue<string> _writtenKeys = new();
    private readonly ConcurrentQueue<string> _renewedKeys = new();
    private readonly ConcurrentQueue<CopyObjectRequest> _copies = new();
    private int _metadataReadCount;

    public FakeAmazonS3Client()
        : base(
            new BasicAWSCredentials("fake-access-key", "fake-secret-key"),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = true,
                MaxErrorRetry = 0,
                Timeout = TimeSpan.FromSeconds(5),
            })
    {
    }

    public IReadOnlyList<GetObjectRequest> Reads => _reads.ToArray();

    public IReadOnlyList<string> WrittenKeys => _writtenKeys.ToArray();

    public IReadOnlyList<string> RenewedKeys => _renewedKeys.ToArray();

    public IReadOnlyList<CopyObjectRequest> Copies => _copies.ToArray();

    public int MetadataReadCount => Volatile.Read(ref _metadataReadCount);

    public Func<GetObjectRequest, Task>? BeforeRead { get; set; }

    public Func<GetObjectRequest, byte[], byte[]>? RewriteBody { get; set; }

    public Func<ListObjectsV2Request, Task>? BeforeList { get; set; }

    public Func<CopyObjectRequest, Task>? BeforeCopy { get; set; }

    public int MaxKeysPerPage { get; set; } = int.MaxValue;

    public void Put(string key, byte[] bytes) => _objects[key] = new StoredObject(bytes, DateTime.UtcNow);

    public byte[] Read(string key) => _objects[key].Bytes;

    public DateTime LastModified(string key) => _objects[key].LastModified;

    public void Backdate(string key, TimeSpan age)
    {
        StoredObject stored = _objects[key];
        _objects[key] = stored with { LastModified = DateTime.UtcNow - age };
    }

    public void OverrideContentLength(string key, long contentLength)
    {
        StoredObject stored = _objects[key];
        _objects[key] = stored with { ReportedLength = contentLength };
    }

    public override async Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken)
    {
        _reads.Enqueue(request);

        if (BeforeRead is not null)
        {
            await BeforeRead(request);
        }

        if (!_objects.TryGetValue(request.Key, out StoredObject? stored))
        {
            throw NoSuchKey(request.Key);
        }

        byte[] content = stored.Bytes;
        byte[] body = request.ByteRange is null
            ? content
            : content.AsSpan((int)request.ByteRange.Start, (int)(request.ByteRange.End - request.ByteRange.Start + 1)).ToArray();

        if (RewriteBody is not null)
        {
            body = RewriteBody(request, body);
        }

#pragma warning disable CA2000 // The response owns the stream and the cache client disposes the response.
        return new GetObjectResponse
        {
            BucketName = request.BucketName,
            Key = request.Key,
            ContentLength = body.Length,
            HttpStatusCode = HttpStatusCode.OK,
            ResponseStream = new MemoryStream(body, writable: false),
        };
#pragma warning restore CA2000
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _metadataReadCount);

        if (!_objects.TryGetValue(request.Key, out StoredObject? stored))
        {
            throw NoSuchKey(request.Key);
        }

        return Task.FromResult(new GetObjectMetadataResponse
        {
            ContentLength = stored.ReportedLength ?? stored.Bytes.Length,
            LastModified = stored.LastModified,
            HttpStatusCode = HttpStatusCode.OK,
        });
    }

    public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        await request.InputStream.CopyToAsync(buffer, cancellationToken);

        _objects[request.Key] = new StoredObject(buffer.ToArray(), DateTime.UtcNow);
        _writtenKeys.Enqueue(request.Key);

        return new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    public override async Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken)
    {
        if (BeforeList is not null)
        {
            await BeforeList(request);
        }

        List<string> keys = _objects.Keys
            .Where(key => key.StartsWith(request.Prefix, StringComparison.Ordinal))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        int start = request.ContinuationToken is null ? 0 : keys.IndexOf(request.ContinuationToken);
        List<S3Object> page = keys
            .Skip(start)
            .Take(MaxKeysPerPage)
            .Select(key => new S3Object { BucketName = request.BucketName, Key = key, Size = _objects[key].Bytes.Length })
            .ToList();

        int next = start + page.Count;
        return new ListObjectsV2Response
        {
            S3Objects = page,
            KeyCount = page.Count,
            Prefix = request.Prefix,
            IsTruncated = next < keys.Count,
            NextContinuationToken = next < keys.Count ? keys[next] : null,
            HttpStatusCode = HttpStatusCode.OK,
        };
    }

    public override async Task<CopyObjectResponse> CopyObjectAsync(CopyObjectRequest request, CancellationToken cancellationToken)
    {
        _copies.Enqueue(request);
        if (BeforeCopy is not null)
        {
            await BeforeCopy(request);
        }

        if (!_objects.TryGetValue(request.SourceKey, out StoredObject? stored))
        {
            throw NoSuchKey(request.SourceKey);
        }

        if (string.Equals(request.SourceKey, request.DestinationKey, StringComparison.Ordinal)
            && string.Equals(request.SourceBucket, request.DestinationBucket, StringComparison.Ordinal)
            && request.MetadataDirective != S3MetadataDirective.REPLACE)
        {
            throw new AmazonS3Exception("Copying an object to itself requires a change.", ErrorType.Sender, "InvalidRequest", "fake-request-id", HttpStatusCode.BadRequest);
        }

        _objects[request.DestinationKey] = stored with { LastModified = DateTime.UtcNow };
        _renewedKeys.Enqueue(request.DestinationKey);
        return new CopyObjectResponse { HttpStatusCode = HttpStatusCode.OK };
    }

    public static AmazonS3Exception NoSuchKey(string key)
        => new($"The specified key '{key}' does not exist.", ErrorType.Sender, "NoSuchKey", "fake-request-id", HttpStatusCode.NotFound);

    private sealed record StoredObject(byte[] Bytes, DateTime LastModified, long? ReportedLength = null);
}
