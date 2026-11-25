// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;

namespace Microsoft.MSBuildCache.S3;

// The SDK retries failed requests, but not a response body which fails mid-stream. Reads which still fail are
// reported as absent so that the build continues without the cache.
internal sealed class S3ReadRetry
{
    internal const int DefaultMaxAttempts = 3;

    private readonly int _maxAttempts;
    private readonly TimeSpan _initialBackoff;

    internal S3ReadRetry(int maxAttempts = DefaultMaxAttempts, TimeSpan? initialBackoff = null)
    {
        _maxAttempts = Math.Max(1, maxAttempts);
        _initialBackoff = initialBackoff ?? TimeSpan.FromMilliseconds(200);
    }

    internal async Task<(bool Succeeded, T? Value)> TryRunAsync<T>(
        string description,
        Func<Task<T>> operationAsync,
        Action<string> trace,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                T value = await operationAsync();
                if (attempt > 1)
                {
                    trace($"{description} succeeded on attempt {attempt} of {_maxAttempts}.");
                }

                return (true, value);
            }
            catch (AmazonS3Exception ex) when (S3CacheClient.IsNotFound(ex))
            {
                throw;
            }
            // The SDK reports its own request timeout as a cancellation, so only the caller's token stops retries.
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= _maxAttempts)
                {
                    warn($"{description} failed on all {_maxAttempts} attempts: {ex}");
                    return (false, default);
                }

                TimeSpan backoff = TimeSpan.FromMilliseconds(_initialBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
                trace($"{description} failed on attempt {attempt} of {_maxAttempts}, retrying in {backoff.TotalMilliseconds:F0} ms: {ex.Message}");
                await Task.Delay(backoff, cancellationToken);
            }
        }
    }

    internal async Task<bool> TryRunAsync(
        string description,
        Func<Task> operationAsync,
        Action<string> trace,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        (bool succeeded, _) = await TryRunAsync(
            description,
            async () =>
            {
                await operationAsync();
                return true;
            },
            trace,
            warn,
            cancellationToken);

        return succeeded;
    }
}
