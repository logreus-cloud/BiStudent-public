using BiStudent.Contracts;

namespace BiStudent;

internal sealed class TodayPage : Page
{
    private readonly KpiTile attendance = new("Посещаемость за 7 дней");
    private readonly KpiTile automatic = new("Отмечено камерой за неделю");
    private readonly KpiTile score = new("Средний балл за 30 дней") { Accent = true };
    private readonly KpiTile risk = new("В зоне риска");
    private readonly CardPanel nowCard = new("Сейчас");
    private readonly CardPanel lessonsCard = new("Занятия сегодня", "двойной клик — открыть занятие");
    private readonly CardPanel riskCard = new("Зона риска", "по данным за 30 дней");
    private readonly Panel pending = new() { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Surface, Visible = false };
    private readonly Label pendingText = Ui.Label("", 11, Theme.Yellow, FontStyle.Bold);
    private readonly ListView lessons = DarkList();
    private readonly FlowLayoutPanel riskList = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 60000 };
    private List<LessonDto> today = [];
    private bool loading;
    private bool left;

    public TodayPage(ApiClient api) : base(api, "Сегодня", UiFormat.LongDate(DateOnly.FromDateTime(DateTime.Now)))
    {
        var kpis = new TableLayoutPanel { Dock = DockStyle.Top, Height = 124, ColumnCount = 4, BackColor = Theme.Background, Padding = new Padding(0, 0, 0, 16) };
        for (var i = 0; i < 4; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        foreach (var tile in new[] { attendance, automatic, score, risk }) { tile.Dock = DockStyle.Fill; kpis.Controls.Add(tile); }
        risk.Margin = Padding.Empty;

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Background };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52)); middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        nowCard.Dock = DockStyle.Fill; nowCard.Margin = new Padding(0, 0, 14, 0);
        lessonsCard.Dock = DockStyle.Fill; lessonsCard.Margin = Padding.Empty;
        lessons.Columns.Add("Время", 80); lessons.Columns.Add("Занятие", 300); lessons.Columns.Add("Статус", 110);
        lessons.Resize += (_, _) => lessons.Columns[1].Width = Math.Max(1, lessons.ClientSize.Width - lessons.Columns[0].Width - lessons.Columns[2].Width - SystemInformation.VerticalScrollBarWidth - 4);
        lessons.DoubleClick += async (_, _) =>
        {
            if (lessons.SelectedItems.Count == 1 && lessons.SelectedItems[0].Tag is LessonDto lesson && Shell is { } shell) await shell.OpenLessonAsync(lesson);
        };
        lessonsCard.Body.Controls.Add(lessons);
        middle.Controls.Add(nowCard, 0, 0); middle.Controls.Add(lessonsCard, 1, 0);

        riskCard.Dock = DockStyle.Bottom; riskCard.Height = 210; riskCard.Margin = Padding.Empty;
        Resize += (_, _) => riskCard.Height = Height < 760 ? 186 : 210;
        riskCard.Body.Controls.Add(riskList);
        var spacer = new Panel { Dock = DockStyle.Bottom, Height = 16, BackColor = Theme.Background };

        var check = new ModernButton { Text = "Проверить", Width = 140, Dock = DockStyle.Right };
        check.Click += async (_, _) =>
        {
            using var dialog = new FaceSubmissionsDialog(Api);
            dialog.ShowDialog(FindForm());
            if (dialog.Changed && !IsDisposed)
            {
                try { await LoadAsync(); }
                catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
            }
        };
        pending.Padding = new Padding(18, 10, 18, 10);
        pendingText.Dock = DockStyle.Fill;
        pending.Controls.Add(pendingText);
        pending.Controls.Add(check);
        Body.Controls.Add(middle); Body.Controls.Add(spacer); Body.Controls.Add(riskCard);
        Body.Controls.Add(pending); Body.Controls.Add(kpis);
        refresh.Tick += async (_, _) =>
        {
            try { await LoadAsync(); }
            catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        };
    }

    private static ListView DarkList()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.None,
            BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(10.5f),
            MultiSelect = false, OwnerDraw = true
        };
        list.SmallImageList = new ImageList { ImageSize = new Size(1, 44) };
        list.DrawItem += (_, e) => e.DrawDefault = false;
        list.DrawSubItem += (_, e) =>
        {
            if (e.Item is null || e.SubItem is null) return;
            var selected = e.Item.Selected;
            using (var back = new SolidBrush(selected ? Theme.SurfaceHover : Theme.Surface)) e.Graphics.FillRectangle(back, e.Bounds);
            var color = e.ColumnIndex == 2 && e.SubItem.Tag is Color c ? c : e.ColumnIndex == 0 ? Theme.Muted : Theme.Text;
            var font = e.ColumnIndex == 1 ? Theme.Font(10.5f, FontStyle.Bold) : Theme.Font(10);
            var lines = e.SubItem.Text.Split('\n');
            if (lines.Length == 2)
            {
                TextRenderer.DrawText(e.Graphics, lines[0], font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 3, e.Bounds.Width - 8, 20), color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(e.Graphics, lines[1], Theme.Font(9), new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 22, e.Bounds.Width - 8, 18), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, Rectangle.Inflate(e.Bounds, -6, 0), color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var line = new Pen(Theme.Line);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        };
        return list;
    }

    private static string Greeting(DateTime now) => now.Hour switch { < 5 => "Доброй ночи", < 12 => "Доброе утро", < 18 => "Добрый день", _ => "Добрый вечер" };

    private static string ShortName(string fullName)
    {
        var clean = System.Text.RegularExpressions.Regex.Replace(fullName, @"\s*\(.*?\)", "").Trim();
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? $"{parts[1]} {parts[2]}" : clean;
    }

    public override async Task LoadAsync()
    {
        if (loading) return;
        loading = true;
        try
        {
            var now = DateTime.Now;
            var name = ShortName(Api.Teacher?.FullName ?? "");
            Title = string.IsNullOrEmpty(name) ? Greeting(now) : $"{Greeting(now)}, {name}";
            Subtitle = UiFormat.LongDate(DateOnly.FromDateTime(now));
            var data = await Api.DashboardAsync();
            if (Gone) return;
            today = data.Today;
            var percents = new Dictionary<int, double>();
            foreach (var lesson in today.Where(l => !l.Cancelled && l.EndsAt <= now))
            {
                var rows = await Api.AttendanceAsync(lesson.Slot.Id, lesson.Date);
                if (Gone) return;
                percents[lesson.Slot.Id] = rows.Count == 0 ? 0 : 100d * rows.Count(r => r.Status == AttendanceStatus.Present) / rows.Count;
            }
            List<AttendanceRowDto>? currentRows = null;
            if (data.Current is { } live)
            {
                currentRows = await Api.AttendanceAsync(live.Slot.Id, live.Date);
                if (Gone) return;
            }
            var has7Days = data.Trend.Any(x => x.Date >= DateOnly.FromDateTime(now).AddDays(-6));
            attendance.Set(has7Days ? UiFormat.Percent(data.Attendance7Days) : "—",
                data.Trend.Count == 0 ? "посещаемость ещё не отмечали" : $"за 30 дней — {UiFormat.Percent(data.Attendance30Days)}",
                has7Days ? UiFormat.PercentColor(data.Attendance7Days) : Theme.Muted);
            automatic.Set(data.CameraMarks7Days.ToString(), $"за месяц — {data.CameraMarks30Days}");
            score.Set(data.AverageScore30Days is { } average ? average.ToString("0.0") : "—", "по 100-балльной шкале");
            risk.Set(data.AtRisk.Count.ToString(), "студентов требуют внимания", data.AtRisk.Count > 0 ? Theme.Red : Theme.Green);
            pending.Visible = data.PendingFaceSubmissions > 0;
            pendingText.Text = $"Заявки на регистрацию лица: {data.PendingFaceSubmissions}";
            BuildNow(data, now, currentRows);
            BuildLessons(now, percents);
            BuildRisk(data.AtRisk, data.Trend.Count > 0);
            if (!left) refresh.Start();
        }
        finally { loading = false; }
    }

    private bool Gone => left || IsDisposed;

    private void BuildNow(DashboardDto data, DateTime now, List<AttendanceRowDto>? rows)
    {
        foreach (var old in nowCard.Body.Controls.Cast<Control>().ToList()) old.Dispose();
        nowCard.Body.Controls.Clear();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, BackColor = Theme.Surface };
        // Строки по высоте содержимого, свободное место — над кнопками: карточка помещается и в окне 1180×720.
        foreach (var height in new[] { 38, 26, 24, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var lesson = data.Current ?? data.Next;
        if (lesson is null)
        {
            nowCard.Caption = "Сегодня";
            layout.Controls.Add(Ui.Label("Занятий сегодня больше нет", 18, Theme.Text, FontStyle.Bold), 0, 0);
            layout.Controls.Add(Ui.Label("Отметки прошедших занятий доступны в журнале.", 10, Theme.Muted), 0, 1);
            var journal = new ModernButton { Text = "Открыть журнал", Width = 180 };
            journal.Click += async (_, _) => { if (Shell is { } shell) await shell.ShowPageAsync("journal"); };
            var row = Ui.Row(); row.Controls.Add(journal); layout.Controls.Add(row, 0, 5);
            nowCard.Body.Controls.Add(layout);
            return;
        }
        var live = data.Current is not null;
        nowCard.Caption = live ? "● Идёт сейчас" : "Следующее занятие";
        var subject = Ui.Label(lesson.Slot.Subject, 18, Theme.Text, FontStyle.Bold); subject.AutoEllipsis = true;
        layout.Controls.Add(subject, 0, 0);
        layout.Controls.Add(Ui.Label($"{lesson.Slot.GroupName} · {UiFormat.Range(lesson.Slot)}{(lesson.Slot.Room is { } room ? $" · ауд. {room}" : "")}", 10.5f, Theme.Muted), 0, 1);
        if (live)
        {
            var progress = new ProgressLine { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), Value = (now - lesson.StartsAt).TotalMinutes / (lesson.EndsAt - lesson.StartsAt).TotalMinutes * 100 };
            layout.Controls.Add(progress, 0, 2);
            rows ??= [];
            var marked = rows.Count(r => r.Status is not null);
            layout.Controls.Add(Ui.Label($"{marked} из {rows.Count} отмечено", 13, Theme.Text, FontStyle.Bold), 0, 3);
        }
        else
        {
            layout.Controls.Add(Ui.Label($"Начнётся через {UiFormat.Duration((lesson.StartsAt - now).TotalMinutes)}", 10.5f, Theme.AccentHover), 0, 2);
            layout.Controls.Add(Ui.Label("Камеру можно запустить заранее — отметка начнётся со звонком.", 9.5f, Theme.Muted), 0, 3);
        }
        var actions = Ui.Row();
        var camera = new ModernButton { Text = live ? "Начать отметку" : "Подготовить камеру", Icon = "◉", Primary = true };
        camera.Click += async (_, _) => { if (Shell is { } shell) await shell.OpenCamera(lesson); };
        var manual = new ModernButton { Text = "Открыть занятие" };
        camera.Width = TextRenderer.MeasureText(camera.Text, camera.Font).Width + 64;
        manual.Width = TextRenderer.MeasureText(manual.Text, manual.Font).Width + 40;
        manual.Click += async (_, _) => { if (Shell is { } shell) await shell.OpenLessonAsync(lesson); };
        actions.Controls.Add(camera); actions.Controls.Add(manual);
        layout.Controls.Add(actions, 0, 5);
        nowCard.Body.Controls.Add(layout);
    }

    private void BuildLessons(DateTime now, Dictionary<int, double> percents)
    {
        lessons.BeginUpdate();
        try
        {
        lessons.Items.Clear();
        foreach (var lesson in today)
        {
            string state;
            Color color;
            if (lesson.Cancelled) { state = "отменена"; color = Theme.Red; }
            else if (lesson.EndsAt <= now)
            {
                var percent = percents.GetValueOrDefault(lesson.Slot.Id);
                state = UiFormat.Percent(percent); color = UiFormat.PercentColor(percent);
            }
            else if (lesson.StartsAt <= now) { state = "● идёт"; color = Theme.Green; }
            else { state = "впереди"; color = Theme.Muted; }
            var item = new ListViewItem(UiFormat.Time(lesson.Slot.Start)) { Tag = lesson };
            item.SubItems.Add($"{lesson.Slot.Subject}\n{lesson.Slot.GroupName}{(lesson.Slot.Room is { } room ? $" · ауд. {room}" : "")}");
            item.SubItems.Add(new ListViewItem.ListViewSubItem { Text = state, Tag = color });
            lessons.Items.Add(item);
        }
        if (today.Count == 0) lessons.Items.Add(new ListViewItem(["", "По расписанию сегодня занятий нет", ""]));
        }
        finally { lessons.EndUpdate(); }
    }

    private void BuildRisk(List<AtRiskStudentDto> students, bool hasData)
    {
        riskList.SuspendLayout();
        foreach (Control control in riskList.Controls.Cast<Control>().ToList()) control.Dispose();
        riskList.Controls.Clear();
        if (students.Count == 0)
        {
            var ok = Ui.Label(hasData ? "Все студенты посещают занятия стабильно." : "Посещаемость за 30 дней ещё не отмечали.", 10, Theme.Muted);
            ok.Dock = DockStyle.None; ok.AutoSize = true;
            riskList.Controls.Add(ok);
        }
        foreach (var student in students.Take(8))
        {
            var item = new SmoothPanel { Width = 330, Height = 64, BackColor = Theme.SurfaceHover, Radius = 12, Padding = new Padding(14, 6, 14, 6), Margin = new Padding(0, 0, 12, 12) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            var name = Ui.Label(student.FullName, 10, Theme.Text, FontStyle.Bold); name.AutoEllipsis = true;
            var reason = Ui.Label($"{student.GroupName} · {student.Reason}", 8.5f, Theme.Muted); reason.AutoEllipsis = true;
            var percent = Ui.Label(UiFormat.Percent(student.Percent), 13, UiFormat.PercentColor(student.Percent), FontStyle.Bold);
            percent.TextAlign = ContentAlignment.MiddleRight;
            grid.Controls.Add(name, 0, 0); grid.Controls.Add(reason, 0, 1); grid.Controls.Add(percent, 1, 0); grid.SetRowSpan(percent, 2);
            item.Controls.Add(grid);
            riskList.Controls.Add(item);
        }
        riskList.ResumeLayout();
    }

    public override Task OnLeaveAsync()
    {
        left = true;
        refresh.Stop();
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) refresh.Dispose();
        base.Dispose(disposing);
    }
}
