using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

[Collection("firebase-emulator")]
public sealed class FirebaseSyncScenarioTests : IDisposable
{
    private const string Project = "demo-bistudent";
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        $"BiStudent.FirebaseScenarios.{Guid.NewGuid():N}");
    private readonly string? authHost = Environment.GetEnvironmentVariable("FIREBASE_AUTH_EMULATOR_HOST");
    private readonly string? firestoreHost = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");

    public FirebaseSyncScenarioTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task StrangerCannotCaptureBootstrap()
    {
        await ClearAsync();
        using var http = new HttpClient();
        var stranger = await SignUpAsync(http, "stranger@bistudent.app", "Stranger-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden,
            await CommitAsync(http, stranger.Token, new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["name"] = DocumentName("meta/bootstrap"),
                    ["fields"] = new JsonObject { ["uid"] = StringValue(stranger.Uid) }
                }
            }));

        using var factory = Factory("a");
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var uid = await db.Teachers.Where(teacher => teacher.Login == "admin")
            .Select(teacher => teacher.FirebaseUid).SingleAsync();
        var bootstrap = await OwnerDocumentAsync(http, "meta/bootstrap");
        Assert.NotNull(bootstrap);
        Assert.Equal(uid, Field(bootstrap, "uid"));
    }

    [Fact]
    public async Task ChangesTravelBothWaysIncludingEditsAndDeletion()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var firstClient = first.CreateClient();
        await BootstrapAsync(firstClient);
        var temporary = await PostAsync<TemporaryPasswordDto>(firstClient,
            "/api/admin/teachers", new SaveTeacherRequest("anna", "Анна Иванова", false, true));
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Groups.Add(new Group { Name = "Общая группа" });
            await db.SaveChangesAsync();
        }
        await RunAsync(firstClient);

        using var second = Factory("b");
        using var secondClient = second.CreateClient();
        Authorize(secondClient, await LoginAsync(secondClient, "anna", temporary.Password));
        await RunAsync(secondClient);
        using (var scope = second.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var group = await db.Groups.SingleAsync(x => x.Name == "Общая группа");
            db.Students.Add(new Student { FullName = "Иванов Пётр", GroupId = group.Id });
            await db.SaveChangesAsync();
        }
        await RunAsync(secondClient);
        await RunAsync(firstClient);

        string cloudId;
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = await db.Students.SingleAsync(x => x.FullName == "Иванов Пётр");
            cloudId = await db.SyncLinks.Where(x => x.Table == "Students" && x.LocalId == student.Id)
                .Select(x => x.CloudId).SingleAsync();
            student.FullName = "Иванов Пётр Сергеевич";
            await db.SaveChangesAsync();
        }
        await RunAsync(firstClient);
        await RunAsync(secondClient);
        using (var scope = second.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.Students.AnyAsync(x => x.FullName == "Иванов Пётр Сергеевич"));
        }

        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var student = await db.Students.SingleAsync(x => x.FullName == "Иванов Пётр Сергеевич");
            db.Students.Remove(student);
            await db.SaveChangesAsync();
        }
        await RunAsync(firstClient);
        await RunAsync(secondClient);
        using (var scope = second.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Students.AnyAsync(x => x.FullName == "Иванов Пётр Сергеевич"));
        }
        using var http = new HttpClient();
        var tombstone = await OwnerDocumentAsync(http, $"Students/{cloudId}");
        Assert.NotNull(tombstone);
        Assert.True(tombstone["fields"]!["_deleted"]!["booleanValue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LegacyTeacherClaimsAccountAndSignsInOnAnotherComputer()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var adminClient = first.CreateClient();
        await BootstrapAsync(adminClient);
        await AddLegacyTeacherAsync(first);
        await RunAsync(adminClient);
        using var http = new HttpClient();
        var teacher = Assert.Single(await OwnerDocumentsAsync(http, "Teachers"),
            document => Field(document, "Login") == "petrov");
        Assert.Null(teacher["fields"]?["ClaimToken"]);
        Assert.NotNull(teacher["fields"]?["ClaimHash"]?["bytesValue"]);

        using var petrovClient = first.CreateClient();
        Authorize(petrovClient, await LoginAsync(petrovClient, "petrov", "Petrov-pass-1"));
        string uid;
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            uid = (await db.Teachers.Where(x => x.Login == "petrov")
                .Select(x => x.FirebaseUid).SingleAsync())!;
        }
        Assert.False(string.IsNullOrWhiteSpace(uid));
        Assert.NotNull(await OwnerDocumentAsync(http, $"TeacherUids/{uid}"));
        var linked = await OwnerDocumentAsync(http, $"Teachers/{DocumentId(teacher)}");
        Assert.NotNull(linked);
        Assert.Equal(uid, Field(linked, "FirebaseUid"));

        using var second = Factory("b");
        using var secondClient = second.CreateClient();
        Authorize(secondClient, await LoginAsync(secondClient, "petrov", "Petrov-pass-1"));
    }

    [Fact]
    public async Task DisabledLegacyTeacherCannotClaimWithStaleLocalCache()
    {
        await ClearAsync();
        using var factory = Factory("a");
        using var adminClient = factory.CreateClient();
        await BootstrapAsync(adminClient);
        await AddLegacyTeacherAsync(factory);
        await RunAsync(adminClient);

        int teacherId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            teacherId = await db.Teachers.Where(teacher => teacher.Login == "petrov")
                .Select(teacher => teacher.Id).SingleAsync();
        }
        var disabled = await adminClient.PutAsJsonAsync(
            $"/api/admin/teachers/{teacherId}",
            new SaveTeacherRequest("petrov", "Петров", false, false));
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await RunAsync(adminClient);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var teacher = await db.Teachers.SingleAsync(candidate => candidate.Id == teacherId);
            var applyingRemote = db.ApplyingRemote;
            db.ApplyingRemote = true;
            try
            {
                teacher.IsActive = true;
                await db.SaveChangesAsync();
            }
            finally { db.ApplyingRemote = applyingRemote; }
        }

        using var petrovClient = factory.CreateClient();
        var login = await petrovClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("petrov", "Petrov-pass-1"));
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDuplicateGroupsShareOneCloudDocument()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var firstClient = first.CreateClient();
        await BootstrapAsync(firstClient);
        var temporary = await PostAsync<TemporaryPasswordDto>(firstClient,
            "/api/admin/teachers", new SaveTeacherRequest("anna", "Анна Иванова", false, true));
        await RunAsync(firstClient);

        using var second = Factory("b");
        using var secondClient = second.CreateClient();
        Authorize(secondClient, await LoginAsync(secondClient, "anna", temporary.Password));
        await RunAsync(secondClient);
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Groups.Add(new Group { Name = "Дубль" });
            await db.SaveChangesAsync();
        }
        using (var scope = second.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Groups.Add(new Group { Name = "Дубль" });
            await db.SaveChangesAsync();
        }
        await RunAsync(firstClient);
        await RunAsync(secondClient);
        await RunAsync(firstClient);
        await RunAsync(secondClient);

        using var http = new HttpClient();
        var documents = await OwnerDocumentsAsync(http, "Groups");
        var cloud = Assert.Single(documents, document =>
            Field(document, "Name") == "Дубль" && !Deleted(document));
        var cloudId = DocumentId(cloud);
        foreach (var factory in new[] { first, second })
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var local = Assert.Single(await db.Groups.Where(x => x.Name == "Дубль").ToListAsync());
            var linkedId = await db.SyncLinks.Where(x => x.Table == "Groups" && x.LocalId == local.Id)
                .Select(x => x.CloudId).SingleAsync();
            Assert.Equal(cloudId, linkedId);
        }
    }

    [Fact]
    public async Task StrangerCannotClaimLegacyTeacher()
    {
        await ClearAsync();
        using var factory = Factory("a");
        using var client = factory.CreateClient();
        await BootstrapAsync(client);
        await AddLegacyTeacherAsync(factory);
        await RunAsync(client);
        using var http = new HttpClient();
        var teacher = Assert.Single(await OwnerDocumentsAsync(http, "Teachers"),
            document => Field(document, "Login") == "petrov");
        var account = await SignUpAsync(http, "petrov@bistudent.app", "Stranger-pass-1");
        var teacherId = DocumentId(teacher);
        var falseProof = Guid.NewGuid().ToString("N");

        Assert.Equal(HttpStatusCode.Forbidden,
            await CommitClaimAsync(http, account.Token, teacherId, account.Uid, null));
        Assert.Equal(HttpStatusCode.Forbidden,
            await CommitClaimAsync(http, account.Token, teacherId, account.Uid, falseProof));
        Assert.Equal(HttpStatusCode.Forbidden,
            await CommitUidAsync(http, account.Token, teacherId, account.Uid));
    }

    [Fact]
    public async Task SeededAdministratorUsesCloudPasswordOnNewComputer()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var firstClient = first.CreateClient();
        await BootstrapAsync(firstClient);
        await RunAsync(firstClient);

        using (var second = Factory("b"))
        using (var secondClient = second.CreateClient())
        {
            Authorize(secondClient,
                await LoginAsync(secondClient, "admin", "Admin-pass-1"));
            var me = await secondClient.GetFromJsonAsync<JsonObject>("/api/me");
            Assert.NotNull(me);
            Assert.True(me["isAdmin"]!.GetValue<bool>());
        }

        using var offline = Factory("b", "127.0.0.1:1");
        using var offlineClient = offline.CreateClient();
        var oldPassword = await offlineClient.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin", "admin"));
        Assert.NotEqual(HttpStatusCode.OK, oldPassword.StatusCode);
        using var scope = offline.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teacher = await db.Teachers.SingleAsync(candidate => candidate.Login == "admin");
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Teacher>>();
        Assert.NotEqual(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(teacher, teacher.PasswordHash, "Admin-pass-1"));
        Assert.Equal(PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(teacher, teacher.PasswordHash, "admin"));
    }

    [Fact]
    public async Task ResetPasswordLinksUnclaimedTeacher()
    {
        await ClearAsync();
        using var first = Factory("a");
        using var firstClient = first.CreateClient();
        await BootstrapAsync(firstClient);
        int teacherId;
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var teacher = new Teacher { Login = "sidorov", FullName = "Сидоров" };
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Teacher>>();
            teacher.PasswordHash = hasher.HashPassword(teacher, "Sidorov-pass-1");
            db.Teachers.Add(teacher);
            await db.SaveChangesAsync();
            teacherId = teacher.Id;
        }
        var temporary = await PostAsync<TemporaryPasswordDto>(firstClient,
            $"/api/admin/teachers/{teacherId}/reset-password", new { });
        await RunAsync(firstClient);

        using var second = Factory("b");
        using var secondClient = second.CreateClient();
        Authorize(secondClient,
            await LoginAsync(secondClient, "sidorov", temporary.Password));
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

    private static async Task AddLegacyTeacherAsync(EmulatorFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teacher = new Teacher { Login = "petrov", FullName = "Петров", FirebaseUid = null };
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Teacher>>();
        teacher.PasswordHash = hasher.HashPassword(teacher, "Petrov-pass-1");
        db.Teachers.Add(teacher);
        await db.SaveChangesAsync();
    }

    private static async Task RunAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/sync/run", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<SyncStatusDto>();
        Assert.NotNull(status);
        Assert.Equal("Ok", status.State);
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

    private async Task<JsonObject?> OwnerDocumentAsync(HttpClient http, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{firestoreHost}/v1/projects/{Project}/databases/(default)/documents/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private async Task<List<JsonObject>> OwnerDocumentsAsync(HttpClient http, string collection)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{firestoreHost}/v1/projects/{Project}/databases/(default)/documents/{collection}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var documents = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["documents"]?.AsArray();
        return documents is null ? [] : documents.Select(item => item!.AsObject()).ToList();
    }

    private async Task<(string Uid, string Token)> SignUpAsync(
        HttpClient http, string email, string password)
    {
        using var response = await http.PostAsJsonAsync(
            $"http://{authHost}/identitytoolkit.googleapis.com/v1/accounts:signUp?key=fake-key",
            new { email, password, returnSecureToken = true });
        response.EnsureSuccessStatusCode();
        var account = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (account["localId"]!.GetValue<string>(), account["idToken"]!.GetValue<string>());
    }

    private Task<HttpStatusCode> CommitClaimAsync(
        HttpClient http, string token, string teacherId, string uid, string? proof)
    {
        var fields = new JsonObject { ["FirebaseUid"] = StringValue(uid) };
        var mask = new JsonArray();
        mask.Add("FirebaseUid");
        if (proof is not null)
        {
            fields["ClaimProof"] = StringValue(proof);
            mask.Add("ClaimProof");
        }
        return CommitAsync(http, token, new JsonObject
        {
            ["update"] = new JsonObject
            {
                ["name"] = DocumentName($"Teachers/{teacherId}"),
                ["fields"] = fields
            },
            ["updateMask"] = new JsonObject { ["fieldPaths"] = mask }
        });
    }

    private Task<HttpStatusCode> CommitUidAsync(
        HttpClient http, string token, string teacherId, string uid) =>
        CommitAsync(http, token, new JsonObject
        {
            ["update"] = new JsonObject
            {
                ["name"] = DocumentName($"TeacherUids/{uid}"),
                ["fields"] = new JsonObject { ["teacher"] = StringValue(teacherId) }
            }
        });

    private async Task<HttpStatusCode> CommitAsync(
        HttpClient http, string token, JsonObject write)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://{firestoreHost}/v1/projects/{Project}/databases/(default)/documents:commit")
        {
            Content = JsonContent.Create(new JsonObject { ["writes"] = new JsonArray(write) })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    private static JsonObject StringValue(string value) => new() { ["stringValue"] = value };
    private static string DocumentName(string path) =>
        $"projects/{Project}/databases/(default)/documents/{path}";
    private static string DocumentId(JsonObject document)
    {
        var name = document["name"]!.GetValue<string>();
        return name[(name.LastIndexOf('/') + 1)..];
    }
    private static string Field(JsonObject document, string field) =>
        document["fields"]?[field]?["stringValue"]?.GetValue<string>() ?? "";
    private static bool Deleted(JsonObject document) =>
        document["fields"]?["_deleted"]?["booleanValue"]?.GetValue<bool>() == true;

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
