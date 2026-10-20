using BiStudent.Contracts;
using BiStudent.Server.Data;
using Xunit;

namespace BiStudent.Tests;

public sealed class StatsServiceTests
{
    [Fact]
    public async Task DashboardAsync_CountsCameraSavingsReportsAndConsecutiveMisses()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван", "Анна");
        var slot = test.AddSlot(teacher, group, validFrom: new DateOnly(2026, 9, 8));
        var risk = group.Students[0];
        var regular = group.Students[1];
        var first = new DateOnly(2026, 9, 8);
        var second = new DateOnly(2026, 9, 15);
        var third = new DateOnly(2026, 9, 22);
        var fourth = new DateOnly(2026, 9, 29);
        test.Db.AttendanceMarks.AddRange(
            new AttendanceMark { SlotId = slot.Id, Date = first, StudentId = regular.Id, Status = AttendanceStatus.Present, Method = MarkMethod.Manual, MarkedAt = first.ToDateTime(new TimeOnly(9, 5)) },
            new AttendanceMark { SlotId = slot.Id, Date = second, StudentId = regular.Id, Status = AttendanceStatus.Present, Method = MarkMethod.Camera, MarkedAt = second.ToDateTime(new TimeOnly(9, 5)) },
            new AttendanceMark { SlotId = slot.Id, Date = third, StudentId = regular.Id, Status = AttendanceStatus.Present, Method = MarkMethod.Manual, MarkedAt = third.ToDateTime(new TimeOnly(9, 5)) },
            new AttendanceMark { SlotId = slot.Id, Date = fourth, StudentId = regular.Id, Status = AttendanceStatus.Present, Method = MarkMethod.Camera, MarkedAt = fourth.ToDateTime(new TimeOnly(9, 5)) },
            new AttendanceMark { SlotId = slot.Id, Date = fourth, StudentId = risk.Id, Status = AttendanceStatus.Present, Method = MarkMethod.Manual, MarkedAt = fourth.ToDateTime(new TimeOnly(9, 5)) });
        test.Db.AppSettings.AddRange(
            new AppSetting { Key = "stats.reports_exported.2026-09-10", Value = "1" },
            new AppSetting { Key = "stats.reports_exported.2026-09-29", Value = "2" },
            new AppSetting { Key = "stats.reports_exported.2026-08-31", Value = "9" },
            new AppSetting { Key = "stats.reports_exported.2026-10-01", Value = "9" });
        test.Db.SaveChanges();
        var dashboard = await test.Stats.DashboardAsync(teacher.Id, new DateTime(2026, 9, 30, 18, 0, 0));
        Assert.Equal(1, dashboard.CameraMarks7Days);
        Assert.Equal(2, dashboard.CameraMarks30Days);
        Assert.True(Math.Abs(dashboard.SavedMinutes7Days - 40.1) < 0.0001);
        Assert.True(Math.Abs(dashboard.SavedMinutes30Days - 60.2) < 0.0001);
        Assert.Contains(dashboard.AtRisk, x => x.StudentId == risk.Id &&
            x.Reason == "Три и более пропуска подряд без уважительной причины");
    }

    [Fact]
    public async Task DashboardAsync_WithoutMarksHasNoAttendanceData()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван", "Анна");
        test.AddSlot(teacher, group, validFrom: new DateOnly(2026, 9, 8));

        var dashboard = await test.Stats.DashboardAsync(teacher.Id, new DateTime(2026, 9, 30, 18, 0, 0));

        Assert.Empty(dashboard.AtRisk);
        Assert.Empty(dashboard.Groups);
        Assert.Empty(dashboard.Trend);
    }

    [Fact]
    public async Task DashboardAsync_CountsOnlyMarkedLessons()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1", "Иван", "Анна");
        var slot = test.AddSlot(teacher, group, validFrom: new DateOnly(2026, 9, 8));
        var present = group.Students[0];
        var unmarked = group.Students[1];
        var date = new DateOnly(2026, 9, 29);
        test.Db.AttendanceMarks.Add(new AttendanceMark
        {
            SlotId = slot.Id, Date = date, StudentId = present.Id, Status = AttendanceStatus.Present,
            Method = MarkMethod.Manual, MarkedAt = date.ToDateTime(new TimeOnly(9, 5))
        });
        test.Db.SaveChanges();

        var dashboard = await test.Stats.DashboardAsync(teacher.Id, new DateTime(2026, 9, 30, 18, 0, 0));

        Assert.Equal(50d, dashboard.Attendance7Days);
        Assert.Equal(50d, dashboard.Attendance30Days);
        Assert.Equal(50d, Assert.Single(dashboard.Groups).Percent);
        Assert.Equal(date, Assert.Single(dashboard.Trend).Date);
        Assert.Contains(dashboard.AtRisk, x => x.StudentId == unmarked.Id && x.Percent == 0);
    }

    [Fact]
    public async Task ReportsExportedAsync_SumsOnlyDatesInPeriod()
    {
        using var test = new TestDb();
        test.Db.AppSettings.AddRange(
            new AppSetting { Key = "stats.reports_exported.2026-09-09", Value = "9" },
            new AppSetting { Key = "stats.reports_exported.2026-09-10", Value = "2" },
            new AppSetting { Key = "stats.reports_exported.2026-09-20", Value = "3" },
            new AppSetting { Key = "stats.reports_exported.2026-09-21", Value = "9" },
            new AppSetting { Key = "stats.reports_exported.invalid", Value = "7" },
            new AppSetting { Key = "stats.reports_exported.2026-09-15", Value = "invalid" });
        test.Db.SaveChanges();
        Assert.Equal(5, await test.Stats.ReportsExportedAsync(new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 20)));
    }
}
