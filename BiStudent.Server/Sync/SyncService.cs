using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiStudent.Server.Data;
using BiStudent.Server.Firebase;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

namespace BiStudent.Server.Sync;

public enum SyncState { Disabled, NotSignedIn, Offline, Ok, Error }

public sealed record SyncStatus(
    SyncState State, DateTimeOffset? LastSuccessAt, int Pending, string? Error);

public sealed record SyncReport(
    SyncState State, int Sent, int Received, bool FacesChanged, int Pending, string? Error);

public sealed class SyncService(
    IServiceScopeFactory scopes, FirestoreClient firestore, FirebaseSessionStore sessions,
    IOptions<FirebaseOptions> options, IDataProtectionProvider protection,
    ILogger<SyncService> logger, TimeProvider clock)
{
    private sealed record UploadItem(
        FirestoreWrite Write, List<SyncOutboxEntry> Entries, SyncLink? Link);
    private enum ApplyResult { Applied, Ignored, Deferred }

    private static readonly MethodInfo LoadMethod = typeof(SyncService)
        .GetMethod(nameof(LoadCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo MissingMethod = typeof(SyncService)
        .GetMethod(nameof(MissingCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo KeyMethod = typeof(SyncService)
        .GetMethod(nameof(FindKeyCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo OtherKeyMethod = typeof(SyncService)
        .GetMethod(nameof(FindOtherKeyCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo DependentsMethod = typeof(SyncService)
        .GetMethod(nameof(FindDependentsCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim generationGate = new(1, 1);
    private readonly FirebaseOptions settings = options.Value;
    private readonly IDataProtector images = protection.CreateProtector("BiStudent.FaceSamples");
    private int generation;
    private SyncStatus status = new(options.Value.Enabled ? SyncState.NotSignedIn : SyncState.Disabled,
        null, 0, null);

    public event Action? FacesChanged;
    public SyncStatus Status => status;

    public void Reset()
    {
        generationGate.Wait();
        try
        {
            Interlocked.Increment(ref generation);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SyncOutbox.Where(entry => entry.Error != null)
                .ExecuteUpdate(setters => setters.SetProperty(entry => entry.Error, (string?)null));
            status = new SyncStatus(settings.Enabled ? SyncState.NotSignedIn : SyncState.Disabled,
                null, db.SyncOutbox.Count(), null);
        }
        finally { generationGate.Release(); }
    }

    private bool Current(int expected) => Volatile.Read(ref generation) == expected;

    private async Task<bool> SaveIfCurrentAsync(
        AppDbContext db, int expected, Action changes, CancellationToken cancellationToken)
    {
        await generationGate.WaitAsync(cancellationToken);
        try
        {
            if (!Current(expected)) return false;
            changes();
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally { generationGate.Release(); }
    }

    public async Task PullAsync(
        IReadOnlyCollection<string> tables, CancellationToken ct, FirebaseSession? session = null)
    {
        await gate.WaitAsync(ct);
        var currentGeneration = Volatile.Read(ref generation);
        var facesChanged = false;
        try
        {
            session ??= await sessions.GetAsync(ct)
                ?? throw new InvalidOperationException("Для загрузки данных нужен вход в Firebase.");
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var selected = new SyncModel(db).Tables
                .Where(table => tables.Contains(table.Name)).ToArray();
            if (selected.Length != tables.Distinct().Count())
                throw new ArgumentException("Неизвестная таблица синхронизации.", nameof(tables));
            await ReceiveAsync(db, selected, session.IdToken, () => facesChanged = true,
                currentGeneration, ct);
        }
        finally
        {
            gate.Release();
            if (facesChanged) NotifyFacesChanged();
        }
    }

    public async Task<string> EnsureLinkAsync(AppDbContext db, string table, int localId)
    {
        if (!new SyncModel(db).Tables.Any(candidate => candidate.Name == table))
            throw new ArgumentException("Неизвестная таблица синхронизации.", nameof(table));
        return (await EnsureLinkCoreAsync(db, table, localId, null, CancellationToken.None)).CloudId;
    }

    private static async Task<SyncLink> EnsureLinkCoreAsync(
        AppDbContext db, string table, int localId, string? cloudId,
        CancellationToken cancellationToken)
    {
        var link = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
            candidate.Table == table && candidate.LocalId == localId, cancellationToken);
        if (link is not null) return link;
        link = new SyncLink
        {
            Table = table, LocalId = localId,
            CloudId = cloudId ?? Guid.NewGuid().ToString("N")
        };
        db.SyncLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return link;
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.Entry(link).State = EntityState.Detached;
            var existing = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
                candidate.Table == table && candidate.LocalId == localId, cancellationToken);
            if (existing is null) throw;
            return existing;
        }
    }

    public async Task<FirestoreWrite> BuildWriteAsync(AppDbContext db, string table, int localId)
    {
        var model = new SyncModel(db);
        var selected = model.Tables.Single(candidate => candidate.Name == table);
        var device = await GetOrCreateAsync(db, "sync:device",
            () => Guid.NewGuid().ToString("N"), CancellationToken.None);
        var item = await PrepareWriteAsync(db, selected,
            [new SyncOutboxEntry { Table = table, LocalId = localId }], device,
            Volatile.Read(ref generation), CancellationToken.None);
        return item?.Write ?? throw new InvalidOperationException("Не удалось подготовить документ.");
    }

    public async Task<SyncReport> RunAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        var currentGeneration = Volatile.Read(ref generation);
        var facesChanged = false;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = SyncState.Disabled;
            var sent = 0;
            var received = 0;
            string? error = null;
            int pending;
            try
            {
                if (settings.Enabled)
                {
                    var session = await sessions.GetAsync(cancellationToken);
                    if (session is null) state = SyncState.NotSignedIn;
                    else
                    {
                        var model = new SyncModel(db);
                        var device = await GetOrCreateAsync(db, "sync:device",
                            () => Guid.NewGuid().ToString("N"), cancellationToken);
                        var initialKey = "sync:initial-done:" + session.Uid;
                        if (await db.AppSettings.FindAsync([initialKey], cancellationToken) is null)
                            await QueueInitialAsync(db, model, cancellationToken);

                        sent = await SendAsync(db, model, session, device,
                            currentGeneration, cancellationToken);
                        if (Current(currentGeneration) &&
                            !await db.SyncOutbox.AnyAsync(cancellationToken) &&
                            await db.AppSettings.FindAsync([initialKey], cancellationToken) is null)
                            await SetSettingIfCurrentAsync(
                                db, initialKey, "true", currentGeneration, cancellationToken);

                        received = await ReceiveAsync(db, model.Tables, session.IdToken,
                            () => facesChanged = true, currentGeneration, cancellationToken);
                        state = SyncState.Ok;
                    }
                }
            }
            catch (FirebaseUnavailableException exception)
            {
                state = SyncState.Offline;
                error = exception.Message;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Ошибка синхронизации Firebase");
                state = SyncState.Error;
                error = exception.Message;
            }
            finally
            {
                await generationGate.WaitAsync(CancellationToken.None);
                try
                {
                    pending = await db.SyncOutbox.CountAsync(CancellationToken.None);
                    var blocked = await db.SyncOutbox.CountAsync(
                        entry => entry.Error != null, CancellationToken.None);
                    if (blocked > 0)
                    {
                        var reason = await db.SyncOutbox
                            .Where(entry => entry.Error != null)
                            .OrderBy(entry => entry.Id)
                            .Select(entry => entry.Error)
                            .FirstOrDefaultAsync(CancellationToken.None);
                        error = $"{blocked} изменений не удаётся отправить: {reason}" +
                            (error is null ? "" : $"; {error}");
                    }
                    if (Current(currentGeneration))
                        status = new SyncStatus(state,
                            state == SyncState.Ok ? clock.GetUtcNow() : status.LastSuccessAt,
                            pending, error);
                }
                finally { generationGate.Release(); }
            }
            return new SyncReport(state, sent, received, facesChanged, pending, error);
        }
        finally
        {
            gate.Release();
            if (facesChanged) NotifyFacesChanged();
        }
    }

    private void NotifyFacesChanged()
    {
        if (FacesChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Ошибка обработчика FacesChanged");
            }
        }
    }

    private static async Task<string> GetOrCreateAsync(
        AppDbContext db, string key, Func<string> create, CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings.FindAsync([key], cancellationToken);
        if (setting is not null) return setting.Value;
        var value = create();
        db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        await db.SaveChangesAsync(cancellationToken);
        return value;
    }

    private async Task SetSettingIfCurrentAsync(
        AppDbContext db, string key, string value, int expected,
        CancellationToken cancellationToken)
    {
        await generationGate.WaitAsync(cancellationToken);
        try
        {
            if (!Current(expected)) return;
            var setting = await db.AppSettings.FindAsync([key], cancellationToken);
            if (setting is null)
                db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            else setting.Value = value;
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { generationGate.Release(); }
    }

    private static async Task QueueInitialAsync(
        AppDbContext db, SyncModel model, CancellationToken cancellationToken)
    {
        foreach (var table in model.Tables)
        {
            var ids = await MissingAsync(db, table, cancellationToken);
            foreach (var id in ids)
                db.SyncOutbox.Add(new SyncOutboxEntry { Table = table.Name, LocalId = id });
            if (ids.Count > 0) await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<int> SendAsync(
        AppDbContext db, SyncModel model, FirebaseSession session, string device,
        int currentGeneration, CancellationToken cancellationToken)
    {
        var sent = 0;
        var teacherId = sessions.TeacherId;
        var isAdmin = teacherId is not null && await db.Teachers.AsNoTracking()
            .AnyAsync(teacher => teacher.Id == teacherId &&
                teacher.FirebaseUid == session.Uid && teacher.IsAdmin, cancellationToken);
        var limitedUid = isAdmin ? null : session.Uid;
        foreach (var deleted in new[] { true, false })
        {
            var tables = deleted ? model.Tables.Reverse() : model.Tables;
            foreach (var table in tables)
            {
                if (!Current(currentGeneration)) return sent;
                var entries = await db.SyncOutbox
                    .Where(entry => entry.Table == table.Name && entry.Deleted == deleted &&
                        entry.Error == null)
                    .OrderBy(entry => entry.Id).ToListAsync(cancellationToken);
                var groups = entries.GroupBy(entry => deleted
                    ? "D:" + entry.CloudId : "U:" + entry.LocalId.ToString(CultureInfo.InvariantCulture))
                    .Select(group => group.ToList()).ToArray();
                var items = new List<UploadItem>();
                var bytes = 13;
                var refused = false;
                foreach (var group in groups)
                {
                    if (!Current(currentGeneration)) return sent;
                    var item = await PrepareWriteAsync(db, table, group, device,
                        currentGeneration, cancellationToken, limitedUid);
                    if (item is null) continue;
                    var size = WriteSize(item.Write) + 1;
                    if (items.Count == 400 || bytes + size > 8_000_000)
                    {
                        var result = await SendPortionAsync(db, session.IdToken, items,
                            currentGeneration, cancellationToken);
                        sent += result.Sent;
                        refused = result.Refused;
                        items.Clear();
                        bytes = 13;
                        if (refused) break;
                    }
                    items.Add(item);
                    bytes += size;
                }
                if (!refused && items.Count > 0)
                {
                    var result = await SendPortionAsync(db, session.IdToken, items,
                        currentGeneration, cancellationToken);
                    sent += result.Sent;
                }
            }
        }
        return sent;
    }

    private int WriteSize(FirestoreWrite write)
    {
        var name = $"projects/{settings.ProjectId}/databases/(default)/documents/" +
            write.Collection + "/" + write.Id;
        var fields = write.Fields!.ToDictionary(
            field => field.Key, field => FirestoreValues.ToValue(field.Value));
        var item = new Dictionary<string, object?>
        {
            ["update"] = new { name, fields }
        };
        if (write.UpdateMask is not null)
            item["updateMask"] = new { fieldPaths = write.UpdateMask };
        if (write.ServerTimestamp)
            item["updateTransforms"] = new[]
            {
                new { fieldPath = "_updatedAt", setToServerValue = "REQUEST_TIME" }
            };
        return JsonSerializer.SerializeToUtf8Bytes(item).Length;
    }

    private async Task<(int Sent, bool Refused)> SendPortionAsync(
        AppDbContext db, string token, List<UploadItem> items,
        int currentGeneration, CancellationToken cancellationToken)
    {
        try
        {
            var results = await firestore.CommitAsync(token,
                items.Select(item => item.Write).ToArray(), cancellationToken);
            if (!await SaveIfCurrentAsync(db, currentGeneration, () =>
                {
                    for (var i = 0; i < items.Count; i++)
                        ApplyWriteResult(db, items[i],
                            i < results.Count ? results[i].UpdateTime : null);
                }, cancellationToken))
                return (0, true);
            return (items.Count, false);
        }
        catch (FirestorePermissionException)
        {
            var sent = 0;
            foreach (var item in items)
            {
                if (!Current(currentGeneration)) return (sent, true);
                try
                {
                    var results = await firestore.CommitAsync(
                        token, [item.Write], cancellationToken);
                    if (!await SaveIfCurrentAsync(db, currentGeneration,
                        () => ApplyWriteResult(db, item,
                            results.Count > 0 ? results[0].UpdateTime : null),
                        cancellationToken))
                        return (sent, true);
                    sent++;
                }
                catch (FirestorePermissionException exception)
                {
                    logger.LogWarning(exception, "Нет прав на документ {Table}/{Id}",
                        item.Write.Collection, item.Write.Id);
                    if (!await SaveIfCurrentAsync(db, currentGeneration, () =>
                        {
                            if (item.Write.Collection == "Teachers")
                                db.SyncOutbox.RemoveRange(item.Entries);
                            else
                                foreach (var entry in item.Entries)
                                    entry.Error = $"Нет прав на изменение {item.Write.Collection}/{item.Write.Id}.";
                        }, cancellationToken))
                        return (sent, true);
                }
            }
            return (sent, true);
        }
    }

    private async Task<UploadItem?> PrepareWriteAsync(
        AppDbContext db, SyncTable table, List<SyncOutboxEntry> entries, string device,
        int currentGeneration, CancellationToken cancellationToken, string? limitedUid = null)
    {
        var first = entries[0];
        if (first.Deleted)
        {
            if (first.CloudId is null || table.ClrType == typeof(Teacher) &&
                limitedUid is not null)
            {
                db.SyncOutbox.RemoveRange(entries);
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }
            return new UploadItem(new FirestoreWrite(table.Name, first.CloudId,
                new Dictionary<string, object?>
                {
                    ["_deleted"] = true,
                    ["_device"] = device
                }), entries, null);
        }

        var link = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
            candidate.Table == table.Name && candidate.LocalId == first.LocalId,
            cancellationToken);
        if (link is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            link = await EnsureLinkCoreAsync(
                db, table.Name, first.LocalId, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        var row = await LoadAsync(db, table, first.LocalId, true, cancellationToken);
        if (row is null)
        {
            db.SyncOutbox.RemoveRange(entries);
            db.SyncLinks.Remove(link);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        if (row is Teacher limitedTeacher && limitedUid is not null)
        {
            if (limitedTeacher.FirebaseUid != limitedUid)
            {
                db.SyncOutbox.RemoveRange(entries);
                await db.SaveChangesAsync(cancellationToken);
                return null;
            }
            return new UploadItem(new FirestoreWrite(table.Name, link.CloudId,
                new Dictionary<string, object?>
                {
                    ["MustChangePassword"] = limitedTeacher.MustChangePassword,
                    ["_device"] = device
                }, ["MustChangePassword", "_device"]), entries, link);
        }
        if (row is Teacher unclaimed && unclaimed.FirebaseUid is null &&
            unclaimed.ClaimToken is null)
        {
            unclaimed.ClaimToken = Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var applyingRemote = db.ApplyingRemote;
            db.ApplyingRemote = true;
            try { await db.SaveChangesAsync(cancellationToken); }
            finally { db.ApplyingRemote = applyingRemote; }
        }

        var fields = new Dictionary<string, object?>
        {
            ["_deleted"] = false,
            ["_device"] = device
        };
        foreach (var property in table.Properties)
        {
            var value = property.PropertyInfo?.GetValue(row);
            if (property.Name == "ProtectedImage" &&
                table.ClrType is var type &&
                (type == typeof(FaceSample) || type == typeof(FaceSubmissionImage)))
            {
                try { fields["Image"] = images.Unprotect((byte[])value!); }
                catch (CryptographicException exception)
                {
                    logger.LogWarning(exception, "Не удалось прочитать изображение {Table}/{Id}",
                        table.Name, first.LocalId);
                    await SaveIfCurrentAsync(db, currentGeneration, () =>
                    {
                        foreach (var entry in entries) entry.Error = "Не удалось расшифровать изображение.";
                    }, cancellationToken);
                    return null;
                }
            }
            else fields[property.Name] = value;
        }
        if (row is Teacher teacher)
        {
            fields["Email"] = settings.EmailFor(teacher.Login);
            fields["ClaimHash"] = teacher.ClaimToken is null ? null :
                SHA256.HashData(Encoding.UTF8.GetBytes(teacher.ClaimToken));
        }

        foreach (var key in table.ForeignKeys)
        {
            var property = key.Property;
            var local = property.PropertyInfo?.GetValue(row);
            if (local is null)
            {
                fields[property.Name] = null;
                continue;
            }
            var parentTable = key.PrincipalTable;
            var parentId = Convert.ToInt32(local, CultureInfo.InvariantCulture);
            var parent = await db.SyncLinks.FirstOrDefaultAsync(link =>
                link.Table == parentTable && link.LocalId == parentId, cancellationToken);
            if (parent is null)
            {
                if (!key.IsRequired)
                {
                    fields[property.Name] = null;
                    continue;
                }
                if (!await db.SyncOutbox.AnyAsync(entry =>
                    entry.Table == parentTable && entry.LocalId == parentId &&
                    !entry.Deleted && entry.Error == null, cancellationToken))
                {
                    await RemoveErroredAsync(db, parentTable, parentId, cancellationToken);
                    db.SyncOutbox.Add(new SyncOutboxEntry
                    {
                        Table = parentTable,
                        LocalId = parentId
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
                return null;
            }
            fields[property.Name] = parent.CloudId;
        }

        var size = JsonSerializer.SerializeToUtf8Bytes(fields.ToDictionary(
            field => field.Key, field => FirestoreValues.ToValue(field.Value))).Length;
        if (size > 900_000)
        {
            logger.LogWarning("Документ {Table}/{Id} слишком большой для Firestore",
                table.Name, first.LocalId);
            await SaveIfCurrentAsync(db, currentGeneration, () =>
            {
                foreach (var entry in entries) entry.Error = "Документ слишком большой для Firestore.";
            }, cancellationToken);
            return null;
        }

        return new UploadItem(new FirestoreWrite(table.Name, link.CloudId, fields),
            entries, link);
    }

    private static void ApplyWriteResult(
        AppDbContext db, UploadItem item, DateTimeOffset? updated)
    {
        var lastId = item.Entries.Max(entry => entry.Id);
        db.SyncOutbox.RemoveRange(db.SyncOutbox.Where(entry =>
            entry.Table == item.Write.Collection &&
            entry.LocalId == item.Entries[0].LocalId &&
            entry.Error != null && entry.Id < lastId).ToList());
        db.SyncOutbox.RemoveRange(item.Entries);
        if (item.Entries[0].Deleted)
        {
            var link = db.SyncLinks.Local.FirstOrDefault(candidate =>
                candidate.Table == item.Write.Collection &&
                candidate.CloudId == item.Write.Id);
            if (link is not null) db.SyncLinks.Remove(link);
            else
            {
                var existing = db.SyncLinks.FirstOrDefault(candidate =>
                    candidate.Table == item.Write.Collection &&
                    candidate.CloudId == item.Write.Id);
                if (existing is not null) db.SyncLinks.Remove(existing);
            }
        }
        else if (item.Link is not null && item.Write.UpdateMask is null)
            item.Link.CloudUpdatedAt = updated;
    }

    private async Task<int> ReceiveAsync(
        AppDbContext db, IReadOnlyList<SyncTable> tables, string token,
        Action facesChanged, int currentGeneration, CancellationToken cancellationToken)
    {
        var received = 0;
        foreach (var table in tables)
        {
            if (!Current(currentGeneration)) return received;
            var cursorKey = "sync:cursor:" + table.Name;
            var setting = await db.AppSettings.FindAsync([cursorKey], cancellationToken);
            DateTimeOffset? cursor = setting is null ? null :
                DateTimeOffset.Parse(setting.Value, CultureInfo.InvariantCulture);
            var after = cursor is null ? null :
                SubtractOrNull(cursor.Value, TimeSpan.FromMinutes(2));
            if (after is null) cursor = null;
            FirestoreDocument? position = null;
            DateTimeOffset? blockedAt = null;
            var saveCursor = true;
            while (true)
            {
                if (!Current(currentGeneration)) return received;
                var page = await firestore.QueryChangedAsync(
                    token, table.Name, after, 300, cancellationToken, position);
                if (page.Count == 0) break;
                foreach (var document in page)
                {
                    if (!Current(currentGeneration)) return received;
                    if (!ValidUpdatedAt(document))
                    {
                        logger.LogWarning("Неверный формат документа {Table}/{Id}",
                            table.Name, document.Id);
                        continue;
                    }
                    var result = await ApplyDocumentAsync(
                        db, table, document, currentGeneration, cancellationToken);
                    if (result == ApplyResult.Deferred)
                    {
                        if (blockedAt is null || document.UpdatedAt < blockedAt)
                            blockedAt = document.UpdatedAt;
                        continue;
                    }
                    if (result == ApplyResult.Applied)
                    {
                        received++;
                        if (table.ClrType == typeof(Student) ||
                            table.ClrType == typeof(FaceSample))
                            facesChanged();
                    }
                    if (cursor is null || document.UpdatedAt > cursor)
                        cursor = document.UpdatedAt;
                }
                if (page.Count < 300) break;
                position = page.LastOrDefault(document => document.RawUpdatedAt is not null);
                if (position is null)
                {
                    logger.LogWarning(
                        "Нет позиции следующей страницы для таблицы {Table}", table.Name);
                    saveCursor = false;
                    break;
                }
            }
            if (blockedAt is not null && (cursor is null || cursor >= blockedAt))
                cursor = SubtractOrNull(blockedAt.Value, TimeSpan.FromMilliseconds(1));
            if (cursor is null && setting is not null)
                await SaveIfCurrentAsync(db, currentGeneration,
                    () => db.AppSettings.Remove(setting), cancellationToken);
            else if (saveCursor && cursor is not null)
                await SetSettingIfCurrentAsync(db, cursorKey,
                    cursor.Value.ToString("O", CultureInfo.InvariantCulture),
                    currentGeneration, cancellationToken);
        }
        return received;
    }

    private static DateTimeOffset? SubtractOrNull(DateTimeOffset value, TimeSpan amount)
    {
        try { return value.Subtract(amount); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static bool ValidUpdatedAt(FirestoreDocument document) =>
        document.Fields.TryGetValue("_updatedAt", out var field) &&
        field.ValueKind == JsonValueKind.Object &&
        field.TryGetProperty("timestampValue", out var value) &&
        value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var updated) &&
        document.UpdatedAt == updated;

    private async Task<ApplyResult> ApplyDocumentAsync(
        AppDbContext db, SyncTable table, FirestoreDocument document,
        int currentGeneration, CancellationToken cancellationToken)
    {
        await generationGate.WaitAsync(cancellationToken);
        try
        {
            if (!Current(currentGeneration)) return ApplyResult.Deferred;
            return await ApplyDocumentCoreAsync(db, table, document, cancellationToken);
        }
        finally { generationGate.Release(); }
    }

    private async Task<ApplyResult> ApplyDocumentCoreAsync(
        AppDbContext db, SyncTable table, FirestoreDocument document,
        CancellationToken cancellationToken)
    {
        bool deleted;
        try
        {
            deleted = false;
            if (document.Fields.TryGetValue("_deleted", out var flag))
            {
                if (flag.ValueKind != JsonValueKind.Object ||
                    !flag.TryGetProperty("booleanValue", out var boolean) ||
                    boolean.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("Неверное поле _deleted.");
                deleted = boolean.GetBoolean();
            }
        }
        catch (FormatException exception)
        {
            logger.LogWarning(exception, "Неверный формат документа {Table}/{Id}",
                table.Name, document.Id);
            return ApplyResult.Ignored;
        }
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.ApplyingRemote = true;
        try
        {
            var link = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
                candidate.Table == table.Name && candidate.CloudId == document.Id,
                cancellationToken);
            if (link is null && await db.SyncAliases.AnyAsync(alias =>
                alias.Table == table.Name && alias.CloudId == document.Id, cancellationToken))
                return ApplyResult.Ignored;
            if (link?.CloudUpdatedAt >= document.UpdatedAt) return ApplyResult.Ignored;
            if (link is null && await db.SyncOutbox.AnyAsync(entry =>
                entry.Table == table.Name && entry.CloudId == document.Id &&
                entry.Deleted, cancellationToken))
                return ApplyResult.Deferred;
            if (deleted)
            {
                if (link is null) return ApplyResult.Ignored;
                if (await db.SyncOutbox.AnyAsync(entry =>
                    entry.Table == table.Name && entry.LocalId == link.LocalId,
                    cancellationToken))
                    return ApplyResult.Deferred;
                var row = await LoadAsync(db, table, link!.LocalId, true, cancellationToken);
                if (row is not null)
                {
                    foreach (var key in table.NaturalKeys)
                    {
                        var values = key.Properties.ToDictionary(property => property.Name,
                            property => db.Entry(row).Property(property.Name).CurrentValue);
                        var canonical = await FindOtherKeyAsync(db, table, key, values,
                            link.LocalId, document.Id, cancellationToken);
                        if (canonical is null) continue;
                        var canonicalId = (int)db.Entry(canonical).Property("Id").CurrentValue!;
                        var canonicalLink = await db.SyncLinks.SingleAsync(candidate =>
                            candidate.Table == table.Name && candidate.LocalId == canonicalId,
                            cancellationToken);
                        await TransferDependentsAsync(db, table, link.LocalId, canonicalId,
                            cancellationToken);
                        await AddAliasAsync(db, table.Name, document.Id, canonicalLink.CloudId,
                            cancellationToken);
                        break;
                    }
                    db.Remove(row);
                }
                db.SyncLinks.Remove(link);
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                var values = new Dictionary<string, object?>();
                var aliasUsed = false;
                try
                {
                    foreach (var property in table.Properties)
                    {
                        var name = property.Name == "ProtectedImage" &&
                            (table.ClrType == typeof(FaceSample) ||
                                table.ClrType == typeof(FaceSubmissionImage))
                            ? "Image" : property.Name;
                        if (!document.Fields.TryGetValue(name, out var value)) continue;
                        var converted = FirestoreValues.FromValue(value, property.ClrType);
                        if (name == "Image" && converted is byte[] bytes)
                            converted = images.Protect(bytes);
                        values[property.Name] = converted;
                    }
                    foreach (var key in table.ForeignKeys)
                    {
                        var property = key.Property;
                        if (!document.Fields.TryGetValue(property.Name, out var value)) continue;
                        if (value.TryGetProperty("nullValue", out _))
                        {
                            if (key.IsRequired) return ApplyResult.Deferred;
                            values[property.Name] = null;
                            continue;
                        }
                        var cloudId = (string?)FirestoreValues.FromValue(value, typeof(string));
                        var (parent, viaAlias) = await ResolveLinkAsync(
                            db, key.PrincipalTable, cloudId!, cancellationToken);
                        if (parent is null && key.IsRequired)
                        {
                            logger.LogDebug("Нет родителя {Table}/{Id} для {Child}/{ChildId}",
                                key.PrincipalTable, cloudId, table.Name, document.Id);
                            return ApplyResult.Deferred;
                        }
                        values[property.Name] = parent?.LocalId;
                        aliasUsed |= viaAlias;
                    }
                }
                catch (Exception exception) when (exception is FormatException or
                    InvalidCastException or OverflowException or KeyNotFoundException or
                    InvalidOperationException)
                {
                    logger.LogWarning(exception, "Неверный формат документа {Table}/{Id}",
                        table.Name, document.Id);
                    return ApplyResult.Ignored;
                }

                object? row = link is null ? null :
                    await LoadAsync(db, table, link.LocalId, true, cancellationToken);
                if (row is null && link is null && table.ClrType == typeof(Teacher) &&
                    values.TryGetValue(nameof(Teacher.Login), out var login) &&
                    login is string teacherLogin)
                {
                    var lower = teacherLogin.ToLowerInvariant();
                    row = await db.Teachers.FirstOrDefaultAsync(teacher =>
                        teacher.Login.ToLower() == lower, cancellationToken);
                }
                if (row is null && link is null)
                {
                    foreach (var key in table.UniqueIndexes.Select(index => new SyncKey(index.Properties, false))
                        .Concat(table.NaturalKeys))
                    {
                        if (key.Properties.Any(property => !values.ContainsKey(property.Name) ||
                            values[property.Name] is null && (!key.NullsEqual || !property.IsNullable))) continue;
                        row = await FindKeyAsync(db, table, key, values, cancellationToken);
                        if (row is not null) break;
                    }
                }
                var old = row is null || link is not null ? null :
                    await db.SyncLinks.FirstOrDefaultAsync(candidate =>
                        candidate.Table == table.Name &&
                        candidate.LocalId == (int)db.Entry(row).Property("Id").CurrentValue!,
                        cancellationToken);
                var teacherAdopt = link is null && row is Teacher { FirebaseUid: null };
                if (table.ClrType == typeof(Teacher) && old is not null &&
                    !teacherAdopt)
                {
                    logger.LogWarning(
                        "Преподаватель {Id} уже связан с другим документом Firebase",
                        old.LocalId);
                    return ApplyResult.Ignored;
                }
                if (row is not null)
                {
                    var rowId = (int)db.Entry(row).Property("Id").CurrentValue!;
                    var queued = await db.SyncOutbox
                        .Where(entry => entry.Table == table.Name && entry.LocalId == rowId)
                        .ToListAsync(cancellationToken);
                    if (queued.Count > 0 && !teacherAdopt) return ApplyResult.Deferred;
                    if (teacherAdopt) db.SyncOutbox.RemoveRange(queued);
                }

                var applyValues = true;
                var queueRow = aliasUsed;
                var replaceLink = teacherAdopt && old is not null;
                if (old is not null)
                {
                    var newer = teacherAdopt || old.CloudUpdatedAt is null ||
                        document.UpdatedAt > old.CloudUpdatedAt;
                    if (teacherAdopt || string.CompareOrdinal(document.Id, old.CloudId) < 0)
                    {
                        replaceLink = true;
                        applyValues = newer;
                        queueRow |= !newer;
                    }
                    else
                    {
                        applyValues = newer;
                        queueRow |= newer;
                    }
                }

                if (old is not null && !replaceLink)
                {
                    await AddAliasAsync(db, table.Name, document.Id, old.CloudId,
                        cancellationToken);
                    db.SyncOutbox.Add(new SyncOutboxEntry
                    {
                        Table = table.Name,
                        LocalId = old.LocalId,
                        CloudId = document.Id,
                        Deleted = true
                    });
                }
                if (replaceLink)
                {
                    var oldId = old!.CloudId;
                    await AddAliasAsync(db, table.Name, oldId, document.Id,
                        cancellationToken);
                    db.SyncLinks.Remove(old);
                    await db.SaveChangesAsync(cancellationToken);
                    if (!teacherAdopt)
                    {
                        db.SyncOutbox.Add(new SyncOutboxEntry
                        {
                            Table = table.Name,
                            LocalId = old.LocalId,
                            CloudId = oldId,
                            Deleted = true
                        });
                    }
                }

                var inserted = row is null;
                row ??= Activator.CreateInstance(table.ClrType)
                    ?? throw new InvalidOperationException("Не удалось создать сущность.");
                if (applyValues && row is Teacher incomingTeacher &&
                    values.TryGetValue(nameof(Teacher.FirebaseUid), out var uid) &&
                    uid is string newUid && incomingTeacher.FirebaseUid != newUid)
                    incomingTeacher.PasswordHash = "";
                if (applyValues && row is Teacher teacher && !inserted &&
                    (Changed(teacher.Login, values, nameof(Teacher.Login)) ||
                        Changed(teacher.IsAdmin, values, nameof(Teacher.IsAdmin)) ||
                        Changed(teacher.IsActive, values, nameof(Teacher.IsActive)) ||
                        Changed(teacher.FirebaseUid, values, nameof(Teacher.FirebaseUid))))
                    teacher.SecurityStamp = Guid.NewGuid().ToString("N");
                if (applyValues)
                    foreach (var (name, value) in values)
                        db.Entry(row).Property(name).CurrentValue = value;
                if (inserted)
                {
                    if (row is Teacher newTeacher)
                    {
                        newTeacher.PasswordHash = "";
                        newTeacher.LastLoginAt = null;
                    }
                    db.Add(row);
                    await db.SaveChangesAsync(cancellationToken);
                }

                var localId = (int)db.Entry(row).Property("Id").CurrentValue!;
                if (link is null && (old is null || replaceLink))
                    link = await EnsureLinkCoreAsync(
                        db, table.Name, localId, document.Id, cancellationToken);
                else if (link is not null) link.LocalId = localId;
                if (link is not null) link.CloudUpdatedAt = document.UpdatedAt;
                if (queueRow) await QueueAsync(db, table.Name, localId, cancellationToken);
                if (replaceLink || teacherAdopt)
                    await QueueDependentsAsync(db, table.Name, localId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return ApplyResult.Applied;
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Не удалось применить {Table}/{Id}",
                table.Name, document.Id);
            return ApplyResult.Deferred;
        }
        finally
        {
            db.ApplyingRemote = false;
        }
    }

    private static async Task AddAliasAsync(
        AppDbContext db, string table, string cloudId, string canonicalCloudId,
        CancellationToken cancellationToken)
    {
        if (db.SyncAliases.Local.Any(alias =>
                alias.Table == table && alias.CloudId == cloudId) ||
            await db.SyncAliases.AnyAsync(alias =>
                alias.Table == table && alias.CloudId == cloudId, cancellationToken))
            return;
        db.SyncAliases.Add(new SyncAlias
        {
            Table = table, CloudId = cloudId, CanonicalCloudId = canonicalCloudId
        });
    }

    private static async Task<(SyncLink? Link, bool ViaAlias)> ResolveLinkAsync(
        AppDbContext db, string table, string cloudId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var viaAlias = false;
        while (visited.Add(cloudId))
        {
            var link = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
                candidate.Table == table && candidate.CloudId == cloudId,
                cancellationToken);
            if (link is not null) return (link, viaAlias);
            var alias = await db.SyncAliases.FirstOrDefaultAsync(candidate =>
                candidate.Table == table && candidate.CloudId == cloudId,
                cancellationToken);
            if (alias is null) break;
            cloudId = alias.CanonicalCloudId;
            viaAlias = true;
        }
        return (null, viaAlias);
    }

    private static async Task QueueAsync(
        AppDbContext db, string table, int localId, CancellationToken cancellationToken)
    {
        if (db.SyncOutbox.Local.Any(entry =>
                entry.Table == table && entry.LocalId == localId &&
                !entry.Deleted && entry.Error == null) ||
            await db.SyncOutbox.AnyAsync(entry =>
                entry.Table == table && entry.LocalId == localId &&
                !entry.Deleted && entry.Error == null,
                cancellationToken))
            return;
        await RemoveErroredAsync(db, table, localId, cancellationToken);
        db.SyncOutbox.Add(new SyncOutboxEntry { Table = table, LocalId = localId });
    }

    private static async Task RemoveErroredAsync(
        AppDbContext db, string table, int localId, CancellationToken cancellationToken)
    {
        var entries = await db.SyncOutbox.Where(entry =>
            entry.Table == table && entry.LocalId == localId && entry.Error != null)
            .ToListAsync(cancellationToken);
        db.SyncOutbox.RemoveRange(entries);
    }

    private static async Task QueueDependentsAsync(
        AppDbContext db, string table, int localId, CancellationToken cancellationToken)
    {
        foreach (var child in new SyncModel(db).Tables)
            foreach (var key in child.ForeignKeys.Where(key => key.PrincipalTable == table))
            {
                var ids = await (Task<List<int>>)DependentsMethod.MakeGenericMethod(child.ClrType)
                    .Invoke(null, [db, key.Property, localId, cancellationToken])!;
                foreach (var id in ids)
                    await QueueAsync(db, child.Name, id, cancellationToken);
            }
    }

    private static async Task TransferDependentsAsync(
        AppDbContext db, SyncTable parent, int oldId, int canonicalId,
        CancellationToken cancellationToken)
    {
        foreach (var child in new SyncModel(db).Tables)
            foreach (var key in child.ForeignKeys.Where(key => key.PrincipalTable == parent.Name))
            {
                var ids = await (Task<List<int>>)DependentsMethod.MakeGenericMethod(child.ClrType)
                    .Invoke(null, [db, key.Property, oldId, cancellationToken])!;
                foreach (var id in ids)
                {
                    var row = await LoadAsync(db, child, id, true, cancellationToken);
                    if (row is null || !Equals(db.Entry(row).Property(key.Property.Name).CurrentValue, oldId))
                        continue;
                    var conflict = false;
                    foreach (var index in child.UniqueIndexes.Where(index => index.Properties.Contains(key.Property)))
                    {
                        var values = index.Properties.ToDictionary(property => property.Name,
                            property => property.Name == key.Property.Name ? (object?)canonicalId :
                                db.Entry(row).Property(property.Name).CurrentValue);
                        if (values.Values.Any(value => value is null)) continue;
                        var found = await FindKeyAsync(db, child, new SyncKey(index.Properties, false),
                            values, cancellationToken);
                        if (found is not null && (int)db.Entry(found).Property("Id").CurrentValue! != id)
                            conflict = true;
                        if (db.ChangeTracker.Entries().Any(entry =>
                            entry.Metadata.ClrType == child.ClrType &&
                            entry.State is not (EntityState.Detached or EntityState.Deleted) &&
                            !Equals(entry.Property("Id").CurrentValue, id) &&
                            index.Properties.All(property => Equals(
                                entry.Property(property.Name).CurrentValue, values[property.Name]))))
                            conflict = true;
                        if (conflict) break;
                    }
                    if (conflict)
                    {
                        var pending = await db.SyncOutbox.Where(entry =>
                            entry.Table == child.Name && entry.LocalId == id)
                            .ToListAsync(cancellationToken);
                        pending.AddRange(db.SyncOutbox.Local.Where(entry =>
                            entry.Table == child.Name && entry.LocalId == id &&
                            !pending.Contains(entry)));
                        db.SyncOutbox.RemoveRange(pending);
                        var link = await db.SyncLinks.FirstOrDefaultAsync(candidate =>
                            candidate.Table == child.Name && candidate.LocalId == id,
                            cancellationToken);
                        if (link is not null) db.SyncLinks.Remove(link);
                        db.Remove(row);
                    }
                    else
                    {
                        db.Entry(row).Property(key.Property.Name).CurrentValue = canonicalId;
                        await QueueAsync(db, child.Name, id, cancellationToken);
                    }
                }
            }
    }

    private static Task<List<int>> FindDependentsCoreAsync<T>(
        AppDbContext db, IProperty property, int localId,
        CancellationToken cancellationToken) where T : class
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        var left = Expression.Call(typeof(EF), nameof(EF.Property),
            [property.ClrType], parameter, Expression.Constant(property.Name));
        var right = Expression.Convert(Expression.Constant(localId), property.ClrType);
        var predicate = Expression.Lambda<Func<T, bool>>(
            Expression.Equal(left, right), parameter);
        return db.Set<T>().AsNoTracking().Where(predicate)
            .Select(row => EF.Property<int>(row, "Id")).ToListAsync(cancellationToken);
    }

    private static bool Changed<T>(T current, Dictionary<string, object?> values, string name) =>
        values.TryGetValue(name, out var next) && !Equals(current, next);

    private static Task<object?> LoadAsync(
        AppDbContext db, SyncTable table, int id, bool tracked, CancellationToken cancellationToken) =>
        (Task<object?>)LoadMethod.MakeGenericMethod(table.ClrType)
            .Invoke(null, [db, id, tracked, cancellationToken])!;

    private static async Task<object?> LoadCoreAsync<T>(
        AppDbContext db, int id, bool tracked, CancellationToken cancellationToken) where T : class
    {
        IQueryable<T> query = db.Set<T>();
        if (!tracked) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(
            row => EF.Property<int>(row, "Id") == id, cancellationToken);
    }

    private static Task<List<int>> MissingAsync(
        AppDbContext db, SyncTable table, CancellationToken cancellationToken) =>
        (Task<List<int>>)MissingMethod.MakeGenericMethod(table.ClrType)
            .Invoke(null, [db, table.Name, cancellationToken])!;

    private static Task<List<int>> MissingCoreAsync<T>(
        AppDbContext db, string table, CancellationToken cancellationToken) where T : class =>
        db.Set<T>().AsNoTracking()
            .Where(row => !db.SyncLinks.Any(link =>
                    link.Table == table && link.LocalId == EF.Property<int>(row, "Id")) &&
                !db.SyncOutbox.Any(entry =>
                    entry.Table == table && entry.LocalId == EF.Property<int>(row, "Id")))
            .Select(row => EF.Property<int>(row, "Id"))
            .ToListAsync(cancellationToken);

    private static Task<object?> FindKeyAsync(
        AppDbContext db, SyncTable table, SyncKey key,
        Dictionary<string, object?> values, CancellationToken cancellationToken) =>
        (Task<object?>)KeyMethod.MakeGenericMethod(table.ClrType)
            .Invoke(null, [db, key, values, cancellationToken])!;

    private static async Task<object?> FindKeyCoreAsync<T>(
        AppDbContext db, SyncKey key, Dictionary<string, object?> values,
        CancellationToken cancellationToken) where T : class
        => await db.Set<T>().FirstOrDefaultAsync(
            KeyPredicate<T>(key, values), cancellationToken);

    private static Task<object?> FindOtherKeyAsync(
        AppDbContext db, SyncTable table, SyncKey key,
        Dictionary<string, object?> values, int excludedId, string cloudId,
        CancellationToken cancellationToken) =>
        (Task<object?>)OtherKeyMethod.MakeGenericMethod(table.ClrType)
            .Invoke(null, [db, table.Name, key, values, excludedId, cloudId, cancellationToken])!;

    private static async Task<object?> FindOtherKeyCoreAsync<T>(
        AppDbContext db, string table, SyncKey key, Dictionary<string, object?> values,
        int excludedId, string cloudId, CancellationToken cancellationToken) where T : class =>
        await db.Set<T>().Where(KeyPredicate<T>(key, values, excludedId))
            .Where(row => db.SyncLinks.Any(link => link.Table == table &&
                link.LocalId == EF.Property<int>(row, "Id") && link.CloudId != cloudId))
            .OrderBy(row => EF.Property<int>(row, "Id"))
            .FirstOrDefaultAsync(cancellationToken);

    private static Expression<Func<T, bool>> KeyPredicate<T>(
        SyncKey key, Dictionary<string, object?> values, int? excludedId = null)
    {
        var parameter = Expression.Parameter(typeof(T), "row");
        Expression? predicate = null;
        foreach (var property in key.Properties)
        {
            var left = Expression.Call(typeof(EF), nameof(EF.Property),
                [property.ClrType], parameter, Expression.Constant(property.Name));
            var value = values[property.Name];
            Expression right = value is null
                ? Expression.Constant(null, property.ClrType)
                : Expression.Convert(Expression.Constant(value), property.ClrType);
            var equal = Expression.Equal(left, right);
            predicate = predicate is null ? equal : Expression.AndAlso(predicate, equal);
        }
        if (excludedId is int id)
        {
            var rowId = Expression.Call(typeof(EF), nameof(EF.Property),
                [typeof(int)], parameter, Expression.Constant("Id"));
            predicate = Expression.AndAlso(predicate!,
                Expression.NotEqual(rowId, Expression.Constant(id)));
        }
        return Expression.Lambda<Func<T, bool>>(predicate!, parameter);
    }
}
