using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace BiStudent.Server.Services;

/// Одно занятие из файла: либо еженедельное в диапазоне недель (FromWeek–ToWeek), либо разовое (FromWeek == ToWeek, Once).
public sealed record ParsedLesson(string Sheet, string Group, DayOfWeek Day, TimeOnly Start, TimeOnly End,
    string Subject, string? Room, string Teacher, int FromWeek, int ToWeek, bool Once);

public sealed record ParsedSchedule(DateOnly SemesterStart, List<ParsedLesson> Lessons, List<(string Sheet, string Group)> Groups,
    List<string> Warnings, string? FileTeacher);

/// Разбор расписания колледжа в формате «блок из пяти колонок на группу»:
/// день · время звонка · № пары · аудитория · группа. В ячейке группы — строки «[ТИП] Предмет · аудитория · нед. 2-4, 8 (разово)»,
/// строкой ниже — преподаватели в том же порядке («Фамилия И.О. · аудитория»).
public static partial class CollegeScheduleParser
{
    public const string Vacancy = "Вакансия";

    public static ParsedSchedule Parse(Stream xlsx, DateOnly fallbackSemesterStart)
    {
        using var workbook = new XLWorkbook(xlsx);
        var lessons = new List<ParsedLesson>();
        var groups = new List<(string, string)>();
        var warnings = new List<string>();
        DateOnly? semester = null;
        var fileTeachers = new List<string>();
        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used is null) continue;
            var lastRow = used.LastRow().RowNumber();
            var lastColumn = used.LastColumn().ColumnNumber();
            var fileTeacher = FindFileTeacher(sheet, Math.Min(lastRow, 10), lastColumn);
            if (fileTeacher is not null && !fileTeachers.Contains(fileTeacher, StringComparer.OrdinalIgnoreCase))
                fileTeachers.Add(fileTeacher);
            semester ??= FindSemesterStart(sheet, Math.Min(lastRow, 10), lastColumn);
            var header = FindHeaderRow(sheet, Math.Min(lastRow, 30), lastColumn);
            if (header is not int headerRow) continue;
            for (var column = 5; column <= lastColumn; column++)
            {
                var name = Text(sheet.Cell(headerRow, column));
                if (!GroupName().IsMatch(name)) continue;
                groups.Add((sheet.Name, name));
                ParseGroup(sheet, name, headerRow, column, lastRow, lessons, warnings);
            }
        }
        if (fileTeachers.Count > 1)
            warnings.Add($"В файле указаны разные преподаватели: {string.Join(", ", fileTeachers).TrimEnd('.')}.");
        return new ParsedSchedule(semester ?? fallbackSemesterStart, UnifyTeachers(lessons), groups, warnings,
            fileTeachers.Count == 1 ? fileTeachers[0] : null);
    }

    private static void ParseGroup(IXLWorksheet sheet, string group, int headerRow, int column, int lastRow,
        List<ParsedLesson> lessons, List<string> warnings)
    {
        int dayColumn = column - 4, timeColumn = column - 3;
        for (var row = headerRow + 1; row <= lastRow; row++)
        {
            var timeCell = sheet.Cell(row, timeColumn);
            var merged = timeCell.MergedRange();
            // Пара занимает две строки (предмет и преподаватель); берём первую строку объединённой ячейки времени.
            if (merged is not null && merged.FirstRow().RowNumber() != row) continue;
            var time = Text(merged?.FirstCell() ?? timeCell);
            var times = TimePattern().Matches(time);
            if (times.Count < 2) continue;
            var dayCell = sheet.Cell(row, dayColumn);
            var day = ParseDay(Text(dayCell.MergedRange()?.FirstCell() ?? dayCell));
            if (day is not DayOfWeek weekday) continue;
            var start = TimeOnly.ParseExact(times[0].Value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture);
            var end = TimeOnly.ParseExact(times[1].Value, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture);
            var subjectCell = sheet.Cell(row, column);
            var subjectText = Text(subjectCell);
            if (subjectText.Length == 0) continue;
            var teacherRow = merged?.LastRow().RowNumber() is int last && last > row ? last : row + 1;
            var entries = SplitEntries(subjectText);
            var teacherText = Text(sheet.Cell(teacherRow, column));
            var teacherLines = teacherText.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var teachers = (teacherLines.Count == entries.Count ? teacherLines : SplitEntries(teacherText)).Select(TeacherName).ToList();
            for (var i = 0; i < entries.Count; i++)
            {
                var teacher = teachers.Count == 0 ? Vacancy : teachers[Math.Min(i, teachers.Count - 1)];
                if (!TryParseEntry(entries[i], out var subject, out var room, out var weeks))
                {
                    warnings.Add($"{sheet.Name}, {group}, {DayName(weekday)} {start:HH\\:mm}: не удалось разобрать «{entries[i]}».");
                    continue;
                }
                foreach (var (from, to, once) in weeks)
                    lessons.Add(new ParsedLesson(sheet.Name, group, weekday, start, end, subject, room, teacher, from, to, once));
            }
        }
    }

    /// Строка ячейки может содержать несколько записей: по строкам, через два и более пробела или перед новым «[ТИП]».
    public static List<string> SplitEntries(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var chunks = EntrySeparator().Split(line).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            for (var i = 0; i < chunks.Count; i++)
            {
                // Двойной пробел внутри текста («экономики и  предпринимательской») — не новая запись: продолжение начинается со строчной буквы.
                if (i > 0 && (char.IsLower(chunks[i][0]) || chunks[i][0] == ')')) result[^1] += " " + chunks[i];
                else result.Add(chunks[i]);
            }
        }
        return result;
    }

    public static bool TryParseEntry(string entry, out string subject, out string? room, out List<(int From, int To, bool Once)> weeks)
    {
        subject = ""; room = null; weeks = [];
        var tag = TagPattern().Match(entry);
        var kind = tag.Success ? tag.Groups[1].Value.Trim().ToUpperInvariant() : "";
        var rest = (tag.Success ? entry[tag.Length..].Trim() : entry.Trim()).TrimEnd(',', ' ', '\t');
        var weeksMatch = WeeksPattern().Match(rest);
        if (!weeksMatch.Success) weeksMatch = TrailingWeeksPattern().Match(rest);
        if (!weeksMatch.Success) return false;
        weeks = ParseWeeks(weeksMatch.Groups["weeks"].Value);
        if (weeks.Count == 0) return false;
        var head = rest[..weeksMatch.Index].Trim().TrimEnd('·', '-', ',', ' ');
        var parts = head.Split('·').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (parts.Count == 1 && DashRoom().Match(parts[0]) is { Success: true } dashed)
            parts = [dashed.Groups["subject"].Value.Trim(), dashed.Groups["room"].Value.Trim()];
        parts.RemoveAll(x => x.StartsWith("с ", StringComparison.OrdinalIgnoreCase) && GroupName().IsMatch(x[2..].Trim()));
        if (kind == "СРОП" && parts.Count > 0 && parts[0].Equals("СРОП", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
        if (parts.Count == 0) return false;
        subject = Regex.Replace(parts[0], @"\s+", " ");
        room = parts.Count > 1 ? Room(parts[1]) : null;
        return subject.Length > 0;
    }

    public static List<(int From, int To, bool Once)> ParseWeeks(string text)
    {
        var result = new List<(int, int, bool)>();
        foreach (var raw in text.Split(','))
        {
            var token = raw.Trim().Replace('–', '-').Replace('—', '-');
            if (token.Length == 0) continue;
            var once = token.Contains("разово", StringComparison.OrdinalIgnoreCase);
            var numbers = new List<int>();
            foreach (Match match in Regex.Matches(token, @"\d+"))
            {
                if (!int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    numbers.Clear();
                    break;
                }
                numbers.Add(n);
            }
            if (numbers.Count == 0 || numbers.Any(x => x is < 1 or > 60)) continue;
            var from = numbers[0];
            var to = numbers.Count > 1 && token.Contains('-') ? numbers[1] : from;
            if (to < from) (from, to) = (to, from);
            result.Add((from, to, once || from == to));
        }
        return result;
    }

    public static string TeacherName(string line)
    {
        var name = line.Split('·')[0].Trim();
        if (name.Length == 0 || name.StartsWith("вакансия", StringComparison.OrdinalIgnoreCase)) return Vacancy;
        name = TeacherRoomSuffix().Replace(name, "");
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    /// «Ниязова» и «Ниязова М.Е.» — один человек, если полная запись с этой фамилией единственная.
    private static List<ParsedLesson> UnifyTeachers(List<ParsedLesson> lessons)
    {
        var full = lessons.Select(x => x.Teacher).Where(x => x != Vacancy && x.Contains(' ')).Distinct().ToList();
        return lessons.Select(lesson =>
        {
            if (lesson.Teacher == Vacancy || lesson.Teacher.Contains(' ')) return lesson;
            var matches = full.Where(x => x.Split(' ')[0].Equals(lesson.Teacher, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 ? lesson with { Teacher = matches[0] } : lesson;
        }).ToList();
    }

    private static string? Room(string value)
    {
        var room = value.Trim().TrimEnd('-').Trim();
        return room.Length == 0 || room.StartsWith("уточн", StringComparison.OrdinalIgnoreCase) ? null : room;
    }

    private static int? FindHeaderRow(IXLWorksheet sheet, int lastRow, int lastColumn)
    {
        for (var row = 1; row <= lastRow; row++)
            for (var column = 5; column <= lastColumn; column++)
                if (GroupName().IsMatch(Text(sheet.Cell(row, column))) &&
                    Text(sheet.Cell(row, column - 4)).Contains("Дни", StringComparison.OrdinalIgnoreCase))
                    return row;
        return null;
    }

    private static string? FindFileTeacher(IXLWorksheet sheet, int lastRow, int lastColumn)
    {
        for (var row = 1; row <= lastRow; row++)
            for (var column = 1; column <= Math.Min(lastColumn, 5); column++)
            {
                var match = FileTeacherPattern().Match(Text(sheet.Cell(row, column)));
                if (match.Success) return TeacherName(match.Groups["name"].Value);
            }
        return null;
    }

    private static DateOnly? FindSemesterStart(IXLWorksheet sheet, int lastRow, int lastColumn)
    {
        for (var row = 1; row <= lastRow; row++)
            for (var column = 1; column <= Math.Min(lastColumn, 5); column++)
                if (SemesterPattern().Match(Text(sheet.Cell(row, column))) is { Success: true } match &&
                    DateOnly.TryParseExact(match.Groups[1].Value, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return null;
    }

    private static DayOfWeek? ParseDay(string text)
    {
        var value = text.ToLowerInvariant();
        if (value.Contains("понедельник")) return DayOfWeek.Monday;
        if (value.Contains("вторник")) return DayOfWeek.Tuesday;
        if (value.Contains("среда")) return DayOfWeek.Wednesday;
        if (value.Contains("четверг")) return DayOfWeek.Thursday;
        if (value.Contains("пятница")) return DayOfWeek.Friday;
        if (value.Contains("суббота")) return DayOfWeek.Saturday;
        if (value.Contains("воскресенье")) return DayOfWeek.Sunday;
        return null;
    }

    public static string DayName(DayOfWeek day) => CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetAbbreviatedDayName(day);

    private static string Text(IXLCell cell) => cell.GetFormattedString().Replace("\r", "").Trim();

    [GeneratedRegex(@"^\d{2}-[А-ЯЁA-Z]{1,4}-\d{2,4}-\S+$")]
    private static partial Regex GroupName();

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^\[([^\]]+)\]")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"нед\.?\s*,?\s*(?<weeks>\d[\d\s,\-–—()а-яё]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex WeeksPattern();

    [GeneratedRegex(@"\s-\s*(?<weeks>\d+(?:\s*-\s*\d+)?(?:\s*,\s*\d+(?:\s*-\s*\d+)?)*)$")]
    private static partial Regex TrailingWeeksPattern();

    [GeneratedRegex(@"^(?<subject>.+?)\s+-\s*(?<room>[^\s-][^-]*)$")]
    private static partial Regex DashRoom();

    [GeneratedRegex(@"\s{2,}|(?<=\S)\s+(?=\[[А-ЯЁA-Z]+\])")]
    private static partial Regex EntrySeparator();

    [GeneratedRegex(@"\s*-\s*(?:уточн\S*|[A-Za-zА-ЯЁа-яё]?\d+[A-Za-zА-ЯЁа-яё]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TeacherRoomSuffix();

    [GeneratedRegex(@"^\s*Преподаватель\s*:\s*(?<name>.+?)\s*(?:·.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex FileTeacherPattern();

    [GeneratedRegex(@"недели\s*\d+\s*-\s*\d+\s*,\s*(\d{2}\.\d{2}\.\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex SemesterPattern();
}
