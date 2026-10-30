using BiStudent.Server.Firebase;
using BiStudent.Server.Services;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Sync;

public sealed class SyncWorker(
    SyncService sync, SyncSignal signal, BackupService backup,
    PendingCloudSignIn pending, IServiceScopeFactory scopes,
    IOptions<FirebaseOptions> options, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (backup.TryEnterRequest())
                {
                    try
                    {
                        if (pending.TryGet(out _, out _))
                        {
                            try
                            {
                                using var scope = scopes.CreateScope();
                                await scope.ServiceProvider.GetRequiredService<CloudAccountService>()
                                    .TryCompleteDeferredSignInAsync(stoppingToken);
                            }
                            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                logger.LogWarning(exception, "Ошибка отложенного облачного входа");
                            }
                        }
                        await sync.RunAsync(stoppingToken);
                    }
                    finally { backup.ExitRequest(); }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ошибка фоновой синхронизации Firebase");
            }

            try
            {
                var interval = pending.TryGet(out _, out _) ||
                    sync.Status.State is SyncState.Offline or SyncState.Error
                    ? Math.Min(options.Value.SyncIntervalSeconds, 20)
                    : options.Value.SyncIntervalSeconds;
                await signal.WaitAsync(
                    TimeSpan.FromSeconds(interval), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
