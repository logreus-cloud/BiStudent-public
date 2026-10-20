using System.Diagnostics;
using BiStudent.Contracts;

namespace BiStudent;

internal sealed class JournalPage : Page
{
    private readonly ModernComboBox groups = new() { Width = 160, DisplayMember = nameof(GroupDto.Name) };
    private readonly ModernComboBox subjects = new() { Width = 190 };
    private readonly DateTimePicker from = Picker();
    private readonly DateTimePicker to = Picker();
    private readonly ModernButton week = new() { Text = "Неделя", Width = 95 };
    private readonly ModernButton month = new() { Text = "Месяц", Width = 95 };
    private readonly ModernButton semester = new() { Text = "Семестр", Width = 105 };
    private readonly ModernButton addAssessment = new() { Text = "Добавить контрольную", Width = 215 };
    private readonly ModernButton fillFinal = new() { Text = "Заполнить итог", Width = 170 };
    private readonly ModernButton excel = new() { Text = "Скачать Excel", Primary = true, Width = 170 };
    private readonly DataGridView grid = Ui.Grid();
    private readonly CardPanel card = new("Журнал", "Клик по ячейке — изменить; двойной клик по заголовку — открыть");
    private readonly Label empty = Ui.Label("За выбранный период занятий и контрольных нет", 12, Theme.Muted);
    private Dictionary<(int Id, DateOnly Date), Task<List<AttendanceRowDto>>> attendance = new();
    private JournalDto? journal;
    private JournalFilter? loadedFilter;
    private bool busy;
    private bool changing;

