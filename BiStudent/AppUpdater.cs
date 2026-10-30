using Velopack;
using Velopack.Sources;

namespace BiStudent;

internal static class AppUpdater
{
    // Без VelopackApp.Build().Run() (тесты, другой хост) UpdateManager не создаётся — считаем, что программа не установлена.
    // Обновление не должно мешать открытию окна, поэтому любая ошибка здесь лишь отключает его.
    private static readonly Lazy<UpdateManager?> manager = new(() =>
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("BISTUDENT_UPDATE_SOURCE");
            IUpdateSource source = string.IsNullOrWhiteSpace(path)
                ? new GithubSource("https://github.com/logreus-cloud/BiStudent-public", accessToken: null, prerelease: false)
                : new SimpleFileSource(new DirectoryInfo(path));
            return new UpdateManager(source);
        }
        catch (Exception ex)
        {
            InitError = ex.ToString();
            return null;
        }
    });
    // Несколько окон после повторного входа не должны одновременно скачивать один пакет.
    private static readonly SemaphoreSlim gate = new(1, 1);
    private static UpdateInfo? downloaded;

    public static bool IsInstalled => manager.Value?.IsInstalled == true;
    public static string? CurrentVersion => IsInstalled ? manager.Value!.CurrentVersion?.ToString() : null;
    public static string? LastError { get; private set; }
    public static string? InitError { get; private set; }

    public static async Task<string?> CheckAndDownloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LastError = null;
                if (manager.Value is not { IsInstalled: true } updater) return null;
                if (downloaded is { } existing) return existing.TargetFullRelease.Version.ToString();
                var info = await updater.CheckForUpdatesAsync().ConfigureAwait(false);
                if (info is null) return null;
                await updater.DownloadUpdatesAsync(info, cancelToken: cancellationToken).ConfigureAwait(false);
                downloaded = info;
                return info.TargetFullRelease.Version.ToString();
            }
            finally { gate.Release(); }
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            return null;
        }
    }

    public static void ApplyAndRestart()
    {
        if (downloaded is { } info) manager.Value?.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    public static void ApplyOnExit()
    {
        if (downloaded is { } info)
            manager.Value?.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
    }
}
