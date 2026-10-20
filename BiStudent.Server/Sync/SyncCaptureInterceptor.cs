using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace BiStudent.Server.Sync;

public sealed class SyncCaptureInterceptor(SyncSignal signal) : SaveChangesInterceptor
{
    private sealed record Pending(
        EntityEntry? Entry, string Table, int LocalId, bool Deleted, string? CloudId);

    private sealed class State
    {
        public IDbContextTransaction? Transaction;
        public bool ManualOutbox;
        public List<Pending> Entries { get; } = [];
        public HashSet<(string Table, int LocalId)> Replacements { get; } = [];
    }

    private readonly ConditionalWeakTable<DbContext, State> states = new();

    public static void Enqueue<T>(AppDbContext db, int id) where T : class
    {
        var table = db.Model.FindEntityType(typeof(T))?.GetTableName()
            ?? throw new InvalidOperationException("Таблица не найдена.");
        db.SyncOutbox.Add(new SyncOutboxEntry { Table = table, LocalId = id });
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is AppDbContext db && !db.ApplyingRemote)
            Prepare(db);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db && !db.ApplyingRemote)
            await PrepareAsync(db, cancellationToken);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (eventData.Context is AppDbContext db)
            Finish(db);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
            await FinishAsync(db, cancellationToken);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is AppDbContext db)
            Fail(db);
    }

    public override async Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
            await FailAsync(db, cancellationToken);
    }

    public override void SaveChangesCanceled(DbContextEventData eventData)
    {
        if (eventData.Context is AppDbContext db)
            Fail(db);
    }

    public override async Task SaveChangesCanceledAsync(
        DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AppDbContext db)
            await FailAsync(db, CancellationToken.None);
    }

    private State? Collect(AppDbContext db)
    {
        states.Remove(db);
        var model = new SyncModel(db);
        var names = model.Tables.ToDictionary(table => table.ClrType, table => table.Name);
        var state = new State();
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.Entity is SyncOutboxEntry manual && entry.State == EntityState.Added)
            {
                state.ManualOutbox = true;
                state.Replacements.Add((manual.Table, manual.LocalId));
            }
            if (!names.TryGetValue(entry.Metadata.ClrType, out var table)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            if (entry.Metadata.ClrType == typeof(Teacher) &&
                entry.State == EntityState.Modified &&
                !entry.Properties.Any(property => property.IsModified &&
                    property.Metadata.Name is not (nameof(Teacher.PasswordHash) or
                        nameof(Teacher.SecurityStamp) or
                        nameof(Teacher.LastLoginAt) or
                        nameof(Teacher.ClaimToken))))
                continue;
            state.Entries.Add(new Pending(entry, table,
                entry.State == EntityState.Added ? 0 :
                    (int)entry.Property("Id").CurrentValue!,
                entry.State == EntityState.Deleted, null));
        }
        if (state.Entries.Count == 0 && !state.ManualOutbox) return null;
        states.Add(db, state);
        return state;
    }

    private void Prepare(AppDbContext db)
    {
        var state = Collect(db);
        if (state is null) return;
        try
        {
            if ((state.Entries.Count > 0 || state.ManualOutbox) &&
                db.Database.CurrentTransaction is null)
                state.Transaction = db.Database.BeginTransaction();
            ExpandCascades(db, state);
            for (var i = state.Entries.Count - 1; i >= 0; i--)
            {
                var entry = state.Entries[i];
                if (!entry.Deleted) continue;
                var cloudId = db.SyncLinks.AsNoTracking()
                    .Where(link => link.Table == entry.Table && link.LocalId == entry.LocalId)
                    .Select(link => link.CloudId).FirstOrDefault();
                if (cloudId is null) state.Entries.RemoveAt(i);
                else state.Entries[i] = entry with { CloudId = cloudId };
            }
        }
        catch
        {
            states.Remove(db);
            state.Transaction?.Dispose();
            throw;
        }
    }

    private async Task PrepareAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var state = Collect(db);
        if (state is null) return;
        try
        {
            if ((state.Entries.Count > 0 || state.ManualOutbox) &&
                db.Database.CurrentTransaction is null)
                state.Transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await ExpandCascadesAsync(db, state, cancellationToken);
            for (var i = state.Entries.Count - 1; i >= 0; i--)
            {
                var entry = state.Entries[i];
                if (!entry.Deleted) continue;
                var cloudId = await db.SyncLinks.AsNoTracking()
                    .Where(link => link.Table == entry.Table && link.LocalId == entry.LocalId)
                    .Select(link => link.CloudId).FirstOrDefaultAsync(cancellationToken);
                if (cloudId is null) state.Entries.RemoveAt(i);
                else state.Entries[i] = entry with { CloudId = cloudId };
            }
        }
        catch
        {
            states.Remove(db);
            if (state.Transaction is not null)
                await state.Transaction.DisposeAsync();
            throw;
        }
    }

    private void Finish(AppDbContext db)
    {
        if (!states.TryGetValue(db, out var state)) return;
        try
        {
            var queuedAt = DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            foreach (var entry in state.Entries)
                state.Replacements.Add((entry.Table, entry.Entry is null ? entry.LocalId :
                    (int)entry.Entry.Property("Id").CurrentValue!));
            foreach (var (table, localId) in state.Replacements)
                db.Database.ExecuteSql(
                    $"DELETE FROM \"SyncOutbox\" WHERE \"Table\" = {table} AND \"LocalId\" = {localId} AND \"Error\" IS NOT NULL");
            foreach (var entry in state.Entries)
            {
                var table = entry.Table;
                var localId = entry.Entry is null ? entry.LocalId :
                    (int)entry.Entry.Property("Id").CurrentValue!;
                var cloudId = entry.CloudId;
                var deleted = entry.Deleted ? 1 : 0;
                db.Database.ExecuteSql(
                    $"INSERT INTO \"SyncOutbox\" (\"Table\", \"LocalId\", \"CloudId\", \"Deleted\", \"QueuedAt\") VALUES ({table}, {localId}, {cloudId}, {deleted}, {queuedAt})");
            }
            state.Transaction?.Commit();
            if (state.Entries.Count > 0 || state.ManualOutbox) signal.Notify();
        }
        catch
        {
            state.Transaction?.Rollback();
            throw;
        }
        finally
        {
            state.Transaction?.Dispose();
            states.Remove(db);
        }
    }

    private async Task FinishAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (!states.TryGetValue(db, out var state)) return;
        try
        {
            var queuedAt = DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
            foreach (var entry in state.Entries)
                state.Replacements.Add((entry.Table, entry.Entry is null ? entry.LocalId :
                    (int)entry.Entry.Property("Id").CurrentValue!));
            foreach (var (table, localId) in state.Replacements)
                await db.Database.ExecuteSqlAsync(
                    $"DELETE FROM \"SyncOutbox\" WHERE \"Table\" = {table} AND \"LocalId\" = {localId} AND \"Error\" IS NOT NULL",
                    cancellationToken);
            foreach (var entry in state.Entries)
            {
                var table = entry.Table;
                var localId = entry.Entry is null ? entry.LocalId :
                    (int)entry.Entry.Property("Id").CurrentValue!;
                var cloudId = entry.CloudId;
                var deleted = entry.Deleted ? 1 : 0;
                await db.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"SyncOutbox\" (\"Table\", \"LocalId\", \"CloudId\", \"Deleted\", \"QueuedAt\") VALUES ({table}, {localId}, {cloudId}, {deleted}, {queuedAt})",
                    cancellationToken);
            }
            if (state.Transaction is not null)
                await state.Transaction.CommitAsync(cancellationToken);
            if (state.Entries.Count > 0 || state.ManualOutbox) signal.Notify();
        }
        catch
        {
            if (state.Transaction is not null)
                await state.Transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (state.Transaction is not null)
                await state.Transaction.DisposeAsync();
            states.Remove(db);
        }
    }

    private void Fail(AppDbContext db)
    {
        if (!states.TryGetValue(db, out var state)) return;
        try { state.Transaction?.Rollback(); }
        finally
        {
            state.Transaction?.Dispose();
            states.Remove(db);
        }
    }

    private async Task FailAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (!states.TryGetValue(db, out var state)) return;
        try
        {
            if (state.Transaction is not null)
                await state.Transaction.RollbackAsync(CancellationToken.None);
        }
        finally
        {
            if (state.Transaction is not null)
                await state.Transaction.DisposeAsync();
            states.Remove(db);
        }
    }

    private static void ExpandCascades(AppDbContext db, State state)
    {
        var tables = new SyncModel(db).Tables.ToDictionary(table => table.ClrType);
        var queue = new Queue<(Type Type, int Id)>(state.Entries
            .Where(entry => entry.Deleted)
            .Select(entry => (entry.Entry!.Metadata.ClrType, entry.LocalId)));
        var seen = queue.ToHashSet();
        while (queue.TryDequeue(out var parent))
        {
            var entity = db.Model.FindEntityType(parent.Type)!;
            foreach (var key in entity.GetReferencingForeignKeys()
                .Where(key => key.DeleteBehavior == DeleteBehavior.Cascade &&
                    key.Properties.Count == 1 &&
                    tables.ContainsKey(key.DeclaringEntityType.ClrType)))
            {
                var child = tables[key.DeclaringEntityType.ClrType];
                foreach (var id in DependentIds(db, child.Name, key.Properties[0].Name, parent.Id))
                {
                    var candidate = (child.ClrType, id);
                    if (!CurrentCascade(db, child.ClrType, id, key.Properties[0].Name,
                        parent.Id)) continue;
                    if (!seen.Add(candidate)) continue;
                    queue.Enqueue(candidate);
                    state.Entries.Add(new Pending(null, child.Name, id, true, null));
                }
            }
        }
    }

    private static async Task ExpandCascadesAsync(
        AppDbContext db, State state, CancellationToken cancellationToken)
    {
        var tables = new SyncModel(db).Tables.ToDictionary(table => table.ClrType);
        var queue = new Queue<(Type Type, int Id)>(state.Entries
            .Where(entry => entry.Deleted)
            .Select(entry => (entry.Entry!.Metadata.ClrType, entry.LocalId)));
        var seen = queue.ToHashSet();
        while (queue.TryDequeue(out var parent))
        {
            var entity = db.Model.FindEntityType(parent.Type)!;
            foreach (var key in entity.GetReferencingForeignKeys()
                .Where(key => key.DeleteBehavior == DeleteBehavior.Cascade &&
                    key.Properties.Count == 1 &&
                    tables.ContainsKey(key.DeclaringEntityType.ClrType)))
            {
                var child = tables[key.DeclaringEntityType.ClrType];
                var ids = await DependentIdsAsync(
                    db, child.Name, key.Properties[0].Name, parent.Id, cancellationToken);
                foreach (var id in ids)
                {
                    var candidate = (child.ClrType, id);
                    if (!CurrentCascade(db, child.ClrType, id, key.Properties[0].Name,
                        parent.Id)) continue;
                    if (!seen.Add(candidate)) continue;
                    queue.Enqueue(candidate);
                    state.Entries.Add(new Pending(null, child.Name, id, true, null));
                }
            }
        }
    }

    private static bool CurrentCascade(
        AppDbContext db, Type type, int id, string foreignKey, int parentId)
    {
        var tracked = db.ChangeTracker.Entries().FirstOrDefault(entry =>
            entry.Metadata.ClrType == type && entry.State != EntityState.Detached &&
            (int)entry.Property("Id").CurrentValue! == id);
        return tracked is null ||
            (tracked.State != EntityState.Added &&
                Equals(tracked.Property(foreignKey).CurrentValue, parentId));
    }

    private static List<int> DependentIds(
        AppDbContext db, string table, string column, int parentId)
    {
        using var command = DependentCommand(db, table, column, parentId);
        using var reader = command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static async Task<List<int>> DependentIdsAsync(
        AppDbContext db, string table, string column, int parentId,
        CancellationToken cancellationToken)
    {
        await using var command = DependentCommand(db, table, column, parentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt32(0));
        return ids;
    }

    private static DbCommand DependentCommand(
        AppDbContext db, string table, string column, int parentId)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT \"Id\" FROM \"{table.Replace("\"", "\"\"")}\" " +
            $"WHERE \"{column.Replace("\"", "\"\"")}\" = @parent";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@parent";
        parameter.Value = parentId;
        command.Parameters.Add(parameter);
        return command;
    }
}
