using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed record AttendanceRow(int StudentId, string FullName, AttendanceStatus? Status, MarkMethod? Method,
    DateTime? MarkedAt, ExcuseReason? Reason, string? Comment, bool HasDocument, int? Score);
public sealed record JournalColumn(JournalColumnKind Kind, int Id, DateOnly Date, TimeOnly? Start, string Subject,
    string? Title = null, AssessmentKind? AssessmentKind = null);
public sealed record JournalCell(AttendanceStatus? Status, ExcuseReason? Reason, string? Comment, bool HasDocument, int? Score, bool InGroup = true);
public sealed record JournalStudent(int StudentId, string FullName, List<JournalCell> Cells,
    int Present, int Absent, int Excused, int MissedHours, int ExcusedHours, double Percent, double? Average, int? Final);
public sealed record JournalMatrix(string TeacherName, string GroupName, string? Subject, DateOnly From, DateOnly To,
    int HoursPerLesson, List<JournalColumn> Columns, List<JournalStudent> Students);

public sealed class AttendanceNotifier
{
    public event Action<int, DateOnly>? Changed;
    public void Notify(int slotId, DateOnly date) => Changed?.Invoke(slotId, date);
}

public sealed class AttendanceService(AppDbContext db, IConfiguration configuration, ScheduleService schedule, AttendanceNotifier notifier)
{
    public async Task<List<AttendanceRow>> ForLessonAsync(int slotId, DateOnly date)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        var members = db.MembershipsOn(date).Where(x => x.GroupId == slot.GroupId).Select(x => x.StudentId);
        var students = await db.Students.AsNoTracking().Where(x => members.Contains(x.Id)).OrderBy(x => x.FullName).ToListAsync();
        var marks = await db.AttendanceMarks.AsNoTracking().Where(x => x.SlotId == slotId && x.Date == date).ToDictionaryAsync(x => x.StudentId);
        var grades = await db.LessonGrades.AsNoTracking().Where(x => x.SlotId == slotId && x.Date == date).ToDictionaryAsync(x => x.StudentId);
        return students.Select(x => {
            marks.TryGetValue(x.Id, out var mark);
            grades.TryGetValue(x.Id, out var grade);
            return new AttendanceRow(x.Id, x.FullName, mark?.Status, mark?.Method, mark?.MarkedAt,
                mark?.ExcuseReason, mark?.Comment, mark?.HasDocument ?? false, grade?.Score);
        }).ToList();
    }

    public async Task<bool> MarkAsync(int slotId, DateOnly date, int studentId, AttendanceStatus status, MarkMethod method,
        ExcuseReason? reason = null, string? comment = null, bool hasDocument = false, bool onlyIfUnmarked = false)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        if (!await db.MembershipsOn(date).AnyAsync(x => x.StudentId == studentId && x.GroupId == slot.GroupId))
            throw new InvalidOperationException("Студент не принадлежит группе этой пары.");
        if (!Enum.IsDefined(status)) throw new InvalidOperationException("Неверный статус посещаемости.");
        var now = DateTime.Now;
        if (method == MarkMethod.Camera) status = AttendanceStatus.Present;
        if (status == AttendanceStatus.Excused)
        {
            reason ??= ExcuseReason.Other;
            if (!Enum.IsDefined(reason.Value)) throw new InvalidOperationException("Неверная причина отсутствия.");
            comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
            if (comment?.Length > 500) throw new InvalidOperationException("Комментарий длиннее 500 символов.");
        }
        else { reason = null; comment = null; hasDocument = false; }
        var mark = await db.AttendanceMarks.SingleOrDefaultAsync(x => x.SlotId == slotId && x.Date == date && x.StudentId == studentId);
        if (onlyIfUnmarked && mark is not null) return false;
        if (mark is not null && (method == MarkMethod.Camera && mark.Method == MarkMethod.Manual ||
            mark.Status == status && mark.Method == method && mark.ExcuseReason == reason &&
            mark.Comment == comment && mark.HasDocument == hasDocument)) return false;
        if (mark is null)
        {
            mark = new AttendanceMark { SlotId = slotId, Date = date, StudentId = studentId };
            db.AttendanceMarks.Add(mark);
        }
        mark.Status = status;
        mark.Method = method;
        mark.MarkedAt = now;
        mark.ExcuseReason = reason;
        mark.Comment = comment;
        mark.HasDocument = hasDocument;
        await db.SaveChangesAsync();
        notifier.Notify(slotId, date);
        return true;
    }


    public async Task SetLessonGradeAsync(int slotId, DateOnly date, int studentId, int? score)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        if (!await db.MembershipsOn(date).AnyAsync(x => x.StudentId == studentId && x.GroupId == slot.GroupId))
            throw new InvalidOperationException("Студент не принадлежит группе этой пары.");
        if (score is < 1 or > 100) throw new InvalidOperationException("Балл должен быть от 1 до 100.");
        var grade = await db.LessonGrades.SingleOrDefaultAsync(x => x.SlotId == slotId && x.Date == date && x.StudentId == studentId);
        if (score is null)
        {
            if (grade is null) return;
            db.LessonGrades.Remove(grade);
        }
        else if (grade is null)
            db.LessonGrades.Add(new LessonGrade { SlotId = slotId, Date = date, StudentId = studentId, Score = score.Value });
        else
        {
            if (grade.Score == score) return;
            grade.Score = score.Value;
            grade.UpdatedAt = DateTime.Now;
        }
        await db.SaveChangesAsync();
        notifier.Notify(slotId, date);
    }
    public async Task ClearAsync(int slotId, DateOnly date, int studentId)
    {
        var mark = await db.AttendanceMarks.SingleOrDefaultAsync(x => x.SlotId == slotId && x.Date == date && x.StudentId == studentId);
        if (mark is null) return;
        db.AttendanceMarks.Remove(mark);
        await db.SaveChangesAsync();
        notifier.Notify(slotId, date);
    }

    public async Task<int> MarkRestAbsentAsync(int slotId, DateOnly date)
    {
        var slot = await db.ScheduleSlots.FindAsync(slotId) ?? throw new KeyNotFoundException("Пара не найдена.");
        var marked = await db.AttendanceMarks.Where(x => x.SlotId == slotId && x.Date == date).Select(x => x.StudentId).ToListAsync();
        var missing = await db.MembershipsOn(date).Where(x => x.GroupId == slot.GroupId && !marked.Contains(x.StudentId))
            .Select(x => x.StudentId).Distinct().ToListAsync();
        var now = DateTime.Now;
        db.AttendanceMarks.AddRange(missing.Select(id => new AttendanceMark {
            SlotId = slotId, Date = date, StudentId = id, Status = AttendanceStatus.Absent, Method = MarkMethod.Manual, MarkedAt = now
        }));
        if (missing.Count > 0) { await db.SaveChangesAsync(); notifier.Notify(slotId, date); }
        return missing.Count;
    }

    public async Task<JournalMatrix> JournalAsync(int teacherId, int groupId, DateOnly from, DateOnly to, string? subject)
    {
        if (to < from) throw new InvalidOperationException("Конец периода раньше начала.");
        var teacher = await db.Teachers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == teacherId) ?? throw new KeyNotFoundException("Преподаватель не найден.");
        var group = await db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId) ?? throw new KeyNotFoundException("Группа не найдена.");
        subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        var lessonTo = to < DateOnly.FromDateTime(DateTime.Now) ? to : DateOnly.FromDateTime(DateTime.Now);
        var occurrences = new List<LessonOccurrence>();
        var firstSlotDate = await db.ScheduleSlots.AsNoTracking()
            .Where(x => x.TeacherId == teacherId && x.GroupId == groupId)
            .OrderBy(x => x.ValidFrom).Select(x => (DateOnly?)x.ValidFrom).FirstOrDefaultAsync();
        if (firstSlotDate is { } firstDate)
            for (var day = from > firstDate ? from : firstDate; day <= lessonTo; day = day.AddDays(1))
                occurrences.AddRange((await schedule.ForDayAsync(teacherId, day)).Where(x =>
                    !x.Cancelled && x.Slot.GroupId == groupId &&
                    (subject is null || x.Slot.Subject == subject) && x.EndsAt <= DateTime.Now));
        var assessmentsQuery = db.Assessments.AsNoTracking()
            .Where(x => x.TeacherId == teacherId && x.GroupId == groupId && x.Date >= from && x.Date <= to);
        if (subject is not null) assessmentsQuery = assessmentsQuery.Where(x => x.Subject == subject);
        var assessments = await assessmentsQuery.ToListAsync();
        var columns = occurrences.Select(x => new JournalColumn(
                JournalColumnKind.Lesson, x.Slot.Id, x.Date, x.Slot.Start, x.Slot.Subject))
            .Concat(assessments.Select(x => new JournalColumn(
                JournalColumnKind.Assessment, x.Id, x.Date, null, x.Subject, x.Title, x.Kind)))
            .OrderBy(x => x.Date).ThenBy(x => x.Kind).ThenBy(x => x.Start).ThenBy(x => x.Id).ToList();
        var periods = await db.MembershipsDuring(from, to).AsNoTracking()
            .Where(x => x.GroupId == groupId).ToListAsync();
        var studentIds = periods.Select(x => x.StudentId).Distinct().ToArray();
        var students = await db.Students.AsNoTracking().Where(x => studentIds.Contains(x.Id))
            .OrderBy(x => x.FullName).ToListAsync();
        var membershipMap = periods.GroupBy(x => x.StudentId).ToDictionary(x => x.Key, x => x.ToList());
        var slotIds = occurrences.Select(x => x.Slot.Id).Distinct().ToArray();
        var assessmentIds = assessments.Select(x => x.Id).ToArray();
        var marks = await db.AttendanceMarks.AsNoTracking()
            .Where(x => slotIds.Contains(x.SlotId) && x.Date >= from && x.Date <= lessonTo).ToListAsync();
        var lessonGrades = await db.LessonGrades.AsNoTracking()
            .Where(x => slotIds.Contains(x.SlotId) && x.Date >= from && x.Date <= lessonTo).ToListAsync();
        var assessmentGrades = await db.AssessmentGrades.AsNoTracking()
            .Where(x => assessmentIds.Contains(x.AssessmentId)).ToListAsync();
        var finals = subject is null ? new List<FinalGrade>() : await db.FinalGrades.AsNoTracking()
            .Where(x => x.TeacherId == teacherId && x.GroupId == groupId && x.Subject == subject).ToListAsync();
        var markMap = marks.ToDictionary(x => (x.SlotId, x.Date, x.StudentId));
        var markedLessons = marks.Select(x => (x.SlotId, x.Date)).ToHashSet();
        var lessonGradeMap = lessonGrades.ToDictionary(x => (x.SlotId, x.Date, x.StudentId));
        var assessmentGradeMap = assessmentGrades.ToDictionary(x => (x.AssessmentId, x.StudentId));
        var finalMap = finals.ToDictionary(x => x.StudentId, x => x.Score);
        var hours = configuration.GetValue("Attendance:HoursPerLesson", 2);
        var rows = students.Select(student => {
            var cells = columns.Select(column => {
                var inGroup = membershipMap[student.Id].Any(x => x.From <= column.Date && (x.To is null || x.To >= column.Date));
                if (!inGroup) return new JournalCell(null, null, null, false, null, false);
                if (column.Kind == JournalColumnKind.Assessment)
                    return new JournalCell(null, null, null, false,
                        assessmentGradeMap.TryGetValue((column.Id, student.Id), out var assessmentGrade) ? assessmentGrade.Score : null);
                markMap.TryGetValue((column.Id, column.Date, student.Id), out var mark);
                return new JournalCell(mark?.Status, mark?.ExcuseReason, mark?.Comment, mark?.HasDocument ?? false,
                    lessonGradeMap.TryGetValue((column.Id, column.Date, student.Id), out var lessonGrade) ? lessonGrade.Score : null);
            }).ToList();
            var lessonCount = cells.Where((cell, index) => cell.InGroup && columns[index].Kind == JournalColumnKind.Lesson &&
                markedLessons.Contains((columns[index].Id, columns[index].Date))).Count();
            var present = cells.Count(x => x.InGroup && x.Status == AttendanceStatus.Present);
            var absent = cells.Count(x => x.InGroup && x.Status == AttendanceStatus.Absent);
            var excused = cells.Count(x => x.InGroup && x.Status == AttendanceStatus.Excused);
            var scores = cells.Where(x => x.InGroup && x.Score.HasValue).Select(x => x.Score!.Value).ToList();
            return new JournalStudent(student.Id, student.FullName, cells, present, absent, excused,
                (absent + excused) * hours, excused * hours,
                lessonCount == 0 ? 0 : 100d * present / lessonCount,
                scores.Count == 0 ? null : Math.Round(scores.Average(), 1),
                finalMap.TryGetValue(student.Id, out var final) ? final : null);
        }).ToList();
        return new JournalMatrix(teacher.FullName, group.Name, subject, from, to, hours, columns, rows);
    }
}
