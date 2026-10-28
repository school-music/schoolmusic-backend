namespace schoolmusic_backend.Services;

/// <summary>
/// Zapewnia, że w jednej instancji aplikacji tylko jedno żądanie jednocześnie odświeża cache statystyk.
/// </summary>
public interface IStatisticsRefreshCoordinator
{
    Task<DashboardStatisticsDto> ExecuteAsync(
        Func<Task<DashboardStatisticsDto>> refresh,
        CancellationToken cancellationToken);
}

public sealed class StatisticsRefreshCoordinator : IStatisticsRefreshCoordinator, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<DashboardStatisticsDto> ExecuteAsync(
        Func<Task<DashboardStatisticsDto>> refresh,
        CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await refresh();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
