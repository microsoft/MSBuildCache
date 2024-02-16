// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.Hashing;

namespace Microsoft.MSBuildCache.Hashing;

internal sealed class OutputHasher : IAsyncDisposable
{
    private static readonly int HashingParallelism = Environment.ProcessorCount;

    private readonly Func<string, CancellationToken, Task<ContentHash>> _computeHashAsync;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Channel<HashOperationContext> _hashingChannel;
    private readonly Task[] _channelWorkerTasks;

    public OutputHasher(IContentHasher hasher)
        : this(async (path, ct) => await hasher.GetFileHashAsync(path))
    { }

    public OutputHasher(Func<string, CancellationToken, Task<ContentHash>> computeHashAsync)
    {
        _computeHashAsync = computeHashAsync;
        _cancellationTokenSource = new CancellationTokenSource();
        _hashingChannel = Channel.CreateUnbounded<HashOperationContext>();

        // Create a bunch of worker tasks to process the hash operations.
        _channelWorkerTasks = new Task[HashingParallelism];
        for (int i = 0; i < _channelWorkerTasks.Length; i++)
        {
            _channelWorkerTasks[i] = Task.Run(
                async () =>
                {
                    // Not using 'Reader.ReadAllAsync' because its not available in the version we use here.
                    // Also not passing using the cancellation token here as we need to drain the entire channel to ensure we don't leave dangling Tasks.
                    while (await _hashingChannel.Reader.WaitToReadAsync())
                    {
                        while (_hashingChannel.Reader.TryRead(out HashOperationContext context))
                        {
                            await ComputeHashInternalAsync(context, _cancellationTokenSource.Token);
                        }
                    }
                });
        }
    }

    public async Task<ContentHash> ComputeHashAsync(string filePath, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ContentHash> taskCompletionSource = new();
        HashOperationContext context = new(filePath, taskCompletionSource);
        await _hashingChannel.Writer.WriteAsync(context, cancellationToken);
        return await taskCompletionSource.Task;
    }

    public async ValueTask DisposeAsync()
    {
#if NETFRAMEWORK
        _cancellationTokenSource.Cancel();
#else
        await _cancellationTokenSource.CancelAsync();
#endif
        _hashingChannel.Writer.Complete();
        await _hashingChannel.Reader.Completion;
        await Task.WhenAll(_channelWorkerTasks);
        _cancellationTokenSource.Dispose();
    }

    private async Task ComputeHashInternalAsync(HashOperationContext context, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            context.TaskCompletionSource.TrySetCanceled(cancellationToken);
            return;
        }

        try
        {
            ContentHash contentHash = await _computeHashAsync(context.FilePath, cancellationToken);
            context.TaskCompletionSource.TrySetResult(contentHash);
        }
        catch (Exception ex)
        {
            context.TaskCompletionSource.TrySetException(ex);
        }
    }

    private readonly struct HashOperationContext
    {
        public HashOperationContext(string filePath, TaskCompletionSource<ContentHash> taskCompletionSource)
        {
            FilePath = filePath;
            TaskCompletionSource = taskCompletionSource;
        }

        public string FilePath { get; }

        public TaskCompletionSource<ContentHash> TaskCompletionSource { get; }
    }
}
