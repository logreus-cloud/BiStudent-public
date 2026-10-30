using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

public sealed class ScheduleConflictTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public ScheduleConflictTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ScheduleFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task TeacherImport_WithoutTeacherIdAssignsOwnSlotsAndCreatesNoAccounts()
    {
        var teacher = await CreateNonAdminAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teacherCount = await db.Teachers.CountAsync();

        var result = await PostFileAsync<ScheduleImportResultDto>(
            "/api/schedule/import-xlsx?groups=23-ПО-103-4р-1", ImportTests.CollegeSchedule());

        Assert.Equal(4, result.Added);
        var imported = Assert.Single(result.Teachers);
        Assert.Equal(teacher.Login, imported.Login);
        Assert.False(imported.Created);
        Assert.Null(imported.TemporaryPassword);
        var slots = await db.ScheduleSlots.AsNoTracking()
            .Where(x => x.Group.Name == "23-ПО-103-4р-1").ToListAsync();
        Assert.Equal(result.Added, slots.Count);
        Assert.All(slots, x => Assert.Equal(teacher.Id, x.TeacherId));
        Assert.Equal(teacherCount, await db.Teachers.CountAsync());
    }

    [Fact]
    public async Task TeacherImport_WithAnotherTeacherIdReturnsForbidden()
    {
        var teacher = await CreateNonAdminAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var otherId = await db.Teachers.Where(x => x.Id != teacher.Id).Select(x => x.Id).FirstAsync();

        var response = await client.PostAsync(
            $"/api/schedule/import-xlsx?groups=23-ПО-103-4р-1&teacherId={otherId}",
            new ByteArrayContent(ImportTests.CollegeSchedule()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("только администратор", error.Message);
    }

    [Fact]
    public async Task TeacherImport_KeepsAnotherTeachersSlotInSameGroup()
    {
        var teacher = await CreateNonAdminAsync();
        int otherSlotId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var otherId = await db.Teachers.Where(x => x.Id != teacher.Id).Select(x => x.Id).FirstAsync();
            var group = new Group { Name = "23-ПО-103-4р-1" };
            db.Groups.Add(group);
            await db.SaveChangesAsync();
            var otherSlot = new ScheduleSlot
            {
                TeacherId = otherId, GroupId = group.Id, Subject = "Чужая пара",
                DayOfWeek = DayOfWeek.Monday, Start = new TimeOnly(15, 0), End = new TimeOnly(16, 0),
                Parity = WeekParity.Every, ValidFrom = new DateOnly(2026, 8, 31)
            };
            db.ScheduleSlots.Add(otherSlot);
            await db.SaveChangesAsync();
            otherSlotId = otherSlot.Id;
        }

        await PostFileAsync<ScheduleImportResultDto>(
            "/api/schedule/import-xlsx?groups=23-ПО-103-4р-1", ImportTests.CollegeSchedule());

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slots = await checkDb.ScheduleSlots.AsNoTracking()
            .Where(x => x.Group.Name == "23-ПО-103-4р-1").ToListAsync();
        Assert.Contains(slots, x => x.Id == otherSlotId && x.Subject == "Чужая пара" && x.TeacherId != teacher.Id);
        Assert.Equal(4, slots.Count(x => x.TeacherId == teacher.Id));
    }

    [Fact]
    public async Task AddSlot_WithOverlapReturnsConflictAndBlockingId()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var groupId = await AddGroupAsync();
        var date = new DateOnly(2035, 9, 4);
        var existing = await PostSlotAsync(OneOff(groupId, "Первая пара", date));

        var response = await client.PostAsJsonAsync("/api/schedule/slots",
            OneOff(groupId, "Конфликт", date) with
            {
                Start = new TimeOnly(3, 30),
                End = new TimeOnly(4, 30)
            }, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ScheduleConflictDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("уже есть пара", error.Message);
        Assert.Contains(error.Conflicts, x => x.Id == existing.Id);
    }

    [Fact]
    public async Task ReplaceRegularWithOneOff_CancelsOnlyThatDate()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        var date = new DateOnly(2026, 9, 8);
        var regular = test.AddSlot(teacher, group, day: date.DayOfWeek);
        var oneOff = test.NewSlot(teacher, group, day: date.DayOfWeek,
            validFrom: date, validTo: date, oneOffDate: date, subject: "Замена");

        var saved = await test.Schedule.ReplaceAndSaveAsync(oneOff, [regular.Id], false);

        Assert.Equal(date, saved.OneOffDate);
        Assert.Equal(saved.Id, (await test.Db.ScheduleSlots.AsNoTracking()
            .SingleAsync(x => x.Subject == "Замена")).Id);
        Assert.True(await test.Db.ScheduleSlots.AnyAsync(x => x.Id == regular.Id));
        Assert.True(await test.Db.SlotCancellations.AnyAsync(x => x.SlotId == regular.Id && x.Date == date));
    }

    [Fact]
    public async Task ReplaceStartedRegular_EndsOldYesterdayAndStartsNewToday()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-21);
        var old = test.AddSlot(teacher, group, day: today.DayOfWeek, validFrom: from);
        var replacement = test.NewSlot(teacher, group, day: today.DayOfWeek,
            validFrom: from, subject: "Новая пара");

        var saved = await test.Schedule.ReplaceAndSaveAsync(replacement, [old.Id], false);

        var archived = await test.Db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.Id == old.Id);
        Assert.Equal(today.AddDays(-1), archived.ValidTo);
        Assert.Equal(today, saved.ValidFrom);
        Assert.Equal(today, (await test.Db.ScheduleSlots.AsNoTracking()
            .SingleAsync(x => x.Id == saved.Id)).ValidFrom);
    }

    [Fact]
    public async Task ReplaceRegular_DoesNotExtendEarlierEndOrProcessExpiredConflict()
    {
        using var test = new TestDb();
        var teacher = test.AddTeacher();
        var group = test.AddGroup("ИВТ-1");
        var today = DateOnly.FromDateTime(DateTime.Now);
        var from = today.AddDays(-35);
        var ended = test.AddSlot(teacher, group, day: today.DayOfWeek, validFrom: from);
        ended.ValidTo = today.AddDays(-14);
        await test.Db.SaveChangesAsync();
        var active = test.AddSlot(teacher, group, day: today.DayOfWeek, validFrom: today.AddDays(-21));
        var replacement = test.NewSlot(teacher, group, day: today.DayOfWeek,
            validFrom: from, subject: "Новая пара");

        var saved = await test.Schedule.ReplaceAndSaveAsync(replacement, [ended.Id, active.Id], false);

        Assert.Equal(today, saved.ValidFrom);
        Assert.Equal(today.AddDays(-14), (await test.Db.ScheduleSlots.AsNoTracking()
            .SingleAsync(x => x.Id == ended.Id)).ValidTo);
        Assert.Equal(today.AddDays(-1), (await test.Db.ScheduleSlots.AsNoTracking()
            .SingleAsync(x => x.Id == active.Id)).ValidTo);
    }

    [Fact]
    public async Task UpdateRegular_ReplacingRegularReturnsBadRequestWithoutMovingValidFrom()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var groupId = await AddGroupAsync();
        var old = await PostSlotAsync(new SaveSlotRequest(groupId, null, "Старая пара", null,
            DayOfWeek.Sunday, new TimeOnly(3, 0), new TimeOnly(4, 0), WeekParity.Every, null));
        var editing = await PostSlotAsync(new SaveSlotRequest(groupId, null, "Редактируемая пара", null,
            DayOfWeek.Sunday, new TimeOnly(4, 0), new TimeOnly(5, 0), WeekParity.Every, null));

        var response = await client.PutAsJsonAsync($"/api/schedule/slots/{editing.Id}",
            new SaveSlotRequest(groupId, null, "Редактируемая пара", null,
                DayOfWeek.Sunday, new TimeOnly(3, 0), new TimeOnly(4, 0), WeekParity.Every, null, [old.Id]), JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("Регулярную пару нельзя заменить при редактировании", error.Message);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(editing.ValidFrom, (await db.ScheduleSlots.AsNoTracking()
            .SingleAsync(x => x.Id == editing.Id)).ValidFrom);
    }

    [Fact]
    public async Task RestoreCancelledRegular_WithOneOffReturnsConflictAndKeepsCancellation()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var groupId = await AddGroupAsync();
        var date = new DateOnly(2035, 9, 4);
        var regular = await PostSlotAsync(new SaveSlotRequest(groupId, null, "Регулярная пара", null,
            date.DayOfWeek, new TimeOnly(3, 0), new TimeOnly(4, 0), WeekParity.Every, null));
        var cancellation = await client.PostAsJsonAsync(
            $"/api/schedule/slots/{regular.Id}/cancel", new CancelLessonRequest(date, "Отменена"), JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);
        var oneOff = await PostSlotAsync(OneOff(groupId, "Разовая пара", date));

        var response = await client.PostAsJsonAsync($"/api/schedule/slots/{regular.Id}/restore",
            new RestoreLessonRequest(date), JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ScheduleConflictDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains(error.Conflicts, x => x.Id == oneOff.Id);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.SlotCancellations.AnyAsync(x => x.SlotId == regular.Id && x.Date == date));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplaceOneOff_RemovesUnmarkedSlotButRejectsSlotWithAttendance(bool hasAttendance)
    {
        await AuthenticateAsync("teacher", "teacher123");
        var groupId = await AddGroupAsync();
        var date = new DateOnly(2035, 9, 4);
        var old = await PostSlotAsync(OneOff(groupId, "Старая пара", date));
        if (hasAttendance)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = new Student { FullName = "Тестовый студент", GroupId = groupId };
            db.Students.Add(student);
            await db.SaveChangesAsync();
            db.AttendanceMarks.Add(new AttendanceMark
            {
                SlotId = old.Id, StudentId = student.Id, Date = date,
                Status = AttendanceStatus.Present, Method = MarkMethod.Manual,
                MarkedAt = date.ToDateTime(new TimeOnly(4, 0))
            });
            await db.SaveChangesAsync();
        }

        var response = await client.PostAsJsonAsync("/api/schedule/slots",
            OneOff(groupId, "Новая пара", date, [old.Id]), JsonOptions);

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (hasAttendance)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
            Assert.NotNull(error);
            Assert.Contains("с отметками нельзя заменить", error.Message);
            Assert.True(await checkDb.ScheduleSlots.AnyAsync(x => x.Id == old.Id));
            Assert.False(await checkDb.ScheduleSlots.AnyAsync(x => x.Subject == "Новая пара"));
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var saved = await response.Content.ReadFromJsonAsync<SlotDto>(JsonOptions);
            Assert.NotNull(saved);
            Assert.False(await checkDb.ScheduleSlots.AnyAsync(x => x.Id == old.Id));
            Assert.True(await checkDb.ScheduleSlots.AnyAsync(x => x.Id == saved.Id && x.Subject == "Новая пара"));
        }
    }

    [Fact]
    public async Task ReplaceSlotIds_WithNonConflictingSlotReturnsBadRequest()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var groupId = await AddGroupAsync();
        var date = new DateOnly(2035, 9, 4);
        var unrelated = await PostSlotAsync(OneOff(groupId, "Другая дата", date));

        var response = await client.PostAsJsonAsync("/api/schedule/slots",
            OneOff(groupId, "Новая пара", date.AddDays(1), [unrelated.Id]), JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("не конфликтует", error.Message);
    }

    [Fact]
    public async Task OneOff_OnCancelledRegularDateHasNoConflict()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var groupId = await AddGroupAsync();
        var date = new DateOnly(2035, 9, 4);
        var regular = await PostSlotAsync(new SaveSlotRequest(groupId, null, "Регулярная пара", null,
            date.DayOfWeek, new TimeOnly(3, 0), new TimeOnly(4, 0), WeekParity.Every, null));
        var cancellation = await client.PostAsJsonAsync(
            $"/api/schedule/slots/{regular.Id}/cancel", new CancelLessonRequest(date, "Отменена"), JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, cancellation.StatusCode);

        var replacement = await client.PostAsJsonAsync("/api/schedule/slots",
            OneOff(groupId, "Разовая пара", date), JsonOptions);

        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var saved = await replacement.Content.ReadFromJsonAsync<SlotDto>(JsonOptions);
        Assert.NotNull(saved);
        Assert.Equal(date, saved.OneOffDate);
    }

    private static SaveSlotRequest OneOff(int groupId, string subject, DateOnly date, List<int>? replaceSlotIds = null) =>
        new(groupId, null, subject, null, date.DayOfWeek, new TimeOnly(3, 0), new TimeOnly(4, 0),
            WeekParity.Every, date, replaceSlotIds);

    private async Task<int> AddGroupAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var group = new Group { Name = "Конфликты" };
        db.Groups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<SlotDto> PostSlotAsync(SaveSlotRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/schedule/slots", request, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SlotDto>(JsonOptions))!;
    }

    private async Task<T> PostFileAsync<T>(string path, byte[] file)
    {
        var response = await client.PostAsync(path, new ByteArrayContent(file));
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task<TeacherDto> CreateNonAdminAsync()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var result = await PostFileAsync<ScheduleImportResultDto>(
            "/api/schedule/import-xlsx?groups=23-ПО-103-4р-2", ImportTests.CollegeSchedule());
        var imported = Assert.Single(result.Teachers, x => x.FullName == "Ниязова М.Е.");
        Assert.True(imported.Created);
        Assert.NotNull(imported.TemporaryPassword);
        await AuthenticateAsync(imported.Login, imported.TemporaryPassword);
        return (await client.GetFromJsonAsync<TeacherDto>("/api/me", JsonOptions))!;
    }

    private async Task AuthenticateAsync(string login, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class ScheduleFactory(string directory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
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
}
