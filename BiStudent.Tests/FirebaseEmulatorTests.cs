using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

[CollectionDefinition("firebase-emulator", DisableParallelization = true)]
public sealed class FirebaseEmulatorCollection;

[Collection("firebase-emulator")]
public sealed class FirebaseEmulatorTests : IDisposable
{
    private const string Project = "demo-bistudent";
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        $"BiStudent.FirebaseTests.{Guid.NewGuid():N}");
    private readonly string? authHost = Environment.GetEnvironmentVariable("FIREBASE_AUTH_EMULATOR_HOST");
    private readonly string? firestoreHost = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");

    public FirebaseEmulatorTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task FirstAdministratorMigratesAndSyncs()
    {
        await ClearAsync();
        using var factory = Factory("a");
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        await RunAsync(client);
        var status = await client.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
        Assert.NotNull(status);
        Assert.Equal("Ok", status.State);
        Assert.Equal(0, status.Pending);
        using var http = new HttpClient();
        Assert.NotNull(await OwnerDocumentAsync(http, "meta/bootstrap"));
        var teachers = await OwnerDocumentsAsync(http, "Teachers");
        var admin = Assert.Single(teachers);
        var uid = admin.GetProperty("fields").GetProperty("FirebaseUid")
            .GetProperty("stringValue").GetString();
        Assert.False(string.IsNullOrWhiteSpace(uid));
        Assert.NotNull(await OwnerDocumentAsync(http, $"TeacherUids/{uid}"));
    }

    [Fact]
    public async Task NewTeacherReceivesDataOnAnotherComputer()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var firstClient = first.CreateClient();
        await BootstrapAsync(firstClient);
        var group = await PostAsync<GroupDto>(firstClient, "/api/groups",
            new CreateGroupRequest("Облачная группа"));
        await PostAsync<StudentDto>(firstClient, "/api/students",
            new SaveStudentRequest("Облачный студент", group.Id));
        var temporary = await PostAsync<TemporaryPasswordDto>(firstClient,
            "/api/admin/teachers", new SaveTeacherRequest("anna", "Анна Иванова", false, true));
        Assert.Equal("Ok", (await RunAsync(firstClient)).State);

        using var second = Factory("b");
        using var secondClient = second.CreateClient();
        Authorize(secondClient, await LoginAsync(secondClient, "anna", temporary.Password));
        Assert.Equal("Ok", (await RunAsync(secondClient)).State);
        using var scope = second.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Groups.AnyAsync(candidate => candidate.Name == "Облачная группа"));
        Assert.True(await db.Students.AnyAsync(
            candidate => candidate.FullName == "Облачный студент"));
    }

    [Fact]
    public async Task RulesRejectUnknownAndDisabledTeachers()
    {
        await ClearAsync();
        using var factory = Factory("a");
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        var temporary = await PostAsync<TemporaryPasswordDto>(client,
            "/api/admin/teachers", new SaveTeacherRequest("anna", "Анна Иванова", false, true));
        await RunAsync(client);
        using var http = new HttpClient();
        var unknown = await SignUpAsync(http, "unknown@bistudent.app", "Unknown-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden,
            await ReadAsAsync(http, unknown, "Students/anything"));
        var teacherToken = await FirebaseSignInAsync(
            http, "anna@bistudent.app", temporary.Password);
        Assert.Equal(HttpStatusCode.NotFound,
            await ReadAsAsync(http, teacherToken, "Groups/anything"));
        var accounts = await client.GetFromJsonAsync<List<TeacherAccountDto>>(
            "/api/admin/teachers");
        var teacher = Assert.Single(accounts!, candidate => candidate.Login == "anna");
        var disabled = await client.PutAsJsonAsync($"/api/admin/teachers/{teacher.Id}",
            new SaveTeacherRequest("anna", "Анна Иванова", false, false));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await RunAsync(client);
        Assert.Equal(HttpStatusCode.Forbidden,
            await ReadAsAsync(http, teacherToken, "Groups/anything"));
    }

    [Fact]
    public async Task AdministratorOnAnotherComputerUsesCloudAccount()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var firstClient = first.CreateClient();
        await BootstrapAsync(firstClient);
        await PostAsync<GroupDto>(firstClient, "/api/groups", new CreateGroupRequest("Облачная группа"));
        Assert.Equal("Ok", (await RunAsync(firstClient)).State);

        using var second = Factory("b");
        using var secondClient = second.CreateClient();
        // Новый компьютер: admin/admin либо не пускает (эмулятор сразу отвечает EMAIL_EXISTS), либо пускает только
        // к обязательной смене пароля (Firebase сначала отвечает WEAK_PASSWORD), и смена не отрезает его от облака.
        var local = await secondClient.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin"));
        if (local.StatusCode == HttpStatusCode.OK)
        {
            var token = await local.Content.ReadFromJsonAsync<TokenResponse>();
            Assert.NotNull(token);
            Authorize(secondClient, token);
            var changed = await secondClient.PostAsJsonAsync("/api/auth/change-password",
                new ChangePasswordRequest("admin", "Other-pass-1"));
            Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
            secondClient.DefaultRequestHeaders.Authorization = null;
        }
        else Assert.Equal(HttpStatusCode.Unauthorized, local.StatusCode);

        Authorize(secondClient, await LoginAsync(secondClient, "admin", "Admin-pass-1"));
        Assert.Equal("Ok", (await RunAsync(secondClient)).State);
        using var scope = second.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Groups.AnyAsync(candidate => candidate.Name == "Облачная группа"));
    }

    [Fact]
    public async Task OfflineLoginUsesLocalPasswordCache()
    {
        await ClearAsync();
        using (var online = Factory("a"))
        using (var client = online.CreateClient())
            await BootstrapAsync(client);

        using var offline = Factory("a", "127.0.0.1:1");
        using var offlineClient = offline.CreateClient();
        var known = await offlineClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("admin", "Admin-pass-1"));
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        var unknown = await offlineClient.PostAsJsonAsync("/api/auth/login",
            new LoginRequest("unknown", "Unknown-pass-1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
    }

    private async Task ClearAsync()
    {
        if (string.IsNullOrWhiteSpace(authHost) ||
            string.IsNullOrWhiteSpace(firestoreHost))
            Assert.Skip("Нужен Firebase Emulator: FIREBASE_AUTH_EMULATOR_HOST и FIRESTORE_EMULATOR_HOST.");
        using var http = new HttpClient();
        using var documents = await http.DeleteAsync(
            $"http://{firestoreHost}/emulator/v1/projects/{Project}/databases/(default)/documents");
        documents.EnsureSuccessStatusCode();
        using var accounts = await http.DeleteAsync(
            $"http://{authHost}/emulator/v1/projects/{Project}/accounts");
        accounts.EnsureSuccessStatusCode();
    }

    private EmulatorFactory Factory(string name, string? overrideAuthHost = null)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(path);
        return new EmulatorFactory(path, overrideAuthHost ?? authHost!, firestoreHost!);
    }

    private static async Task BootstrapAsync(HttpClient client)
    {
        Authorize(client, await LoginAsync(client, "admin", "admin"));
        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest("admin", "Admin-pass-1"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var token = await changed.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Authorize(client, token);
    }

    private static async Task<SyncStatusDto> RunAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/sync/run", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<SyncStatusDto>();
        Assert.NotNull(status);
        return status;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(result);
        return result!;
    }

    private static async Task<TokenResponse> LoginAsync(
        HttpClient client, string login, string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(login, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token;
    }

    private static void Authorize(HttpClient client, TokenResponse token) =>
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.AccessToken);

    private async Task<JsonElement?> OwnerDocumentAsync(HttpClient http, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{firestoreHost}/v1/projects/{Project}/databases/(default)/documents/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }

    private async Task<List<JsonElement>> OwnerDocumentsAsync(
        HttpClient http, string collection)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{firestoreHost}/v1/projects/{Project}/databases/(default)/documents/{collection}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("documents").EnumerateArray()
            .Select(item => item.Clone()).ToList();
    }

    private async Task<string> SignUpAsync(HttpClient http, string email, string password)
    {
        using var response = await http.PostAsJsonAsync(
            $"http://{authHost}/identitytoolkit.googleapis.com/v1/accounts:signUp?key=fake-key",
            new { email, password, returnSecureToken = true });
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("idToken").GetString()!;
    }

    private async Task<string> FirebaseSignInAsync(
        HttpClient http, string email, string password)
    {
        using var response = await http.PostAsJsonAsync(
            $"http://{authHost}/identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=fake-key",
            new { email, password, returnSecureToken = true });
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("idToken").GetString()!;
    }

    private async Task<HttpStatusCode> ReadAsAsync(
        HttpClient http, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{firestoreHost}/v1/projects/{Project}/databases/(default)/documents/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class EmulatorFactory(
        string directory, string authHost, string firestoreHost)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(ServerRoot());
            builder.UseSetting("ConnectionStrings:Default",
                $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
            builder.UseSetting("Demo:Enabled", "false");
            builder.UseSetting("Firebase:ApiKey", "fake-key");
            builder.UseSetting("Firebase:ProjectId", Project);
            builder.UseSetting("Firebase:AuthEmulatorHost", authHost);
            builder.UseSetting("Firebase:FirestoreEmulatorHost", firestoreHost);
            builder.UseSetting("Firebase:SyncIntervalSeconds", "3600");
        }

        private static string ServerRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory);
                current is not null; current = current.Parent)
            {
                var candidate = Path.Combine(current.FullName, "BiStudent.Server");
                if (File.Exists(Path.Combine(candidate, "seed", "students.demo.csv")))
                    return candidate;
            }
            throw new DirectoryNotFoundException("BiStudent.Server/seed/students.demo.csv");
        }
    }
}
