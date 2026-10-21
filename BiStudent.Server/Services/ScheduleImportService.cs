using System.Globalization;
using System.Text;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

/// Импорт расписания колледжа (см. CollegeScheduleParser) в базу: группы, учётные записи преподавателей и занятия.
/// Повторный импорт заменяет расписание выбранных групп, сохраняя занятия с данными журнала.
public sealed class ScheduleImportService(AppDbContext db, AccountService accounts, IConfiguration configuration)
{
    public async Task<ScheduleImportPreviewDto> PreviewAsync(Stream xlsx)
    {
        var parsed = Parse(xlsx);
        var storedGroups = await db.Groups.AsNoTracking().ToListAsync();
        var groupIds = storedGroups.ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var teacherNames = parsed.Lessons.Select(x => x.Teacher).Distinct().ToList();
        var storedTeachers = await db.Teachers.AsNoTracking().Where(x => teacherNames.Contains(x.FullName)).ToListAsync();
        var teacherIds = storedTeachers.GroupBy(x => x.FullName).ToDictionary(x => x.Key, x => x.First().Id);
        var nextId = 0;
        foreach (var name in teacherNames)
            if (!teacherIds.ContainsKey(name)) teacherIds[name] = --nextId;
        var slots = await db.ScheduleSlots.AsNoTracking()
            .Where(x => groupIds.Values.Contains(x.GroupId)).ToListAsync();
        var groups = new List<ScheduleImportGroupDto>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var group in parsed.Groups)
        {
            var exists = groupIds.TryGetValue(group.Group, out var groupId);
            var lessons = parsed.Lessons.Where(x => x.Group == group.Group).ToList();
            var incoming = exists ? lessons.Select(x => ToSlot(x, groupId, teacherIds[x.Teacher], parsed.SemesterStart)).ToList() : [];
            var old = slots.Where(x => x.GroupId == groupId).ToList();
            groups.Add(new ScheduleImportGroupDto(group.Sheet, group.Group, lessons.Count, exists,
                exists && (await ScheduleReplacement.IsUnchangedAsync(db, old, incoming, today)).Unchanged));
        }
        var teachers = parsed.Lessons.Select(x => x.Teacher).Distinct().Order(StringComparer.CurrentCulture).ToList();
        return new ScheduleImportPreviewDto(parsed.SemesterStart, groups, teachers, parsed.Warnings);
    }

    public async Task<ScheduleImportResultDto> ImportAsync(Stream xlsx, IReadOnlyCollection<string> selectedGroups)
    {
        if (selectedGroups.Count == 0) throw new InvalidOperationException("Выберите хотя бы одну группу.");
        var parsed = Parse(xlsx);
        var selected = selectedGroups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = selected.Where(g => parsed.Groups.All(x => !x.Group.Equals(g, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0) throw new InvalidOperationException($"В файле нет групп: {string.Join(", ", unknown)}.");
        var lessons = parsed.Lessons.Where(x => selected.Contains(x.Group)).ToList();
        var warnings = parsed.Warnings.Where(w => selected.Any(g => w.Contains(g, StringComparison.OrdinalIgnoreCase))).ToList();
        var semester = DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);
        if (Monday(semester) != parsed.SemesterStart)
            warnings.Add($"Начало семестра в файле — {parsed.SemesterStart:dd.MM.yyyy}, в настройках сервера — {semester:dd.MM.yyyy}. Недели считаются по файлу.");

        await using var transaction = await db.Database.BeginTransactionAsync();
        var groupsCreated = 0;
        var groupIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupNames = parsed.Groups.Select(x => x.Group).Where(selected.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var name in groupNames)
        {
            var group = await db.Groups.SingleOrDefaultAsync(x => x.Name == name);
            if (group is null) { group = new Group { Name = name }; db.Groups.Add(group); await db.SaveChangesAsync(); groupsCreated++; }
            groupIds[name] = group.Id;
        }

        var teachers = new List<ImportedTeacherDto>();
        var teacherIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in lessons.Select(x => x.Teacher).Distinct())
        {
            var vacancy = name == CollegeScheduleParser.Vacancy;
            var existing = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(x => x.FullName == name);
            if (existing is not null)
            {
                teacherIds[name] = existing.Id;
                teachers.Add(new ImportedTeacherDto(existing.FullName, existing.Login, null, false, existing.IsActive));
                continue;
            }
            var login = await FreeLoginAsync(vacancy ? "vacancy" : Login(name));
            var created = await accounts.CreateAsync(new SaveTeacherRequest(login, name, false, !vacancy));
            teacherIds[name] = (await db.Teachers.AsNoTracking().SingleAsync(x => x.Login == login)).Id;
            teachers.Add(new ImportedTeacherDto(name, login, vacancy ? null : created.Password, true, !vacancy));
        }

        var existingSlots = await db.ScheduleSlots
            .Where(x => groupIds.Values.Contains(x.GroupId)).ToListAsync();
        var added = 0;
        var updated = 0;
        var removed = 0;
        var unchanged = new List<string>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var name in groupNames)
        {
            var old = existingSlots.Where(x => x.GroupId == groupIds[name]).ToList();
            var incoming = lessons.Where(x => x.Group.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(x => ToSlot(x, groupIds[name], teacherIds[x.Teacher], parsed.SemesterStart)).ToList();
            if ((await ScheduleReplacement.IsUnchangedAsync(db, old, incoming, today)).Unchanged)
            {
                unchanged.Add(name);
                continue;
            }
            var replacement = await ScheduleReplacement.ReplaceAsync(db, old, incoming, today);
            added += replacement.Added;
            updated += replacement.Updated;
            removed += replacement.Removed;
        }
        if (unchanged.Count == groupNames.Count)
            throw new InvalidOperationException(ScheduleReplacement.AlreadyLoadedMessage);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return new ScheduleImportResultDto(added, 0, groupsCreated, teachers, warnings, updated, removed, unchanged);
    }

    private ParsedSchedule Parse(Stream xlsx)
    {
        var fallback = DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);
        try { return CollegeScheduleParser.Parse(xlsx, Monday(fallback)); }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось прочитать файл расписания. Нужен файл Excel (.xlsx) в формате расписания колледжа.", ex);
        }
    }

    private static ScheduleSlot ToSlot(ParsedLesson lesson, int groupId, int teacherId, DateOnly semesterStart)
    {
        var monday = semesterStart.AddDays(7 * (lesson.FromWeek - 1));
        var offset = ((int)lesson.Day + 6) % 7;
        var slot = new ScheduleSlot
        {
            TeacherId = teacherId, GroupId = groupId,
            Subject = lesson.Subject.Length > 200 ? lesson.Subject[..200] : lesson.Subject, Room = lesson.Room,
            DayOfWeek = lesson.Day, Start = lesson.Start, End = lesson.End, Parity = WeekParity.Every
        };
        if (lesson.Once)
        {
            var date = monday.AddDays(offset);
            slot.OneOffDate = date;
            slot.ValidFrom = date;
            slot.ValidTo = date;
        }
        else
        {
            slot.ValidFrom = monday;
            slot.ValidTo = semesterStart.AddDays(7 * lesson.ToWeek - 1);
        }
        return slot;
    }

    private static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    internal async Task<string> FreeLoginAsync(string login)
    {
        var candidate = login;
        for (var i = 2; await db.Teachers.AnyAsync(x => x.Login.ToLower() == candidate.ToLower()); i++)
        {
            var suffix = i.ToString(CultureInfo.InvariantCulture);
            candidate = login[..Math.Min(login.Length, 32 - suffix.Length)] + suffix;
        }
        return candidate;
    }

    /// «Ниязова М.Е.» → niyazova.me; «Хамит А.Ү.» → khamit.au; «Халель» → khalel.
    public static string Login(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var surname = Translit(parts[0]);
        var initials = string.Concat(parts.Skip(1).SelectMany(p => p.Split('.', StringSplitOptions.RemoveEmptyEntries)).Select(p => Translit(p[..1])));
        var login = initials.Length > 0 ? $"{surname}.{initials}" : surname;
        login = new string(login.Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').ToArray()).Trim('.');
        if (login.Length < 3) login = (login + "teacher")[..Math.Max(3, login.Length)];
        return login.Length > 32 ? login[..32] : login;
    }

    private static string Translit(string text)
    {
        var map = new Dictionary<char, string>
        {
            ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z",
            ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
            ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
            ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
            ['ә'] = "a", ['ғ'] = "g", ['қ'] = "k", ['ң'] = "n", ['ө'] = "o", ['ұ'] = "u", ['ү'] = "u", ['һ'] = "h", ['і'] = "i"
        };
        var builder = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
            builder.Append(map.TryGetValue(c, out var latin) ? latin : char.IsAsciiLetterOrDigit(c) ? c.ToString() : "");
        return builder.ToString();
    }
}