    public JournalPage(ApiClient api) : base(api, "Журнал", "Посещаемость и успеваемость группы по дисциплине")
    {
        addAssessment.Click += async (_, _) => await AddAssessmentAsync();
        fillFinal.Click += async (_, _) => await FillFinalAsync();
        excel.Click += async (_, _) => await ExportAsync();
        Actions.Controls.Add(addAssessment); Actions.Controls.Add(fillFinal); Actions.Controls.Add(excel);

        var filterCard = new SmoothPanel { Dock = DockStyle.Top, Height = 116, Padding = new Padding(16, 8, 8, 8) };
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Color.Transparent };
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); filters.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var fields = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        fields.Controls.Add(FilterLabel("Группа")); fields.Controls.Add(groups);
        fields.Controls.Add(FilterLabel("Дисциплина")); fields.Controls.Add(subjects);
        fields.Controls.Add(FilterLabel("С")); fields.Controls.Add(from);
        fields.Controls.Add(FilterLabel("По")); fields.Controls.Add(to);
        var periods = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
        periods.Controls.Add(week); periods.Controls.Add(month); periods.Controls.Add(semester);
        filters.Controls.Add(fields, 0, 0); filters.Controls.Add(periods, 0, 1);
        filterCard.Controls.Add(filters);
        groups.SelectedIndexChanged += async (_, _) => { if (!changing) await ExecuteAsync(() => RefreshAsync(true)); };
        subjects.SelectedIndexChanged += async (_, _) => { if (!changing) await ExecuteAsync(() => RefreshAsync(false)); };
        from.ValueChanged += async (_, _) => await DatesChangedAsync(true);
        to.ValueChanged += async (_, _) => await DatesChangedAsync(false);
        week.Click += async (_, _) => await PresetAsync(week, UiFormat.Monday(DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue), UiFormat.Monday(DateOnly.FromDateTime(DateTime.Today)).AddDays(6).ToDateTime(TimeOnly.MinValue));
        month.Click += async (_, _) => await PresetAsync(month, new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month)));
        semester.Click += async (_, _) => await PresetAsync(semester, SemesterStart(DateTime.Today), DateTime.Today);
        changing = true;
        from.Value = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        to.Value = DateTime.Today;
        month.Selected = true;
        changing = false;

        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        grid.ColumnHeadersHeight = 76;
        grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.CellClick += async (_, e) => await EditCellAsync(e.RowIndex, e.ColumnIndex);
        grid.CellPainting += PaintLesson;
        grid.CellMouseEnter += async (_, e) => await LoadTooltipAsync(e.RowIndex, e.ColumnIndex);
        grid.ColumnHeaderMouseDoubleClick += async (_, e) => await OpenColumnAsync(e.ColumnIndex);
        empty.TextAlign = ContentAlignment.MiddleCenter;
        card.Dock = DockStyle.Fill;
        var legend = Ui.Label("П — присутствовал · Н — отсутствовал · Н жёлтым — по уважительной причине · баллы по 100-балльной шкале", 9, Theme.Muted);
        legend.Dock = DockStyle.Bottom; legend.Height = 38; legend.Padding = new Padding(8, 0, 0, 0);
        card.Body.Controls.Add(grid); card.Body.Controls.Add(empty); card.Body.Controls.Add(legend);
        Body.Controls.Add(card);
        Body.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 16, BackColor = Theme.Background });
        Body.Controls.Add(filterCard);
        SetBusy(false);
    }

    private static DateTimePicker Picker() => new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "dd.MM.yyyy", Width = 115,
        Font = Theme.Font(10), CalendarMonthBackground = Theme.Surface, CalendarForeColor = Theme.Text,
        Margin = new Padding(0, 5, 10, 0)
    };

    private static Label FilterLabel(string text) => new()
    {
        Text = text, AutoSize = true, Font = Theme.Font(9), ForeColor = Theme.Muted,
        BackColor = Color.Transparent, Margin = new Padding(0, 12, 7, 0)
    };

    private static DateTime SemesterStart(DateTime today) => today.Month switch
    {
        >= 9 => new DateTime(today.Year, 9, 1),
        >= 2 => new DateTime(today.Year, 2, 1),
        _ => new DateTime(today.Year - 1, 9, 1)
    };

    public override async Task LoadAsync()
    {
        await ExecuteAsync(async () =>
        {
            var selectedId = (groups.SelectedItem as GroupDto)?.Id;
            var items = await Api.GroupsAsync();
            if (IsDisposed) return;
            changing = true;
            groups.Items.Clear();
            foreach (var item in items) groups.Items.Add(item);
            groups.SelectedItem = groups.Items.Cast<GroupDto>().FirstOrDefault(x => x.Id == selectedId) ?? groups.Items.Cast<GroupDto>().FirstOrDefault();
            changing = false;
            await RefreshAsync(true);
        });
    }

    private async Task DatesChangedAsync(bool start)
    {
        if (changing) return;
        changing = true;
        if (from.Value.Date > to.Value.Date)
        {
            if (start) to.Value = from.Value.Date;
            else from.Value = to.Value.Date;
        }
        week.Selected = month.Selected = semester.Selected = false;
        changing = false;
        await ExecuteAsync(() => RefreshAsync(false));
    }

    private async Task PresetAsync(ModernButton chosen, DateTime start, DateTime end)
    {
        if (busy) return;
        changing = true;
        from.Value = start; to.Value = end;
        week.Selected = chosen == week; month.Selected = chosen == month; semester.Selected = chosen == semester;
        changing = false;
        await ExecuteAsync(() => RefreshAsync(false));
    }

    private string? SelectedSubject => subjects.SelectedIndex > 0 ? subjects.SelectedItem as string : null;
    private JournalFilter? CurrentFilter => groups.SelectedItem is GroupDto group
        ? new(group.Id, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(to.Value), SelectedSubject) : null;

    private async Task RefreshAsync(bool updateSubjects, bool preserve = false)
    {
        if (groups.SelectedItem is not GroupDto group)
        {
            ClearJournal("Нет групп со студентами");
            return;
        }
        var start = DateOnly.FromDateTime(from.Value);
        var end = DateOnly.FromDateTime(to.Value);
        try
        {
            if (updateSubjects)
            {
                var names = await Api.SubjectsAsync(group.Id);
                if (IsDisposed) return;
                SetSubjects(names);
            }
            var subject = SelectedSubject;
            var data = await Api.JournalAsync(group.Id, start, end, subject);
            if (IsDisposed) return;
            ShowJournal(data, preserve);
            loadedFilter = new(group.Id, start, end, subject);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) ClearJournal(ex.Message);
            throw;
        }
    }

    private void ClearJournal(string message)
    {
        journal = null; loadedFilter = null; attendance = new();
        grid.Rows.Clear(); grid.Columns.Clear(); grid.Visible = false;
        empty.Text = message; empty.Visible = true;
    }

    private void SetSubjects(List<string> names)
    {
        var selected = SelectedSubject;
        changing = true;
        subjects.Items.Clear();
        subjects.Items.Add("Все дисциплины");
        foreach (var name in names.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            subjects.Items.Add(name);
        subjects.SelectedItem = selected is not null && subjects.Items.Contains(selected) ? selected : subjects.Items.Count > 1 ? subjects.Items[1] : subjects.Items[0];
        changing = false;
    }

    private static string ShortTitle(string title) => title.Length <= 13 ? title : title[..12] + "…";

    private void ShowJournal(JournalDto data, bool preserve)
    {
        var firstRow = preserve && grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
        var horizontal = preserve ? grid.HorizontalScrollingOffset : 0;
        (int StudentId, JournalColumnKind Kind, int Id, DateOnly Date)? selected = null;
        if (preserve && grid.CurrentCell is { } current
            && grid.Rows[current.RowIndex].Tag is JournalStudentDto selectedStudent
            && grid.Columns[current.ColumnIndex].Tag is JournalEntry selectedEntry)
            selected = (selectedStudent.StudentId, selectedEntry.Column.Kind, selectedEntry.Column.Id, selectedEntry.Column.Date);
        journal = data;
        attendance = new();
        grid.SuspendLayout();
        grid.Rows.Clear(); grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "№", Width = 52, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ФИО студента", Width = 240, Frozen = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Font = Theme.Font(10, FontStyle.Bold) } });
        var entries = data.Columns.Select((column, index) => new JournalEntry(column, index))
            .Where(x => SelectedSubject is not null || x.Column.Kind == JournalColumnKind.Lesson).ToList();
        foreach (var entry in entries)
        {
            var column = entry.Column;
            var assessment = column.Kind == JournalColumnKind.Assessment;
            var kind = UiFormat.AssessmentKindShort(column.AssessmentKind ?? AssessmentKind.Other);
            var title = column.Title ?? "";
            var header = assessment ? $"{column.Date:dd.MM}\n{kind}\n{ShortTitle(title)}"
                : $"{column.Date:dd.MM}\n{column.Start?.ToString("HH:mm") ?? "—"}";
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = header, Width = assessment ? 112 : 90, Tag = entry,
                ToolTipText = assessment ? $"{column.Date:dd.MM.yyyy} · {UiFormat.AssessmentKindText(column.AssessmentKind ?? AssessmentKind.Other)} · {title}"
                    : $"{column.Subject} · {column.Date:dd.MM.yyyy} {column.Start?.ToString("HH:mm") ?? "—"}",
                SortMode = DataGridViewColumnSortMode.NotSortable,
                HeaderCell = { Style = { BackColor = assessment ? Theme.SurfaceAlt : Theme.Surface,
                    SelectionBackColor = assessment ? Theme.SurfaceAlt : Theme.Surface } }
            });
        }
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Пропущено, ч", Width = 118, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Посещ., %", Width = 105, SortMode = DataGridViewColumnSortMode.NotSortable });
        if (SelectedSubject is not null)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ср. балл", Width = 105, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Итог", Width = 90, Name = "final", SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        for (var rowIndex = 0; rowIndex < data.Students.Count; rowIndex++)
        {
            var student = data.Students[rowIndex];
            var row = grid.Rows[grid.Rows.Add()];
            row.Tag = student;
            row.Cells[0].Value = rowIndex + 1;
            row.Cells[1].Value = student.FullName;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var cell = entry.Index < student.Cells.Count ? student.Cells[entry.Index] : null;
                var view = row.Cells[i + 2];
                if (cell is { InGroup: false })
                {
                    view.Value = "—";
                    view.Style.ForeColor = Theme.Muted;
                    continue;
                }
                if (entry.Column.Kind == JournalColumnKind.Lesson)
                    view.ToolTipText = LessonTooltip(entry.Column, cell, null);
                else
                {
                    view.Value = cell?.Score?.ToString() ?? "·";
                    view.Style.Font = Theme.Font(12, FontStyle.Bold);
                    view.Style.ForeColor = cell?.Score is null ? Theme.Muted : Theme.Text;
                }
            }
            var summary = entries.Count + 2;
            row.Cells[summary].Value = student.MissedHours;
            row.Cells[summary].ToolTipText = $"Из них по уважительной: {student.ExcusedHours} ч";
            row.Cells[summary + 1].Value = student.Present + student.Absent + student.Excused == 0 ? "—" : UiFormat.Percent(student.Percent);
            row.Cells[summary + 1].Style.ForeColor = student.Present + student.Absent + student.Excused == 0
                ? Theme.Muted : student.Percent >= 85 ? Theme.Green : student.Percent >= 70 ? Theme.Yellow : Theme.Red;
            row.Cells[summary + 1].Style.Font = Theme.Font(10, FontStyle.Bold);
            if (SelectedSubject is not null)
            {
                row.Cells[summary + 2].Value = student.Average?.ToString("0.0") ?? "—";
                row.Cells[summary + 3].Value = student.Final?.ToString() ?? "—";
                row.Cells[summary + 3].Style.Font = Theme.Font(10, FontStyle.Bold);
            }
        }
        grid.ResumeLayout();
        empty.Text = "За выбранный период занятий и контрольных нет";
        empty.Visible = entries.Count == 0;
        grid.Visible = entries.Count > 0;
        if (firstRow >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(firstRow, grid.Rows.Count - 1);
        if (horizontal > 0) grid.HorizontalScrollingOffset = horizontal;
        if (selected is { } cellSelection)
        {
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x => x.Tag is JournalStudentDto xStudent && xStudent.StudentId == cellSelection.StudentId);
            var column = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(x => x.Tag is JournalEntry xEntry
                && xEntry.Column.Kind == cellSelection.Kind && xEntry.Column.Id == cellSelection.Id && xEntry.Column.Date == cellSelection.Date);
            if (row is not null && column is not null) grid.CurrentCell = row.Cells[column.Index];
        }
    }

    private static string LessonTooltip(JournalColumnDto column, JournalCellDto? cell, AttendanceRowDto? row)
    {
        var lines = new List<string> { $"{column.Subject} · {column.Date:dd.MM.yyyy} {column.Start?.ToString("HH:mm") ?? "—"}" };
        lines.Add(cell?.Status switch
        {
            AttendanceStatus.Present => "Присутствовал",
            AttendanceStatus.Absent => "Отсутствовал",
            AttendanceStatus.Excused => "Отсутствовал по уважительной причине",
            _ => "Нет отметки"
        });
        if (cell?.Status == AttendanceStatus.Excused)
        {
            if (cell.Reason is { } reason) lines.Add(UiFormat.ExcuseReasonText(reason));
            lines.Add(row is null ? "Сведения о документе загружаются" :
                row.HasDocument ? "Документ предоставлен" : "Документ не предоставлен");
        }
        if (!string.IsNullOrWhiteSpace(cell?.Comment)) lines.Add(cell.Comment);
        if (cell?.Score is { } score) lines.Add($"Балл: {score}");
        return string.Join(Environment.NewLine, lines);
    }

    private void PaintLesson(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Tag is not JournalEntry entry
            || entry.Column.Kind != JournalColumnKind.Lesson) return;
        var student = grid.Rows[e.RowIndex].Tag as JournalStudentDto;
        if (student is null) return;
        var cell = entry.Index < student.Cells.Count ? student.Cells[entry.Index] : null;
        if (cell is { InGroup: false })
        {
            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
            var mutedBounds = new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y, e.CellBounds.Width - 18, e.CellBounds.Height);
            TextRenderer.DrawText(e.Graphics!, "—", Theme.Font(13, FontStyle.Bold), mutedBounds, Theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            e.Handled = true;
            return;
        }
        var status = cell?.Status;
        var color = status switch
        {
            AttendanceStatus.Present => Theme.Green,
            AttendanceStatus.Absent => Theme.Red,
            AttendanceStatus.Excused => Theme.Yellow,
            _ => Theme.Muted
        };
        e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.Border);
        var bounds = new Rectangle(e.CellBounds.X + 10, e.CellBounds.Y, e.CellBounds.Width - 18, e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics!, UiFormat.StatusSymbol(status), Theme.Font(13, FontStyle.Bold), bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (SelectedSubject is not null && cell?.Score is { } score)
            TextRenderer.DrawText(e.Graphics!, score.ToString(), Theme.Font(10, FontStyle.Bold), bounds, Theme.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        if (status == AttendanceStatus.Excused)
        {
            var state = e.Graphics!.Save();
            e.Graphics.SetClip(e.CellBounds);
            TextRenderer.DrawText(e.Graphics!, "у", Theme.Font(8, FontStyle.Bold),
                new Rectangle(e.CellBounds.Right - 18, e.CellBounds.Top + 4, 14, 14), Theme.Yellow,
                TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            e.Graphics.Restore(state);
        }
        e.Handled = true;
    }

    private async Task<List<AttendanceRowDto>> AttendanceRowsAsync(JournalColumnDto column)
    {
        var cache = attendance;
        var key = (column.Id, column.Date);
        if (!cache.TryGetValue(key, out var task))
        {
            task = Api.AttendanceAsync(column.Id, column.Date);
            cache[key] = task;
        }
        try { return await task; }
        catch { cache.Remove(key); throw; }
    }

    private async Task LoadTooltipAsync(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || columnIndex < 0 || journal is not { } data
            || grid.Columns[columnIndex].Tag is not JournalEntry entry
            || entry.Column.Kind != JournalColumnKind.Lesson
            || grid.Rows[rowIndex].Tag is not JournalStudentDto student
            || entry.Index >= student.Cells.Count
            || student.Cells[entry.Index].Status != AttendanceStatus.Excused) return;
        try
        {
            var rows = await AttendanceRowsAsync(entry.Column);
            if (IsDisposed || !ReferenceEquals(journal, data) || columnIndex >= grid.Columns.Count
                || grid.Columns[columnIndex].Tag is not JournalEntry current || current != entry) return;
            foreach (DataGridViewRow view in grid.Rows)
            {
                if (view.Tag is not JournalStudentDto item || entry.Index >= item.Cells.Count
                    || item.Cells[entry.Index].Status != AttendanceStatus.Excused) continue;
                var attendanceRow = rows.FirstOrDefault(x => x.StudentId == item.StudentId);
                view.Cells[columnIndex].ToolTipText = LessonTooltip(entry.Column, item.Cells[entry.Index], attendanceRow);
            }
        }
        catch { }
    }

    private async Task EditCellAsync(int rowIndex, int columnIndex)
    {
        if (busy || loadedFilter != CurrentFilter || rowIndex < 0 || columnIndex < 0
            || grid.Rows[rowIndex].Tag is not JournalStudentDto student
            || groups.SelectedItem is not GroupDto group
            || FindForm() is not { } owner) return;
        var selected = grid.Columns[columnIndex];
        if (selected.Tag is not JournalEntry && (selected.Name != "final" || SelectedSubject is null)) return;
        if (selected.Tag is JournalEntry selectedEntry && selectedEntry.Index < student.Cells.Count
            && !student.Cells[selectedEntry.Index].InGroup) return;
        var bounds = grid.GetCellDisplayRectangle(columnIndex, rowIndex, false);
        var near = grid.PointToScreen(new Point(bounds.Left, bounds.Bottom));
        await ExecuteAsync(async () =>
        {
            GradeEditor editor;
            if (selected.Tag is JournalEntry entry)
            {
                var cell = entry.Index < student.Cells.Count ? student.Cells[entry.Index] : new JournalCellDto(null, null, null, null);
                AttendanceRowDto? row = null;
                if (entry.Column.Kind == JournalColumnKind.Lesson)
                    row = (await AttendanceRowsAsync(entry.Column)).FirstOrDefault(x => x.StudentId == student.StudentId);
                if (IsDisposed || owner.IsDisposed) return;
                editor = new GradeEditor(Api, entry.Column, cell, student.StudentId, student.FullName, attendance: row, attendanceOnly: SelectedSubject is null);
            }
            else editor = new GradeEditor(Api, group.Id, SelectedSubject!, student.StudentId, student.FullName, student.Final);
            using (editor)
            {
                var changed = false;
                editor.Saved += () => changed = true;
                editor.ShowDialog(owner, near);
                if (changed && !IsDisposed) await RefreshAsync(false, true);
            }
        });
    }

    private async Task OpenColumnAsync(int columnIndex)
    {
        if (busy || loadedFilter != CurrentFilter || columnIndex < 0
            || grid.Columns[columnIndex].Tag is not JournalEntry entry
            || groups.SelectedItem is not GroupDto group || FindForm() is not { } owner) return;
        await ExecuteAsync(async () =>
        {
            if (entry.Column.Kind == JournalColumnKind.Lesson)
            {
                var lessons = await Api.DayAsync(entry.Column.Date);
                if (IsDisposed || owner.IsDisposed) return;
                var lesson = lessons.FirstOrDefault(x => x.Slot.Id == entry.Column.Id);
                if (lesson is null) { Ui.Toast(this, "Занятие не найдено в расписании.", true); return; }
                using var dialog = new LessonDialog(Api, lesson);
                dialog.ShowDialog(owner);
                if (!IsDisposed) await RefreshAsync(false, true);
                return;
            }
            if (SelectedSubject is not { } subject) return;
            var assessments = await Api.AssessmentsAsync(group.Id, subject);
            if (IsDisposed || owner.IsDisposed) return;
            var assessment = assessments.FirstOrDefault(x => x.Id == entry.Column.Id);
            if (assessment is null) { Ui.Toast(this, "Контрольная не найдена.", true); return; }
            using var edit = new AssessmentDialog(Api, group.Id, group.Name, subject, assessments, assessment);
            if (edit.ShowDialog(owner) == DialogResult.OK && !IsDisposed) await RefreshAsync(false, true);
        });
    }

    private async Task AddAssessmentAsync()
    {
        if (busy || loadedFilter != CurrentFilter || groups.SelectedItem is not GroupDto group
            || SelectedSubject is not { } subject || FindForm() is not { } owner) return;
        await ExecuteAsync(async () =>
        {
            var assessments = await Api.AssessmentsAsync(group.Id, subject);
            if (IsDisposed || owner.IsDisposed) return;
            using var dialog = new AssessmentDialog(Api, group.Id, group.Name, subject, assessments);
            if (dialog.ShowDialog(owner) == DialogResult.OK && !IsDisposed) await RefreshAsync(false, true);
        });
    }

    private async Task FillFinalAsync()
    {
        if (busy || loadedFilter != CurrentFilter || groups.SelectedItem is not GroupDto group
            || SelectedSubject is not { } subject || FindForm() is not { } owner
            || !Ui.Confirm(owner, "Заполнить итог?",
                "Итоговый балл будет рассчитан как среднее всех баллов за семестр для студентов, у которых итог ещё не выставлен.",
                "Заполнить")) return;
        await ExecuteAsync(async () =>
        {
            var result = await Api.FillFinalAsync(group.Id, subject);
            if (IsDisposed) return;
            Ui.Toast(this, $"Итог выставлен: {result.Count}");
            await RefreshAsync(false, true);
        });
    }

    private async Task ExportAsync()
    {
        if (groups.SelectedItem is not GroupDto group) return;
        await ExecuteAsync(async () =>
        {
            var (content, name) = await Api.JournalFileAsync(group.Id, DateOnly.FromDateTime(from.Value), DateOnly.FromDateTime(to.Value), SelectedSubject, "xlsx");
            if (IsDisposed) return;
            using var dialog = new SaveFileDialog { FileName = name, DefaultExt = "xlsx", AddExtension = true, Filter = "Excel (*.xlsx)|*.xlsx" };
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            await File.WriteAllBytesAsync(dialog.FileName, content);
            if (!IsDisposed) ShowSavedToast(dialog.FileName);
        });
    }

    private void ShowSavedToast(string path)
    {
        var form = FindForm();
        if (form is null || form.IsDisposed) return;
        var toast = new SmoothPanel { Width = 320, Height = 50, Radius = 12, Padding = new Padding(12, 4, 8, 4), BackColor = Theme.SurfaceAlt };
        var label = Ui.Label("Файл сохранён", 10, Theme.Text, FontStyle.Bold);
        var open = new ModernButton { Text = "Открыть", Width = 100, Dock = DockStyle.Right, Margin = Padding.Empty };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { Ui.Toast(this, $"Не удалось открыть файл: {ex.Message}", true); }
            if (!toast.IsDisposed) toast.Dispose();
        };
        toast.Controls.Add(label); toast.Controls.Add(open);
        form.Controls.Add(toast);
        toast.Location = new Point(form.ClientSize.Width - toast.Width - 24, form.ClientSize.Height - toast.Height - 24);
        toast.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        toast.BringToFront();
        var timer = new System.Windows.Forms.Timer { Interval = 8000 };
        timer.Tick += (_, _) => { timer.Dispose(); if (!toast.IsDisposed) toast.Dispose(); };
        toast.Disposed += (_, _) => timer.Dispose();
        timer.Start();
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy || IsDisposed) return;
        busy = true; SetBusy(true); UseWaitCursor = true;
        try { await action(); }
        catch (ApiException ex) { if (!IsDisposed) Ui.Toast(this, ex.Message, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!IsDisposed) Ui.Toast(this, ex.Message, true);
        }
        finally { busy = false; if (!IsDisposed) { UseWaitCursor = false; SetBusy(false); } }
    }

    private void SetBusy(bool state)
    {
        groups.Enabled = !state && groups.Items.Count > 0;
        subjects.Enabled = !state && groups.SelectedItem is GroupDto;
        from.Enabled = to.Enabled = week.Enabled = month.Enabled = semester.Enabled = !state && groups.SelectedItem is GroupDto;
        addAssessment.Enabled = fillFinal.Enabled = !state && groups.SelectedItem is GroupDto && SelectedSubject is not null;
        excel.Enabled = !state && groups.SelectedItem is GroupDto;
        grid.Enabled = !state && journal is { Columns.Count: > 0 };
        card.Caption = state ? "Загрузка..." : "Журнал";
    }

    private readonly record struct JournalEntry(JournalColumnDto Column, int Index);
    private readonly record struct JournalFilter(int GroupId, DateOnly From, DateOnly To, string? Subject);
}
