using BiStudent.Contracts;
using System.Globalization;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed record LessonOccurrence(ScheduleSlot Slot, DateOnly Date, DateTime StartsAt, DateTime EndsAt, bool Cancelled, string GroupName);

public sealed class ScheduleConflictException(string message, List<ScheduleSlot> conflicts) : InvalidOperationException(message)
{
    public List<ScheduleSlot> Conflicts { get; } = conflicts;
}

public sealed class ScheduleService(AppDbContext db, IConfiguration configuration)
{
    private DateOnly SemesterStart => DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);

    public WeekParity WeekParityOf(DateOnly date)
    {
        var week = (int)Math.Floor((date.DayNumber - SemesterStart.DayNumber) / 7d) + 1;
        return Math.Abs(week % 2) == 1 ? WeekParity.Odd : WeekParity.Even;
    }

    private bool Applies(ScheduleSlot slot, DateOnly date) =>
        date >= slot.ValidFrom && (slot.ValidTo is null || date <= slot.ValidTo) &&
        (slot.OneOffDate is DateOnly once
            ? date == once
            : slot.DayOfWeek == date.DayOfWeek && (slot.Parity == WeekParity.Every || slot.Parity == WeekParityOf(date)));

    public async Task<LessonOccurrence?> OccurrenceAsync(int slotId, DateOnly date)
    {
        var slot = await db.ScheduleSlots.AsNoTracking().Include(x => x.Group).SingleOrDefaultAsync(x => x.Id == slotId);
        if (slot is null || !Applies(slot, date)) return null;
        var cancelled = await db.SlotCancellations.AnyAsync(x => x.SlotId == slotId && x.Date == date);
        return new LessonOccurrence(slot, date, date.ToDateTime(slot.Start), date.ToDateTime(slot.End),
            cancelled, slot.Group.Name);
    }

    public async Task<List<LessonOccurrence>> ForDayAsync(int teacherId, DateOnly date)
    {
        var slots = await db.ScheduleSlots.AsNoTracking().Include(x => x.Group).Where(x => x.TeacherId == teacherId && x.ValidFrom <= date && (x.ValidTo == null || x.ValidTo >= date)).ToListAsync();
        var ids = slots.Select(x => x.Id).ToArray();
        var cancelled = await db.SlotCancellations.AsNoTracking().Where(x => ids.Contains(x.SlotId) && x.Date == date).Select(x => x.SlotId).ToListAsync();
        return slots.Where(x => Applies(x, date)).Select(x => new LessonOccurrence(
            x, date, date.ToDateTime(x.Start), date.ToDateTime(x.End), cancelled.Contains(x.Id), x.Group.Name))
            .OrderBy(x => x.StartsAt).ThenBy(x => x.Slot.Id).ToList();
    }

    public async Task<List<LessonOccurrence>> ForWeekAsync(int teacherId, DateOnly monday)
    {
        var result = new List<LessonOccurrence>();
        for (var day = 0; day < 7; day++) result.AddRange(await ForDayAsync(teacherId, monday.AddDays(day)));
        return result;
    }

    public async Task<LessonOccurrence?> CurrentAsync(int teacherId, DateTime now)
    {
        var active = (await ForDayAsync(teacherId, DateOnly.FromDateTime(now)))
            .Where(x => !x.Cancelled && x.StartsAt <= now && now < x.EndsAt).Take(2).ToList();
        return active.Count == 1 ? active[0] : null;
    }

    private static bool TimesOverlap(ScheduleSlot a, ScheduleSlot b) => a.Start < b.End && b.Start < a.End;

    private bool DatesOverlap(ScheduleSlot a, ScheduleSlot b, HashSet<(int SlotId, DateOnly Date)> cancelled)
    {
        var first = a.ValidFrom > b.ValidFrom ? a.ValidFrom : b.ValidFrom;
        var last = a.ValidTo is null ? b.ValidTo : b.ValidTo is null ? a.ValidTo : a.ValidTo < b.ValidTo ? a.ValidTo : b.ValidTo;
        if (last is DateOnly end && end < first) return false;
        if (a.OneOffDate is DateOnly aDate)
            return (!last.HasValue || aDate <= last) && aDate >= first && Applies(b, aDate) &&
                !cancelled.Contains((a.Id, aDate)) && !cancelled.Contains((b.Id, aDate));
        if (b.OneOffDate is DateOnly bDate)
            return (!last.HasValue || bDate <= last) && bDate >= first && Applies(a, bDate) &&
                !cancelled.Contains((a.Id, bDate)) && !cancelled.Contains((b.Id, bDate));
        for (var i = 0; i < 14 && (!last.HasValue || first.AddDays(i) <= last); i++)
            if (Applies(a, first.AddDays(i)) && Applies(b, first.AddDays(i))) return true;
        return false;
    }

    private async Task ValidateAsync(ScheduleSlot slot, IReadOnlySet<int>? ignored = null)
    {
        if (slot.Start >= slot.End) throw new InvalidOperationException("Начало пары должно быть раньше окончания.");
        if (slot.ValidTo is DateOnly end && end < slot.ValidFrom) throw new InvalidOperationException("Конец периода раньше начала.");
        if (slot.OneOffDate is DateOnly once && (once < slot.ValidFrom || slot.ValidTo is DateOnly to && once > to))
            throw new InvalidOperationException("Разовое занятие вне периода действия.");
        var conflicts = (await ConflictsAsync(slot)).Where(x => ignored is null || !ignored.Contains(x.Id)).ToList();
        if (conflicts.Count > 0)
            throw new ScheduleConflictException("У преподавателя уже есть пара в это время.", conflicts);
    }

    public async Task<ScheduleSlot> AddSlotAsync(ScheduleSlot slot)
    {
        await ValidateAsync(slot);
        db.ScheduleSlots.Add(slot);
        await db.SaveChangesAsync();
        return slot;
    }

    private async Task<List<ScheduleSlot>> ConflictsAsync(ScheduleSlot slot)
    {
        var others = await db.ScheduleSlots.AsNoTracking().Where(x => x.TeacherId == slot.TeacherId && x.Id != slot.Id).ToListAsync();
        var ids = others.Select(x => x.Id).Append(slot.Id).ToArray();
        var cancelled = (await db.SlotCancellations.AsNoTracking().Where(x => ids.Contains(x.SlotId))
            .Select(x => new { x.SlotId, x.Date }).ToListAsync()).Select(x => (x.SlotId, x.Date)).ToHashSet();
        return others.Where(x => TimesOverlap(x, slot) && DatesOverlap(x, slot, cancelled)).ToList();
    }

    public async Task UpdateSlotAsync(ScheduleSlot slot)
    {
        await ValidateAsync(slot);
        var current = await db.ScheduleSlots.FindAsync(slot.Id) ?? throw new KeyNotFoundException("Пара не найдена.");
        current.TeacherId = slot.TeacherId;
        current.GroupId = slot.GroupId;
        current.Subject = slot.Subject;
        current.Room = slot.Room;
        current.DayOfWeek = slot.DayOfWeek;
        current.Start = slot.Start;
        current.End = slot.End;
        current.Parity = slot.Parity;
        current.ValidFrom = slot.ValidFrom;
        current.ValidTo = slot.ValidTo;
        current.OneOffDate = slot.OneOffDate;
        await db.SaveChangesAsync();
    }

    public async Task<ScheduleSlot> ReplaceAndSaveAsync(ScheduleSlot slot, List<int> replaceSlotIds, bool updating)
    {
        var ids = replaceSlotIds.ToHashSet();
        var conflicts = await ConflictsAsync(slot);
        if (ids.Count != replaceSlotIds.Count || ids.Any(id => conflicts.All(x => x.Id != id)))
            throw new InvalidOperationException("Указана пара, которая не конфликтует с новой или не принадлежит вам.");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var yesterday = today.AddDays(-1);
        if (updating && slot.OneOffDate is null && conflicts.Any(x => ids.Contains(x.Id) && x.OneOffDate is null))
            throw new InvalidOperationException("Регулярную пару нельзя заменить при редактировании — создайте новую пару или сначала измените старую.");
        if (!updating && slot.OneOffDate is null && conflicts.Any(x => ids.Contains(x.Id) && x.OneOffDate is null) && slot.ValidFrom < today)
        {
            slot.ValidFrom = today;
            conflicts = await ConflictsAsync(slot);
        }
        await ValidateAsync(slot, ids);
        foreach (var conflict in conflicts.Where(x => ids.Contains(x.Id)))
        {
            var current = await db.ScheduleSlots.FindAsync(conflict.Id) ?? throw new KeyNotFoundException("Пара не найдена.");
            var hasJournal = await db.AttendanceMarks.AnyAsync(x => x.SlotId == current.Id) ||
                await db.LessonGrades.AnyAsync(x => x.SlotId == current.Id);
            if (current.OneOffDate is not null)
            {
                if (hasJournal) throw new InvalidOperationException("Пару с отметками нельзя заменить — сначала перенесите или отмените её.");
                db.ScheduleSlots.Remove(current);
            }
            else if (slot.OneOffDate is DateOnly date)
                db.SlotCancellations.Add(new SlotCancellation { SlotId = current.Id, Date = date, Reason = $"Заменена: {slot.Subject}" });
            else if (current.ValidFrom < today || hasJournal)
            {
                if (current.ValidFrom > yesterday) current.ValidFrom = yesterday;
                current.ValidTo = current.ValidTo is DateOnly end && end < yesterday ? end : yesterday;
            }
            else db.ScheduleSlots.Remove(current);
        }
        await db.SaveChangesAsync();
        if (updating) await UpdateSlotAsync(slot);
        else slot = await AddSlotAsync(slot);
        return slot;
    }

    public async Task DeleteSlotAsync(int slotId)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        db.ScheduleSlots.Remove(slot);
        await db.SaveChangesAsync();
    }

    public async Task CancelAsync(int slotId, DateOnly date, string reason)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        if (!Applies(slot, date)) throw new InvalidOperationException("Пары в указанную дату нет.");
        if (await db.SlotCancellations.AnyAsync(x => x.SlotId == slotId && x.Date == date)) return;
        db.SlotCancellations.Add(new SlotCancellation { SlotId = slotId, Date = date, Reason = reason });
        await db.SaveChangesAsync();
    }

    public async Task RestoreAsync(int slotId, DateOnly date)
    {
        var cancellation = await db.SlotCancellations.SingleOrDefaultAsync(x => x.SlotId == slotId && x.Date == date);
        if (cancellation is null) return;
        var transaction = db.Database.CurrentTransaction;
        var ownsTransaction = transaction is null;
        if (ownsTransaction) transaction = await db.Database.BeginTransactionAsync();
        var savepoint = $"RestoreSlot_{Guid.NewGuid():N}";
        if (!ownsTransaction) await transaction!.CreateSavepointAsync(savepoint);
        try
        {
            db.SlotCancellations.Remove(cancellation);
            await db.SaveChangesAsync();
            var slot = await db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.Id == slotId);
            var occurrence = new ScheduleSlot
            {
                Id = slot.Id, TeacherId = slot.TeacherId, GroupId = slot.GroupId, Subject = slot.Subject,
                DayOfWeek = date.DayOfWeek, Start = slot.Start, End = slot.End, Parity = WeekParity.Every,
                ValidFrom = date, ValidTo = date, OneOffDate = date
            };
            var conflicts = await ConflictsAsync(occurrence);
            if (conflicts.Count > 0)
                throw new ScheduleConflictException("На это время уже стоит другая пара.", conflicts);
            if (ownsTransaction) await transaction!.CommitAsync();
            else await transaction!.ReleaseSavepointAsync(savepoint);
        }
        catch
        {
            if (ownsTransaction) await transaction!.RollbackAsync();
            else await transaction!.RollbackToSavepointAsync(savepoint);
            db.Entry(cancellation).State = EntityState.Detached;
            throw;
        }
        finally
        {
            if (ownsTransaction) await transaction!.DisposeAsync();
        }
    }

    public Task<ScheduleSlot> AddOneOffAsync(ScheduleSlot slot, DateOnly date)
    {
        slot.OneOffDate = date;
        slot.ValidFrom = date;
        slot.ValidTo = date;
        return AddSlotAsync(slot);
    }

    public async Task<int> ImportCsvAsync(int teacherId, Stream csv)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var reader = new StreamReader(csv, leaveOpen: true);
        var errors = new List<string>();
        var parsed = new List<(ScheduleSlot Slot, int Line)>();
        var line = 0;
        while (await reader.ReadLineAsync() is { } raw)
        {
            line++;
            if (line == 1 || string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var parts = raw.Split(raw.Contains(';') ? ';' : ',').Select(x => x.Trim()).ToArray();
                if (parts.Length < 7) throw new FormatException("Ожидаются семь столбцов.");
                var day = parts[0].ToLowerInvariant() switch
                {
                    "пн" or "1" => DayOfWeek.Monday, "вт" or "2" => DayOfWeek.Tuesday,
                    "ср" or "3" => DayOfWeek.Wednesday, "чт" or "4" => DayOfWeek.Thursday,
                    "пт" or "5" => DayOfWeek.Friday, "сб" or "6" => DayOfWeek.Saturday,
                    "вс" or "7" => DayOfWeek.Sunday, _ => throw new FormatException("Неверный день недели.")
                };
                var parity = parts[6].ToLowerInvariant() switch
                {
                    "каждая" => WeekParity.Every, "нечёт" or "нечет" => WeekParity.Odd,
                    "чёт" or "чет" => WeekParity.Even, _ => throw new FormatException("Неверная чётность.")
                };
                var group = await db.Groups.SingleOrDefaultAsync(x => x.Name == parts[4]);
                if (group is null) { group = new Group { Name = parts[4] }; db.Groups.Add(group); await db.SaveChangesAsync(); }
                var slot = new ScheduleSlot {
                    TeacherId = teacherId, GroupId = group.Id, DayOfWeek = day,
                    Start = TimeOnly.Parse(parts[1], CultureInfo.GetCultureInfo("ru-RU")),
                    End = TimeOnly.Parse(parts[2], CultureInfo.GetCultureInfo("ru-RU")),
                    Subject = parts[3], Room = string.IsNullOrWhiteSpace(parts[5]) ? null : parts[5],
                    Parity = parity, ValidFrom = SemesterStart
                };
                if (slot.Start >= slot.End) throw new InvalidOperationException("Начало пары должно быть раньше окончания.");
                parsed.Add((slot, line));
            }
            catch (Exception ex) { errors.Add($"Строка {line}: {ex.Message}"); }
        }
        if (errors.Count > 0)
        {
            await transaction.RollbackAsync();
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
        var groupIds = parsed.Select(x => x.Slot.GroupId).Distinct().ToArray();
        var existing = await db.ScheduleSlots
            .Where(x => x.TeacherId == teacherId && groupIds.Contains(x.GroupId)).ToListAsync();
        var unchanged = 0;
        var added = 0;
        var updated = 0;
        var archived = new HashSet<int>();
        var stored = new Dictionary<ScheduleSlot, ScheduleSlot>();
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var group in parsed.GroupBy(x => x.Slot.GroupId))
        {
            var old = existing.Where(x => x.GroupId == group.Key).ToList();
            var incoming = group.Select(x => x.Slot).ToList();
            var comparison = await ScheduleReplacement.IsUnchangedAsync(db, old, incoming, today);
            if (comparison.Unchanged)
            {
                unchanged++;
                archived.UnionWith(comparison.ArchivedIds);
                continue;
            }
            var replacement = await ScheduleReplacement.ReplaceAsync(db, old, incoming, today);
            added += replacement.Added;
            updated += replacement.Updated;
            archived.UnionWith(replacement.ArchivedIds);
            foreach (var (source, target) in replacement.Slots) stored.Add(source, target);
        }
        if (groupIds.Length > 0 && unchanged == groupIds.Length)
            throw new InvalidOperationException(ScheduleReplacement.AlreadyLoadedMessage);
        await db.SaveChangesAsync();
        foreach (var (slot, sourceLine) in parsed)
        {
            if (!stored.TryGetValue(slot, out var target)) continue;
            try { await ValidateAsync(target, archived); }
            catch (InvalidOperationException ex) { errors.Add($"Строка {sourceLine}: {ex.Message}"); }
        }
        if (errors.Count > 0)
        {
            await transaction.RollbackAsync();
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
        await transaction.CommitAsync();
        return added + updated;
    }
}
