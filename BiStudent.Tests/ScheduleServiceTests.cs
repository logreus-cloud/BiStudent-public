using System.Text;
using BiStudent.Contracts;
using BiStudent.Server.Services;
using Xunit;

namespace BiStudent.Tests;

public sealed class ScheduleServiceTests
{
    [Fact]
    public void WeekParityOf_AlternatesFromSemesterStart()
    {
        using var test = new TestDb();
        Assert.Equal(WeekParity.Odd, test.Schedule.WeekParityOf(new DateOnly(2026, 9, 1)));
        Assert.Equal(WeekParity.Even, test.Schedule.WeekParityOf(new DateOnly(2026, 9, 8)));
    }

    [Fact]
    public async Task ForDayAsync_AppliesParityDatesOneOffAndCancellation()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        var first = new DateOnly(2026, 9, 1);
        var odd = test.AddSlot(teacher, group, parity: WeekParity.Odd, subject: "Odd");
        test.AddSlot(teacher, group, parity: WeekParity.Even, subject: "Even");
        test.AddSlot(teacher, group, validFrom: first.AddDays(7), validTo: first.AddDays(7), subject: "Window");
        test.AddSlot(teacher, group, day: DayOfWeek.Sunday, oneOffDate: first, subject: "OneOff");
        await test.Schedule.CancelAsync(odd.Id, first, "Cancelled");
        var weekOne = await test.Schedule.ForDayAsync(teacher.Id, first);
        Assert.Contains(weekOne, x => x.Slot.Id == odd.Id && x.Cancelled);
        Assert.Contains(weekOne, x => x.Slot.Subject == "OneOff");
        Assert.DoesNotContain(weekOne, x => x.Slot.Subject == "Even");
        Assert.DoesNotContain(weekOne, x => x.Slot.Subject == "Window");
        var weekTwo = await test.Schedule.ForDayAsync(teacher.Id, first.AddDays(7));
        Assert.Contains(weekTwo, x => x.Slot.Subject == "Even");
        Assert.Contains(weekTwo, x => x.Slot.Subject == "Window");
        Assert.DoesNotContain(weekTwo, x => x.Slot.Subject == "Odd");
        Assert.DoesNotContain(weekTwo, x => x.Slot.Subject == "OneOff");
        Assert.DoesNotContain(await test.Schedule.ForDayAsync(teacher.Id, first.AddDays(14)),
            x => x.Slot.Subject == "Window");
    }

    [Fact]
    public async Task CurrentAsync_ExcludesEndAndAmbiguousOverlap()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        var slot = test.AddSlot(teacher, group);
        var now = new DateTime(2026, 9, 1, 9, 30, 0);
        Assert.Equal(slot.Id, (await test.Schedule.CurrentAsync(teacher.Id, now))?.Slot.Id);
        Assert.Null(await test.Schedule.CurrentAsync(teacher.Id, new DateTime(2026, 9, 1, 10, 0, 0)));
        test.AddSlot(teacher, group, start: new TimeOnly(9, 15), end: new TimeOnly(9, 45), subject: "Overlap");
        Assert.Null(await test.Schedule.CurrentAsync(teacher.Id, now));
    }

    [Fact]
    public async Task AddSlotAsync_RejectsOverlapAndInvalidTimesButAllowsOppositeParity()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        await test.Schedule.AddSlotAsync(test.NewSlot(teacher, group, parity: WeekParity.Odd));
        await Assert.ThrowsAsync<ScheduleConflictException>(() =>
            test.Schedule.AddSlotAsync(test.NewSlot(teacher, group, parity: WeekParity.Odd)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Schedule.AddSlotAsync(test.NewSlot(teacher, group, start: new TimeOnly(10, 0),
                end: new TimeOnly(10, 0), parity: WeekParity.Even)));
        var other = await test.Schedule.AddSlotAsync(test.NewSlot(teacher, group, parity: WeekParity.Even));
        Assert.Equal(WeekParity.Even, other.Parity);
    }

    [Fact]
    public async Task ImportCsvAsync_AddsSlotsAndGroup()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("День;Начало;Конец;Предмет;Группа;Аудитория;Чётность\n1;09:00;10:00;Алгебра;ИВТ-1;305;каждая\n2;11:00;12:00;Физика;ИВТ-2;;чёт\n"));
        Assert.Equal(2, await test.Schedule.ImportCsvAsync(teacher.Id, csv));
        Assert.Equal(2, test.Db.ScheduleSlots.Count());
        Assert.Contains(test.Db.Groups, x => x.Name == "ИВТ-2");
    }

    [Fact]
    public async Task ImportCsvAsync_ReportsAllBadLineNumbers()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("День;Начало;Конец;Предмет;Группа;Аудитория;Чётность\nbad;09:00;10:00;Алгебра;ИВТ-1;305;каждая\n2;09:00\n"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Schedule.ImportCsvAsync(teacher.Id, csv));
        Assert.Contains("Строка 2", error.Message);
        Assert.Contains("Строка 3", error.Message);
    }
}
