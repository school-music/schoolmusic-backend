using System.Collections.Concurrent;

namespace schoolmusic_backend.Services;

/// <summary>
/// Porządkuje równoczesne operacje zmieniające kolejkę tej samej przerwy w ramach jednej instancji aplikacji.
/// Ograniczenia unikalne bazy danych pozostają ostatecznym zabezpieczeniem integralności danych.
/// </summary>
public interface IQueueMutationLock
{
    ValueTask<IAsyncDisposable> AcquireAsync(int breakId, CancellationToken cancellationToken = default);
}

public sealed class QueueMutationLock : IQueueMutationLock
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(int breakId, CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(breakId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
