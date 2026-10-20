using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BiStudent.Server.Data;
using BiStudent.Server.Endpoints;
using BiStudent.Server.Firebase;
using BiStudent.Server.Services;
using BiStudent.Server.Sync;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
var configuredConnection = builder.Configuration.GetConnectionString("Default");
var defaultData = builder.Configuration["Storage:DataDirectory"] ??
    (OperatingSystem.IsWindows()
        // Облачная версия держит базу отдельно: иначе первый вход выгрузил бы в облако старую локальную базу со снимками лиц.
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            FirebaseOptions.IsConfigured(builder.Configuration) ? "BiStudent-Cloud" : "BiStudent")
        : Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
if (!Path.IsPathRooted(defaultData))
    defaultData = Path.Combine(builder.Environment.ContentRootPath, defaultData);
var sqlite = new SqliteConnectionStringBuilder(configuredConnection ?? "");
if (configuredConnection is null) sqlite.DataSource = Path.Combine(defaultData, "bistudent.db");
if (!Path.IsPathRooted(sqlite.DataSource))
    sqlite.DataSource = Path.Combine(builder.Environment.ContentRootPath, sqlite.DataSource);
var data = Path.GetDirectoryName(Path.GetFullPath(sqlite.DataSource))!;
Directory.CreateDirectory(data);
Directory.CreateDirectory(Path.Combine(data, "keys"));

var firebase = builder.Configuration.GetSection("Firebase").Get<FirebaseOptions>() ?? new FirebaseOptions();
builder.Services.Configure<FirebaseOptions>(builder.Configuration.GetSection("Firebase"));
builder.Services.AddSingleton<SyncSignal>();
builder.Services.AddSingleton<SyncCaptureInterceptor>();
builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    options.UseSqlite(sqlite.ToString());
    // Изменения попадают в очередь отправки в Firestore, только если Firebase настроен.
    if (firebase.Enabled) options.AddInterceptors(services.GetRequiredService<SyncCaptureInterceptor>());
});
// Firebase: SQLite — локальный кеш, основная копия данных и учётки — в Firestore и Firebase Authentication.
builder.Services.AddHttpClient<FirebaseAuthClient>();
builder.Services.AddHttpClient<FirestoreClient>();
builder.Services.AddSingleton<FirebaseSessionStore>();
builder.Services.AddSingleton<SyncService>();
builder.Services.AddScoped<CloudAccountService>();
builder.Services.AddHostedService<SyncWorker>();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys")))
    .SetApplicationName("BiStudent");
builder.Services.AddAuthentication(BearerTokenDefaults.AuthenticationScheme)
    .AddBearerToken(options =>
    {
        options.BearerTokenExpiration = TimeSpan.FromHours(1);
        options.RefreshTokenExpiration = TimeSpan.FromDays(30);
    });
builder.Services.AddAuthorization(options =>
    options.AddPolicy("Admin", policy => policy.RequireClaim("bistudent:admin", "true")));
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), AutoReplenishment = true }));
    options.AddPolicy("public-check", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), AutoReplenishment = true }));
    options.AddPolicy("public-submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), AutoReplenishment = true }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new BiStudent.Contracts.ErrorDto("Слишком много попыток входа. Подождите минуту."), cancellationToken: ct);
    };
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddScoped<IPasswordHasher<Teacher>, PasswordHasher<Teacher>>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<AttendanceService>();
builder.Services.AddScoped<StudentService>();
builder.Services.AddScoped<StatsService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<SeedService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<GradeService>();
builder.Services.AddScoped<ScheduleImportService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AttendanceNotifier>();
builder.Services.AddSingleton<RecognitionService>();
builder.Services.AddSingleton(new StoragePaths(Path.GetFullPath(sqlite.DataSource), sqlite.ToString()));
builder.Services.AddSingleton<SessionGeneration>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<BackupService.AutoBackupWorker>();
builder.Services.AddScoped<FaceSampleHealth>();

