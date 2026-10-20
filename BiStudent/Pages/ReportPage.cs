using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class ReportPage : Page
{
    private readonly Panel scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background };
    private readonly Panel content = new() { BackColor = Theme.Background };
    private readonly Banner banner = new();
    private readonly Label period = Ui.Label("", 9, Theme.Violet, FontStyle.Bold);
    private readonly Label heading = Ui.Label("Что автоматизация даёт педсоставу", 22, Theme.Text, FontStyle.Bold);
    private readonly Label description = Ui.Label(
        "BiStudent снимает с преподавателя перекличку, сведение журнала и контроль пропусков.\nНиже — фактические данные одного преподавателя и расчёт для всего колледжа.",
        10.5f, Theme.Text);
    private readonly KpiTile saved = new("Сэкономлено преподавателем", 30) { Accent = true };
    private readonly KpiTile camera = new("Отмечено камерой", 30);
    private readonly KpiTile exports = new("Выгрузок журнала", 30);
    private readonly KpiTile attendance = new("Средняя посещаемость", 30);
    private readonly TableLayoutPanel kpis = new() { ColumnCount = 4, RowCount = 1, BackColor = Theme.Background };
    private readonly CardPanel comparison = new("Было → стало");
    private readonly CardPanel trendCard = new("Посещаемость по дням");
    private readonly CardPanel groupsCard = new("По группам", "посещаемость и зона риска");
    private readonly CardPanel scaleCard = new("Масштаб колледжа");
    private readonly Label manualRollCall = Ui.Label("", 10, Theme.Text, FontStyle.Bold);
    private readonly Label manualReport = Ui.Label("", 10, Theme.Text, FontStyle.Bold);
    private readonly Label rollCallDetail = Detail("");
    private readonly Label formula = Ui.Label("", 8.5f, Theme.Muted);
    private readonly Label scaleText = Ui.Label("", 10, Theme.Text);
    private readonly Label scaleFormula = Ui.Label("", 8.5f, Theme.Muted);
    private readonly NumericUpDown teachers = new()
    {
        Minimum = 1, Maximum = 1000, Value = 40, Width = 76, Height = 30,
        BackColor = Theme.SurfaceHover, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
        Font = Theme.Font(11, FontStyle.Bold), TextAlign = HorizontalAlignment.Center
    };
    private readonly KpiTile perTeacher = new("На одного преподавателя", 22);
    private readonly KpiTile allTeachers = new("На весь педсостав", 22);
    private readonly KpiTile workDays = new("Рабочих дней по 8 часов", 22);
    private readonly KpiTile schoolYear = new("За учебный год", 22);
    private readonly TrendChart trend = new() { Dock = DockStyle.Fill };
    private readonly GroupChart groups = new() { Dock = DockStyle.Fill };
    private readonly ModernButton saveButton = new() { Text = "Сохранить PNG", Width = 164 };
    private readonly ModernButton printButton = new() { Text = "Печать…", Width = 126 };
    private ReportDto? report;
    private bool loading;
    private bool exporting;
    private int chartHeight = 340;

    public ReportPage(ApiClient api) : base(api, "Отчёт", "Посещаемость, зона риска и эффект от внедрения за 30 дней")
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Body.Controls.Add(scroll);
        scroll.Controls.Add(content);

        banner.Height = 216;
        period.Dock = DockStyle.None;
        period.SetBounds(26, 20, 560, 28);
        heading.Dock = DockStyle.None;
        heading.SetBounds(26, 74, 790, 48);
        description.Dock = DockStyle.None;
        description.SetBounds(28, 130, 830, 70);
        saveButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        printButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        saveButton.Top = printButton.Top = 25;
        banner.Controls.Add(period);
        banner.Controls.Add(heading);
        banner.Controls.Add(description);
        banner.Controls.Add(saveButton);
        banner.Controls.Add(printButton);
        saveButton.BringToFront();
        printButton.BringToFront();
        content.Controls.Add(banner);

        for (var i = 0; i < 4; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var tiles = new[] { saved, camera, exports, attendance };
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i].Dock = DockStyle.Fill;
            tiles[i].Margin = new Padding(0, 0, i == 3 ? 0 : 12, 0);
            kpis.Controls.Add(tiles[i], i, 0);
        }
        content.Controls.Add(kpis);

        var before = ComparisonColumn("БЕЗ СИСТЕМЫ", Theme.Mix(Theme.Surface, Theme.Red, .13f),
            [
                (manualRollCall, rollCallDetail),
                (manualReport, Detail("Переписать отметки в таблицу и посчитать проценты")),
                (Ui.Label("Расписание переписывается каждую неделю", 10, Theme.Text, FontStyle.Bold), Detail("Изменения приходится отслеживать отдельно")),
                (Ui.Label("Пропуски ищут по бумажному журналу", 10, Theme.Text, FontStyle.Bold), Detail("Проблему замечают поздно"))
            ]);
        var after = ComparisonColumn("С BISTUDENT", Theme.Mix(Theme.Surface, Theme.Green, .12f),
            [
                (Ui.Label("Без устной переклички", 10, Theme.Text, FontStyle.Bold), Detail("Камера отмечает присутствие во время занятия")),
                (Ui.Label("Журнал готов в любой момент", 10, Theme.Text, FontStyle.Bold), Detail("Excel и CSV одной кнопкой")),
                (Ui.Label("Шаблон на семестр", 10, Theme.Text, FontStyle.Bold), Detail("Чётность, разовые занятия и отмены")),
                (Ui.Label("Автоматическая «зона риска»", 10, Theme.Text, FontStyle.Bold), Detail("Ниже 70% или три пропуска подряд"))
            ]);
        var compareColumns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Theme.Surface };
        compareColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        compareColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        before.Dock = after.Dock = DockStyle.Fill;
        before.Margin = new Padding(0, 0, 8, 0);
        after.Margin = new Padding(8, 0, 0, 0);
        compareColumns.Controls.Add(before, 0, 0);
        compareColumns.Controls.Add(after, 1, 0);
        var compareFooter = new Panel { Dock = DockStyle.Bottom, Height = 29, BackColor = Theme.Surface };
        formula.Dock = DockStyle.Fill;
        compareFooter.Controls.Add(formula);
        comparison.Body.Controls.Add(compareColumns);
        comparison.Body.Controls.Add(compareFooter);
        content.Controls.Add(comparison);

        trendCard.Body.Controls.Add(trend);
        groupsCard.Body.Controls.Add(groups);
        content.Controls.Add(trendCard);
        content.Controls.Add(groupsCard);

        var scaleIntro = new TableLayoutPanel { Dock = DockStyle.Top, Height = 44, ColumnCount = 3, BackColor = Theme.Surface };
        scaleIntro.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scaleIntro.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        scaleIntro.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var intro = Ui.Label("Если перейдут", 10, Theme.Text);
        intro.AutoSize = true;
        intro.Margin = new Padding(0, 0, 10, 0);
        scaleText.AutoEllipsis = true;
        scaleIntro.Controls.Add(intro, 0, 0);
        scaleIntro.Controls.Add(teachers, 1, 0);
        scaleIntro.Controls.Add(scaleText, 2, 0);
        var scaleKpis = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, BackColor = Theme.Surface };
        for (var i = 0; i < 4; i++) scaleKpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        var scaleTiles = new[] { perTeacher, allTeachers, workDays, schoolYear };
        for (var i = 0; i < scaleTiles.Length; i++)
        {
            scaleTiles[i].Dock = DockStyle.Fill;
            scaleTiles[i].Margin = new Padding(0, 0, i == 3 ? 0 : 10, 0);
            scaleKpis.Controls.Add(scaleTiles[i], i, 0);
        }
        var scaleFooter = new Panel { Dock = DockStyle.Bottom, Height = 35, BackColor = Theme.Surface };
        scaleFormula.Dock = DockStyle.Fill;
        scaleFooter.Controls.Add(scaleFormula);
        scaleCard.Body.Controls.Add(scaleKpis);
        scaleCard.Body.Controls.Add(scaleFooter);
        scaleCard.Body.Controls.Add(scaleIntro);
        content.Controls.Add(scaleCard);

        teachers.ValueChanged += (_, _) => UpdateScale();
        scroll.Resize += (_, _) => LayoutContent();
        saveButton.Click += (_, _) => Export(SavePng);
        printButton.Click += (_, _) => Export(Print);
        LayoutContent();
    }

    private static Label Detail(string text) => Ui.Label(text, 8.5f, Theme.Muted);

    private static SmoothPanel ComparisonColumn(string title, Color back, (Label Heading, Label Detail)[] items)
    {
        var panel = new SmoothPanel { BackColor = back, Padding = new Padding(16, 9, 16, 9) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, BackColor = Color.Transparent };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        layout.Controls.Add(Ui.Label(title, 9, title == "БЕЗ СИСТЕМЫ" ? Theme.Red : Theme.Green, FontStyle.Bold), 0, 0);
        for (var i = 0; i < items.Length; i++)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent };
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            items[i].Heading.AutoEllipsis = true;
            row.Controls.Add(items[i].Heading, 0, 0);
            row.Controls.Add(items[i].Detail, 0, 1);
            layout.Controls.Add(row, 0, i + 1);
        }
        panel.Controls.Add(layout);
        return panel;
    }

    private void LayoutContent()
    {
        var width = Math.Max(1, scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        var y = 0;
        banner.SetBounds(0, y, width, 216);
        printButton.Left = width - printButton.Width - 22;
        saveButton.Left = printButton.Left - saveButton.Width - 10;
        period.Width = Math.Max(1, saveButton.Left - period.Left - 16);
        heading.Width = Math.Max(1, width - heading.Left - 28);
        description.Width = Math.Max(1, width - description.Left - 28);
        y += 234;
        kpis.SetBounds(0, y, width, 150);
        y += 168;
        comparison.SetBounds(0, y, width, 342);
        y += 360;
        var leftWidth = (width - 16) / 2;
        trendCard.SetBounds(0, y, leftWidth, chartHeight);
        groupsCard.SetBounds(leftWidth + 16, y, width - leftWidth - 16, chartHeight);
        y += chartHeight + 18;
        scaleCard.SetBounds(0, y, width, 270);
        content.Size = new Size(width, y + 294);
    }

    public override async Task LoadAsync()
    {
        if (loading) return;
        loading = true;
        UseWaitCursor = true;
        Subtitle = "Загрузка отчёта…";
        try
        {
            var data = await Api.ReportAsync();
            if (IsDisposed) return;
            report = data;
            var today = DateOnly.FromDateTime(DateTime.Today);
            period.Text = $"ОТЧЁТ ЗА 30 ДНЕЙ  ·  {UiFormat.Date(today.AddDays(-29))} — {UiFormat.Date(today)}";
            saved.Set($"{UiFormat.Hours(data.Dashboard.SavedMinutes30Days)} ч", "за месяц", Theme.Text);
            camera.Set(data.Dashboard.CameraMarks30Days.ToString("N0"), "камерой за 30 дней", Theme.Green);
            exports.Set(data.ReportsExported30Days.ToString("N0"), "в Excel одной кнопкой", Theme.Violet);
            attendance.Set(data.Dashboard.Trend.Count == 0 ? "—" : UiFormat.Percent(data.Dashboard.Attendance30Days),
                data.Dashboard.Trend.Count == 0 ? "посещаемость ещё не отмечали" : $"{data.Dashboard.AtRisk.Count} в зоне риска",
                data.Dashboard.Trend.Count == 0 ? Theme.Muted : UiFormat.PercentColor(data.Dashboard.Attendance30Days));
            var rollCall = data.AverageGroupSize * data.SecondsPerManualMark / 60d;
            manualRollCall.Text = $"Устная перекличка ≈ {rollCall:0.#} мин на занятие";
            rollCallDetail.Text = $"{data.AverageGroupSize} студентов × {data.SecondsPerManualMark} сек на фамилию и отметку";
            manualReport.Text = $"Сведение журнала ≈ {data.MinutesPerManualReport} мин на группу";
            formula.Text = $"Факт за месяц: {data.Dashboard.SavingsFormula}";
            trend.Points = data.Dashboard.Trend;
            groups.Groups = data.Dashboard.Groups;
            groups.Risk = data.Dashboard.AtRisk;
            chartHeight = Math.Max(340, 135 + data.Dashboard.Groups.Count * 31 + Math.Min(5, data.Dashboard.AtRisk.Count) * 25);
            scaleText.Text = $"преподавателей с нагрузкой как у этого ({data.LessonsPerWeek} занятий в неделю, {data.GroupCount} групп):";
            UpdateScale();
            LayoutContent();
            Subtitle = "Посещаемость, зона риска и эффект от внедрения за 30 дней";
        }
        catch (ApiException ex) { Ui.Toast(this, ex.Message, true); Subtitle = "Не удалось загрузить отчёт"; }
        finally
        {
            loading = false;
            if (!IsDisposed) UseWaitCursor = false;
        }
    }

    private void UpdateScale()
    {
        if (report is null) return;
        var rollCall = report.AverageGroupSize * report.SecondsPerManualMark / 60d;
        var minutes = report.LessonsPerWeek * 4.3 * rollCall + report.GroupCount * report.MinutesPerManualReport;
        var hours = minutes / 60;
        var total = hours * (double)teachers.Value;
        perTeacher.Set($"{hours:0.#} ч", "в месяц", Theme.AccentHover);
        allTeachers.Set($"{total:0.#} ч", "в месяц", Theme.Green);
        workDays.Set($"{total / 8:0.#}", "дней в месяц", Theme.Yellow);
        schoolYear.Set($"{total * 9:0.#} ч", "за 9 месяцев", Theme.Violet);
        scaleFormula.Text = $"Расчёт: {report.LessonsPerWeek} занятий × 4,3 недели × {rollCall:0.#} мин + {report.GroupCount} групп × {report.MinutesPerManualReport} мин. Один рабочий день — 8 часов.";
    }

    private void Export(Action action)
    {
        if (exporting || loading || report is null) return;
        exporting = true;
        saveButton.Enabled = printButton.Enabled = false;
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            Ui.Toast(this, $"Не удалось подготовить отчёт: {ex.Message}", true);
        }
        finally
        {
            exporting = false;
            if (!IsDisposed) saveButton.Enabled = printButton.Enabled = true;
        }
    }

    private Bitmap RenderReport()
    {
        var bitmap = new Bitmap(content.Width, content.Height);
        try
        {
            content.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void SavePng()
    {
        using var dialog = new SaveFileDialog { Title = "Сохранить отчёт", Filter = "Изображение PNG (*.png)|*.png", FileName = $"BiStudent-отчёт-{DateTime.Today:yyyy-MM-dd}.png" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        using var image = RenderReport();
        image.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png);
        Ui.Toast(this, "Отчёт сохранён в PNG");
    }

    private void Print()
    {
        using var image = RenderReport();
        using var document = new PrintDocument();
        document.DefaultPageSettings.Landscape = true;
        document.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169);
        document.DefaultPageSettings.Margins = new Margins(50, 50, 50, 50);
        document.PrintPage += (_, e) =>
        {
            if (e.Graphics is null) return;
            e.Graphics.Clear(Color.White);
            var area = e.MarginBounds;
            var ratio = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
            var width = (int)Math.Round(image.Width * ratio);
            var height = (int)Math.Round(image.Height * ratio);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(image, new Rectangle(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
            e.HasMorePages = false;
        };
        using var preview = new PrintPreviewDialog { Document = document, Width = 1100, Height = 780 };
        preview.ShowDialog(FindForm());
    }

    private sealed class Banner : SmoothPanel
    {
        public Banner() { Padding = Padding.Empty; Radius = 18; }

        protected override void PaintSurface(Graphics g, GraphicsPath path)
        {
            using var gradient = new LinearGradientBrush(ClientRectangle, Color.FromArgb(25, 45, 83), Color.FromArgb(31, 42, 76), 0f);
            g.FillPath(gradient, path);
            var state = g.Save();
            g.SetClip(path);
            using var glowPath = new GraphicsPath();
            glowPath.AddEllipse(Width - 360, -170, 500, 420);
            using var glow = new PathGradientBrush(glowPath)
            {
                CenterColor = Color.FromArgb(100, Theme.Violet),
                SurroundColors = [Color.FromArgb(0, Theme.Violet)]
            };
            g.FillPath(glow, glowPath);
            g.Restore(state);
        }
    }

    private sealed class TrendChart : Control
    {
        private List<TrendPointDto> points = [];
        public List<TrendPointDto> Points { get => points; set { points = value; Invalidate(); } }

        public TrendChart()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var plot = new Rectangle(45, 14, Math.Max(1, Width - 64), Math.Max(1, Height - 62));
            for (var i = 0; i <= 2; i++)
            {
                var y = plot.Top + i * plot.Height / 2;
                using var line = new Pen(Theme.Line);
                g.DrawLine(line, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, $"{100 - i * 50}%", Theme.Font(8), new Rectangle(0, y - 9, 40, 20), Theme.Muted, TextFormatFlags.Right);
            }
            if (points.Count == 0)
            {
                TextRenderer.DrawText(g, "Пока нет данных о посещаемости", Theme.Font(10), plot, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            var linePoints = points.Select((point, i) => new PointF(
                plot.Left + (points.Count == 1 ? plot.Width / 2f : i * plot.Width / (float)(points.Count - 1)),
                plot.Bottom - (float)Math.Clamp(point.Percent, 0, 100) * plot.Height / 100)).ToArray();
            using var area = new GraphicsPath();
            area.AddLine(linePoints[0].X, plot.Bottom, linePoints[0].X, linePoints[0].Y);
            if (linePoints.Length > 1) area.AddLines(linePoints);
            area.AddLine(linePoints[^1].X, linePoints[^1].Y, linePoints[^1].X, plot.Bottom);
            area.CloseFigure();
            using (var fill = new LinearGradientBrush(plot, Color.FromArgb(100, Theme.Accent), Color.FromArgb(5, Theme.Accent), 90f))
                g.FillPath(fill, area);
            using (var pen = new Pen(Theme.AccentHover, 2.5f))
                if (linePoints.Length > 1) g.DrawLines(pen, linePoints);
            using (var dot = new SolidBrush(Theme.AccentHover))
                foreach (var point in linePoints) g.FillEllipse(dot, point.X - 3, point.Y - 3, 6, 6);
            var step = Math.Max(1, (int)Math.Ceiling(points.Count / 6d));
            for (var i = 0; i < points.Count; i += step)
                TextRenderer.DrawText(g, points[i].Date.ToString("dd.MM"), Theme.Font(8),
                    new Rectangle((int)linePoints[i].X - 23, plot.Bottom + 11, 46, 20), Theme.Muted, TextFormatFlags.HorizontalCenter);
        }
    }

    private sealed class GroupChart : Control
    {
        private List<GroupAttendanceDto> groupData = [];
        private List<AtRiskStudentDto> riskData = [];
        public List<GroupAttendanceDto> Groups { get => groupData; set { groupData = value; Invalidate(); } }
        public List<AtRiskStudentDto> Risk { get => riskData; set { riskData = value; Invalidate(); } }

        public GroupChart()
        {
            DoubleBuffered = true;
            BackColor = Theme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var y = 8;
            if (groupData.Count == 0)
            {
                TextRenderer.DrawText(g, "Данных по группам пока нет", Theme.Font(9), new Rectangle(0, y, Width, 28), Theme.Muted);
                y += 32;
            }
            foreach (var group in groupData)
            {
                TextRenderer.DrawText(g, group.GroupName, Theme.Font(9, FontStyle.Bold), new Rectangle(0, y, 115, 22),
                    Theme.Text, TextFormatFlags.EndEllipsis);
                var track = new Rectangle(122, y + 5, Math.Max(1, Width - 185), 12);
                Ui.FillRounded(g, track, 6, Theme.SurfaceAlt);
                var filled = (int)Math.Round(track.Width * Math.Clamp(group.Percent, 0, 100) / 100);
                if (filled > 0)
                {
                    using var path = ModernButton.Rounded(new Rectangle(track.X, track.Y, filled, track.Height), 6);
                    using var brush = new LinearGradientBrush(track, Theme.Accent, Theme.Green, 0f);
                    g.FillPath(brush, path);
                }
                TextRenderer.DrawText(g, UiFormat.Percent(group.Percent), Theme.Font(9, FontStyle.Bold),
                    new Rectangle(Width - 57, y, 55, 22), UiFormat.PercentColor(group.Percent), TextFormatFlags.Right);
                y += 31;
            }
            y += 13;
            TextRenderer.DrawText(g, "ЗОНА РИСКА", Theme.Font(9, FontStyle.Bold), new Rectangle(0, y, Width, 25), Theme.Red);
            y += 28;
            if (riskData.Count == 0)
                TextRenderer.DrawText(g, "Студентов в зоне риска нет", Theme.Font(9), new Rectangle(0, y, Width, 24), Theme.Muted);
            foreach (var student in riskData.Take(5))
            {
                TextRenderer.DrawText(g, $"{student.FullName} · {student.GroupName}", Theme.Font(9),
                    new Rectangle(0, y, Math.Max(1, Width - 65), 23), Theme.Text, TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, UiFormat.Percent(student.Percent), Theme.Font(9, FontStyle.Bold),
                    new Rectangle(Width - 60, y, 58, 23), Theme.Red, TextFormatFlags.Right);
                y += 25;
            }
        }
    }
}
