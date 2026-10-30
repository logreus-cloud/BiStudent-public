using System.Text;
using BiStudent.Contracts;

namespace BiStudent;

/// Импорт расписания колледжа из Excel: выбор групп по результатам предпросмотра, затем импорт на сервере.
internal sealed class ScheduleImportDialog : DialogShell
{
    private readonly ApiClient api;
    private readonly byte[] file;
    private List<ScheduleImportGroupDto> groups;
    private readonly List<TeacherAccountDto> teachers;
    private readonly TeacherDto? self;
    private readonly CheckedListBox list = new()
    {
        Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
        CheckOnClick = true, Font = Theme.Font(10), IntegralHeight = false
    };
    private readonly ComboBox teacherChoice = new()
    {
        Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(10)
    };
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton import;
    private bool busy;
    private int? previewTeacherId;
    private bool previewReady = true;

    public ScheduleImportResultDto? Result { get; private set; }

    public ScheduleImportDialog(ApiClient api, byte[] file, ScheduleImportPreviewDto preview, IReadOnlyList<TeacherAccountDto> teachers, TeacherDto? self = null) : base("Импорт расписания из Excel", 720, 640)
    {
        this.api = api;
        this.file = file;
        this.self = self;
        groups = preview.Groups;
        this.teachers = teachers.OrderBy(x => x.FullName, StringComparer.CurrentCulture).ToList();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, preview.Warnings.Count > 0 ? 44 : 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(Ui.Label($"Начало семестра: {preview.SemesterStart:dd.MM.yyyy}. Найдено групп: {groups.Count}.\n" +
            "Отметьте группы для импорта. Изменённые группы будут заменены.", 10, Theme.Text), 0, 0);
        ShowGroups(preview.Groups);
        var listCard = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 4) };
        listCard.Controls.Add(list);
        layout.Controls.Add(listCard, 0, 1);
        var teacherBlock = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background };
        teacherBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        teacherBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        teacherBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var teacherLabel = "Преподаватель для всех занятий:";
        if (preview.FileTeacher is { } fileTeacher && preview.FileTeacherId is null)
            teacherLabel += $"\nВ файле указан преподаватель «{fileTeacher}», но такой учётной записи нет.";
        if (self is not null)
            teacherBlock.Controls.Add(Ui.Label($"Расписание будет назначено вам: {self.FullName}.\nЗаменяются только ваши пары в выбранных группах.", 9, Theme.Text), 0, 0);
        else
        {
            teacherBlock.Controls.Add(Ui.Label(teacherLabel, 9, Theme.Text), 0, 0);
            teacherChoice.Items.Add($"Как указано в файле ({string.Join(", ", preview.Teachers)})");
            foreach (var teacher in this.teachers) teacherChoice.Items.Add($"{teacher.FullName} ({teacher.Login})");
            var teacherHint = Ui.Label("", 9, Theme.Muted);
            teacherChoice.SelectedIndex = this.teachers.FindIndex(x => x.Id == preview.FileTeacherId) + 1;
            teacherChoice.Enabled = this.teachers.Count > 0;
            teacherHint.Text = teacherChoice.SelectedIndex == 0
                ? "Для новых преподавателей будут созданы учётные записи с временными паролями."
                : "Заменяются только пары выбранного преподавателя в выбранных группах.";
            teacherChoice.SelectedIndexChanged += async (_, _) =>
            {
                teacherHint.Text = teacherChoice.SelectedIndex == 0
                    ? "Для новых преподавателей будут созданы учётные записи с временными паролями."
                    : "Заменяются только пары выбранного преподавателя в выбранных группах.";
                previewReady = false; import!.Enabled = false;
                await RefreshPreviewAsync();
            };
            teacherBlock.Controls.Add(teacherChoice, 0, 1);
            teacherBlock.Controls.Add(teacherHint, 0, 2);
        }
        layout.Controls.Add(teacherBlock, 0, 2);
        if (preview.Warnings.Count > 0)
            layout.Controls.Add(Ui.Label($"Предупреждений: {preview.Warnings.Count} — подробности будут показаны после импорта.", 9, Theme.Yellow), 0, 3);
        error.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(error, 0, 4);
        Content.Controls.Add(layout);

        AddButton("Отмена", DialogResult.Cancel);
        import = new ModernButton { Text = "Импортировать", Primary = true, Width = 170, Height = 40 };
        import.Click += async (_, _) => await ImportAsync();
        Buttons.Controls.Add(import);
        AcceptButton = import;
        previewTeacherId = SelectedTeacherId;
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }

    private async Task ImportAsync()
    {
        if (busy || !previewReady || previewTeacherId != SelectedTeacherId) return;
        var selected = list.CheckedIndices.Cast<int>().Select(i => groups[i].Group).ToList();
        if (selected.Count == 0) { error.Text = "Отметьте хотя бы одну группу."; return; }
        var replacement = self is not null ? "Ваши пары" :
            SelectedTeacherId is int id ? $"Пары преподавателя {teachers.Single(x => x.Id == id).FullName}" : "Расписание";
        if (list.CheckedIndices.Cast<int>().Any(i => groups[i].Exists && !groups[i].AlreadyLoaded) &&
            !Ui.Confirm(FindForm(), "Заменить расписание",
                $"{replacement} в выбранных группах будут заменены новым расписанием. Занятия с отметками и оценками сохранятся до последней отмеченной даты. Продолжить?", "Заменить", true)) return;
        busy = true; import.Enabled = false; UseWaitCursor = true; error.Text = "";
        try
        {
            Result = await api.ImportScheduleXlsxAsync(file, selected, SelectedTeacherId);
            busy = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed) { import.Enabled = previewReady && previewTeacherId == SelectedTeacherId; UseWaitCursor = false; }
        }
    }

    private int? SelectedTeacherId => self?.Id ??
        (teacherChoice.SelectedIndex > 0 ? teachers[teacherChoice.SelectedIndex - 1].Id : null);

    private void ShowGroups(List<ScheduleImportGroupDto> incoming)
    {
        var checkedNames = list.CheckedIndices.Cast<int>().Select(i => groups[i].Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
    /// Итог импорта: изменения расписания и созданные учётные записи с временными паролями.
        var first = list.Items.Count == 0;
        groups = incoming;
        list.Items.Clear();
        foreach (var group in groups)
        {
            var text = $"{group.Group}   ·   {group.Lessons} {UiFormat.Plural(group.Lessons, "занятие", "занятия", "занятий")}   ·   {group.Sheet}" +
                (group.AlreadyLoaded ? "   ·   уже загружено" : group.Exists ? "   ·   будет заменено" : "");
            list.Items.Add(text, first ? group.Exists && !group.AlreadyLoaded : checkedNames.Contains(group.Group) && !group.AlreadyLoaded);
        }
    }

    private async Task RefreshPreviewAsync()
    {
        if (busy || IsDisposed) return;
        var teacherId = SelectedTeacherId;
        previewReady = false;
        busy = true; import.Enabled = false; teacherChoice.Enabled = false; list.Enabled = false; UseWaitCursor = true; error.Text = "";
        try
        {
            var preview = await api.PreviewScheduleXlsxAsync(file, teacherId);
            if (!IsDisposed)
            {
                ShowGroups(preview.Groups);
                previewTeacherId = teacherId;
                previewReady = true;
            }
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed) { import.Enabled = previewReady && previewTeacherId == SelectedTeacherId; teacherChoice.Enabled = true; list.Enabled = true; UseWaitCursor = false; }
        }
    }

    public static void ShowResult(IWin32Window? owner, ScheduleImportResultDto result)
    {
        using var dialog = new DialogShell("Расписание импортировано", 760, 560);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, result.Unchanged is { Count: > 0 } ? 82 : 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, result.Warnings.Count > 0 ? 110 : 0));
        layout.Controls.Add(Ui.Label($"Добавлено занятий: {result.Added}, обновлено: {result.Updated}, удалено старых: {result.Removed}, новых групп: {result.GroupsCreated}.\n" +
            (result.Unchanged is { Count: > 0 } ? $"Уже загружено, без изменений: {string.Join(", ", result.Unchanged)}.\n" : "") +
            (result.Teachers.Any(x => x.Created)
                ? result.Teachers.Any(x => x.TemporaryPassword is not null)
                    ? "Передайте временные пароли преподавателям лично — при первом входе система попросит их сменить."
                    : "Новые учётные записи созданы."
                : "Новые учётные записи не созданы."), 10, Theme.Text), 0, 0);
        var grid = Ui.Grid();
        grid.Dock = DockStyle.Fill;
        grid.Columns.Add("name", "ФИО"); grid.Columns.Add("login", "Логин"); grid.Columns.Add("password", "Временный пароль"); grid.Columns.Add("state", "Статус");
        grid.Columns["name"]!.FillWeight = 43;
        grid.Columns["login"]!.FillWeight = 17;
        grid.Columns["password"]!.FillWeight = 17;
        grid.Columns["state"]!.FillWeight = 23;
        grid.Columns["password"]!.DefaultCellStyle.Font = new Font("Consolas", 11, FontStyle.Bold);
        foreach (var teacher in result.Teachers)
            grid.Rows.Add(teacher.FullName, teacher.Login, teacher.TemporaryPassword ?? "—",
                !teacher.Created ? "уже была" : teacher.Active ? "создана" : "создана, отключена");
        layout.Controls.Add(grid, 0, 1);
        if (result.Warnings.Count > 0)
        {
            var warnings = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Theme.Surface, ForeColor = Theme.Yellow, Font = Theme.Font(9),
                Text = "Предупреждения:\r\n" + string.Join("\r\n", result.Warnings)
            };
            layout.Controls.Add(warnings, 0, 2);
        }
        dialog.Content.Controls.Add(layout);
        dialog.AddButton("Закрыть", DialogResult.OK, true);
        var copy = new ModernButton { Text = "Скопировать логины и пароли", Width = 260, Height = 40 };
        copy.Enabled = result.Teachers.Any(x => x.TemporaryPassword is not null);
        copy.Click += (_, _) =>
        {
            var text = new StringBuilder("ФИО\tЛогин\tВременный пароль\r\n");
            foreach (var teacher in result.Teachers.Where(t => t.TemporaryPassword is not null))
                text.Append($"{teacher.FullName}\t{teacher.Login}\t{teacher.TemporaryPassword}\r\n");
            Clipboard.SetText(text.ToString());
            Ui.Toast(dialog, "Скопировано в буфер обмена");
        };
        dialog.Buttons.Controls.Add(copy);
        dialog.ShowDialog(owner);
    }
}
