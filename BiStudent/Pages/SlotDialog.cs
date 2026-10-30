using BiStudent.Contracts;

namespace BiStudent;

internal sealed class SlotDialog : DarkForm
{
    private readonly ApiClient api;
    private readonly SlotDto? slot;
    private readonly ModernComboBox group = new();
    private readonly TextBox newGroup = Ui.Input("Название новой группы");
    private readonly TextBox subject = Ui.Input("Предмет");
    private readonly TextBox room = Ui.Input("Аудитория");
    private readonly ModernComboBox day = new();
    private readonly ModernComboBox parity = new();
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Short };
    private readonly DateTimePicker start = TimePicker();
    private readonly DateTimePicker end = TimePicker();
    private readonly CheckBox oneOff = new() { Text = "Разовое занятие", ForeColor = Theme.Text, BackColor = Theme.Background, AutoSize = true };
    private readonly Label error = Ui.Label("", 9.5f, Theme.Red);
    private readonly Label dateLabel = Ui.Label("День недели", 9.5f, Theme.Muted);
    private readonly Label newGroupLabel = Ui.Label("Новая группа", 9.5f, Theme.Muted);
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 11, BackColor = Theme.Background, Padding = new Padding(24, 6, 24, 4) };
    private readonly FlowLayoutPanel presets = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Background };
    private readonly List<ModernButton> chips = [];
    private readonly ModernButton save = new() { Text = "Сохранить", Primary = true, Width = 140 };
    private bool loading;

    public SlotDialog(ApiClient api, SlotDto? slot, DateOnly selectedDate, IEnumerable<string> subjects)
    {
        this.api = api; this.slot = slot;
        Text = slot is null ? "Добавить пару" : "Изменить пару";
        ClientSize = new Size(680, 680); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var heading = Ui.Label(Text, 15, Theme.Text, FontStyle.Bold); heading.Dock = DockStyle.Top; heading.Height = 52; heading.Padding = new Padding(24, 8, 0, 0);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Background, Padding = new Padding(14, 8, 14, 8) };
        var cancel = new ModernButton { Text = "Отмена", Width = 120 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        save.Click += async (_, _) => await SaveAsync();
        footer.Controls.Add(save); footer.Controls.Add(cancel); AcceptButton = save; CancelButton = cancel;
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 46, 0, 46, 46, 90, 46, 46, 42, 46, 46, 50 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Add(0, "Группа", group); fields.Controls.Add(newGroupLabel, 0, 1); newGroup.Dock = DockStyle.Fill; fields.Controls.Add(newGroup, 1, 1); Add(2, "Предмет", subject); Add(3, "Аудитория", room);
        Add(4, "Пресеты звонков", presets); Add(5, "Начало", start); Add(6, "Конец", end); Add(7, "", oneOff);
        day.Dock = date.Dock = DockStyle.Fill;
        fields.Controls.Add(dateLabel, 0, 8); fields.Controls.Add(day, 1, 8);
        Add(9, "Неделя", parity);
        fields.Controls.Add(error, 0, 10); fields.SetColumnSpan(error, 2);
        Controls.Add(fields); Controls.Add(footer); Controls.Add(heading);
        foreach (var name in new[] { "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота" }) day.Items.Add(name);
        foreach (var name in new[] { "Каждая", "Нечётная", "Чётная" }) parity.Items.Add(name);
        day.SelectedIndex = Math.Clamp((int)(slot?.DayOfWeek ?? selectedDate.DayOfWeek) - 1, 0, 5);
        parity.SelectedIndex = slot?.Parity switch { WeekParity.Odd => 1, WeekParity.Even => 2, _ => 0 };
        date.Value = (slot?.OneOffDate ?? selectedDate).ToDateTime(TimeOnly.MinValue);
        subject.Text = slot?.Subject ?? ""; room.Text = slot?.Room ?? "";
        start.Value = DateTime.Today.Add((slot?.Start ?? new TimeOnly(8, 30)).ToTimeSpan());
        end.Value = DateTime.Today.Add((slot?.End ?? new TimeOnly(9, 50)).ToTimeSpan());
        var values = new[] { (8, 30, 9, 50), (10, 0, 11, 20), (11, 30, 12, 50), (13, 20, 14, 40), (14, 50, 16, 10), (16, 20, 17, 40) };
        for (var i = 0; i < values.Length; i++)
        {
            var (sh, sm, eh, em) = values[i];
            var chip = new ModernButton { Text = $"{i + 1} пара  {sh:00}:{sm:00}–{eh:00}:{em:00}", Width = 150, Height = 34, Font = Theme.Font(8.5f), Margin = new Padding(0, 0, 7, 6) };
            chip.Click += (_, _) => { start.Value = DateTime.Today.AddHours(sh).AddMinutes(sm); end.Value = DateTime.Today.AddHours(eh).AddMinutes(em); UpdateChips(); };
            chips.Add(chip); presets.Controls.Add(chip);
        }
        start.ValueChanged += (_, _) => UpdateChips(); end.ValueChanged += (_, _) => UpdateChips();
        StepByFive(start); StepByFive(end); UpdateChips();
        var suggestions = new AutoCompleteStringCollection();
        suggestions.AddRange(subjects.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray());
        subject.AutoCompleteMode = AutoCompleteMode.SuggestAppend; subject.AutoCompleteSource = AutoCompleteSource.CustomSource; subject.AutoCompleteCustomSource = suggestions;
        group.SelectedIndexChanged += (_, _) => ShowNewGroup();
        oneOff.Checked = slot?.OneOffDate is not null;
        oneOff.CheckedChanged += (_, _) => ShowDate();
        ShowDate();
        Shown += async (_, _) => await LoadGroupsAsync();
    }

    private void Add(int row, string caption, Control control)
    {
        fields.Controls.Add(Ui.Label(caption, 9.5f, Theme.Muted), 0, row);
        control.Dock = DockStyle.Fill; fields.Controls.Add(control, 1, row);
    }

    private static DateTimePicker TimePicker() => new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 140 };

    private static void StepByFive(DateTimePicker picker)
    {
        var previous = picker.Value;
        var adjusting = false;
        picker.ValueChanged += (_, _) =>
        {
            if (adjusting) return;
            var value = picker.Value;
            var remainder = value.Minute % 5;
            if (remainder != 0)
            {
                adjusting = true;
                try { picker.Value = value.AddMinutes(value > previous ? 5 - remainder : -remainder); }
                finally { adjusting = false; }
            }
            previous = picker.Value;
        };
        picker.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
            picker.Value = picker.Value.AddMinutes(e.KeyCode == Keys.Up ? 5 : -5);
            e.SuppressKeyPress = true;
        };
    }

    private void UpdateChips()
    {
        var values = new[] { (new TimeOnly(8, 30), new TimeOnly(9, 50)), (new TimeOnly(10, 0), new TimeOnly(11, 20)), (new TimeOnly(11, 30), new TimeOnly(12, 50)), (new TimeOnly(13, 20), new TimeOnly(14, 40)), (new TimeOnly(14, 50), new TimeOnly(16, 10)), (new TimeOnly(16, 20), new TimeOnly(17, 40)) };
        for (var i = 0; i < chips.Count; i++) chips[i].Selected = values[i] == (TimeOnly.FromDateTime(start.Value), TimeOnly.FromDateTime(end.Value));
    }

    private void ShowNewGroup()
    {
        var visible = group.SelectedItem is GroupChoice { Id: 0 };
        fields.RowStyles[1].Height = visible ? 46 : 0;
        newGroup.Visible = visible; newGroupLabel.Visible = visible;
    }

    private void ShowDate()
    {
        var single = oneOff.Checked;
        fields.Controls.Remove(single ? day : date);
        fields.Controls.Add(single ? date : day, 1, 8);
        dateLabel.Text = single ? "Дата занятия" : "День недели";
        parity.Enabled = !single;
        parity.Visible = !single;
        fields.RowStyles[9].Height = single ? 0 : 46;
    }

    private async Task LoadGroupsAsync()
    {
        if (loading || IsDisposed) return;
        loading = true; save.Enabled = false; error.Text = "Загрузка групп…";
        try
        {
            var groups = await api.GroupsAsync();
            if (IsDisposed) return;
            group.Items.Clear();
            foreach (var item in groups) group.Items.Add(new GroupChoice(item.Id, item.Name));
            group.Items.Add(new GroupChoice(0, "+ Новая группа…"));
            var index = slot is null ? (groups.Count > 0 ? 0 : group.Items.Count - 1) : groups.FindIndex(x => x.Id == slot.GroupId);
            group.SelectedIndex = index >= 0 ? index : group.Items.Count - 1;
            error.Text = ""; save.Enabled = true;
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { loading = false; }
    }

    private async Task SaveAsync()
    {
        if (loading || IsDisposed) return;
        error.Text = "";
        if (group.SelectedItem is not GroupChoice choice) { error.Text = "Выберите группу."; return; }
        if (choice.Id == 0 && string.IsNullOrWhiteSpace(newGroup.Text)) { error.Text = "Введите название новой группы."; return; }
        if (string.IsNullOrWhiteSpace(subject.Text)) { error.Text = "Введите предмет."; return; }
        var from = TimeOnly.FromDateTime(start.Value); var to = TimeOnly.FromDateTime(end.Value);
        if (to <= from) { error.Text = "Конец пары должен быть позже начала."; return; }
        loading = true; save.Enabled = false; UseWaitCursor = true;
        try
        {
            var selectedDay = (DayOfWeek)(day.SelectedIndex + 1);
            var request = new SaveSlotRequest(choice.Id, choice.Id == 0 ? newGroup.Text.Trim() : null, subject.Text.Trim(),
                string.IsNullOrWhiteSpace(room.Text) ? null : room.Text.Trim(), oneOff.Checked ? date.Value.DayOfWeek : selectedDay,
                from, to, oneOff.Checked ? WeekParity.Every : parity.SelectedIndex switch { 1 => WeekParity.Odd, 2 => WeekParity.Even, _ => WeekParity.Every },
                oneOff.Checked ? DateOnly.FromDateTime(date.Value) : null);
            try { await api.SaveSlotAsync(slot?.Id, request); }
            catch (ApiException ex) when (ex.Conflict is { } conflict)
            {
                if (IsDisposed || Disposing) return;
                if (!ConfirmReplacement(conflict, request)) return;
                await api.SaveSlotAsync(slot?.Id, request with { ReplaceSlotIds = conflict.Conflicts.Select(x => x.Id).ToList() });
            }
            if (IsDisposed) return;
            DialogResult = DialogResult.OK; Close();
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally { loading = false; if (!IsDisposed) { save.Enabled = true; UseWaitCursor = false; } }
    }

    private bool ConfirmReplacement(ScheduleConflictDto conflict, SaveSlotRequest request)
    {
        using var dialog = new DialogShell("Время занято", 760, 480);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
        layout.Controls.Add(Ui.Label(conflict.Message, 9.5f, Theme.Text), 0, 0);
        var grid = Ui.Grid();
        grid.Dock = DockStyle.Fill;
        grid.Columns.Add("group", "Группа");
        grid.Columns.Add("subject", "Предмет");
        grid.Columns.Add("day", "День / дата");
        grid.Columns.Add("time", "Время");
        grid.Columns.Add("room", "Аудитория");
        foreach (var item in conflict.Conflicts)
            grid.Rows.Add(item.GroupName, item.Subject,
                item.OneOffDate is DateOnly date ? UiFormat.Date(date) : UiFormat.DayLong(item.DayOfWeek),
                $"{item.Start:HH:mm}–{item.End:HH:mm}", item.Room ?? "—");
        layout.Controls.Add(grid, 0, 1);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var actions = conflict.Conflicts.Select(item =>
            item.OneOffDate is not null ? "Разовая пара удалится." :
            request.OneOffDate is not null ? "Занятие этой даты отменится." :
            item.ValidFrom < today ? "Старая регулярная пара закончится вчера, новая начнётся с сегодня." :
                "Старая регулярная пара удалится, новая начнётся с сегодня.").Distinct();
        layout.Controls.Add(Ui.Label(string.Join(" ", actions), 9, Theme.Muted), 0, 2);
        dialog.Content.Controls.Add(layout);
        dialog.AddButton("Отмена", DialogResult.Cancel);
        dialog.AddButton("Заменить", DialogResult.OK, true);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    private sealed record GroupChoice(int Id, string Name)
    {
        public override string ToString() => Name;
    }
}
