using System.Net;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class MainForm : DarkForm
{
    private static readonly (string Key, string Text, string Icon)[] Pages =
    [
        ("today", "Сегодня", "◷"), ("camera", "Отметка", "◉"), ("schedule", "Расписание", "▦"),
        ("journal", "Журнал", "☰"), ("groups", "Группы", "◍"), ("report", "Отчёт", "▲"),
        ("admin", "Администрирование", "⚙")
    ];

    private readonly ApiClient api;
    private readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Dictionary<string, ModernButton> nav = [];
    private readonly ContextMenuStrip userMenu = new() { BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, Font = Theme.Font(10) };
    private readonly Label updateStatus = Ui.Label("", 8.5f, Theme.Accent);
    private readonly ToolTip updateToolTip = new();
    private readonly Label syncStatus = Ui.Label("", 8.5f, Theme.Muted);
    private readonly ToolTip syncToolTip = new();
    private readonly System.Windows.Forms.Timer syncTimer = new() { Interval = 30000 };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly CancellationTokenSource updateCancellation = new();
    private Page? current;
    private TaskCompletionSource? switchCompletion;
    private Label? userName;
    private Label? userAvatar;
    private string? updateVersion;
    private UpdateCheckState? updateCheckState;
    private bool switching;
    private bool closingAfterLeave;
    private bool leaving;
    private bool syncBusy;
    private bool updateBusy;
    private bool applyingUpdate;
    private bool closeAfterUpdate;

    public MainForm(ApiClient api)
    {
        this.api = api;
        Text = AppUpdater.CurrentVersion is { } version ? $"BiStudent {version} · Кабинет преподавателя" : "BiStudent · Кабинет преподавателя";
        Size = new Size(1440, 900);
        MinimumSize = new Size(1180, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BuildShell();
        api.Unauthorized += OnUnauthorized;
        syncTimer.Tick += async (_, _) => await UpdateSyncAsync();
        updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
        Shown += async (_, _) =>
        {
            syncTimer.Start(); _ = UpdateSyncAsync();
            updateTimer.Start(); _ = CheckForUpdateAsync();
            if (AutoOpenFirstPage && current is null) await ShowPageAsync("today");
        };
    }

    public bool SignedOut { get; private set; }
    public bool AutoOpenFirstPage { get; set; } = true;
    public LessonDto? PendingCameraLesson { get; set; }
    internal Page? CurrentPage => current;
    internal string? LastPageError { get; private set; }
    internal ApiClient Api => api;

    private void BuildShell()
    {
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Left, Width = 250, BackColor = Theme.Sidebar, RowCount = 5, Padding = new Padding(16, 22, 16, 16) };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var brand = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48)); brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        var mark = new Label { Text = "✓", Size = new Size(38, 38), BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.Font(16, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 4, 0, 0) };
        brand.Controls.Add(mark, 0, 0); brand.SetRowSpan(mark, 2);
        brand.Controls.Add(Ui.Label("BiStudent", 15, Theme.Text, FontStyle.Bold), 1, 0);
        brand.Controls.Add(Ui.Label("Посещаемость и успеваемость", 8.5f, Theme.Muted), 1, 1);
        sidebar.Controls.Add(brand, 0, 0);

        var menu = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Sidebar };
        foreach (var (key, text, icon) in Pages)
        {
            if (key == "admin" && api.Teacher?.IsAdmin != true) continue;
            var button = new ModernButton { Text = text, Icon = icon, Width = 218, Height = 44, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, key == "report" ? 14 : 0, 0, 6), Dashed = key == "report", BackColor = Theme.Sidebar };
            button.Click += async (_, _) => await ShowPageAsync(key);
            nav[key] = button;
            menu.Controls.Add(button);
        }
        sidebar.Controls.Add(menu, 0, 1);

        updateStatus.AutoSize = true; updateStatus.MinimumSize = new Size(0, 28);
        updateStatus.AutoEllipsis = true; updateStatus.Cursor = Cursors.Hand;
        updateStatus.Text = "⟳ Проверка обновлений…"; updateStatus.ForeColor = Theme.Muted;
        updateStatus.Click += async (_, _) =>
        {
            if (updateBusy || applyingUpdate || leaving || IsDisposed) return;
            if (updateCheckState == UpdateCheckState.Available) InstallUpdate();
            else if (updateCheckState is UpdateCheckState.UpToDate or UpdateCheckState.Failed)
                await CheckForUpdateAsync(manual: true);
        };
        sidebar.Controls.Add(updateStatus, 0, 2);

        syncStatus.AutoEllipsis = true; syncStatus.Cursor = Cursors.Hand; syncStatus.Visible = false;
        syncStatus.Click += async (_, _) => await UpdateSyncAsync(run: true);
        sidebar.Controls.Add(syncStatus, 0, 3);

        var user = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Theme.Sidebar, Margin = Padding.Empty };
        user.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); user.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); user.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        user.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); user.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        var name = api.Teacher?.FullName ?? "Преподаватель";
        var avatar = new Label { Text = Initials(name), Size = new Size(36, 36), BackColor = Theme.SurfaceAlt, ForeColor = Theme.Text, Font = Theme.Font(10, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 12, 0, 0) };
        user.Controls.Add(avatar, 0, 0); user.SetRowSpan(avatar, 2);
        userAvatar = avatar;
        var nameLabel = Ui.Label(name, 9.5f, Theme.Text, FontStyle.Bold); nameLabel.AutoEllipsis = true; nameLabel.TextAlign = ContentAlignment.BottomLeft;
        user.Controls.Add(nameLabel, 1, 0);
        userName = nameLabel;
        var roleLabel = Ui.Label(api.Teacher?.IsAdmin == true ? "Администратор" : "Преподаватель", 8.5f, Theme.Muted); roleLabel.TextAlign = ContentAlignment.TopLeft;
        user.Controls.Add(roleLabel, 1, 1);
        var exit = new ModernButton { Text = "↩", Width = 38, Height = 38, Font = Theme.Font(12, FontStyle.Bold), BackColor = Theme.Sidebar, Margin = new Padding(0, 12, 0, 0) };
        exit.Click += (_, _) => SignOut();
        new ToolTip().SetToolTip(exit, "Выйти");
        user.Controls.Add(exit, 2, 0); user.SetRowSpan(exit, 2);
        userMenu.Items.Add("Сменить пароль…", null, (_, _) =>
        {
            using var dialog = new ChangePasswordDialog(api, forced: false);
            if (dialog.ShowDialog(this) == DialogResult.OK) Ui.Toast(this, "Пароль изменён");
        });
        userMenu.Items.Add("Выйти", null, (_, _) => SignOut());
        void OpenUserMenu(object? sender, EventArgs e)
        {
            if (sender is Control control) userMenu.Show(control, new Point(0, control.Height));
        }
        avatar.Click += OpenUserMenu; nameLabel.Click += OpenUserMenu; roleLabel.Click += OpenUserMenu;
        user.Click += OpenUserMenu;
        sidebar.Controls.Add(user, 0, 4);

        Controls.Add(host);
        Controls.Add(sidebar);
    }

    private async void InstallUpdate()
    {
        if (updateVersion is null || applyingUpdate || leaving || IsDisposed) return;
        applyingUpdate = true;
        updateStatus.Enabled = false;
        var pageLeft = false;
        try
        {
            if (MessageBox.Show(this, $"Программа закроется и сразу откроется снова. Установить обновление {updateVersion} сейчас?",
                "BiStudent", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (switchCompletion is { } completion) await completion.Task;
            if (leaving || IsDisposed) return;
            leaving = true;
            pageLeft = true;
            await PrepareToCloseAsync();
            AppUpdater.ApplyAndRestart();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка обновления", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            leaving = false;
            applyingUpdate = false;
            if (!IsDisposed) updateStatus.Enabled = true;
            if (closeAfterUpdate && !IsDisposed) { closeAfterUpdate = false; Close(); }
            // Обновление не встало, а страница уже остановлена (например, камера) — открываем её заново.
            else if (pageLeft && !IsDisposed && nav.FirstOrDefault(x => x.Value.Selected).Key is { } key)
            {
                try { await ShowPageAsync(key); }
                catch (Exception ex) { Ui.Toast(this, ex.Message, true); }
            }
        }
    }

    private async Task CheckForUpdateAsync(bool manual = false)
    {
        if (updateBusy || updateCancellation.IsCancellationRequested || IsDisposed || applyingUpdate) return;
        updateBusy = true;
        updateStatus.Text = "⟳ Проверка обновлений…";
        updateStatus.ForeColor = Theme.Muted;
        updateToolTip.SetToolTip(updateStatus, "");
        try
        {
            var cancellationToken = updateCancellation.Token;
            // Progress создаётся в потоке окна и сам переносит отчёты в него.
            var progress = new Progress<(string Version, int Percent)>(report =>
            {
                if (IsDisposed || updateCancellation.IsCancellationRequested) return;
                updateStatus.Text = $"⬇ Загрузка обновления {report.Version} — {report.Percent}%";
                updateStatus.ForeColor = Theme.Muted;
            });
            var result = await Task.Run(() => AppUpdater.CheckAsync(cancellationToken, progress));
            if (updateCancellation.IsCancellationRequested || IsDisposed) return;
            updateCheckState = result.State;
            updateVersion = result.State == UpdateCheckState.Available ? result.Version : null;
            switch (result.State)
            {
                case UpdateCheckState.NotInstalled:
                    updateStatus.Text = "Автообновление — только в установленной версии";
                    updateStatus.ForeColor = Theme.Muted;
                    updateToolTip.SetToolTip(updateStatus, "Установите программу через BiStudent.Desktop-win-Setup.exe из релиза на GitHub, тогда она будет обновляться сама.");
                    updateTimer.Stop();
                    break;
                case UpdateCheckState.UpToDate:
                    updateStatus.Text = $"✓ Версия {AppUpdater.CurrentVersion} · проверить обновления";
                    updateStatus.ForeColor = Theme.Muted;
                    RestartUpdateTimer(6 * 60 * 60 * 1000);
                    break;
                case UpdateCheckState.Available:
                    updateStatus.Text = $"⬆ Обновление {result.Version} — установить";
                    updateStatus.ForeColor = Theme.Accent;
                    updateTimer.Stop();
                    break;
                case UpdateCheckState.Failed:
                    updateStatus.Text = "⚠ Обновления не проверены · повторить";
                    updateStatus.ForeColor = Theme.Yellow;
                    updateToolTip.SetToolTip(updateStatus, result.Error);
                    RestartUpdateTimer(30 * 60 * 1000);
                    break;
            }
            if (manual && result.State == UpdateCheckState.UpToDate)
                Ui.Toast(this, $"Установлена последняя версия {AppUpdater.CurrentVersion}");
            else if (manual && result.State == UpdateCheckState.Failed)
                Ui.Toast(this, result.Error ?? "Не удалось проверить обновления.", true);
        }
        finally { updateBusy = false; }
    }

    // Присваивание того же Interval не сбрасывает отсчёт — после каждой проверки начинаем его заново.
    private void RestartUpdateTimer(int interval)
    {
        updateTimer.Stop();
        updateTimer.Interval = interval;
        updateTimer.Start();
    }

    private async Task UpdateSyncAsync(bool run = false)
    {
        if (syncBusy || leaving || IsDisposed) return;
        syncBusy = true;
        if (run) { syncStatus.Text = "☁ Синхронизация…"; syncToolTip.SetToolTip(syncStatus, ""); }
        try
        {
            var status = run ? await api.SyncNowAsync() : await api.SyncStatusAsync();
            if (leaving || IsDisposed) return;
            if (!status.Enabled || status.State == "Disabled")
            {
                syncStatus.Visible = false;
                syncToolTip.SetToolTip(syncStatus, "");
                return;
            }
            var queue = status.Pending > 0 ? $" · в очереди {status.Pending}" : "";
            syncStatus.Text = status.State switch
            {
                "Ok" => "☁ Синхронизировано" + queue,
                "Offline" => "☁ Нет связи — работа офлайн" + queue,
                "NotSignedIn" => "☁ Облако не подключено — повторю, когда появится связь",
                "Error" => "☁ Ошибка синхронизации",
                _ => ""
            };
            syncStatus.Visible = syncStatus.Text.Length > 0;
            var details = status.LastSuccessAt is { } last
                ? $"Последняя синхронизация: {last.ToLocalTime():g}"
                : "Синхронизации ещё не было";
            if (!string.IsNullOrWhiteSpace(status.Error)) details += $"\nОшибка: {status.Error}";
            syncToolTip.SetToolTip(syncStatus, details);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            if (leaving || IsDisposed) return;
            syncStatus.Visible = false;
            syncToolTip.SetToolTip(syncStatus, "");
        }
        catch (Exception ex)
        {
            if (leaving || IsDisposed) return;
            syncStatus.Text = "☁ Нет связи с сервером";
            syncStatus.Visible = true;
            syncToolTip.SetToolTip(syncStatus, ex.Message);
        }
        finally { syncBusy = false; }
    }

    private static string Initials(string name) =>
        string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => char.IsLetter(p[0])).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    private Page CreatePage(string key) => key switch
    {
        "today" => new TodayPage(api),
        "schedule" => new SchedulePage(api),
        "journal" => new JournalPage(api),
        "groups" => new GroupsPage(api),
        "camera" => new CameraPage(api),
        "report" => new ReportPage(api),
        "admin" => new AdminPage(api),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
    };

    internal async Task ShowPageAsync(string key)
    {
        if (switching || leaving || applyingUpdate || IsDisposed) return;
        switching = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        switchCompletion = completion;
        if (api.Teacher is { } teacher && userName is not null && userAvatar is not null) { userName.Text = teacher.FullName; userAvatar.Text = Initials(teacher.FullName); }
        try
        {
            if (current is not null)
            {
                await current.OnLeaveAsync();
                // Пока страница останавливалась (например, камера), окно могли начать закрывать.
                if (leaving || IsDisposed) return;
                host.Controls.Remove(current);
                current.Dispose();
                current = null;
            }
            foreach (var (pageKey, button) in nav) button.Selected = pageKey == key;
            var page = CreatePage(key);
            current = page;
            host.Controls.Add(page);
            LastPageError = null;
            try { await page.LoadAsync(); }
            catch (ApiException ex) { LastPageError = ex.Message; Ui.Toast(this, ex.Message, true); }
        }
        finally
        {
            switching = false;
            switchCompletion = null;
            completion.SetResult();
        }
    }

    internal Task OpenCamera(LessonDto lesson)
    {
        PendingCameraLesson = lesson;
        return ShowPageAsync("camera");
    }

    internal async Task OpenLessonAsync(LessonDto lesson)
    {
        var page = current;
        using var dialog = new LessonDialog(api, lesson);
        dialog.ShowDialog(this);
        if (page is null || !ReferenceEquals(current, page) || page.IsDisposed || IsDisposed) return;
        try { await page.LoadAsync(); }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
    }

    private void SignOut()
    {
        api.EndSession();
        var settings = ClientSettings.Load();
        settings.StoreRefreshToken(null);
        settings.Save();
        SignedOut = true;
        Close();
    }

    private void OnUnauthorized()
    {
        if (IsDisposed || SignedOut) return;
        BeginInvoke(() =>
        {
            if (SignedOut) return;
            SignedOut = true;
            Ui.Toast(this, "Сессия истекла — войдите снова", true);
            Close();
        });
    }

    private Task PrepareToCloseAsync() => current?.OnLeaveAsync() ?? Task.CompletedTask;

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        // Выход или истёкшая сессия во время установки обновления: закроемся, если обновление отменят или оно не встанет.
        if (applyingUpdate) { e.Cancel = true; closeAfterUpdate = true; return; }
        syncTimer.Stop();
        updateTimer.Stop();
        updateCancellation.Cancel();
        if (!closingAfterLeave && current is not null)
        {
            e.Cancel = true;
            if (leaving) return;
            leaving = true;
            try { await PrepareToCloseAsync(); }
            finally
            {
                closingAfterLeave = true;
                Close();
            }
            return;
        }
        api.Unauthorized -= OnUnauthorized;
        syncTimer.Dispose();
        updateTimer.Dispose();
        updateCancellation.Dispose();
        syncToolTip.Dispose();
        updateToolTip.Dispose();
        userMenu.Dispose();
        base.OnFormClosing(e);
    }
}
