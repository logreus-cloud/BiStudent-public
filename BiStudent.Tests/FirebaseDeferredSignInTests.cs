using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BiStudent.Contracts;
using BiStudent.Server.Data;
using BiStudent.Server.Firebase;
using BiStudent.Server.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BiStudent.Tests;

[Collection("firebase-emulator")]
public sealed class FirebaseDeferredSignInTests : IDisposable
{
    private const string Project = "demo-bistudent";
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        $"BiStudent.FirebaseDeferred.{Guid.NewGuid():N}");
    private readonly string? authHost = Environment.GetEnvironmentVariable("FIREBASE_AUTH_EMULATOR_HOST");
    private readonly string? firestoreHost = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");

    public FirebaseDeferredSignInTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task OfflineLoginCompletesWhenFirebaseReturnsWithoutInvalidatingClientTokens()
    {
        await ClearAsync();
        var connection = new Connection();
        using var factory = Factory("cloud", connection);
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "admin"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var changed = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest("admin", "Admin-pass-1"));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        var sessions = factory.Services.GetRequiredService<FirebaseSessionStore>();
        Assert.NotNull(sessions.Uid);

        // Сессия потеряна (новый компьютер с перенесённой базой, сброс) и Firebase недоступен — вход локальный.
        sessions.Clear();
        connection.IsOffline = true;
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "Admin-pass-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var localToken = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(localToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", localToken.AccessToken);
        var pending = factory.Services.GetRequiredService<PendingCloudSignIn>();
        Assert.True(pending.TryGet(out _, out _));
        Assert.Null(sessions.Uid);
        var (stamp, lastLogin) = await AdminAsync(factory);

        connection.IsOffline = false;
        using (var scope = factory.Services.CreateScope())
        {
            var cloud = scope.ServiceProvider.GetRequiredService<CloudAccountService>();
            Assert.True(await cloud.TryCompleteDeferredSignInAsync(CancellationToken.None));
        }
        Assert.False(pending.TryGet(out _, out _));
        Assert.NotNull(sessions.Uid);
        Assert.Equal(SyncState.Ok, (await factory.Services.GetRequiredService<SyncService>().RunAsync()).State);
        Assert.Equal((stamp, lastLogin), await AdminAsync(factory));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(localToken.RefreshToken!));
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        // Вход без связи по-прежнему работает: хеш пароля не потерян.
        sessions.Clear();
        connection.IsOffline = true;
        client.DefaultRequestHeaders.Authorization = null;
        var offlineAgain = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "Admin-pass-1"));
        Assert.Equal(HttpStatusCode.OK, offlineAgain.StatusCode);
    }

    [Fact]
    public async Task UnlinkedAccountOfflineLoginIsNotDeferred()
    {
        await ClearAsync();
        var connection = new Connection { IsOffline = true };
        using var factory = Factory("unlinked", connection);
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var teacher = await db.Teachers.SingleAsync(x => x.Login == "admin");
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Teacher>>();
            teacher.PasswordHash = hasher.HashPassword(teacher, "Admin-pass-1");
            teacher.MustChangePassword = false;
            await db.SaveChangesAsync();
        }
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "Admin-pass-1"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(factory.Services.GetRequiredService<PendingCloudSignIn>().TryGet(out _, out _));
        using var deferredScope = factory.Services.CreateScope();
        Assert.False(await deferredScope.ServiceProvider.GetRequiredService<CloudAccountService>()
            .TryCompleteDeferredSignInAsync(CancellationToken.None));
    }

    private static async Task<(string Stamp, DateTime? LastLogin)> AdminAsync(EmulatorFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var admin = await db.Teachers.AsNoTracking().SingleAsync(x => x.Login == "admin");
        return (admin.SecurityStamp, admin.LastLoginAt);
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

    private EmulatorFactory Factory(string name, Connection connection)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(path);
        return new EmulatorFactory(path, authHost!, firestoreHost!, connection);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private sealed class Connection
    {
        private int offline;
        public bool IsOffline
        {
            get => Volatile.Read(ref offline) != 0;
            set => Volatile.Write(ref offline, value ? 1 : 0);
        }
    }

    private sealed class OfflineAuthHandler(Connection connection) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            connection.IsOffline
                ? throw new HttpRequestException("Firebase недоступен.")
                : base.SendAsync(request, cancellationToken);
    }

    private sealed class EmulatorFactory(
        string directory, string authHost, string firestoreHost, Connection connection)
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
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient<FirebaseAuthClient>()
                    .AddHttpMessageHandler(() => new OfflineAuthHandler(connection));
                // Отложенный вход вызывает сам тест: фоновый SyncWorker иначе может успеть раньше.
                foreach (var worker in services.Where(x => x.ImplementationType == typeof(SyncWorker)).ToList())
                    services.Remove(worker);
            });
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
