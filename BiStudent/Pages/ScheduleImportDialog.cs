using System.Text;
using BiStudent.Contracts;

namespace BiStudent;

/// Импорт расписания колледжа из Excel: выбор групп по результатам предпросмотра, затем импорт на сервере.
internal sealed class ScheduleImportDialog : DialogShell
{
    private readonly ApiClient api;
    private readonly byte[] file;
    private readonly List<ScheduleImportGroupDto> groups;
    private readonly CheckedListBox list = new()
    {
        Dock = DockStyle.Fill, BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
        CheckOnClick = true, Font = Theme.Font(10), IntegralHeight = false
    };
    private readonly Label error = Ui.Label("", 9, Theme.Red);
    private readonly ModernButton import;
    private bool busy;

    public ScheduleImportResultDto? Result { get; private set; }

    public ScheduleImportDialog(ApiClient api, byte[] file, ScheduleImportPreviewDto preview) : base("Импорт расписания из Excel", 720, 640)
    {
        this.api = api;
        this.file = file;
        groups = preview.Groups;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, preview.Warnings.Count > 0 ? 44 : 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.Controls.Add(Ui.Label($"Начало семестра: {preview.SemesterStart:dd.MM.yyyy}. Найдено групп: {groups.Count}.\n" +
            "Отметьте группы для импорта. Изменённые группы будут заменены.", 10, Theme.Text), 0, 0);
        foreach (var group in groups)
        {
            var text = $"{group.Group}   ·   {group.Lessons} {UiFormat.Plural(group.Lessons, "занятие", "занятия", "занятий")}   ·   {group.Sheet}" +
                (group.AlreadyLoaded ? "   ·   уже загружено" : group.Exists ? "   ·   будет заменено" : "");
            list.Items.Add(text, group.Exists && !group.AlreadyLoaded);
        }
        var listCard = new SmoothPanel { Dock = DockStyle.Fill, Padding = new Padding(12), BackColor = Theme.Surface, Margin = new Padding(0, 4, 0, 4) };
        listCard.Controls.Add(list);
        layout.Controls.Add(listCard, 0, 1);
        var teacherNames = string.Join(", ", preview.Teachers);
        layout.Controls.Add(Ui.Label("Преподаватели: " + teacherNames +
            (teacherNames.EndsWith('.') ? "" : ".") + "\nДля новых преподавателей будут созданы учётные записи с временными паролями.", 9, Theme.Muted), 0, 2);
        if (preview.Warnings.Count > 0)
            layout.Controls.Add(Ui.Label($"Не удалось разобрать записей: {preview.Warnings.Count} — они будут пропущены и показаны после импорта.", 9, Theme.Yellow), 0, 3);
        error.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(error, 0, 4);
        Content.Controls.Add(layout);

        AddButton("Отмена", DialogResult.Cancel);
        import = new ModernButton { Text = "Импортировать", Primary = true, Width = 170, Height = 40 };
        import.Click += async (_, _) => await ImportAsync();
        Buttons.Controls.Add(import);
        AcceptButton = import;
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }

    private async Task ImportAsync()
    {
        var selected = list.CheckedIndices.Cast<int>().Select(i => groups[i].Group).ToList();
        if (selected.Count == 0) { error.Text = "Отметьте хотя бы одну группу."; return; }
        if (list.CheckedIndices.Cast<int>().Any(i => groups[i].Exists && !groups[i].AlreadyLoaded) &&
            !Ui.Confirm(FindForm(), "Заменить расписание",
                "Расписание выбранных групп будет заменено новым. Занятия с отметками и оценками сохранятся до последней отмеченной даты. Продолжить?", "Заменить", true)) return;
        busy = true; import.Enabled = false; UseWaitCursor = true; error.Text = "";
        try
        {
            Result = await api.ImportScheduleXlsxAsync(file, selected);
            busy = false;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (ApiException ex) { if (!IsDisposed) error.Text = ex.Message; }
        finally
        {
            busy = false;
            if (!IsDisposed) { import.Enabled = true; UseWaitCursor = false; }
        }
    }

    /// Итог импорта: изменения расписания и созданные учётные записи с временными паролями.
    public static void ShowResult(IWin32Window? owner, ScheduleImportResultDto result)
    {
        using var dialog = new DialogShell("Расписание импортировано", 760, 560);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, result.Unchanged is { Count: > 0 } ? 82 : 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, result.Warnings.Count > 0 ? 110 : 0));
        layout.Controls.Add(Ui.Label($"Добавлено занятий: {result.Added}, обновлено: {result.Updated}, удалено старых: {result.Removed}, новых групп: {result.GroupsCreated}.\n" +
            (result.Unchanged is { Count: > 0 } ? $"Уже загружено, без изменений: {string.Join(", ", result.Unchanged)}.\n" : "") +
            "Передайте временные пароли преподавателям лично — при первом входе система попросит их сменить.", 10, Theme.Text), 0, 0);
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
                Text = "Пропущено:\r\n" + string.Join("\r\n", result.Warnings)
            };
            layout.Controls.Add(warnings, 0, 2);
        }
        dialog.Content.Controls.Add(layout);
        dialog.AddButton("Закрыть", DialogResult.OK, true);
        var copy = new ModernButton { Text = "Скопировать логины и пароли", Width = 260, Height = 40 };
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
