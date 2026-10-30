using System.Globalization;
using Velopack;

namespace BiStudent;

internal static class Program
{
    private static readonly string[] PageKeys = ["today", "camera", "schedule", "journal", "groups", "report", "admin"];

    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CrashGuard.Install();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--update-self-test")) return UpdateSelfTest();
        var screenshots = Array.IndexOf(args, "--screenshots");
        if (screenshots >= 0 && args.Length >= screenshots + 4)
            return Screenshots(args[screenshots + 1], args[screenshots + 2], args[screenshots + 3]);

        // Встроенный сервер живёт вместе с клиентом, поэтому второй экземпляр не запускаем.
        using var instance = new Mutex(true, @"Local\BiStudent.Client", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("BiStudent уже открыт.", "BiStudent", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        LocalServer.Start();
        while (true)
        {
            var settings = ClientSettings.Load();
            if (LocalServer.IsBundled && !ClientSettings.SameServer(settings.ServerUrl, LocalServer.Url))
            {
                settings.ServerUrl = LocalServer.Url;
                settings.Save();
            }
            ApiClient? resumed = null;
            string? loginError = null;
            if (settings.RememberMe && settings.LoadRefreshToken() is { } token)
            {
                try
                {
                    if (LocalServer.IsBundled && !Task.Run(() => LocalServer.WaitUntilReadyAsync(TimeSpan.FromSeconds(60))).GetAwaiter().GetResult())
                        throw new InvalidOperationException("Не удалось запустить сервер BiStudent. Перезапустите программу.");
                    resumed = new ApiClient(settings);
                    resumed.RefreshTokenChanged += value => { settings.StoreRefreshToken(value); settings.Save(); };
                    if (!Task.Run(() => resumed.TryResumeAsync(token)).GetAwaiter().GetResult())
                    {
                        resumed.Dispose();
                        resumed = null;
                        settings.StoreRefreshToken(null);
                        settings.Save();
                    }
                }
                catch (Exception ex)
                {
                    resumed?.Dispose();
                    resumed = null;
                    loginError = ex.Message;
                }
            }
            ApiClient api;
            if (resumed is not null) api = resumed;
            else
            {
                using var login = new LoginForm(loginError);
                if (login.ShowDialog() != DialogResult.OK || login.Api is null) return 0;
                api = login.Api;
            }
            using (api)
            {
                if (api.Teacher?.MustChangePassword == true)
                {
                    using var change = new ChangePasswordDialog(api, forced: true);
                    if (change.ShowDialog() != DialogResult.OK)
                    {
                        api.EndSession();
                        var saved = ClientSettings.Load();
                        saved.StoreRefreshToken(null);
                        saved.Save();
                        continue;
                    }
                }
                using var main = new MainForm(api);
                Application.Run(main);
                if (!main.SignedOut)
                {
                    try { AppUpdater.ApplyOnExit(); }
                    catch { }
                    return 0;
                }
            }
        }
    }

    private static string Diagnostics()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "diagnostics");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static int UpdateSelfTest()
    {
        try
        {
            var report = Path.Combine(Diagnostics(), "update.txt");
            if (!AppUpdater.IsInstalled && AppUpdater.InitError is { } initError)
            {
                File.WriteAllText(report, initError);
                return 1;
            }
            if (!AppUpdater.IsInstalled)
            {
                File.WriteAllText(report, "Программа не установлена через Velopack.");
                return 3;
            }
            var version = AppUpdater.CheckAndDownloadAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (AppUpdater.LastError is { } error)
            {
                File.WriteAllText(report, error);
                return 1;
            }
            if (version is null)
            {
                File.WriteAllText(report, "Обновлений нет.");
                return 2;
            }
            AppUpdater.ApplyOnExit();
            File.WriteAllText(report, $"Обновление {version} скачано и поставлено в очередь.");
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "diagnostics", "update.txt"), ex.ToString()); }
            catch { }
            return 1;
        }
    }

    private static int SelfTest()
    {
        try
        {
            var directory = Diagnostics();
            using (var engine = new FaceEngine())
            using (var frame = new OpenCvSharp.Mat(new OpenCvSharp.Size(640, 480), OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black))
            {
                if (engine.Scan(frame).Count != 0) throw new InvalidOperationException("На пустом кадре найдено лицо.");
            }
            _ = CameraDevices.List();
            using (var form = new LoginForm())
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-2000, -2000);
                form.Show();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(directory, "login.png"));
                form.Close();
            }
            File.WriteAllText(Path.Combine(directory, "result.txt"), "OK");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static int Screenshots(string server, string login, string password)
    {
        var directory = Diagnostics();
        var report = Path.Combine(directory, "screenshots.txt");
        var settings = ClientSettings.Load();
        settings.ServerUrl = server;
        using var api = new ApiClient(settings);
        var code = 0;
        var saved = new List<string>();
        // Входим до создания окна: боковая панель (роль, статус облака) строится как в обычном запуске после входа.
        try { Task.Run(async () => { await api.LoginAsync(login, password); await api.LoadMeAsync(); }).GetAwaiter().GetResult(); }
        catch (Exception ex) { File.WriteAllText(report, ex.ToString()); return 1; }
        using var form = new MainForm(api) { AutoOpenFirstPage = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Size = new Size(1440, 900) };
        form.Shown += async (_, _) =>
        {
            try
            {
                foreach (var key in PageKeys.Where(key => key != "admin" || api.Teacher?.IsAdmin == true))
                {
                    await form.ShowPageAsync(key);
                    if (form.LastPageError is { } error) throw new InvalidOperationException($"Страница {key}: {error}");
                    await Task.Delay(700);
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    var file = Path.Combine(directory, $"page-{key}.png");
                    bitmap.Save(file);
                    saved.Add(file);
                }
                File.WriteAllLines(report, saved);
            }
            catch (Exception ex)
            {
                code = 1;
                File.WriteAllText(report, ex.ToString());
            }
            finally { form.Close(); }
        };
        Application.Run(form);
        return code;
    }
}
