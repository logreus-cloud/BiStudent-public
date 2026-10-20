using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

public sealed class ApiEndpointsTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public ApiEndpointsTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ApiFactory(temporaryDirectory);
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task SettingsDashboardAndPublicRoutes()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
        await AuthenticateAsync();
        var automation = await client.GetFromJsonAsync<AutomationSettingsDto>("/api/settings/automation", JsonOptions);
        Assert.True(automation!.SecondsPerManualMark > 0 && automation.MinutesPerManualReport > 0 && automation.FramesInRow > 0);
        var recognition = await client.GetFromJsonAsync<RecognitionSettingsDto>("/api/settings/recognition", JsonOptions);
        Assert.True(recognition!.MatchThreshold > 0 && recognition.FramesInRow > 0);
        var updated = new RecognitionSettingsDto(0.64, 0.04, 4);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/settings/recognition", updated, JsonOptions)).StatusCode);
        var saved = await client.GetFromJsonAsync<RecognitionSettingsDto>("/api/settings/recognition", JsonOptions);
        Assert.Equal(updated, saved);
        var temporary = await PostAsync<TemporaryPasswordDto>("/api/admin/teachers", new SaveTeacherRequest("regular", "Обычный преподаватель", false, true));
        using var other = factory.CreateClient();
        var token = await LoginAsync(other, "regular", temporary.Password);
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync("/api/settings/recognition", updated, JsonOptions)).StatusCode);
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Статистика"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Студент статистики", group.Id));
        var today = DateOnly.FromDateTime(DateTime.Now);
        var slot = await PostAsync<SlotDto>("/api/schedule/slots", new SaveSlotRequest(group.Id, null, "Статистика", null, today.DayOfWeek, new TimeOnly(2, 0), new TimeOnly(3, 0), WeekParity.Every, today));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/lessons/{slot.Id}/{today:yyyy-MM-dd}/grade", new GradeRequest(student.Id, 88), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/lessons/{slot.Id}/{today:yyyy-MM-dd}/attendance", new MarkRequest(student.Id, AttendanceStatus.Present), JsonOptions)).StatusCode);
        var dashboard = await client.GetFromJsonAsync<DashboardDto>("/api/dashboard", JsonOptions);
        Assert.NotNull(dashboard!.AverageScore30Days);
        var report = await client.GetFromJsonAsync<ReportDto>("/api/report", JsonOptions);
        Assert.NotNull(report!.Dashboard.AverageScore30Days);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/register/", root.Headers.Location?.ToString());
        var register = await client.GetAsync("/register");
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        var page = await client.GetAsync("/register/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.True(page.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task GroupsStudentsAndImport()
    {
        await AuthenticateAsync();
        var empty = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Пустая группа"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/groups", new CreateGroupRequest(empty.Name), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/groups/{empty.Id}")).StatusCode);
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Исходная группа"));
        var target = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Новая группа"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Старое имя", group.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/groups/{group.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/students/{student.Id}", new SaveStudentRequest("Новое имя", target.Id), JsonOptions)).StatusCode);
        var roster = await client.GetFromJsonAsync<List<StudentDto>>($"/api/groups/{target.Id}/students", JsonOptions);
        Assert.Contains(roster!, x => x.Id == student.Id && x.FullName == "Новое имя");
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/students/999999", new SaveStudentRequest("Имя", target.Id), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/students/{student.Id}")).StatusCode);
        var imported = await (await PostCsvAsync("/api/students/import", $"ФИО;Группа\nИмпортирован;{target.Name}\nИмпортирован;{target.Name}\n")).Content.ReadFromJsonAsync<ImportResultDto>(JsonOptions);
        Assert.Equal(1, imported!.Added);
        Assert.Equal(1, imported.Skipped);
    }

    [Fact]
    public async Task ScheduleCanBeEditedCancelledRestoredAndImported()
    {
        await AuthenticateAsync();
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
        var slot = await PostAsync<SlotDto>("/api/schedule/slots", new SaveSlotRequest(0, "Группа расписания", "Алгебра", null, date.DayOfWeek, new TimeOnly(2, 0), new TimeOnly(3, 0), WeekParity.Every, date));
        Assert.Contains((await client.GetFromJsonAsync<List<GroupDto>>("/api/groups", JsonOptions))!, x => x.Id == slot.GroupId);
        var day = await client.GetFromJsonAsync<List<LessonDto>>($"/api/schedule/day?date={date:yyyy-MM-dd}", JsonOptions);
        Assert.Contains(day!, x => x.Slot.Id == slot.Id);
        var week = await client.GetFromJsonAsync<List<LessonDto>>($"/api/schedule/week?monday={date:yyyy-MM-dd}", JsonOptions);
        Assert.Contains(week!, x => x.Slot.Id == slot.Id);
        var parity = await client.GetFromJsonAsync<WeekParity>($"/api/schedule/parity?date={date:yyyy-MM-dd}", JsonOptions);
        Assert.Contains(parity, new[] { WeekParity.Odd, WeekParity.Even });
        var updated = new SaveSlotRequest(slot.GroupId, null, "Геометрия", "12", date.DayOfWeek, slot.Start, slot.End, WeekParity.Every, date);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/schedule/slots/{slot.Id}", updated, JsonOptions)).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<List<LessonDto>>($"/api/schedule/day?date={date:yyyy-MM-dd}", JsonOptions))!, x => x.Slot.Id == slot.Id && x.Slot.Subject == "Геометрия");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/schedule/slots", updated with { Subject = " " }, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/schedule/slots", updated with { GroupId = 999999 }, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/schedule/slots/999999", updated, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/schedule/slots/{slot.Id}/cancel", new CancelLessonRequest(date, null), JsonOptions)).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<List<LessonDto>>($"/api/schedule/day?date={date:yyyy-MM-dd}", JsonOptions))!, x => x.Slot.Id == slot.Id && x.Cancelled);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/schedule/slots/{slot.Id}/restore", new RestoreLessonRequest(date), JsonOptions)).StatusCode);
        Assert.Contains((await client.GetFromJsonAsync<List<LessonDto>>($"/api/schedule/day?date={date:yyyy-MM-dd}", JsonOptions))!, x => x.Slot.Id == slot.Id && !x.Cancelled);
        var valid = await (await PostCsvAsync("/api/schedule/import", "День;Начало;Конец;Предмет;Группа;Аудитория;Чётность\nпн;05:00;06:00;История;Группа импорта;;каждая\n")).Content.ReadFromJsonAsync<ImportResultDto>(JsonOptions);
        Assert.True(valid!.Added > 0);
        var invalid = await (await PostCsvAsync("/api/schedule/import", "День;Начало;Конец;Предмет;Группа;Аудитория;Чётность\nнеизвестно;05:00;06:00;История;Группа импорта;;каждая\n")).Content.ReadFromJsonAsync<ImportResultDto>(JsonOptions);
        Assert.NotEmpty(invalid!.Errors);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/schedule/slots/{slot.Id}")).StatusCode);
    }

    [Fact]
    public async Task AttendanceAndJournalExports()
    {
        await AuthenticateAsync();
        var (group, first, slot, date) = await LessonAsync();
        var second = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Второй", group.Id));
        var third = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Третий", group.Id));
        var fourth = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Четвёртый", group.Id));
        var path = $"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/attendance";
        Assert.Equal(4, (await client.GetFromJsonAsync<List<AttendanceRowDto>>(path, JsonOptions))!.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new MarkRequest(first.Id, AttendanceStatus.Present), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new MarkRequest(second.Id, AttendanceStatus.Absent), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new MarkRequest(third.Id, AttendanceStatus.Excused, ExcuseReason.Illness, "Справка", true), JsonOptions)).StatusCode);
        var rows = await client.GetFromJsonAsync<List<AttendanceRowDto>>(path, JsonOptions);
        Assert.Equal(AttendanceStatus.Absent, rows!.Single(x => x.StudentId == second.Id).Status);
        Assert.True(rows!.Single(x => x.StudentId == third.Id) is { Status: AttendanceStatus.Excused, Reason: ExcuseReason.Illness, Comment: "Справка", HasDocument: true });
        var finished = await (await client.PostAsync($"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/finish", null)).Content.ReadFromJsonAsync<CountResult>(JsonOptions);
        Assert.Equal(1, finished!.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(path, new MarkRequest(first.Id, null), JsonOptions)).StatusCode);
        var present = await (await client.PostAsync($"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/all-present", null)).Content.ReadFromJsonAsync<CountResult>(JsonOptions);
        Assert.Equal(1, present!.Count);
        var journalPath = $"/api/journal/{group.Id}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&subject=Алгебра";
        var journal = await client.GetFromJsonAsync<JournalDto>(journalPath, JsonOptions);
        Assert.Contains(journal!.Columns, x => x.Subject == "Алгебра");
        Assert.Contains(journal.Students, x => x.StudentId == fourth.Id && x.Absent == 1);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/journal/{group.Id}?from=2026-11-03&to=2026-11-02")).StatusCode);
        var xlsx = await client.GetAsync($"/api/journal/{group.Id}/xlsx?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Журнал_", xlsx.Content.Headers.ContentDisposition?.FileNameStar ?? "");
        var csv = await client.GetAsync($"/api/journal/{group.Id}/csv?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.True((await csv.Content.ReadAsByteArrayAsync()).Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()));
    }

    [Fact]
    public async Task AttendanceRejectsMissingAndCancelledLesson()
    {
        await AuthenticateAsync();
        var (_, student, slot, date) = await LessonAsync();
        var path = $"/api/lessons/{slot.Id}/{date.AddDays(1):yyyy-MM-dd}/attendance";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync(path, new MarkRequest(student.Id, AttendanceStatus.Present), JsonOptions)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsJsonAsync($"/api/schedule/slots/{slot.Id}/cancel", new CancelLessonRequest(date, null), JsonOptions)).StatusCode);
        path = $"/api/lessons/{slot.Id}/{date:yyyy-MM-dd}/attendance";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync(path, new MarkRequest(student.Id, AttendanceStatus.Present), JsonOptions)).StatusCode);
    }

    [Fact]
    public async Task FutureLessonRejectsFinishButAllowsExcuseAndClear()
    {
        await AuthenticateAsync();
        var (group, student, _, date) = await LessonAsync();
        var slot = await PostAsync<SlotDto>("/api/schedule/slots", new SaveSlotRequest(group.Id, null, "Геометрия", null, date.DayOfWeek,
            new TimeOnly(4, 0), new TimeOnly(5, 0), WeekParity.Every, null));
        var future = date.AddDays(14);
        var futurePath = $"/api/lessons/{slot.Id}/{future:yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"{futurePath}/finish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"{futurePath}/attendance",
                new MarkRequest(student.Id, AttendanceStatus.Excused), JsonOptions)).StatusCode);

        var missing = future.AddDays(1);
        var missingPath = $"/api/lessons/{slot.Id}/{missing:yyyy-MM-dd}";
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"{missingPath}/attendance", new MarkRequest(student.Id, null), JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync($"{missingPath}/grade", new GradeRequest(student.Id, null), JsonOptions)).StatusCode);
    }

    private async Task<T> PostAsync<T>(string path, object value)
    {
        var response = await client.PostAsJsonAsync(path, value, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return result!;
    }

    private async Task<(GroupDto Group, StudentDto Student, SlotDto Slot, DateOnly Date)> LessonAsync()
    {
        var group = await PostAsync<GroupDto>("/api/groups", new CreateGroupRequest("Группа журнала"));
        var student = await PostAsync<StudentDto>("/api/students", new SaveStudentRequest("Первый", group.Id));
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-7);
        var slot = await PostAsync<SlotDto>("/api/schedule/slots", new SaveSlotRequest(group.Id, null, "Алгебра", null, date.DayOfWeek, new TimeOnly(2, 0), new TimeOnly(3, 0), WeekParity.Every, date));
        return (group, student, slot, date);
    }

    private async Task AuthenticateAsync()
    {
        var token = await LoginAsync(client, "teacher", "teacher123");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient target, string login, string password)
    {
        var response = await target.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.NotNull(token);
        return token;
    }

    private Task<HttpResponseMessage> PostCsvAsync(string path, string csv)
    {
        return client.PostAsync(path, new StringContent(csv, Encoding.UTF8, "text/csv"));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class ApiFactory(string directory) : WebApplicationFactory<Program>
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
