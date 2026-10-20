using BiStudent.Contracts;
using System.Globalization;
using BiStudent.Server.Data;
using BiStudent.Server.Firebase;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BiStudent.Server.Services;

public sealed class SeedService(AppDbContext db, IConfiguration configuration, IWebHostEnvironment environment,
    IPasswordHasher<Teacher> hasher, ScheduleService schedule, ScheduleImportService importer, ILogger<SeedService> logger)
{
    public async Task SeedAsync()
    {
        if (await db.Teachers.AnyAsync()) return;
        // Демо-данные в облачном режиме ушли бы в общую базу, поэтому с Firebase создаётся только администратор.
        var demo = configuration.GetValue("Demo:Enabled", false) && !FirebaseOptions.IsConfigured(configuration);
        var teacher = new Teacher {
            Login = demo ? configuration["Demo:Login"] ?? "teacher" : configuration["Setup:AdminLogin"] ?? "admin",
            FullName = demo ? configuration["Demo:TeacherName"] ?? "Преподаватель (демо)"
                : configuration["Setup:AdminName"] ?? "Администратор",
            IsAdmin = true,
            MustChangePassword = !demo
        };
        teacher.PasswordHash = hasher.HashPassword(teacher, demo
            ? configuration["Demo:Password"] ?? "teacher123"
            : configuration["Setup:AdminPassword"] ?? "admin");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
        if (!demo)
        {
            logger.LogWarning("Создан администратор «{Login}». Войдите и смените пароль.", teacher.Login);
            return;
        }

        var path = new[] {
            Path.Combine(environment.ContentRootPath, "seed", "students.local.csv"),
            Path.Combine(AppContext.BaseDirectory, "seed", "students.local.csv"),
            Path.Combine(environment.ContentRootPath, "seed", "students.demo.csv"),
            Path.Combine(AppContext.BaseDirectory, "seed", "students.demo.csv")
        }.FirstOrDefault(File.Exists);
        if (path is null)
        {
            logger.LogWarning("Список студентов для демо (seed/students.demo.csv) не найден: создан только преподаватель.");
            return;
        }
        foreach (var raw in await File.ReadAllLinesAsync(path))
        {
            var parts = raw.Split(';').Select(x => x.Trim()).ToArray();
            if (parts.Length < 2 || parts[0].Equals("ФИО", StringComparison.OrdinalIgnoreCase)) continue;
            var group = await db.Groups.SingleOrDefaultAsync(x => x.Name == parts[1]);
            if (group is null) { group = new Group { Name = parts[1] }; db.Groups.Add(group); await db.SaveChangesAsync(); }
            if (!await db.Students.AnyAsync(x => x.GroupId == group.Id && x.FullName == parts[0]))
                db.Students.Add(new Student { FullName = parts[0], GroupId = group.Id });
        }
        await db.SaveChangesAsync();

        var start = DateOnly.Parse(configuration["Semester:Start"] ?? "2026-09-01", CultureInfo.InvariantCulture);
        var teacherIds = new List<int> { teacher.Id };
        if (await ImportLocalScheduleAsync(teacher) is { } imported)
        {
            teacher = imported.Main;
            teacherIds = imported.Teachers;
        }
        else await AddDemoScheduleAsync(teacher, start);
        await AddHistoryAsync(teacherIds, start);
    }

    /// Реальное расписание колледжа для демо (seed/schedule.local.xlsx, в git не хранится): занятия групп из списка студентов.
    /// Демо-вход получает преподаватель с наибольшей нагрузкой.
    private async Task<(Teacher Main, List<int> Teachers)?> ImportLocalScheduleAsync(Teacher placeholder)
    {
        if (!configuration.GetValue("Demo:UseLocalSchedule", false)) return null;
        var path = new[] {
            Path.Combine(environment.ContentRootPath, "seed", "schedule.local.xlsx"),
            Path.Combine(AppContext.BaseDirectory, "seed", "schedule.local.xlsx")
        }.FirstOrDefault(File.Exists);
        if (path is null) return null;
        var bytes = await File.ReadAllBytesAsync(path);
        var preview = await importer.PreviewAsync(new MemoryStream(bytes));
        var groups = preview.Groups.Where(x => x.Exists).Select(x => x.Group).ToList();
        if (groups.Count == 0)
        {
            logger.LogWarning("В seed/schedule.local.xlsx нет групп из списка студентов — используется демонстрационное расписание.");
            return null;
        }
        await importer.ImportAsync(new MemoryStream(bytes), groups);
        var load = await db.ScheduleSlots.AsNoTracking().GroupBy(x => x.TeacherId)
            .Select(x => new { TeacherId = x.Key, Count = x.Count() }).ToListAsync();
        var active = await db.Teachers.Where(x => x.IsActive && x.Id != placeholder.Id).ToListAsync();
        var main = active.OrderByDescending(t => load.FirstOrDefault(x => x.TeacherId == t.Id)?.Count ?? 0).First();
        db.Teachers.Remove(placeholder);
        await db.SaveChangesAsync();
        main.Login = placeholder.Login;
        main.IsAdmin = true;
        main.MustChangePassword = false;
        main.PasswordHash = hasher.HashPassword(main, configuration["Demo:Password"] ?? "teacher123");
        await db.SaveChangesAsync();
        logger.LogInformation("Демо: импортировано расписание групп {Groups}; вход «{Login}» — {Teacher}.",
            string.Join(", ", groups), main.Login, main.FullName);
        return (main, load.Select(x => x.TeacherId).ToList());
    }

    private async Task AddDemoScheduleAsync(Teacher teacher, DateOnly start)
    {
        var subjects = new[] { "Базы данных", "DevOps", "Веб-программирование", "Алгоритмы", "Английский язык", "Компьютерные сети" };
        var bells = new[] { (new TimeOnly(8, 30), new TimeOnly(9, 50)), (new TimeOnly(10, 0), new TimeOnly(11, 20)),
            (new TimeOnly(11, 30), new TimeOnly(12, 50)), (new TimeOnly(13, 20), new TimeOnly(14, 40)),
            (new TimeOnly(14, 50), new TimeOnly(16, 10)), (new TimeOnly(16, 20), new TimeOnly(17, 40)) };
        var groups = await db.Groups.OrderByDescending(x => x.Name).ToListAsync();
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            for (var day = 0; day < 5; day++)
            {
                foreach (var pair in new[] { groupIndex * 2 % 6, (groupIndex * 2 + 3) % 6 })
                {
                    var subject = subjects[(day * 2 + pair + groupIndex) % subjects.Length];
                    db.ScheduleSlots.Add(new ScheduleSlot {
                        TeacherId = teacher.Id, GroupId = groups[groupIndex].Id,
                        Subject = subject, Room = $"{305 + groupIndex * 4 + pair % 2}",
                        DayOfWeek = (DayOfWeek)(day + 1), Start = bells[pair].Item1, End = bells[pair].Item2,
                        Parity = day == 4 && pair > 2 ? (groupIndex % 2 == 0 ? WeekParity.Odd : WeekParity.Even) : WeekParity.Every,
                        ValidFrom = start
                    });
                }
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task AddHistoryAsync(List<int> teacherIds, DateOnly start)
    {
        var yesterday = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        var first = yesterday.AddDays(-27);
        if (first < start) first = start;
        var past = new List<LessonOccurrence>();
        foreach (var teacherId in teacherIds)
            for (var day = first; day <= yesterday; day = day.AddDays(1))
                past.AddRange(await schedule.ForDayAsync(teacherId, day));
        var cancelled = past.FirstOrDefault();
        if (cancelled is not null)
        {
            db.SlotCancellations.Add(new SlotCancellation {
                SlotId = cancelled.Slot.Id, Date = cancelled.Date, Reason = "Перенос по распоряжению учебной части"
            });
        }
        var students = await db.Students.OrderBy(x => x.Id).ToListAsync();
        var riskIds = students.Take(3).Select(x => x.Id).ToHashSet();
        var random = new Random(42);
        foreach (var lesson in past.Where(x => x != cancelled))
        {
            foreach (var student in students.Where(x => x.GroupId == lesson.Slot.GroupId))
            {
                var roll = random.NextDouble();
                var risk = riskIds.Contains(student.Id);
                var status = roll < (risk ? 0.60 : 0.89) ? AttendanceStatus.Present :
                    roll < (risk ? 0.68 : 0.93) ? AttendanceStatus.Excused : AttendanceStatus.Absent;
                var reasonRoll = random.NextDouble();
                var reason = reasonRoll < 0.70 ? ExcuseReason.Illness :
                    reasonRoll < 0.85 ? ExcuseReason.Family : ExcuseReason.Official;
                var attended = status == AttendanceStatus.Present;
                db.AttendanceMarks.Add(new AttendanceMark {
                    SlotId = lesson.Slot.Id, Date = lesson.Date, StudentId = student.Id, Status = status,
                    Method = attended && random.NextDouble() < 0.93 ? MarkMethod.Camera : MarkMethod.Manual,
                    ExcuseReason = status == AttendanceStatus.Excused ? reason : null,
                    HasDocument = status == AttendanceStatus.Excused && reason == ExcuseReason.Illness && random.NextDouble() < 0.80,
                    MarkedAt = attended
                        ? lesson.StartsAt.AddMinutes(random.Next(-4, 6)).AddSeconds(random.Next(60))
                        : lesson.EndsAt.AddMinutes(-random.Next(5))
                });
                if (attended && random.NextDouble() < 0.35)
                    db.LessonGrades.Add(new LessonGrade {
                        SlotId = lesson.Slot.Id, Date = lesson.Date, StudentId = student.Id,
                        Score = risk ? random.Next(45, 81) : random.Next(60, 101)
                    });
            }
        }
        var assessments = await db.ScheduleSlots.AsNoTracking()
            .Where(x => teacherIds.Contains(x.TeacherId))
            .Select(x => new { x.TeacherId, x.GroupId, x.Subject }).Distinct().ToListAsync();
        foreach (var pair in assessments)
        {
            foreach (var (date, title, kind) in new[] {
                (yesterday.AddDays(-13), "Тест №1", AssessmentKind.Test),
                (yesterday.AddDays(-6), "Контрольная работа №1", AssessmentKind.ControlWork)
            })
            {
                if (date < start) continue;
                var assessment = new Assessment {
                    TeacherId = pair.TeacherId, GroupId = pair.GroupId, Subject = pair.Subject,
                    Title = title, Kind = kind, Date = date
                };
                db.Assessments.Add(assessment);
                var members = students.Where(x => x.GroupId == pair.GroupId).OrderBy(_ => random.Next()).ToList();
                var withoutGrade = Math.Min(members.Count, random.Next(1, 3));
                foreach (var student in members.Skip(withoutGrade))
                    assessment.Grades.Add(new AssessmentGrade {
                        StudentId = student.Id, Score = riskIds.Contains(student.Id) ? random.Next(35, 76) : random.Next(45, 101)
                    });
            }
        }
        for (var week = 0; week < 4; week++)
        {
            var exported = yesterday.AddDays(-7 * week);
            if (exported < start) break;
            db.AppSettings.Add(new AppSetting {
                Key = $"stats.reports_exported.{exported.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", Value = "2"
            });
        }
        await db.SaveChangesAsync();
    }
}
