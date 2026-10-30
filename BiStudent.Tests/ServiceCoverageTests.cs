using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BiStudent.Tests;

public sealed class ServiceCoverageTests
{
    private static readonly DateOnly FirstTuesday = new(2026, 9, 1);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    [Fact]
    public async Task ScheduleHandlesOneOffParityOverlapAndWeek()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("10-А");
        var once = new DateOnly(2026, 9, 3);
        var oneOff = await test.Schedule.AddOneOffAsync(test.NewSlot(teacher, group), once);

        Assert.DoesNotContain(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday), x => x.Slot.Id == oneOff.Id);
        Assert.Contains(await test.Schedule.ForDayAsync(teacher.Id, once), x => x.Slot.Id == oneOff.Id);
        Assert.DoesNotContain(await test.Schedule.ForDayAsync(teacher.Id, once.AddDays(7)), x => x.Slot.Id == oneOff.Id);

        var odd = await test.Schedule.AddSlotAsync(test.NewSlot(teacher, group, parity: WeekParity.Odd));
        var even = await test.Schedule.AddSlotAsync(test.NewSlot(teacher, group, parity: WeekParity.Even));
        Assert.Equal(WeekParity.Odd, test.Schedule.WeekParityOf(FirstTuesday));
        Assert.Equal(WeekParity.Even, test.Schedule.WeekParityOf(FirstTuesday.AddDays(7)));
        Assert.Contains(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday), x => x.Slot.Id == odd.Id);
        Assert.DoesNotContain(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday), x => x.Slot.Id == even.Id);
        Assert.Contains(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday.AddDays(7)), x => x.Slot.Id == even.Id);
        Assert.Equal(2, (await test.Schedule.ForWeekAsync(teacher.Id, new DateOnly(2026, 8, 31))).Count);

        var overlap = test.NewSlot(teacher, group, start: new TimeOnly(9, 30), end: new TimeOnly(10, 30));
        var error = await Assert.ThrowsAsync<ScheduleConflictException>(() => test.Schedule.AddSlotAsync(overlap));
        Assert.Contains("уже есть пара", error.Message);
    }

    [Fact]
    public async Task ScheduleCancellationCurrentLessonAndDeletion()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("10-А", "Анна");
        var slot = await test.Schedule.AddSlotAsync(test.NewSlot(teacher, group));
        var student = await test.Db.Students.SingleAsync();
        var now = FirstTuesday.ToDateTime(new TimeOnly(9, 30));

        Assert.Equal(slot.Id, (await test.Schedule.CurrentAsync(teacher.Id, now))?.Slot.Id);
        await test.Schedule.CancelAsync(slot.Id, FirstTuesday, "Каникулы");
        Assert.True(Assert.Single(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday)).Cancelled);
        Assert.Null(await test.Schedule.CurrentAsync(teacher.Id, now));
        await test.Schedule.RestoreAsync(slot.Id, FirstTuesday);
        Assert.False(Assert.Single(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday)).Cancelled);

        await test.Attendance.MarkAsync(slot.Id, FirstTuesday, student.Id, AttendanceStatus.Present, MarkMethod.Manual);
        await test.Schedule.DeleteSlotAsync(slot.Id);
        Assert.Empty(await test.Schedule.ForDayAsync(teacher.Id, FirstTuesday));
        Assert.False(await test.Db.AttendanceMarks.AnyAsync(x => x.SlotId == slot.Id));
    }

    [Fact]
    public async Task ScheduleImportReportsEachInvalidRow()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        const string csv = "День;Начало;Конец;Предмет;Группа;Кабинет;Чётность\n" +
            "неизвестно;09:00;10:00;Алгебра;10-А;;каждая\n" +
            "вт;11:00;10:00;Алгебра;10-А;;каждая\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => test.Schedule.ImportCsvAsync(teacher.Id, stream));
        Assert.Contains("Строка 2", error.Message);
        Assert.Contains("Неверный день", error.Message);
        Assert.Contains("Строка 3", error.Message);
        Assert.Contains("раньше окончания", error.Message);
    }

    [Fact]
    public async Task StudentValidationImportAndFaceCounts()
    {
        using var test = new TestDb();
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Students.AddGroupAsync(" "));
        var first = await test.Students.AddGroupAsync("10-А");
        var second = await test.Students.AddGroupAsync("10-Б");
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Students.AddGroupAsync(" 10-А "));
        var student = await test.Students.AddStudentAsync(first.Id, " Анна ");
        Assert.Equal("Анна", student.FullName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Students.AddStudentAsync(first.Id, "Анна"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Students.DeleteGroupAsync(first.Id));

        await test.Students.RenameStudentAsync(student.Id, "Анна Иванова");
        await test.Students.MoveStudentAsync(student.Id, second.Id);
        Assert.Equal(second.Id, (await test.Students.ForGroupAsync(second.Id)).Single().GroupId);

        test.Db.FaceSamples.Add(new FaceSample { StudentId = student.Id, ProtectedImage = [1] });
        await test.Db.SaveChangesAsync();
        Assert.True(await test.Students.HasFace(student.Id));
        Assert.Contains(student.Id, await test.Students.WithFaceAsync(second.Id));
        Assert.Empty(await test.Students.WithFaceAsync(first.Id));

        using var csv = new MemoryStream(Encoding.UTF8.GetBytes(
            "ФИО;Группа\nАнна Иванова;10-Б\nБорис;10-Б\nБорис;10-Б\n"));
        var imported = await test.Students.ImportCsvAsync(csv);
        Assert.Equal(1, imported.Added);
        Assert.Equal(2, imported.Skipped);
    }

    [Fact]
    public async Task DeletingStudentRemovesRelatedRecords()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("10-А");
        var student = await test.Students.AddStudentAsync(group.Id, "Анна");
        var slot = test.AddSlot(teacher, group);
        var assessment = new Assessment {
            TeacherId = teacher.Id, GroupId = group.Id, Subject = slot.Subject,
            Title = "Контрольная", Date = FirstTuesday
        };
        test.Db.Assessments.Add(assessment);
        await test.Db.SaveChangesAsync();
        test.Db.FaceSamples.Add(new FaceSample { StudentId = student.Id, ProtectedImage = [1] });
        test.Db.FaceSubmissions.Add(new FaceSubmission {
            StudentId = student.Id, Images = [new FaceSubmissionImage { ProtectedImage = [2] }]
        });
        test.Db.AssessmentGrades.Add(new AssessmentGrade { AssessmentId = assessment.Id, StudentId = student.Id, Score = 80 });
        test.Db.FinalGrades.Add(new FinalGrade {
            TeacherId = teacher.Id, GroupId = group.Id, Subject = slot.Subject, StudentId = student.Id, Score = 80
        });
        await test.Db.SaveChangesAsync();
        await test.Attendance.MarkAsync(slot.Id, FirstTuesday, student.Id, AttendanceStatus.Present, MarkMethod.Manual);
        await test.Attendance.SetLessonGradeAsync(slot.Id, FirstTuesday, student.Id, 80);

        await test.Students.DeleteStudentAsync(student.Id);

        Assert.False(await test.Db.Students.AnyAsync(x => x.Id == student.Id));
        Assert.False(await test.Db.AttendanceMarks.AnyAsync(x => x.StudentId == student.Id));
        Assert.False(await test.Db.LessonGrades.AnyAsync(x => x.StudentId == student.Id));
        Assert.False(await test.Db.AssessmentGrades.AnyAsync(x => x.StudentId == student.Id));
        Assert.False(await test.Db.FinalGrades.AnyAsync(x => x.StudentId == student.Id));
        Assert.False(await test.Db.FaceSamples.AnyAsync(x => x.StudentId == student.Id));
        Assert.False(await test.Db.FaceSubmissions.AnyAsync(x => x.StudentId == student.Id));
    }

    [Fact]
    public async Task AttendanceValidatesAndNotifiesOnChanges()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("10-А", "Анна", "Борис");
        var other = test.AddGroup("10-Б", "Вера");
        var slot = test.AddSlot(teacher, group);
        var students = await test.Students.ForGroupAsync(group.Id);
        var foreign = Assert.Single(await test.Students.ForGroupAsync(other.Id));
        var changed = 0;
        test.Notifier.Changed += (id, date) => {
            Assert.Equal(slot.Id, id);
            Assert.Equal(FirstTuesday, date);
            changed++;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Attendance.MarkAsync(
            slot.Id, FirstTuesday, foreign.Id, AttendanceStatus.Present, MarkMethod.Manual));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Attendance.MarkAsync(
            slot.Id, FirstTuesday, students[0].Id, AttendanceStatus.Excused, MarkMethod.Manual,
            ExcuseReason.Illness, new string('x', 501)));
        await test.Attendance.MarkAsync(slot.Id, FirstTuesday, students[0].Id, AttendanceStatus.Present, MarkMethod.Manual);
        await test.Attendance.ClearAsync(slot.Id, FirstTuesday, students[0].Id);
        await test.Attendance.MarkAsync(slot.Id, FirstTuesday, students[0].Id, AttendanceStatus.Present, MarkMethod.Manual);
        Assert.Equal(1, await test.Attendance.MarkRestAbsentAsync(slot.Id, FirstTuesday));
        await test.Attendance.SetLessonGradeAsync(slot.Id, FirstTuesday, students[0].Id, 75);
        Assert.Equal(5, changed);
        Assert.Equal(AttendanceStatus.Absent,
            (await test.Attendance.ForLessonAsync(slot.Id, FirstTuesday)).Single(x => x.StudentId == students[1].Id).Status);
    }

    [Fact]
    public async Task GradesValidateMoveFillAndDelete()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var first = test.AddGroup("10-А", "Анна");
        var second = test.AddGroup("10-Б", "Борис");
        test.AddSlot(teacher, first);
        test.AddSlot(teacher, second, start: new TimeOnly(11, 0), end: new TimeOnly(12, 0));
        var anna = Assert.Single(await test.Students.ForGroupAsync(first.Id));
        var boris = Assert.Single(await test.Students.ForGroupAsync(second.Id));
        Assert.Contains("Алгебра", await test.Grades.SubjectsAsync(teacher.Id, first.Id));
        Assert.Equal(0, await test.Grades.FillFinalAsync(teacher.Id, first.Id, "Алгебра"));

        var request = new SaveAssessmentRequest(first.Id, "Алгебра", "Контрольная", AssessmentKind.Test, FirstTuesday);
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Grades.SaveAsync(
            teacher.Id, null, request with { Title = " " }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Grades.SaveAsync(
            teacher.Id, null, request with { Title = new string('x', 121) }));
        var assessment = await test.Grades.SaveAsync(teacher.Id, null, request);
        Assert.Single(await test.Grades.ListAsync(teacher.Id, first.Id, " Алгебра "));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            test.Grades.SetGradeAsync(teacher.Id, assessment.Id, boris.Id, 80));
        await test.Grades.SetGradeAsync(teacher.Id, assessment.Id, anna.Id, 80);
        Assert.Equal(1, await test.Grades.FillFinalAsync(teacher.Id, first.Id, "Алгебра"));
        Assert.Equal(80, (await test.Db.FinalGrades.SingleAsync()).Score);
        await test.Grades.SetFinalAsync(teacher.Id, first.Id, "Алгебра", anna.Id, null);
        Assert.Empty(await test.Db.FinalGrades.ToListAsync());

        var movable = await test.Grades.SaveAsync(teacher.Id, null, request with { Title = "Без баллов" });
        var moved = await test.Grades.SaveAsync(teacher.Id, movable.Id,
            request with { GroupId = second.Id, Title = "Перенесённая" });
        Assert.Equal(second.Id, moved.GroupId);
        await test.Grades.DeleteAsync(teacher.Id, movable.Id);
        await test.Grades.DeleteAsync(teacher.Id, assessment.Id);
        Assert.Empty(await test.Grades.ListAsync(teacher.Id, null, null));
    }

    [Fact]
    public async Task ExportsEscapeFormulaeAndCountBothFormats()
    {
        using var test = new TestDb();
        var matrix = new JournalMatrix("Преподаватель", "10-А", null, FirstTuesday, FirstTuesday, 2, [],
            [new JournalStudent(1, "=1+1", [], 0, 0, 0, 0, 0, 0, null, null)]);

        var csv = test.Export.JournalCsv(matrix);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"'=1+1\"", csv);
        Assert.NotEmpty(test.Export.JournalXlsx(matrix, "Журнал"));
        Assert.Equal("Болезнь", ExportService.ExcuseReasonText(ExcuseReason.Illness));
        Assert.Equal("Семейные обстоятельства", ExportService.ExcuseReasonText(ExcuseReason.Family));
        Assert.Equal("Учебная/служебная необходимость", ExportService.ExcuseReasonText(ExcuseReason.Official));
        Assert.Equal("Другая причина", ExportService.ExcuseReasonText(ExcuseReason.Other));
        Assert.Equal("2", (await test.Db.AppSettings.SingleAsync(x => x.Key == "stats.reports_exported")).Value);
    }

    [Fact]
    public async Task AdminAccountValidationAndDisabledRefresh()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var factory = new AdminFactory(directory);
            using var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("teacher", "teacher123"), JsonOptions);
            login.EnsureSuccessStatusCode();
            var adminToken = (await login.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions))!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);
            var created = await client.PostAsJsonAsync("/api/admin/teachers",
                new SaveTeacherRequest("second", "Второй преподаватель", false, true), JsonOptions);
            created.EnsureSuccessStatusCode();
            var password = (await created.Content.ReadFromJsonAsync<TemporaryPasswordDto>(JsonOptions))!;
            var accounts = (await client.GetFromJsonAsync<List<TeacherAccountDto>>("/api/admin/teachers", JsonOptions))!;
            var admin = accounts.Single(x => x.Login == "teacher");
            var second = accounts.Single(x => x.Login == "second");

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/teachers",
                new SaveTeacherRequest("a b", "Ошибка", false, true), JsonOptions)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/admin/teachers",
                new SaveTeacherRequest("ab", "Ошибка", false, true), JsonOptions)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/admin/teachers/{second.Id}",
                new SaveTeacherRequest("teacher", "Второй преподаватель", false, true), JsonOptions)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/admin/teachers/{admin.Id}",
                new SaveTeacherRequest("teacher", admin.FullName, false, true), JsonOptions)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/admin/teachers/{admin.Id}",
                new SaveTeacherRequest("teacher", admin.FullName, true, false), JsonOptions)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.PostAsync($"/api/admin/teachers/{admin.Id}/reset-password", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/admin/teachers/999999",
                new SaveTeacherRequest("missing", "Нет", false, true), JsonOptions)).StatusCode);

            client.DefaultRequestHeaders.Authorization = null;
            var secondLogin = await client.PostAsJsonAsync("/api/auth/login",
                new LoginRequest("second", password.Password, true), JsonOptions);
            secondLogin.EnsureSuccessStatusCode();
            var secondToken = (await secondLogin.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions))!;
            Assert.NotNull(secondToken.RefreshToken);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/admin/teachers/{second.Id}",
                new SaveTeacherRequest("second", "Второй преподаватель", false, false), JsonOptions)).StatusCode);
            client.DefaultRequestHeaders.Authorization = null;
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh",
                new RefreshRequest(secondToken.RefreshToken!), JsonOptions)).StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAutoCreationIsDueOnceAndCorruptArchiveIsRejected()
    {
        using var fixture = new BackupFixture();
        Assert.Empty(fixture.Backups.List());
        Assert.Null(fixture.Backups.Resolve("..\\x"));
        await fixture.Backups.CreateAsync(false);
        Assert.Single(fixture.Backups.List());
        await fixture.Backups.AutoBackupIfDueAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Backups.List().Count);
        await fixture.Backups.AutoBackupIfDueAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Backups.List().Count);
        using var damaged = new MemoryStream(Encoding.UTF8.GetBytes("not a zip archive"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Backups.RestoreAsync(damaged));
        Assert.Contains("Это не резервная копия BiStudent", error.Message);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class AdminFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default",
                $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
            builder.UseSetting("Demo:Enabled", "true");
        }

        private static string ServerRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "BiStudent.Server");
                if (File.Exists(Path.Combine(candidate, "seed", "students.demo.csv"))) return candidate;
            }
            throw new DirectoryNotFoundException("BiStudent.Server/seed/students.demo.csv");
        }
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider services;
        private readonly RecognitionService recognition;
        public BackupService Backups { get; }

        public BackupFixture()
        {
            Directory.CreateDirectory(root);
            var database = Path.Combine(root, "bistudent.db");
            var paths = new StoragePaths(database, new SqliteConnectionStringBuilder {
                DataSource = database, Pooling = false
            }.ToString());
            services = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite(paths.ConnectionString))
                .BuildServiceProvider();
            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                DatabaseInitializer.Initialize(db);
                db.Groups.Add(new Group { Name = "10-А", Students = [new Student { FullName = "Анна" }] });
                db.SaveChanges();
            }
            recognition = new RecognitionService(services.GetRequiredService<IServiceScopeFactory>(),
                DataProtectionProvider.Create("tests"), new ConfigurationBuilder().Build());
            Backups = new BackupService(paths, services.GetRequiredService<IServiceScopeFactory>(),
                recognition, new SessionGeneration(paths), NullLogger<BackupService>.Instance);
        }

        public void Dispose()
        {
            recognition.Dispose();
            services.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
