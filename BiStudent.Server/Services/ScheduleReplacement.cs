using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

internal sealed record ScheduleReplacementResult(
    int Added, int Updated, int Removed,
    Dictionary<ScheduleSlot, ScheduleSlot> Slots, HashSet<int> ArchivedIds);

internal static class ScheduleReplacement
{
    internal const string AlreadyLoadedMessage = "Это расписание уже загружено, изменений нет.";

    internal static async Task<(bool Unchanged, HashSet<int> ArchivedIds)> IsUnchangedAsync(
        AppDbContext db, IReadOnlyList<ScheduleSlot> old, IReadOnlyList<ScheduleSlot> incoming, DateOnly today)
    {
        var (pairs, unmatched) = Plan(old, incoming);
        var unchanged = pairs.All(pair => pair.Old is not null && FullKey(pair.Old) == FullKey(pair.New));
        var archived = new HashSet<int>();
        foreach (var slot in unmatched)
        {
            var (dates, future) = await JournalAsync(db, slot.Id, today);
            if (dates.Count == 0)
            {
                unchanged = false;
                continue;
            }
            archived.Add(slot.Id);
            var (validFrom, validTo) = ArchivedRange(slot, dates, today);
            if (future.Count > 0 || slot.ValidFrom != validFrom || slot.ValidTo != validTo)
                unchanged = false;
        }
        return (unchanged, archived);
    }

    internal static async Task<ScheduleReplacementResult> ReplaceAsync(
        AppDbContext db, IReadOnlyList<ScheduleSlot> old, IReadOnlyList<ScheduleSlot> incoming, DateOnly today)
    {
        var (pairs, unmatched) = Plan(old, incoming);
        var slots = new Dictionary<ScheduleSlot, ScheduleSlot>();
        var archived = new HashSet<int>();
        var added = 0;
        var updated = 0;
        var removed = 0;
        foreach (var (slot, current) in pairs)
        {
            if (current is not null)
            {
                if (FullKey(current) != FullKey(slot))
                {
                    current.Room = slot.Room;
                    current.ValidFrom = slot.ValidFrom;
                    current.ValidTo = slot.ValidTo;
                    updated++;
                }
                slots.Add(slot, current);
            }
            else
            {
                db.ScheduleSlots.Add(slot);
                slots.Add(slot, slot);
                added++;
            }
        }
        foreach (var slot in unmatched)
        {
            var (dates, future) = await JournalAsync(db, slot.Id, today);
            db.SlotCancellations.RemoveRange(future);
            if (dates.Count == 0)
            {
                db.ScheduleSlots.Remove(slot);
                removed++;
            }
            else
            {
                var (validFrom, validTo) = ArchivedRange(slot, dates, today);
                slot.ValidFrom = validFrom;
                slot.ValidTo = validTo;
                archived.Add(slot.Id);
            }
        }
        return new ScheduleReplacementResult(added, updated, removed, slots, archived);
    }

    private static (List<(ScheduleSlot New, ScheduleSlot? Old)> Pairs, List<ScheduleSlot> Unmatched) Plan(
        IReadOnlyList<ScheduleSlot> old, IReadOnlyList<ScheduleSlot> incoming)
    {
        var exact = old.GroupBy(FullKey).ToDictionary(group => group.Key,
            group => new Queue<ScheduleSlot>(group.OrderBy(slot => slot.Id)));
        var remaining = old.ToHashSet();
        var matches = new Dictionary<ScheduleSlot, ScheduleSlot>();
        foreach (var slot in incoming)
            if (exact.TryGetValue(FullKey(slot), out var queue) && queue.Count > 0)
            {
                var current = queue.Dequeue();
                matches.Add(slot, current);
                remaining.Remove(current);
            }

        var queues = remaining.GroupBy(Identity).ToDictionary(group => group.Key,
            group => new Queue<ScheduleSlot>(group.OrderBy(slot => slot.ValidFrom).ThenBy(slot => slot.Id)));
        foreach (var group in incoming.Where(slot => !matches.ContainsKey(slot)).GroupBy(Identity))
            foreach (var slot in group.OrderBy(slot => slot.ValidFrom))
            {
                if (queues.TryGetValue(group.Key, out var queue) && queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    matches.Add(slot, current);
                    remaining.Remove(current);
                }
            }
        return (incoming.Select(slot => (New: slot, Old: matches.GetValueOrDefault(slot))).ToList(),
            remaining.ToList());
    }

    private static (DateOnly ValidFrom, DateOnly? ValidTo) ArchivedRange(
        ScheduleSlot slot, List<DateOnly> dates, DateOnly today)
    {
        if (slot.OneOffDate is not null) return (slot.ValidFrom, slot.ValidTo);
        var last = dates.Max();
        if (last > today) last = today;
        if (slot.ValidFrom > last)
        {
            var first = dates.Min();
            return (first < last ? first : last, last);
        }
        return (slot.ValidFrom, slot.ValidTo is null || slot.ValidTo > last ? last : slot.ValidTo);
    }

    private static async Task<(List<DateOnly> Dates, List<SlotCancellation> Future)> JournalAsync(
        AppDbContext db, int slotId, DateOnly today)
    {
        var cancellations = await db.SlotCancellations.Where(cancel => cancel.SlotId == slotId)
            .ToListAsync();
        var dates = await db.AttendanceMarks.Where(mark => mark.SlotId == slotId)
            .Select(mark => mark.Date).ToListAsync();
        dates.AddRange(await db.LessonGrades.Where(grade => grade.SlotId == slotId)
            .Select(grade => grade.Date).ToListAsync());
        dates.AddRange(cancellations.Where(cancel => cancel.Date <= today).Select(cancel => cancel.Date));
        return (dates, cancellations.Where(cancel => cancel.Date > today).ToList());
    }

    private static (int, int, string, DayOfWeek, TimeOnly, TimeOnly, WeekParity, DateOnly, DateOnly?, DateOnly?, string?) FullKey(ScheduleSlot slot) =>
        (slot.TeacherId, slot.GroupId, slot.Subject, slot.DayOfWeek, slot.Start, slot.End,
            slot.Parity, slot.ValidFrom, slot.ValidTo, slot.OneOffDate, slot.Room);

    private static (int, int, string, DayOfWeek, TimeOnly, TimeOnly, WeekParity, DateOnly?) Identity(ScheduleSlot slot) =>
        (slot.TeacherId, slot.GroupId, slot.Subject, slot.DayOfWeek, slot.Start, slot.End, slot.Parity, slot.OneOffDate);
}
