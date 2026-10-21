using BiStudent.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BiStudent.Server.Sync;

public sealed record SyncForeignKey(
    IProperty Property, string PrincipalTable, bool IsRequired);

public sealed record SyncKey(IReadOnlyList<IProperty> Properties, bool NullsEqual);

public sealed record SyncTable(
    string Name, Type ClrType, IReadOnlyList<IProperty> Properties,
    IReadOnlyList<SyncForeignKey> ForeignKeys, IReadOnlyList<IIndex> UniqueIndexes,
    IReadOnlyList<SyncKey> NaturalKeys);

public sealed class SyncModel
{
    private static readonly HashSet<string> ExcludedTeacherProperties =
    [
        nameof(Teacher.PasswordHash),
        nameof(Teacher.SecurityStamp),
        nameof(Teacher.LastLoginAt),
        nameof(Teacher.ClaimToken)
    ];

    public IReadOnlyList<SyncTable> Tables { get; }

    public SyncModel(AppDbContext db)
    {
        var types = db.Model.GetEntityTypes()
            .Where(type => type.ClrType != typeof(AppSetting) &&
                type.ClrType != typeof(SyncLink) &&
                type.ClrType != typeof(SyncOutboxEntry) &&
                type.ClrType != typeof(SyncAlias))
            .ToDictionary(type => type.ClrType);
        var tables = new Dictionary<Type, SyncTable>();
        foreach (var type in types.Values)
        {
            var key = type.FindPrimaryKey();
            if (key?.Properties.Count != 1 ||
                key.Properties[0].Name != "Id" ||
                key.Properties[0].ClrType != typeof(int))
                throw new InvalidOperationException(
                    $"У {type.ClrType.Name} ожидается первичный ключ int Id.");

            var mappedKeys = type.GetForeignKeys().ToArray();
            if (mappedKeys.Any(foreignKey => foreignKey.Properties.Count != 1 ||
                !types.ContainsKey(foreignKey.PrincipalEntityType.ClrType)))
                throw new InvalidOperationException(
                    $"Внешний ключ {type.ClrType.Name} не поддерживается синхронизацией.");
            var foreignKeys = mappedKeys.Select(key => new SyncForeignKey(
                key.Properties[0], key.PrincipalEntityType.GetTableName()!, key.IsRequired))
                .ToList();
            if (type.ClrType == typeof(FaceSubmission))
            {
                var reviewedBy = type.FindProperty(nameof(FaceSubmission.ReviewedByTeacherId))
                    ?? throw new InvalidOperationException("Поле преподавателя не найдено.");
                foreignKeys.Add(new SyncForeignKey(
                    reviewedBy, types[typeof(Teacher)].GetTableName()!, false));
            }
            var foreignNames = foreignKeys.Select(key => key.Property.Name).ToHashSet();
            var properties = type.GetProperties()
                .Where(property => property.Name != "Id" &&
                    !foreignNames.Contains(property.Name) &&
                    (type.ClrType != typeof(Teacher) ||
                        !ExcludedTeacherProperties.Contains(property.Name)))
                .ToArray();
            IReadOnlyList<SyncKey> naturalKeys = type.ClrType == typeof(Student)
                ? [Key(type, nameof(Student.GroupId), nameof(Student.FullName))]
                : type.ClrType == typeof(ScheduleSlot)
                    ? [Key(type, nameof(ScheduleSlot.TeacherId), nameof(ScheduleSlot.GroupId),
                        nameof(ScheduleSlot.Subject), nameof(ScheduleSlot.DayOfWeek),
                        nameof(ScheduleSlot.Start), nameof(ScheduleSlot.End), nameof(ScheduleSlot.Parity),
                        nameof(ScheduleSlot.ValidFrom), nameof(ScheduleSlot.ValidTo),
                        nameof(ScheduleSlot.OneOffDate), nameof(ScheduleSlot.Room))]
                    : [];
            tables.Add(type.ClrType, new SyncTable(type.GetTableName() ??
                throw new InvalidOperationException("У таблицы нет имени."),
                type.ClrType, properties, foreignKeys,
                type.GetIndexes().Where(index => index.IsUnique).ToArray(), naturalKeys));
        }

        var ordered = new List<SyncTable>();
        var visiting = new HashSet<Type>();
        var visited = new HashSet<Type>();
        foreach (var table in tables.Values.OrderBy(table => table.Name))
            Visit(table);
        Tables = ordered;

        void Visit(SyncTable table)
        {
            if (visited.Contains(table.ClrType)) return;
            if (!visiting.Add(table.ClrType))
                throw new InvalidOperationException("Цикл внешних ключей при синхронизации.");
            foreach (var key in table.ForeignKeys)
                Visit(tables.Values.Single(parent => parent.Name == key.PrincipalTable));
            visiting.Remove(table.ClrType);
            visited.Add(table.ClrType);
            ordered.Add(table);
        }
    }

    private static SyncKey Key(IEntityType type, params string[] names) =>
        new(names.Select(name => type.FindProperty(name) ??
            throw new InvalidOperationException($"Поле {name} не найдено.")).ToArray(), true);

}
