using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using BiStudent.Server.Services;
using BiStudent.Server.Data;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

// Импорт расписания колледжа и списков групп из Excel.
public sealed class ImportTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public ImportTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ImportFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    /// Мини-копия формата расписания колледжа: две группы, объединённые ячейки дня и звонка, строки преподавателей.
    internal static byte[] CollegeSchedule()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("4 курс");
        sheet.Cell(3, 1).Value = "САБАҚ КЕСТЕСІ · 4 курс · 1 СЕМЕСТР (недели 1-18, 31.08.2026–01.01.2027)";
        foreach (var (column, group) in new[] { (5, "23-ПО-103-4р-1"), (10, "23-ПО-103-4р-2") })
        {
            sheet.Cell(6, column - 4).Value = "Күні Дни";
            sheet.Cell(6, column - 3).Value = "Қоңырау уақыты Время звонка";
            sheet.Cell(6, column - 2).Value = "No ; пары";
            sheet.Cell(6, column - 1).Value = "Дәрісхана ; Аудитория";
            sheet.Cell(6, column).Value = group;
            sheet.Cell(7, column - 4).Value = "Дүйсенбі / Понедельник";
            sheet.Range(7, column - 4, 10, column - 4).Merge();
            sheet.Cell(7, column - 3).Value = "08:00 09:20";
            sheet.Range(7, column - 3, 8, column - 3).Merge();
            sheet.Cell(9, column - 3).Value = "09:30 10:50";
            sheet.Range(9, column - 3, 10, column - 3).Merge();
            sheet.Cell(7, column - 2).Value = 1;
            sheet.Cell(9, column - 2).Value = 2;
        }
        sheet.Cell(7, 5).Value = "Искусственный интеллект OpenCV · 711 · нед. 6-7\n[ПРАКТИКА] База данных PL SQL/MONGODB · уточнить · нед. 3 (разово)";
        sheet.Cell(8, 5).Value = "Ниязова М.Е. · 711\nХамит А.Ү. · уточнить";
        sheet.Cell(9, 5).Value = "[СРОП] СРОП · Автоматизация DevOps · 504 · с 23-ПО-103-4р-2 · нед. 1 (разово), 5-7";
        sheet.Cell(10, 5).Value = "вакансия (Основы экономики и  предпринимательской деятельности) · 504";
        sheet.Cell(7, 10).Value = "[ПРАКТИКА] Программирование С# · 505 · нед. 2-4    Искусственный интеллект OpenCV -уточнить нед, 14-15";
        sheet.Cell(8, 10).Value = "Ниязова -505";
        sheet.Cell(9, 10).Value = "Непонятная запись без недель";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Parser_ReadsCollegeFormat()
    {
        using var stream = new MemoryStream(CollegeSchedule());
        var parsed = CollegeScheduleParser.Parse(stream, new DateOnly(2026, 9, 1));
        Assert.Equal(new DateOnly(2026, 8, 31), parsed.SemesterStart);
        Assert.Equal(["23-ПО-103-4р-1", "23-ПО-103-4р-2"], parsed.Groups.Select(x => x.Group));
        var first = parsed.Lessons.Where(x => x.Group == "23-ПО-103-4р-1").ToList();
        Assert.Contains(first, x => x is { Subject: "Искусственный интеллект OpenCV", Room: "711", Teacher: "Ниязова М.Е.", FromWeek: 6, ToWeek: 7, Once: false,
            Day: DayOfWeek.Monday } && x.Start == new TimeOnly(8, 0) && x.End == new TimeOnly(9, 20));
        Assert.Contains(first, x => x is { Subject: "База данных PL SQL/MONGODB", Room: null, Teacher: "Хамит А.Ү.", FromWeek: 3, ToWeek: 3, Once: true });
        // СРОП: дисциплина — второе поле, «с группой» — не аудитория; строка вакансии с двойным пробелом не рвётся.
        var srop = first.Where(x => x.Subject == "Автоматизация DevOps").ToList();
        Assert.Equal(2, srop.Count);
        Assert.All(srop, x => Assert.Equal(("504", CollegeScheduleParser.Vacancy, new TimeOnly(9, 30)), (x.Room, x.Teacher, x.Start)));
        Assert.Contains(srop, x => x is { FromWeek: 1, Once: true });
        Assert.Contains(srop, x => x is { FromWeek: 5, ToWeek: 7, Once: false });
        var second = parsed.Lessons.Where(x => x.Group == "23-ПО-103-4р-2").ToList();
        Assert.Contains(second, x => x is { Subject: "Программирование С#", Room: "505", FromWeek: 2, ToWeek: 4 });
        Assert.Contains(second, x => x is { Subject: "Искусственный интеллект OpenCV", Room: null, FromWeek: 14, ToWeek: 15 });
        // «Ниязова -505» сводится к единственной полной записи с той же фамилией.
        Assert.All(second, x => Assert.Equal("Ниязова М.Е.", x.Teacher));
        Assert.Single(parsed.Warnings);
        Assert.Contains("Непонятная запись", parsed.Warnings[0]);
    }

    [Theory]
    [InlineData("[ПРАКТИКА] Программирование С# · 505 · нед.5,6,7", "Программирование С#", "505", "5-5,6-6,7-7")]
    [InlineData("[ПРАКТИКА] Программирование С# -нед.5-7", "Программирование С#", null, "5-7")]
    [InlineData("[ПРАКТИКА] Программирование С# -5-7", "Программирование С#", null, "5-7")]
    [InlineData("Автоматизация DevOps · 711 · нед. 8 (разово)", "Автоматизация DevOps", "711", "8-8")]
    [InlineData("[ПРАКТИКА] Программирование С# ·504-нед. 9 (разово)", "Программирование С#", "504", "9-9")]
    [InlineData("[ПРАКТИКА] Программирование С# -уточн-нед. 8-9", "Программирование С#", null, "8-9")]
    [InlineData("[ЛЕКЦИЯ] Основы экономики · 804 · с 23-ПО-103-4р-1 · нед. 5-7, 14 (разово)", "Основы экономики", "804", "5-7,14-14")]
    public void Parser_ToleratesHandEditedEntries(string entry, string subject, string? room, string weeks)
    {
        Assert.True(CollegeScheduleParser.TryParseEntry(entry, out var parsedSubject, out var parsedRoom, out var parsedWeeks));
        Assert.Equal(subject, parsedSubject);
        Assert.Equal(room, parsedRoom);
        Assert.Equal(weeks, string.Join(",", parsedWeeks.Select(x => $"{x.From}-{x.To}")));
    }

    [Theory]
    [InlineData("Ниязова М.Е.", "niyazova.me")]
    [InlineData("Хамит А.Ү.", "khamit.au")]
    [InlineData("Байтукешов Т.С.", "baytukeshov.ts")]
    [InlineData("Халель", "khalel")]
    public void Login_IsTransliterated(string name, string login) => Assert.Equal(login, ScheduleImportService.Login(name));

    [Fact]
    public async Task ScheduleImport_CreatesTeachersGroupsAndLessons_RejectsRepeat()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var file = CollegeSchedule();
        var preview = await PostFileAsync<ScheduleImportPreviewDto>("/api/schedule/import-xlsx/preview", file);
        Assert.Equal(new DateOnly(2026, 8, 31), preview.SemesterStart);
        Assert.Equal(2, preview.Groups.Count);
        Assert.Contains("Ниязова М.Е.", preview.Teachers);
        Assert.Single(preview.Warnings);

        var result = await PostFileAsync<ScheduleImportResultDto>("/api/schedule/import-xlsx?groups=23-ПО-103-4р-1", file);
        Assert.Equal(4, result.Added);
        Assert.Equal(0, result.Skipped);
        var niyazova = Assert.Single(result.Teachers, x => x.FullName == "Ниязова М.Е.");
        Assert.True(niyazova is { Created: true, Active: true, Login: "niyazova.me" });
        Assert.NotNull(niyazova.TemporaryPassword);
        var vacancy = Assert.Single(result.Teachers, x => x.FullName == CollegeScheduleParser.Vacancy);
        Assert.True(vacancy is { Active: false, TemporaryPassword: null });

        var loaded = await PostFileAsync<ScheduleImportPreviewDto>("/api/schedule/import-xlsx/preview", file);
        Assert.True(Assert.Single(loaded.Groups, x => x.Group == "23-ПО-103-4р-1").AlreadyLoaded);
        var again = await client.PostAsync("/api/schedule/import-xlsx?groups=23-ПО-103-4р-1", new ByteArrayContent(file));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("уже загружено", await again.Content.ReadAsStringAsync());
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(4, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScheduleSlots.CountAsync(x => x.Group.Name == "23-ПО-103-4р-1"));

        // Новый преподаватель входит временным паролем и видит своё занятие в 6-ю неделю (понедельник 05.10.2026).
        using var teacherClient = factory.CreateClient();
        var token = await LoginAsync(teacherClient, "niyazova.me", niyazova.TemporaryPassword!);
        teacherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var day = await teacherClient.GetFromJsonAsync<List<LessonDto>>("/api/schedule/day?date=2026-10-05", JsonOptions);
        Assert.Contains(day!, x => x.Slot is { Subject: "Искусственный интеллект OpenCV", Room: "711", GroupName: "23-ПО-103-4р-1" });
        Assert.DoesNotContain((await teacherClient.GetFromJsonAsync<List<LessonDto>>("/api/schedule/day?date=2026-10-19", JsonOptions))!,
            x => x.Slot.Subject == "Искусственный интеллект OpenCV");
        // Импорт создаёт учётные записи — неадмину запрещён.
        var forbidden = await teacherClient.PostAsync("/api/schedule/import-xlsx?groups=23-ПО-103-4р-1", new ByteArrayContent(file));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task ScheduleImport_ReplacesChangedLessonsAndKeepsMatchedId()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var path = "/api/schedule/import-xlsx?groups=23-ПО-103-4р-1";
        await PostFileAsync<ScheduleImportResultDto>(path, CollegeSchedule());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var matched = await db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.Subject == "Искусственный интеллект OpenCV");
        var removed = await db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.Subject == "База данных PL SQL/MONGODB");

        var result = await PostFileAsync<ScheduleImportResultDto>(path, ChangedSchedule());

        Assert.Equal((1, 1, 1), (result.Added, result.Updated, result.Removed));
        var slots = await db.ScheduleSlots.AsNoTracking().Where(x => x.GroupId == matched.GroupId).ToListAsync();
        Assert.Equal(4, slots.Count);
        Assert.Contains(slots, x => x.Id == matched.Id && x.Room == "712" &&
            x.ValidTo == new DateOnly(2026, 10, 25));
        Assert.DoesNotContain(slots, x => x.Id == removed.Id);
        Assert.Single(slots, x => x.Subject == "Новый предмет");
    }

    [Fact]
    public async Task ScheduleImport_KeepsAttendanceOnRemovedLesson()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var path = "/api/schedule/import-xlsx?groups=23-ПО-103-4р-1";
        await PostFileAsync<ScheduleImportResultDto>(path, CollegeSchedule());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slot = await db.ScheduleSlots.SingleAsync(x => x.Subject == "База данных PL SQL/MONGODB");
        var student = new Student { FullName = "Тестовый студент", GroupId = slot.GroupId };
        db.Students.Add(student);
        await db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 14);
        db.AttendanceMarks.Add(new AttendanceMark
        {
            SlotId = slot.Id, StudentId = student.Id, Date = date,
            Status = AttendanceStatus.Present, Method = MarkMethod.Manual, MarkedAt = date.ToDateTime(new TimeOnly(9, 0))
        });
        await db.SaveChangesAsync();

        await PostFileAsync<ScheduleImportResultDto>(path, ChangedSchedule());

        var archived = await db.ScheduleSlots.AsNoTracking().SingleAsync(x => x.Id == slot.Id);
        Assert.Equal(date, archived.ValidTo);
        Assert.Single(await db.AttendanceMarks.AsNoTracking().Where(x => x.SlotId == slot.Id).ToListAsync());
    }

    private static byte[] ChangedSchedule()
    {
        using var workbook = new XLWorkbook(new MemoryStream(CollegeSchedule()));
        workbook.Worksheet("4 курс").Cell(7, 5).Value =
            "Искусственный интеллект OpenCV · 712 · нед. 6-8\n[ПРАКТИКА] Новый предмет · 601 · нед. 3 (разово)";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task ScheduleImport_RejectsUnknownGroupsAndBrokenFiles()
    {
        await AuthenticateAsync("teacher", "teacher123");
        var unknown = await client.PostAsync("/api/schedule/import-xlsx?groups=99-XX-000-1", new ByteArrayContent(CollegeSchedule()));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var none = await client.PostAsync("/api/schedule/import-xlsx", new ByteArrayContent(CollegeSchedule()));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        var broken = await client.PostAsync("/api/schedule/import-xlsx/preview", new ByteArrayContent("не excel"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
    }

    [Fact]
    public async Task StudentImport_ReadsJournalWorkbook()
    {
        await AuthenticateAsync("teacher", "teacher123");
        using var workbook = new XLWorkbook();
        foreach (var (group, names) in new[] { ("99-ИМ-001-1", new[] { "Абдиев Артур ", " Ковалев  Тимур" }), ("99-ИМ-001-2", new[] { "Ли Кирилл" }) })
        {
            var sheet = workbook.Worksheets.Add(group);
            sheet.Cell(2, 1).Value = "#"; sheet.Cell(2, 2).Value = "ФИО"; sheet.Cell(2, 3).Value = 16;
            for (var i = 0; i < names.Length; i++) { sheet.Cell(3 + i, 1).Value = i + 1; sheet.Cell(3 + i, 2).Value = names[i]; }
            sheet.Cell(4 + names.Length, 2).Value = "куратор:Рахимкужаева Алия Рахимберлиевна";
        }
        workbook.Worksheets.Add("Пустой").Cell(1, 1).Value = "заметки";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var result = await PostFileAsync<ImportResultDto>("/api/students/import", stream.ToArray());
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Skipped);
        Assert.Single(result.Errors, x => x.Contains("Пустой"));
        var groups = await client.GetFromJsonAsync<List<GroupDto>>("/api/groups", JsonOptions);
        var first = Assert.Single(groups!, x => x.Name == "99-ИМ-001-1");
        var students = await client.GetFromJsonAsync<List<StudentDto>>($"/api/groups/{first.Id}/students", JsonOptions);
        Assert.Equal(["Абдиев Артур", "Ковалев Тимур"], students!.Select(x => x.FullName).Order());

        var again = await PostFileAsync<ImportResultDto>("/api/students/import", stream.ToArray());
        Assert.Equal((0, 3), (again.Added, again.Skipped));
        // CSV по-прежнему поддерживается тем же маршрутом.
        var csv = await PostFileAsync<ImportResultDto>("/api/students/import", "ФИО;Группа\r\nНовиков Пётр;99-ИМ-001-2\r\n"u8.ToArray());
        Assert.Equal(1, csv.Added);
    }

    private async Task<T> PostFileAsync<T>(string path, byte[] file)
    {
        var response = await client.PostAsync(path, new ByteArrayContent(file));
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task AuthenticateAsync(string login, string password)
    {
        var token = await LoginAsync(client, login, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient target, string login, string password)
    {
        var response = await target.PostAsJsonAsync("/api/auth/login", new LoginRequest(login, password), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions))!;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    private sealed class ImportFactory(string directory) : WebApplicationFactory<Program>
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