var app = builder.Build();
if (configuredConnection is null)
{
    var legacy = Path.Combine(builder.Environment.ContentRootPath, "App_Data");
    var oldDatabase = Path.Combine(legacy, "bistudent.db");
    var newDatabase = Path.GetFullPath(sqlite.DataSource);
    if (!File.Exists(newDatabase) && File.Exists(oldDatabase) &&
        !Path.GetFullPath(oldDatabase).Equals(newDatabase,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    {
        var temporary = newDatabase + ".migrating";
        try
        {
            File.Delete(temporary);
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = oldDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = temporary, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }
            var oldKeys = Path.Combine(legacy, "keys");
            if (Directory.Exists(oldKeys))
                foreach (var key in Directory.EnumerateFiles(oldKeys))
                    File.Copy(key, Path.Combine(data, "keys", Path.GetFileName(key)), overwrite: true);
            File.Move(temporary, newDatabase);
            app.Logger.LogInformation("Данные перенесены из {Directory}.", legacy);
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "Не удалось перенести данные из {Directory}. Запуск остановлен, чтобы не создать пустую базу вместо старой; устраните причину и запустите сервер снова.", legacy);
            foreach (var file in new[] { temporary, newDatabase })
            {
                try { File.Delete(file); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    app.Logger.LogWarning(cleanup, "Не удалось удалить {File}.", file);
                }
            }
            throw;
        }
    }
}
app.Logger.LogInformation("Каталог данных: {Directory}.", data);
var culture = CultureInfo.GetCultureInfo("ru-RU");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/register"))
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self'; script-src 'self'; connect-src 'self'; frame-ancestors 'none'";
        context.Response.Headers["Permissions-Policy"] = "camera=(self)";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    // Операции с копиями сериализуются внутри BackupService; остальные запросы к API восстановление дожидается.
    var tracked = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/admin/backups") &&
        !path.StartsWithSegments("/api/admin/restore");
    var backups = context.RequestServices.GetRequiredService<BackupService>();
    if (tracked && !backups.TryEnterRequest())
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new BiStudent.Contracts.ErrorDto("Идёт восстановление резервной копии — повторите через минуту."));
        return;
    }
    try
    {
        if (context.User.Identity?.IsAuthenticated == true && !path.StartsWithSegments("/api/auth/login") &&
            !path.StartsWithSegments("/api/auth/refresh"))
        {
            var db = context.RequestServices.GetRequiredService<AppDbContext>();
            var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var stamp = context.User.FindFirst("bistudent:stamp")?.Value;
            if (!context.RequestServices.GetRequiredService<SessionGeneration>().IsCurrent(context.User) ||
                !int.TryParse(id, out var teacherId) ||
                !await db.Teachers.AsNoTracking().AnyAsync(x => x.Id == teacherId && x.IsActive && x.SecurityStamp == stamp))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new BiStudent.Contracts.ErrorDto("Сессия завершена — войдите снова."));
                return;
            }
        }
        await next();
    }
    finally { if (tracked) backups.ExitRequest(); }
});
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Redirect("/register/"));
// Маршрутизация не различает завершающий слэш, поэтому страницу для «/register/» отдаём здесь же.
app.MapGet("/register", (HttpContext http) => http.Request.Path.Value!.EndsWith('/')
    ? Results.File(Path.Combine(app.Environment.WebRootPath, "register", "index.html"), "text/html; charset=utf-8")
    : Results.Redirect("/register/"));
app.MapApi();
app.MapEnrollment();
app.MapSync();
var sync = app.Services.GetRequiredService<SyncService>();
var recognition = app.Services.GetRequiredService<RecognitionService>();
sync.FacesChanged += recognition.Invalidate;

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseInitializer.InitializeAsync(db);
    await scope.ServiceProvider.GetRequiredService<SeedService>().SeedAsync();
}

app.Run();

public partial class Program;
