using BiStudent.Contracts;
using OpenCvSharp;

namespace BiStudent;

internal sealed class CameraPage : Page
{
    private sealed record LessonChoice(LessonDto Lesson)
    {
        public override string ToString() => $"{UiFormat.Time(Lesson.Slot.Start)} · {Lesson.Slot.Subject} · {Lesson.Slot.GroupName}";
    }

    private readonly ModernComboBox lessons = new() { Dock = DockStyle.Fill };
    private readonly ModernComboBox cameras = new() { Dock = DockStyle.Fill };
    private readonly ModernButton start = new() { Text = "Начать отметку", Primary = true, Width = 170 };
    private readonly ModernButton stop = new() { Text = "Остановить", Width = 140 };
    private readonly ModernButton finish = new() { Text = "Завершить занятие", Danger = true, Width = 200 };
    private readonly PictureBox preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Sidebar };
    private readonly Label placeholder = Ui.Label("Камера выключена. Выберите занятие и нажмите «Начать отметку».", 10, Theme.Muted);
    private readonly Label banner = Ui.Label("", 9.5f, Theme.Red);
    private readonly Label marked = Ui.Label("0 из 0 отмечено", 15, Theme.Text, FontStyle.Bold);
    private readonly Label automatic = Ui.Label("0 камерой", 12, Theme.Green, FontStyle.Bold);
    private readonly Label saved = Ui.Label("", 10, Theme.Muted);
    private readonly Label indicator = Ui.Label("Камера выключена", 9.5f, Theme.Muted);
    private readonly AttendanceList attendance = new() { Dock = DockStyle.Fill, EmptyText = "Выберите занятие, чтобы увидеть список" };
    private readonly CardPanel students = new("Студенты группы");
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 2000 };
    private readonly SemaphoreSlim attendanceWrite = new(1, 1);
    private LessonDto? selectedLesson;
    private DateOnly? loadedDate;
    private AutomationSettingsDto? automation;
    private RecognitionResultDto? lastResult;
    private DateTime resultAt;
    private CancellationTokenSource? cameraCts;
    private Task? cameraTask;
    private Task? recognitionTask;
    private Task? stoppingTask;
    private long recognitionPauseUntil;
    private int pendingFrame;
    private int recognitionBusy;
    private bool recognitionOk;
    private bool running;
    private bool loading;
    private bool leftPage;
    private bool refreshing;
    private bool refreshAgain;
    private bool manualBusy;
    private bool finishing;

    public CameraPage(ApiClient api) : base(api, "Отметка посещаемости", "Камера распознаёт студентов группы и отмечает присутствие во время занятия")
    {
        var left = new CardPanel("Камера", "выберите занятие и устройство") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, BackColor = Theme.Surface };
        foreach (var height in new[] { 48, 48, 50, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 57));
        layout.Controls.Add(Field("Занятие", lessons), 0, 0);
        layout.Controls.Add(Field("Камера", cameras), 0, 1);
        var buttons = Ui.Row();
        buttons.Controls.Add(start); buttons.Controls.Add(stop);
        layout.Controls.Add(buttons, 0, 2);
        banner.TextAlign = ContentAlignment.MiddleLeft;
        banner.AutoEllipsis = true;
        layout.Controls.Add(banner, 0, 3);
        var video = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar };
        placeholder.TextAlign = ContentAlignment.MiddleCenter;
        video.Controls.Add(preview); preview.Controls.Add(placeholder);
        placeholder.BringToFront();
        layout.Controls.Add(video, 0, 4);
        var statistics = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 2, BackColor = Theme.Surface };
        statistics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        statistics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        statistics.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        statistics.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        statistics.Controls.Add(marked, 0, 0);
        statistics.Controls.Add(automatic, 1, 0);
        statistics.Controls.Add(saved, 0, 1);
        statistics.SetColumnSpan(saved, 2);
        layout.Controls.Add(statistics, 0, 5);
        var foot = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Theme.Surface };
        foot.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        foot.Controls.Add(indicator, 0, 0);
        var warning = Ui.Label("Зелёная — студент группы · синяя — уже отмечен · серая — другая группа · красная — не распознан", 8, Theme.Muted);
        warning.AutoEllipsis = true;
        foot.Controls.Add(warning, 0, 1);
        layout.Controls.Add(foot, 0, 6);
        left.Body.Controls.Add(layout);

        students.Dock = DockStyle.Fill;
        students.Margin = Padding.Empty;
        var listArea = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Theme.Surface };
        listArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        finish.Dock = DockStyle.Fill;
        finish.Height = 42;
        listArea.Controls.Add(attendance, 0, 0);
        listArea.Controls.Add(finish, 0, 1);
        students.Body.Controls.Add(listArea);
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Background };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
        columns.Resize += (_, _) => columns.ColumnStyles[1].Width = Math.Max(470, columns.ClientSize.Width * .43f);
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(students, 1, 0);
        Body.Controls.Add(columns);

        lessons.SelectedIndexChanged += async (_, _) =>
        {
            if (!loading && !running) await SelectLessonAsync();
        };
        cameras.SelectedIndexChanged += (_, _) =>
        {
            if (loading || cameras.SelectedItem is not CameraDevice device) return;
            try { CameraDevices.Save(ClientSettings.Load(), device); }
            catch (IOException ex) { Ui.Toast(this, ex.Message, true); }
        };
        start.Click += (_, _) => StartCamera();
        stop.Click += async (_, _) => await StopAsync();
        finish.Click += async (_, _) => await FinishAsync();
        attendance.StatusChosen += MarkAsync;
        attendance.GradeChosen += GradeAsync;
        refresh.Tick += async (_, _) =>
        {
            if (loading) return;
            if (!running && loadedDate != DateOnly.FromDateTime(DateTime.Now)) await RunAsync(LoadAsync);
            else await RefreshAttendanceAsync();
        };
        SetControls();
    }

    private static Control Field(string caption, Control input)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Surface };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(Ui.Label(caption, 9.5f, Theme.Muted), 0, 0);
        row.Controls.Add(input, 1, 0);
        return row;
    }

    public override async Task LoadAsync()
    {
        if (loading || IsDisposed) return;
        loading = true;
        indicator.Text = "Загрузка расписания и настроек…";
        start.Enabled = false;
        try
        {
            var pending = Shell?.PendingCameraLesson;
            if (Shell is { } shell) shell.PendingCameraLesson = null;
            var date = DateOnly.FromDateTime(DateTime.Now);
            var dayTask = Api.DayAsync(date);
            var automationTask = Api.AutomationAsync();
            var statusTask = Api.RecognitionStatusAsync();
            await Task.WhenAll(dayTask, automationTask, statusTask);
            if (IsDisposed) return;
            automation = await automationTask;
            var status = await statusTask;
            recognitionOk = status.Ok;
            SetBanner(status.Ok ? "" : status.Message);
            var day = (await dayTask).Where(item => !item.Cancelled).OrderBy(item => item.StartsAt).ToList();
            lessons.Items.Clear();
            foreach (var item in day) lessons.Items.Add(new LessonChoice(item));
            var now = DateTime.Now;
            var choice = day.FirstOrDefault(item => pending is not null && item.Slot.Id == pending.Slot.Id && item.Date == pending.Date)
                ?? day.FirstOrDefault(item => item.StartsAt <= now && now < item.EndsAt)
                ?? day.FirstOrDefault(item => item.StartsAt > now)
                ?? day.LastOrDefault();
            if (choice is not null)
                lessons.SelectedIndex = day.FindIndex(item => ReferenceEquals(item, choice));
            selectedLesson = choice;
            attendance.SetRows([]);
            SetStatistics([]);
            students.Caption = choice?.Slot.GroupName ?? "Студенты группы";
            placeholder.Text = choice is null
                ? "Сегодня занятий нет"
                : $"Камера выключена. Выберите занятие и нажмите «Начать отметку».\nОтметка ставится после {automation.FramesInRow} кадров подряд.";
            placeholder.Visible = true;
            var devices = CameraDevices.List();
            cameras.Items.Clear();
            foreach (var device in devices) cameras.Items.Add(device);
            if (devices.Count > 0)
            {
                var index = CameraDevices.ResolveIndex(ClientSettings.Load(), devices);
                cameras.SelectedIndex = Math.Max(0, devices.FindIndex(device => device.Index == index));
            }
            indicator.Text = choice is null ? "Сегодня занятий нет" : devices.Count == 0 ? "Камера не найдена" : "Камера выключена";
            await RefreshAttendanceAsync();
            loadedDate = date;
            if (!IsDisposed && !leftPage) refresh.Start();
        }
        finally
        {
            loading = false;
            if (!IsDisposed) SetControls();
        }
    }

    private async Task SelectLessonAsync()
    {
        selectedLesson = (lessons.SelectedItem as LessonChoice)?.Lesson;
        students.Caption = selectedLesson?.Slot.GroupName ?? "Студенты группы";
        attendance.SetRows([]);
        SetStatistics([]);
        SetControls();
        await RefreshAttendanceAsync();
    }

    private async Task RefreshAttendanceAsync(CancellationToken token = default)
    {
        if (refreshing) { refreshAgain = true; return; }
        if (selectedLesson is null) return;
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                var lesson = selectedLesson;
                if (lesson is null) break;
                attendance.EmptyText = "Загрузка списка…";
                try
                {
                    var rows = await Api.AttendanceAsync(lesson.Slot.Id, lesson.Date, token);
                    if (IsDisposed) return;
                    if (ReferenceEquals(lesson, selectedLesson))
                    {
                        attendance.EmptyText = "В группе нет студентов";
                        attendance.SetRows(rows);
                        SetStatistics(rows);
                    }
                    else refreshAgain = true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (ApiException ex)
                {
                    if (!IsDisposed && !token.IsCancellationRequested) Ui.Toast(this, ex.Message, true);
                    return;
                }
            } while (refreshAgain);
        }
        finally { refreshing = false; }
    }

    private void SetStatistics(IReadOnlyList<AttendanceRowDto> rows)
    {
        var count = rows.Count(row => row.Status is not null);
        var cameraCount = rows.Count(row => row.Status is not null && row.Method == MarkMethod.Camera);
        marked.Text = $"{count} из {rows.Count} отмечено";
        automatic.Text = $"{cameraCount} камерой";
        saved.Text = "";
    }

    private async Task MarkAsync(AttendanceRowDto row, AttendanceStatus? status)
    {
        if (manualBusy || finishing || selectedLesson is not { } lesson) return;
        manualBusy = true;
        SetControls();
        try
        {
            await attendanceWrite.WaitAsync();
            try { await Api.MarkAsync(lesson.Slot.Id, lesson.Date, row.StudentId, status); }
            finally { attendanceWrite.Release(); }
            await RefreshAttendanceAsync();
        }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        finally
        {
            manualBusy = false;
            if (!IsDisposed) SetControls();
        }
    }

    private async Task GradeAsync(AttendanceRowDto row)
    {
        if (selectedLesson is not { } lesson) return;
        var column = new JournalColumnDto(JournalColumnKind.Lesson, lesson.Slot.Id, lesson.Date,
            lesson.Slot.Start, lesson.Slot.Subject);
        var cell = new JournalCellDto(row.Status, row.Reason, row.Comment, row.Score);
        using var editor = new GradeEditor(Api, column, cell, row.StudentId, row.FullName, scoreOnly: true, attendance: row);
        var changed = false;
        editor.Saved += () => changed = true;
        editor.ShowDialog(FindForm(), Cursor.Position);
        if (changed) await RefreshAttendanceAsync();
    }

    private async Task FinishAsync()
    {
        if (finishing || selectedLesson is not { } lesson) return;
        if (!Ui.Confirm(FindForm(), "Завершить занятие",
            "Всем студентам без отметки будет поставлено «Н».",
            "Завершить занятие", danger: true)) return;
        finishing = true;
        SetControls();
        try
        {
            await StopAsync();
            await attendanceWrite.WaitAsync();
            try { await Api.FinishAsync(lesson.Slot.Id, lesson.Date); }
            finally { attendanceWrite.Release(); }
            await RefreshAttendanceAsync();
            Ui.Toast(this, "Занятие завершено");
        }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        finally
        {
            finishing = false;
            if (!IsDisposed) SetControls();
        }
    }

    private void StartCamera()
    {
        if (running || stoppingTask is not null || !recognitionOk || selectedLesson is not { } lesson
            || cameras.SelectedItem is not CameraDevice device) return;
        cameraCts = new CancellationTokenSource();
        var token = cameraCts.Token;
        recognitionTask = null;
        lastResult = null;
        Interlocked.Exchange(ref recognitionBusy, 0);
        Interlocked.Exchange(ref recognitionPauseUntil, 0);
        running = true;
        placeholder.Visible = false;
        indicator.Text = "Подключение к камере…";
        SetControls();
        cameraTask = Task.Run(() => CameraLoopAsync(device, lesson, token), token);
    }

    private async Task CameraLoopAsync(CameraDevice device, LessonDto lesson, CancellationToken token)
    {
        VideoCapture? capture = null;
        var nextRetry = DateTime.MinValue;
        var noFrameSince = DateTime.UtcNow;
        var lastRequest = DateTime.MinValue;
        var firstFrame = true;
        string? iriunHint = null;
        var hintCheckedAt = DateTime.MinValue;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                if ((now - hintCheckedAt).TotalSeconds >= 2)
                {
                    hintCheckedAt = now;
                    var hint = CameraDevices.IriunHint(device);
                    if (hint != iriunHint)
                    {
                        iriunHint = hint;
                        var restored = capture is not null && !firstFrame;
                        if (hint is not null) PostUi(() => indicator.Text = hint);
                        else if (restored) PostUi(() => indicator.Text = "● ищу лица");
                    }
                }
                if (capture is null && now >= nextRetry)
                {
                    try
                    {
                        var current = CameraDevices.Find(CameraDevices.List(), device.Id)
                            ?? throw new InvalidOperationException("Выбранная камера не найдена.");
                        capture = CameraDevices.Open(current.Index, 960, 540, allowFallback: true);
                        noFrameSince = DateTime.UtcNow;
                        firstFrame = true;
                    }
                    catch (Exception ex)
                    {
                        capture?.Dispose();
                        capture = null;
                        nextRetry = DateTime.UtcNow.AddSeconds(2);
                        var text = iriunHint ?? $"Камера недоступна: {ex.Message}. Повтор через 2 с…";
                        PostUi(() => indicator.Text = text);
                    }
                }
                if (capture is not null)
                {
                    try
                    {
                        using var frame = new Mat();
                        if (capture.Read(frame) && !frame.Empty())
                        {
                            noFrameSince = DateTime.UtcNow;
                            if (firstFrame)
                            {
                                firstFrame = false;
                                var text = iriunHint ?? "● ищу лица";
                                PostUi(() => indicator.Text = text);
                            }
                            if (Interlocked.CompareExchange(ref pendingFrame, 1, 0) == 0)
                            {
                                Bitmap? bitmap = null;
                                byte[]? jpeg = null;
                                var reserved = false;
                                var posted = false;
                                try
                                {
                                    Cv2.ImEncode(".bmp", frame, out var bytes);
                                    using (var stream = new MemoryStream(bytes))
                                    using (var decoded = new Bitmap(stream)) bitmap = new Bitmap(decoded);
                                    now = DateTime.UtcNow;
                                    if ((now - lastRequest).TotalMilliseconds >= 350
                                        && now.Ticks >= Interlocked.Read(ref recognitionPauseUntil)
                                        && Interlocked.CompareExchange(ref recognitionBusy, 1, 0) == 0)
                                    {
                                        reserved = true;
                                        using var small = new Mat();
                                        Cv2.Resize(frame, small, new OpenCvSharp.Size(640, Math.Max(1, (int)Math.Round(frame.Height * 640d / frame.Width))));
                                        Cv2.ImEncode(".jpg", small, out jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                                        lastRequest = now;
                                    }
                                    var image = bitmap;
                                    if (PostUi(() => FrameReady(image, jpeg, token))) { posted = true; bitmap = null; }
                                }
                                finally
                                {
                                    bitmap?.Dispose();
                                    if (!posted)
                                    {
                                        Interlocked.Exchange(ref pendingFrame, 0);
                                        if (reserved) Interlocked.Exchange(ref recognitionBusy, 0);
                                    }
                                }
                            }
                        }
                        else if ((DateTime.UtcNow - noFrameSince).TotalSeconds > 3)
                        {
                            capture.Dispose();
                            capture = null;
                            nextRetry = DateTime.UtcNow.AddSeconds(2);
                            var text = iriunHint ?? "Нет кадров с камеры — переподключение…";
                            PostUi(() => indicator.Text = text);
                        }
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        capture?.Dispose();
                        capture = null;
                        nextRetry = DateTime.UtcNow.AddSeconds(2);
                        PostUi(() => indicator.Text = $"Ошибка камеры: {ex.Message}. Переподключение…");
                    }
                }
                await Task.Delay(40, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { capture?.Dispose(); }
    }

    private bool PostUi(Action action)
    {
        try { BeginInvoke(action); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void FrameReady(Bitmap bitmap, byte[]? jpeg, CancellationToken token)
    {
        Interlocked.Exchange(ref pendingFrame, 0);
        if (IsDisposed || token.IsCancellationRequested)
        {
            bitmap.Dispose();
            if (jpeg is not null) Interlocked.Exchange(ref recognitionBusy, 0);
            return;
        }
        DrawFaces(bitmap);
        var old = preview.Image;
        preview.Image = bitmap;
        old?.Dispose();
        if (jpeg is not null && selectedLesson is { } lesson)
            recognitionTask = RecognizeFrameAsync(lesson, jpeg, token);
        else if (jpeg is not null) Interlocked.Exchange(ref recognitionBusy, 0);
    }

    private void DrawFaces(Bitmap bitmap)
    {
        if (lastResult is not { } result || (DateTime.UtcNow - resultAt).TotalSeconds > 1.5 || result.FrameWidth <= 0) return;
        var scale = bitmap.Width / (float)result.FrameWidth;
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var fontPixels = Math.Clamp(bitmap.Width / 38, 14, 40);
        var stroke = Math.Max(3, bitmap.Width / 260);
        var accent = Math.Max(3, fontPixels / 5);
        using var font = new Font("Segoe UI", fontPixels, FontStyle.Bold, GraphicsUnit.Pixel);
        foreach (var face in result.Faces)
        {
            var color = face.StudentId is null ? Theme.Red : face.OtherGroup ? Theme.Muted
                : face.Marked || face.AlreadyMarked ? Theme.Accent : Theme.Green;
            var bounds = new Rectangle((int)(face.X * scale), (int)(face.Y * scale),
                Math.Max(1, (int)(face.W * scale)), Math.Max(1, (int)(face.H * scale)));
            using var pen = new Pen(color, stroke);
            graphics.DrawRectangle(pen, bounds);
            var text = face.StudentId is null ? "Не распознан" : face.Name ?? "Студент";
            var size = TextRenderer.MeasureText(graphics, text, font, System.Drawing.Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var pad = Math.Max(6, fontPixels / 3);
            var width = Math.Min(bitmap.Width, size.Width + pad * 2 + accent);
            var height = size.Height + pad;
            var top = bounds.Top - height - stroke >= 0 ? bounds.Top - height - stroke : Math.Min(bitmap.Height - height, bounds.Bottom + stroke);
            var label = new Rectangle(Math.Clamp(bounds.Left, 0, Math.Max(0, bitmap.Width - width)), Math.Max(0, top), width, height);
            Ui.FillRounded(graphics, label, pad, Color.FromArgb(225, Theme.Sidebar));
            Ui.FillRounded(graphics, new Rectangle(label.X, label.Y, accent * 2, label.Height), accent, color);
            TextRenderer.DrawText(graphics, text, font, new Rectangle(label.X + accent + pad, label.Y, label.Width - accent - pad, label.Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    private async Task RecognizeFrameAsync(LessonDto lesson, byte[] jpeg, CancellationToken token)
    {
        try
        {
            var result = await Api.RecognizeAsync(lesson.Slot.Id, lesson.Date, jpeg, token);
            if (IsDisposed || token.IsCancellationRequested) return;
            lastResult = result;
            resultAt = DateTime.UtcNow;
            indicator.Text = result.Faces.Count == 0 ? "● ищу лица" : $"● в кадре лиц: {result.Faces.Count}";
            SetBanner(result.Error ?? "");
            if (!string.IsNullOrWhiteSpace(result.Error)) Interlocked.Exchange(ref recognitionPauseUntil, DateTime.UtcNow.AddSeconds(2).Ticks);
            var newlyMarked = result.Faces.Where(face => face.Marked && face.StudentId is not null)
                .DistinctBy(face => face.StudentId);
            var any = false;
            foreach (var face in newlyMarked)
            {
                any = true;
                attendance.Flash(face.StudentId!.Value);
                Ui.Toast(this, $"{face.Name ?? "Студент"} — отмечен камерой");
            }
            if (any) await RefreshAttendanceAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ApiException ex)
        {
            if (!IsDisposed && !token.IsCancellationRequested)
            {
                indicator.Text = ex.Message;
                Interlocked.Exchange(ref recognitionPauseUntil, DateTime.UtcNow.AddSeconds(2).Ticks);
            }
        }
        finally { Interlocked.Exchange(ref recognitionBusy, 0); }
    }

    private void SetBanner(string message)
    {
        banner.Text = string.IsNullOrWhiteSpace(message) ? "" : $"⚠  {message}";
    }

    private void SetControls()
    {
        lessons.Enabled = !running && !finishing && !manualBusy && lessons.Items.Count > 0;
        cameras.Enabled = !running && cameras.Items.Count > 0;
        start.Enabled = !running && !finishing && !manualBusy && recognitionOk && selectedLesson is not null && cameras.SelectedItem is CameraDevice;
        stop.Enabled = running;
        finish.Enabled = !finishing && !manualBusy && selectedLesson is not null;
        attendance.Enabled = !manualBusy && !finishing;
    }

    private async Task StopAsync()
    {
        if (stoppingTask is not null) { await stoppingTask; return; }
        if (cameraTask is null) return;
        var task = StopCoreAsync();
        stoppingTask = task;
        try { await task; }
        finally { if (ReferenceEquals(stoppingTask, task)) stoppingTask = null; }
    }

    private async Task StopCoreAsync()
    {
        cameraCts?.Cancel();
        var finished = true;
        try
        {
            finished &= await Ui.SettleAsync(cameraTask);
            finished &= await Ui.SettleAsync(recognitionTask);
        }
        finally
        {
            if (finished) cameraCts?.Dispose();
            cameraCts = null;
            cameraTask = null;
            recognitionTask = null;
            running = false;
            lastResult = null;
            if (!IsDisposed)
            {
                var old = preview.Image;
                preview.Image = null;
                old?.Dispose();
                placeholder.Visible = true;
                placeholder.BringToFront();
                indicator.Text = "Камера выключена";
                SetControls();
            }
        }
    }

    public override async Task OnLeaveAsync()
    {
        leftPage = true;
        refresh.Stop();
        await StopAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            cameraCts?.Cancel();
            var image = preview.Image;
            preview.Image = null;
            image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
