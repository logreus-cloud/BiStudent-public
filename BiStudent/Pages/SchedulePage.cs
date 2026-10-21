using System.Text;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class SchedulePage : Page
{
    private DateOnly monday = UiFormat.Monday(DateOnly.FromDateTime(DateTime.Today));
    private readonly Label range = Ui.Label("", 15, Theme.Text, FontStyle.Bold);
    private readonly Label parity = Ui.Label("", 9.5f, Theme.Muted);
    private readonly TableLayoutPanel days = new() { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = Theme.Background };
    private readonly ModernButton previous = new() { Text = "‹", Width = 42 };
    private readonly ModernButton next = new() { Text = "›", Width = 42 };
    private readonly ModernButton today = new() { Text = "Сегодня", Width = 104 };
    private readonly ModernButton import = new() { Text = "Импорт", Width = 120 };
    private readonly ModernButton add = new() { Text = "+ Пара", Primary = true, Width = 112 };
    private List<LessonDto> lessons = [];
    private bool busy;

    public SchedulePage(ApiClient api) : base(api, "Расписание", "Занятия на семестр: еженедельные, по чётным и нечётным неделям, разовые и отменённые")
    {
        Actions.Controls.Add(import); Actions.Controls.Add(add);
        import.Click += async (_, _) => await ExecuteAsync(ImportAsync);
        add.Click += async (_, _) => await OpenSlotAsync(null, monday);
        previous.Click += async (_, _) => await MoveAsync(-7);
        next.Click += async (_, _) => await MoveAsync(7);
        today.Click += async (_, _) => { monday = UiFormat.Monday(DateOnly.FromDateTime(DateTime.Today)); await LoadAsync(); };
        var hint = new AutomationHint("расписание колледжа загружается из Excel («Импорт»), журнал строится по занятиям расписания");
        var navigation = new TableLayoutPanel { Dock = DockStyle.Top, Height = 70, ColumnCount = 5, BackColor = Theme.Background, Padding = new Padding(0, 14, 0, 12) };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 114)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        range.TextAlign = ContentAlignment.MiddleCenter; parity.TextAlign = ContentAlignment.MiddleRight;
        navigation.Controls.Add(previous, 0, 0); navigation.Controls.Add(range, 1, 0); navigation.Controls.Add(next, 2, 0); navigation.Controls.Add(today, 3, 0); navigation.Controls.Add(parity, 4, 0);
        for (var i = 0; i < 6; i++) days.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
        var legend = Ui.Label("● Каждая неделя     ● Нечётная     ● Чётная     ● Разовое занятие     ● Отменена", 9, Theme.Muted);
        legend.Dock = DockStyle.Bottom; legend.Height = 42; legend.TextAlign = ContentAlignment.MiddleCenter;
        Body.Controls.Add(days); Body.Controls.Add(legend); Body.Controls.Add(navigation); Body.Controls.Add(hint);
    }

    public override Task LoadAsync() => ExecuteAsync(RefreshAsync);

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy || IsDisposed) return;
        busy = true; UseWaitCursor = true;
        foreach (var button in new[] { previous, next, today, import, add }) button.Enabled = false;
        try { await action(); }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        finally
        {
            busy = false;
            if (!IsDisposed) { UseWaitCursor = false; foreach (var button in new[] { previous, next, today, import, add }) button.Enabled = true; }
        }
    }

    private async Task MoveAsync(int offset)
    {
        if (busy) return;
        monday = monday.AddDays(offset);
        await LoadAsync();
    }

    private async Task RefreshAsync()
    {
        var weekTask = Api.WeekAsync(monday);
        var parityTask = Api.ParityAsync(monday);
        await Task.WhenAll(weekTask, parityTask);
        if (IsDisposed) return;
        lessons = await weekTask;
        var weekParity = await parityTask;
        range.Text = $"{UiFormat.Date(monday)} — {UiFormat.Date(monday.AddDays(5))}";
        parity.Text = $"{UiFormat.Parity(weekParity)} неделя";
        BuildDays();
    }

    private void BuildDays()
    {
        days.SuspendLayout();
        foreach (Control control in days.Controls.Cast<Control>().ToList()) control.Dispose();
        days.Controls.Clear();
        for (var index = 0; index < 6; index++)
        {
            var date = monday.AddDays(index);
            var column = new SmoothPanel { Dock = DockStyle.Fill, Margin = new Padding(index == 0 ? 0 : 4, 0, index == 5 ? 0 : 4, 0), Padding = new Padding(8), Radius = 12 };
            var header = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Surface };
            var title = Ui.Label($"{UiFormat.DayLong(date.DayOfWeek)}\n{date:dd.MM}", 9.5f, date == DateOnly.FromDateTime(DateTime.Today) ? Theme.AccentHover : Theme.Text, FontStyle.Bold);
            title.TextAlign = ContentAlignment.MiddleCenter;
            header.Controls.Add(title);
            if (date == DateOnly.FromDateTime(DateTime.Today)) header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 2, BackColor = Theme.Accent });
            var list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0) };
            // Горизонтальной прокрутки в колонке дня не бывает: карточки всегда по ширине колонки.
            list.AutoScroll = false; list.HorizontalScroll.Maximum = 0; list.HorizontalScroll.Visible = false; list.AutoScroll = true;
            void SizeCards()
            {
                var overflow = list.Controls.Cast<Control>().Sum(x => x.Height + x.Margin.Vertical) + list.Padding.Vertical > list.ClientSize.Height;
                var width = Math.Max(1, list.ClientSize.Width - (overflow && !list.VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0) - 1);
                foreach (Control item in list.Controls) item.Width = width - item.Margin.Horizontal;
            }
            var entries = lessons.Where(x => x.Date == date).OrderBy(x => x.Slot.Start).ToList();
            if (entries.Count == 0) { var empty = Ui.Label("Нет пар", 9.5f, Theme.Muted); empty.Dock = DockStyle.Top; empty.Height = 40; empty.TextAlign = ContentAlignment.MiddleCenter; list.Controls.Add(empty); }
            foreach (var lesson in entries)
            {
                var card = new LessonCard(lesson);
                card.Click += (_, _) => ShowMenu(card, lesson);
                list.Controls.Add(card);
            }
            list.Resize += (_, _) => SizeCards();
            column.Controls.Add(list); column.Controls.Add(header); days.Controls.Add(column, index, 0);
            SizeCards();
        }
        days.ResumeLayout();
    }

    private void ShowMenu(LessonCard card, LessonDto lesson)
    {
        if (busy) return;
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(10), ShowImageMargin = false };
        var marks = menu.Items.Add("Отметки…");
        marks.Enabled = !lesson.Cancelled;
        marks.Click += async (_, _) => { if (Shell is { } shell) await shell.OpenLessonAsync(lesson); };
        menu.Items.Add("Изменить…").Click += async (_, _) => await OpenSlotAsync(lesson.Slot, lesson.Date);
        menu.Items.Add(lesson.Cancelled ? "Вернуть пару" : "Отменить на эту дату…").Click += async (_, _) =>
        {
            if (lesson.Cancelled) await ExecuteAsync(async () => { await Api.RestoreLessonAsync(lesson.Slot.Id, lesson.Date); await RefreshAsync(); });
            else
            {
                var reason = Ui.Prompt(FindForm(), "Отменить пару", "Причина отмены (можно оставить пустой)");
                if (reason is not null) await ExecuteAsync(async () => { await Api.CancelLessonAsync(lesson.Slot.Id, lesson.Date, reason); await RefreshAsync(); });
            }
        };
        menu.Items.Add("Удалить из расписания…").Click += async (_, _) =>
        {
            if (!Ui.Confirm(FindForm(), "Удалить пару", "Шаблон пары и все её отметки будут удалены. Чтобы убрать одно занятие, лучше отмените его на дату.", "Удалить", true)) return;
            await ExecuteAsync(async () => { await Api.DeleteSlotAsync(lesson.Slot.Id); await RefreshAsync(); });
        };
        // Удалять меню прямо в Closed нельзя: WinForms ещё закрывает его и падает на удалённом объекте.
        menu.Closed += (_, _) => { if (IsHandleCreated) BeginInvoke(menu.Dispose); };
        menu.Show(card, new Point(8, card.Height - 4));
    }

    private async Task OpenSlotAsync(SlotDto? slot, DateOnly date)
    {
        if (busy) return;
        using var dialog = new SlotDialog(Api, slot, date, lessons.Select(x => x.Slot.Subject).Distinct(StringComparer.CurrentCultureIgnoreCase));
        if (dialog.ShowDialog(FindForm()) == DialogResult.OK) await LoadAsync();
    }

    private async Task ImportAsync()
    {
        using var guide = new DialogShell("Импорт расписания", 720, 280);
        var explanation = Ui.Label(
            "Excel — расписание колледжа на семестр (блоки «день · звонок · пара · аудитория · группа»). " +
            "Вы выберете группы, а учётные записи преподавателей создадутся автоматически (нужны права администратора).\n\n" +
            "CSV: День;Начало;Конец;Предмет;Группа;Аудитория;Неделя — одна строка на шаблон пары.", 10, Theme.Text);
        explanation.TextAlign = ContentAlignment.TopLeft;
        guide.Content.Controls.Add(explanation);
        var template = new ModernButton { Text = "Сохранить шаблон…", Width = 180, Height = 40 };
        template.Click += async (_, _) =>
        {
            using var save = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "raspisanie-template.csv" };
            if (save.ShowDialog(guide) != DialogResult.OK) return;
            try
            {
                const string csv = "День;Начало;Конец;Предмет;Группа;Аудитория;Неделя\r\nПн;08:30;09:50;Математика;П-21;101;Каждая\r\nВт;10:00;11:20;Физика;П-21;202;Нечётная\r\n";
                await File.WriteAllTextAsync(save.FileName, csv, new UTF8Encoding(true));
                if (!guide.IsDisposed) Ui.Toast(guide, "Шаблон сохранён");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (!guide.IsDisposed) Ui.Toast(guide, $"Не удалось сохранить шаблон: {ex.Message}", true);
            }
        };
        guide.Buttons.Controls.Add(template);
        guide.AddButton("Отмена", DialogResult.Cancel);
        guide.AddButton("Выбрать CSV…", DialogResult.OK);
        guide.AddButton("Расписание Excel…", DialogResult.Yes, true);
        var choice = guide.ShowDialog(FindForm());
        if (choice == DialogResult.Yes) { await ImportExcelAsync(); return; }
        if (choice != DialogResult.OK) return;
        using var open = new OpenFileDialog { Filter = "CSV (*.csv)|*.csv", CheckFileExists = true };
        if (open.ShowDialog(FindForm()) != DialogResult.OK) return;
        string content;
        try { content = await File.ReadAllTextAsync(open.FileName, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) Ui.Toast(this, $"Не удалось прочитать CSV: {ex.Message}", true);
            return;
        }
        var result = await Api.ImportScheduleAsync(content);
        if (result.Errors.Count > 0) Ui.Message(FindForm(), "Ошибки импорта", string.Join(Environment.NewLine, result.Errors));
        else Ui.Toast(this, $"Импортировано {result.Added} {UiFormat.Plural(result.Added, "занятие", "занятия", "занятий")}");
        await RefreshAsync();
    }

    private async Task ImportExcelAsync()
    {
        using var open = new OpenFileDialog { Title = "Расписание колледжа", Filter = "Excel (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (open.ShowDialog(FindForm()) != DialogResult.OK) return;
        byte[] file;
        try { file = await File.ReadAllBytesAsync(open.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsDisposed) Ui.Toast(this, $"Не удалось прочитать файл: {ex.Message}", true);
            return;
        }
        var preview = await Api.PreviewScheduleXlsxAsync(file);
        if (IsDisposed) return;
        if (preview.Groups.Count == 0) { Ui.Message(FindForm(), "Импорт расписания", "В файле не найдено групп в формате расписания колледжа."); return; }
        using var dialog = new ScheduleImportDialog(Api, file, preview);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK || dialog.Result is not { } result) return;
        ScheduleImportDialog.ShowResult(FindForm(), result);
        await RefreshAsync();
    }

    private sealed class LessonCard : SmoothPanel
    {
        private readonly LessonDto lesson;
        public LessonCard(LessonDto lesson)
        {
            this.lesson = lesson; Height = 94; Padding = Padding.Empty; Margin = new Padding(0, 0, 0, 8); Cursor = Cursors.Hand; Radius = 10;
            BackColor = Theme.SurfaceHover;
        }
        protected override void OnMouseEnter(EventArgs e) { BackColor = Theme.SurfaceAlt; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { BackColor = Theme.SurfaceHover; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var slot = lesson.Slot;
            var color = lesson.Cancelled ? Theme.Red : slot.OneOffDate.HasValue ? Theme.Violet : slot.Parity == WeekParity.Odd ? Theme.Yellow : slot.Parity == WeekParity.Even ? Theme.Green : Theme.Accent;
            using (var brush = new SolidBrush(color)) e.Graphics.FillRectangle(brush, 0, 10, 4, Height - 20);
            var foreground = lesson.Cancelled ? Theme.Mix(Theme.Text, BackColor, .55f) : Theme.Text;
            var font = Theme.Font(9.5f, FontStyle.Bold | (lesson.Cancelled ? FontStyle.Strikeout : FontStyle.Regular));
            TextRenderer.DrawText(e.Graphics, UiFormat.Range(slot), Theme.Font(8.5f, FontStyle.Bold), new Rectangle(13, 8, Width - 20, 20), lesson.Cancelled ? Theme.Red : color, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, slot.Subject, font, new Rectangle(13, 32, Width - 20, 25), foreground, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var details = slot.GroupName + (string.IsNullOrWhiteSpace(slot.Room) ? "" : $" · ауд. {slot.Room}");
            TextRenderer.DrawText(e.Graphics, details, Theme.Font(8.5f), new Rectangle(13, 63, Width - 20, 20), lesson.Cancelled ? Theme.Mix(Theme.Muted, BackColor, .5f) : Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted; base.OnRenderItemText(e); }
        private sealed class DarkColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Theme.Surface;
            public override Color ImageMarginGradientBegin => Theme.Surface;
            public override Color ImageMarginGradientMiddle => Theme.Surface;
            public override Color ImageMarginGradientEnd => Theme.Surface;
            public override Color MenuItemSelected => Theme.SurfaceAlt;
            public override Color MenuItemBorder => Theme.Line;
            public override Color MenuBorder => Theme.Line;
        }
    }
}
