using System.Reflection;
using System.Text;
using BiStudent.Server.Data;
using BiStudent.Server.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BiStudent.Tests;

public sealed class ScheduleReplacementTests
{
    [Fact]
    public async Task Csv_RejectsRepeatAndUpdatesRoom()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("П-21");
        const string header = "День;Начало;Конец;Предмет;Группа;Аудитория;Неделя\r\n";
        const string lesson = "Пн;08:30;09:50;Математика;П-21;101;Каждая\r\n";
        using var first = new MemoryStream(Encoding.UTF8.GetBytes(header + lesson));
        Assert.Equal(1, await test.Schedule.ImportCsvAsync(teacher.Id, first));
        var original = await test.Db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.GroupId == group.Id);

        using var repeat = new MemoryStream(Encoding.UTF8.GetBytes(header + lesson));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => test.Schedule.ImportCsvAsync(teacher.Id, repeat));
        Assert.Equal("Это расписание уже загружено, изменений нет.", error.Message);
        Assert.Equal(1, await test.Db.ScheduleSlots.CountAsync());

        using var changed = new MemoryStream(Encoding.UTF8.GetBytes(
            header + "Пн;08:30;09:50;Математика;П-21;102;Каждая\r\n"));
        Assert.Equal(1, await test.Schedule.ImportCsvAsync(teacher.Id, changed));
        var updated = await test.Db.ScheduleSlots.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal("102", updated.Room);
    }

    [Fact]
    public async Task Csv_RecognizesArchivedSlotAndRemovesFutureCancellation()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("П-21", "Иван Иванов");
        const string header = "День;Начало;Конец;Предмет;Группа;Аудитория;Неделя\r\n";
        const string firstLesson = "Пн;08:30;09:50;Математика;П-21;101;Каждая\r\n";
        const string secondLesson = "Пн;10:00;11:20;Физика;П-21;102;Каждая\r\n";
        using (var initial = new MemoryStream(Encoding.UTF8.GetBytes(
            header + firstLesson + secondLesson)))
            Assert.Equal(2, await test.Schedule.ImportCsvAsync(teacher.Id, initial));
        var archived = await test.Db.ScheduleSlots.SingleAsync(x => x.Subject == "Физика");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var marked = today.AddDays(-7);
        test.Db.AttendanceMarks.Add(new AttendanceMark
        {
            SlotId = archived.Id, StudentId = group.Students.Single().Id, Date = marked
        });
        test.Db.SlotCancellations.Add(new SlotCancellation
        {
            SlotId = archived.Id, Date = today.AddDays(7), Reason = "Перенос"
        });
        await test.Db.SaveChangesAsync();

        using (var changed = new MemoryStream(Encoding.UTF8.GetBytes(header + firstLesson)))
            Assert.Equal(0, await test.Schedule.ImportCsvAsync(teacher.Id, changed));
        Assert.Equal(marked, archived.ValidTo);
        Assert.False(await test.Db.SlotCancellations.AnyAsync(x => x.SlotId == archived.Id));
        Assert.True(await test.Db.AttendanceMarks.AnyAsync(x => x.SlotId == archived.Id));

        using var repeat = new MemoryStream(Encoding.UTF8.GetBytes(header + firstLesson));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => test.Schedule.ImportCsvAsync(teacher.Id, repeat));
        Assert.Equal("Это расписание уже загружено, изменений нет.", error.Message);
    }

    [Fact]
    public async Task Csv_RepeatKeepsMatchedSlotWhenArchivedStartsEarlier()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("П-21", "Иван Иванов");
        var studentId = group.Students.Single().Id;
        var first = test.AddSlot(teacher, group, DayOfWeek.Monday,
            new TimeOnly(8, 30), new TimeOnly(9, 50), subject: "Алгебра");
        var second = test.AddSlot(teacher, group, DayOfWeek.Monday,
            new TimeOnly(8, 30), new TimeOnly(9, 50),
            validFrom: new DateOnly(2026, 10, 1), subject: "Алгебра");
        test.Db.AttendanceMarks.AddRange(
            new AttendanceMark
            {
                SlotId = first.Id, StudentId = studentId, Date = new DateOnly(2026, 9, 7)
            },
            new AttendanceMark
            {
                SlotId = second.Id, StudentId = studentId, Date = new DateOnly(2026, 10, 5)
            });
        await test.Db.SaveChangesAsync();
        test.Configuration["Semester:Start"] = "2026-10-12";
        const string csv = "День;Начало;Конец;Предмет;Группа;Аудитория;Неделя\r\n" +
            "Пн;08:30;09:50;Алгебра;П-21;;Каждая\r\n";

        using (var changed = new MemoryStream(Encoding.UTF8.GetBytes(csv)))
            Assert.Equal(1, await test.Schedule.ImportCsvAsync(teacher.Id, changed));
        Assert.Equal(new DateOnly(2026, 10, 5), second.ValidTo);

        using var repeat = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => test.Schedule.ImportCsvAsync(teacher.Id, repeat));
        Assert.Equal("Это расписание уже загружено, изменений нет.", error.Message);
        var active = await test.Db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.ValidTo == null);
        Assert.Equal(first.Id, active.Id);
        Assert.Equal(new DateOnly(2026, 10, 12), active.ValidFrom);
        Assert.Equal(2, await test.Db.ScheduleSlots.CountAsync());
    }

    [Fact]
    public async Task Csv_IgnoresArchivedSlotOfUnchangedGroup()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var firstGroup = test.AddGroup("G1", "Иван Иванов");
        var secondGroup = test.AddGroup("G2");
        test.AddSlot(teacher, firstGroup, DayOfWeek.Monday,
            new TimeOnly(8, 30), new TimeOnly(9, 50), subject: "Математика");
        var archived = test.AddSlot(teacher, firstGroup, DayOfWeek.Monday,
            new TimeOnly(10, 0), new TimeOnly(11, 20),
            validTo: new DateOnly(2026, 9, 7), subject: "Физика");
        test.Db.AttendanceMarks.Add(new AttendanceMark
        {
            SlotId = archived.Id, StudentId = firstGroup.Students.Single().Id,
            Date = new DateOnly(2026, 9, 7)
        });
        await test.Db.SaveChangesAsync();
        const string csv = "День;Начало;Конец;Предмет;Группа;Аудитория;Неделя\r\n" +
            "Пн;08:30;09:50;Математика;G1;;Каждая\r\n" +
            "Пн;10:00;11:20;Химия;G2;;Каждая\r\n";

        using var import = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        Assert.Equal(1, await test.Schedule.ImportCsvAsync(teacher.Id, import));
        Assert.True(await test.Db.ScheduleSlots.AnyAsync(x =>
            x.GroupId == secondGroup.Id && x.Subject == "Химия"));
        Assert.True(await test.Db.AttendanceMarks.AnyAsync(x => x.SlotId == archived.Id));
    }

    [Fact]
    public async Task Csv_ArchivesSlotWhoseStartMovedIntoFuture()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("П-21", "Иван Иванов");
        test.AddSlot(teacher, group, DayOfWeek.Monday,
            new TimeOnly(8, 30), new TimeOnly(9, 50), subject: "Математика");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var marked = today.AddDays(-7);
        var archived = test.AddSlot(teacher, group, DayOfWeek.Monday,
            new TimeOnly(10, 0), new TimeOnly(11, 20),
            validFrom: today.AddDays(7), subject: "Физика");
        test.Db.AttendanceMarks.Add(new AttendanceMark
        {
            SlotId = archived.Id, StudentId = group.Students.Single().Id, Date = marked
        });
        await test.Db.SaveChangesAsync();
        const string csv = "День;Начало;Конец;Предмет;Группа;Аудитория;Неделя\r\n" +
            "Пн;08:30;09:50;Математика;П-21;;Каждая\r\n";

        using var import = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        Assert.Equal(0, await test.Schedule.ImportCsvAsync(teacher.Id, import));
        Assert.Equal(marked, archived.ValidFrom);
        Assert.Equal(marked, archived.ValidTo);
        Assert.True(archived.ValidFrom <= archived.ValidTo && archived.ValidTo <= today);
        Assert.True(await test.Db.AttendanceMarks.AnyAsync(x => x.SlotId == archived.Id));
    }

    [Fact]
    public async Task NaturalKeys_FindSlotsWithNullFields()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("П-21", "Иван Иванов");
        var slot = test.AddSlot(teacher, group);
        var model = new SyncModel(test.Db);
        var students = model.Tables.Single(x => x.ClrType == typeof(Student));
        var slots = model.Tables.Single(x => x.ClrType == typeof(ScheduleSlot));
        Assert.Contains(students.NaturalKeys, key =>
            key.Properties.Select(x => x.Name).SequenceEqual([nameof(Student.GroupId), nameof(Student.FullName)]));
        var key = Assert.Single(slots.NaturalKeys);
        Assert.Equal(
            [nameof(ScheduleSlot.TeacherId), nameof(ScheduleSlot.GroupId), nameof(ScheduleSlot.Subject),
                nameof(ScheduleSlot.DayOfWeek), nameof(ScheduleSlot.Start), nameof(ScheduleSlot.End),
                nameof(ScheduleSlot.Parity), nameof(ScheduleSlot.ValidFrom), nameof(ScheduleSlot.ValidTo),
                nameof(ScheduleSlot.OneOffDate), nameof(ScheduleSlot.Room)],
            key.Properties.Select(x => x.Name));
        var values = key.Properties.ToDictionary(x => x.Name, x => x.PropertyInfo?.GetValue(slot));
        Assert.Null(values[nameof(ScheduleSlot.ValidTo)]);
        Assert.Null(values[nameof(ScheduleSlot.OneOffDate)]);
        Assert.Null(values[nameof(ScheduleSlot.Room)]);
        var method = typeof(SyncService).GetMethod("FindKeyCoreAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(typeof(ScheduleSlot));
        var found = await (Task<object?>)method.Invoke(null, [test.Db, key, values, CancellationToken.None])!;
        Assert.Equal(slot.Id, Assert.IsType<ScheduleSlot>(found).Id);
    }
}
