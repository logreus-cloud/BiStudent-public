using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BiStudent.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiStudent.Tests;

public sealed class ApiTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"BiStudent.Tests.{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> factory;
    private readonly HttpClient client;

    public ApiTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
        factory = new ApiFactory(temporaryDirectory);
        client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutTokenReturnsUnauthorized()
    {
        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithWrongPasswordReturnsErrorDto()
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("teacher", "wrong"), JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("Неверный", error.Message);
    }

    [Fact]
    public async Task Login_WithTokenCanReadMeGroupsAndDashboard()
    {
        await AuthenticateAsync();
        foreach (var path in new[] { "/api/me", "/api/groups", "/api/dashboard" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AddSlot_WithOverlapReturnsConflictWithSlots()
    {
        await AuthenticateAsync();
        var lessons = await client.GetFromJsonAsync<List<LessonDto>>("/api/schedule/day?date=2026-09-01", JsonOptions);
        Assert.NotNull(lessons);
        Assert.NotEmpty(lessons);
        var slot = lessons[0].Slot;
        var request = new SaveSlotRequest(slot.GroupId, null, "Конфликт", null,
            slot.DayOfWeek, slot.Start, slot.End, slot.Parity, null);
        var response = await client.PostAsJsonAsync("/api/schedule/slots", request, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ScheduleConflictDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("уже есть пара", error.Message);
        Assert.Contains(error.Conflicts, x => x.Id == slot.Id);
    }

    [Fact]
    public async Task Attendance_ForMissingSlotReturnsNotFound()
    {
        await AuthenticateAsync();
        var response = await client.GetAsync("/api/lessons/999999/2026-09-01/attendance");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorDto>(JsonOptions);
        Assert.NotNull(error);
        Assert.Contains("не найдена", error.Message);
    }

    private async Task AuthenticateAsync()
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("teacher", "teacher123"), JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions);
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
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
