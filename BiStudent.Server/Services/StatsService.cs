using BiStudent.Contracts;
using System.Globalization;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed record GroupAttendance(int GroupId, string GroupName, double Percent);
public sealed record AtRiskStudent(int StudentId, string FullName, string GroupName, double Percent, string Reason);
public sealed record AttendanceTrend(DateOnly Date, double Percent);
public sealed record DashboardStats(int LessonsToday, LessonOccurrence? NextOrCurrent, double Attendance7Days,
    double Attendance30Days, List<GroupAttendance> Groups, List<AtRiskStudent> AtRisk,
    int CameraMarks7Days, int CameraMarks30Days, double SavedMinutes7Days, double SavedMinutes30Days,
    string SavingsFormula, List<AttendanceTrend> Trend, double? AverageScore30Days,
    int PendingFaceSubmissions);

public sealed class StatsService(AppDbContext db, ScheduleService schedule, IConfiguration configuration)
{
    public async Task<int> ReportsExportedAsync(DateOnly from, DateOnly to)
    {
        var settings = await db.AppSettings.AsNoTracking().Where(x => x.Key.StartsWith("stats.reports_exported.")).ToListAsync();
        return settings.Sum(x =>
            DateOnly.TryParseExact(x.Key["stats.reports_exported.".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) && date >= from && date <= to &&
            int.TryParse(x.Value, out var count) ? count : 0);
    }

    public async Task<DashboardStats> DashboardAsync(int teacherId, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var from = today.AddDays(-29);
        var occurrences = new List<LessonOccurrence>();
        for (var day = from; day <= today; day = day.AddDays(1))
            occurrences.AddRange((await schedule.ForDayAsync(teacherId, day))
                .Where(x => !x.Cancelled && x.EndsAt <= now));
        var slotIds = occurrences.Select(x => x.Slot.Id).Distinct().ToArray();
        var marks = await db.AttendanceMarks.AsNoTracking()
            .Where(x => slotIds.Contains(x.SlotId) && x.Date >= from && x.Date <= today).ToListAsync();
        var markedLessons = marks.Select(x => (x.SlotId, x.Date)).ToHashSet();
        occurrences = occurrences.Where(x => markedLessons.Contains((x.Slot.Id, x.Date))).ToList();
        var todayLessons = await schedule.ForDayAsync(teacherId, today);
        var next = todayLessons.FirstOrDefault(x => !x.Cancelled && x.EndsAt > now);
        var groupIds = occurrences.Select(x => x.Slot.GroupId).Distinct().ToArray();
        var memberships = await db.MembershipsDuring(from, today).AsNoTracking()
            .Where(x => groupIds.Contains(x.GroupId)).ToListAsync();
        var studentIds = memberships.Select(x => x.StudentId).Distinct().ToArray();
        var students = await db.Students.AsNoTracking().Include(x => x.Group)
            .Where(x => studentIds.Contains(x.Id)).ToListAsync();
        var membershipMap = memberships.GroupBy(x => x.StudentId).ToDictionary(x => x.Key, x => x.ToList());
        bool InGroup(int studentId, int groupId, DateOnly date) =>
            membershipMap.TryGetValue(studentId, out var periods) &&
            periods.Any(x => x.GroupId == groupId && x.From <= date && (x.To is null || x.To >= date));
        var markMap = marks.ToDictionary(x => (x.SlotId, x.Date, x.StudentId));

        double Percent(IEnumerable<LessonOccurrence> lessons, IEnumerable<Student> people)
        {
            var possible = 0;
            var attended = 0;
            foreach (var lesson in lessons)
                foreach (var person in people)
                {
                    if (!InGroup(person.Id, lesson.Slot.GroupId, lesson.Date)) continue;
                    possible++;
                    if (markMap.TryGetValue((lesson.Slot.Id, lesson.Date, person.Id), out var mark) &&
                        mark.Status == AttendanceStatus.Present) attended++;
                }
            return possible == 0 ? 0 : 100d * attended / possible;
        }

        var groupNames = await db.Groups.AsNoTracking().Where(x => groupIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Name }).ToListAsync();
        var groups = groupNames.Where(group => occurrences.Any(x => x.Slot.GroupId == group.Id)).Select(group =>
            new GroupAttendance(group.Id, group.Name,
                Percent(occurrences.Where(x => x.Slot.GroupId == group.Id), students))).OrderBy(x => x.GroupName).ToList();
        var risk = new List<AtRiskStudent>();
        foreach (var student in students)
        {
            var lessons = occurrences.Where(x => InGroup(student.Id, x.Slot.GroupId, x.Date))
                .OrderBy(x => x.StartsAt).ToList();
            var percent = Percent(lessons, new[] { student });
            var missed = 0;
            var longest = 0;
            foreach (var lesson in lessons)
            {
                var absent = !markMap.TryGetValue((lesson.Slot.Id, lesson.Date, student.Id), out var mark) ||
                    mark.Status == AttendanceStatus.Absent;
                missed = absent ? missed + 1 : 0;
                longest = Math.Max(longest, missed);
            }
            if (lessons.Count > 0 && (percent < 70 || longest >= 3))
                risk.Add(new AtRiskStudent(student.Id, student.FullName, student.Group.Name, percent,
                    longest >= 3 ? "Три и более пропуска подряд без уважительной причины" : "Посещаемость ниже 70%"));
        }
        var last7 = today.AddDays(-6);
        var camera7 = marks.Count(x => x.Method == MarkMethod.Camera && x.Date >= last7);
        var camera30 = marks.Count(x => x.Method == MarkMethod.Camera);
        var seconds = configuration.GetValue("Automation:SecondsPerManualMark", 6);
        var reportMinutes = configuration.GetValue("Automation:MinutesPerManualReport", 20);
        var reportSettings = await db.AppSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith("stats.reports_exported.")).ToListAsync();
        int ReportsSince(DateOnly date) => reportSettings.Sum(x =>
            DateOnly.TryParseExact(x.Key["stats.reports_exported.".Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exported) && exported >= date && exported <= today &&
            int.TryParse(x.Value, out var count) ? count : 0);
        var trend = occurrences.GroupBy(x => x.Date).OrderBy(x => x.Key)
            .Select(x => new AttendanceTrend(x.Key, Percent(x, students))).ToList();
        var lessonScores = await db.LessonGrades.AsNoTracking()
            .Where(x => x.Slot.TeacherId == teacherId && x.Date >= from && x.Date <= today)
            .Select(x => x.Score).ToListAsync();
        var assessmentScores = await db.AssessmentGrades.AsNoTracking()
            .Where(x => x.Assessment.TeacherId == teacherId &&
                x.Assessment.Date >= from && x.Assessment.Date <= today)
            .Select(x => x.Score).ToListAsync();
        var scores = lessonScores.Concat(assessmentScores).ToList();
        var pending = await db.FaceSubmissions.CountAsync(x => x.Status == FaceSubmissionStatus.Pending &&
            (!db.ScheduleSlots.Any(slot => slot.GroupId == x.Student.GroupId) ||
                db.ScheduleSlots.Any(slot => slot.GroupId == x.Student.GroupId && slot.TeacherId == teacherId)));
        return new DashboardStats(todayLessons.Count(x => !x.Cancelled), next,
            Percent(occurrences.Where(x => x.Date >= last7), students), Percent(occurrences, students),
            groups, risk.OrderBy(x => x.Percent).ToList(), camera7, camera30,
            camera7 * seconds / 60d + ReportsSince(last7) * reportMinutes,
            camera30 * seconds / 60d + ReportsSince(from) * reportMinutes,
            $"Отметки камерой × {seconds} сек. + выгрузки журнала × {reportMinutes} мин.",
            trend, scores.Count == 0 ? null : scores.Average(), pending);
    }
}
