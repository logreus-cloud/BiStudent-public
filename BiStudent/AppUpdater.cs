using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Velopack;
using Velopack.Sources;

namespace BiStudent;

internal enum UpdateCheckState { NotInstalled, UpToDate, Available, Failed }

internal sealed record UpdateCheckResult(UpdateCheckState State, string? Version, string? Error);

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
        var result = await CheckAsync(cancellationToken).ConfigureAwait(false);
        return result.State == UpdateCheckState.Available ? result.Version : null;
    }

    /// progress получает версию и процент загрузки (0–100), пока скачивается обновление.
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken,
        IProgress<(string Version, int Percent)>? progress = null)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LastError = null;
                if (manager.Value is not { IsInstalled: true } updater)
                    return new(UpdateCheckState.NotInstalled, null, null);
                if (downloaded is { } existing)
                    return new(UpdateCheckState.Available, existing.TargetFullRelease.Version.ToString(), null);
                var info = await updater.CheckForUpdatesAsync().ConfigureAwait(false);
                if (info is null) return new(UpdateCheckState.UpToDate, null, null);
                var version = info.TargetFullRelease.Version.ToString();
                progress?.Report((version, 0));
                await updater.DownloadUpdatesAsync(info,
                    percent => progress?.Report((version, Math.Clamp(percent, 0, 100))),
                    cancelToken: cancellationToken).ConfigureAwait(false);
                downloaded = info;
                return new(UpdateCheckState.Available, info.TargetFullRelease.Version.ToString(), null);
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            LastError = ex.ToString();
            return new(UpdateCheckState.Failed, null, "Проверка обновлений отменена.");
        }
        catch (Exception ex)
        {
            LastError = ex.ToString();
            return new(UpdateCheckState.Failed, null, Describe(ex));
        }
    }

    internal static string Describe(Exception ex)
    {
        List<Exception> errors = [];
        for (Exception? current = ex; current is not null; current = current.InnerException)
            errors.Add(current);

        if (errors.Any(error => error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }
            || error.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)))
            return "GitHub временно ограничил запросы с этой сети (лимит 60 в час). Повторите позже.";
        if (errors.Any(error => error is HttpRequestException { StatusCode: null } or SocketException or TaskCanceledException or TimeoutException))
            return "Нет связи с GitHub — проверьте интернет или ограничения сети колледжа.";
        if (errors.FirstOrDefault(error => error is IOException or UnauthorizedAccessException) is { } storageError)
            return $"Не удалось сохранить обновление: {storageError.Message}";
        return $"Не удалось проверить обновления: {ex.Message}";
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
