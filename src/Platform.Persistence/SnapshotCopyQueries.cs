using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Platform.Persistence;

internal sealed class SqliteSnapshotDbContext(DbContextOptions<SqliteSnapshotDbContext> options) : AkantiDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        foreach (var entity in builder.Model.GetEntityTypes()) foreach (var property in entity.GetProperties()) { property.ValueGenerated = ValueGenerated.Never; property.SetBeforeSaveBehavior(PropertySaveBehavior.Save); }
    }
}
internal static class SnapshotCopyQueries
{
    internal static Task<long> CountAsync(AkantiDbContext data, string table, CancellationToken ct)
    {
        var entity = data.Model.GetEntityTypes().SingleOrDefault(value => value.GetTableName() == table) ?? throw new InvalidOperationException("Snapshot entity is unavailable.");
        return (Task<long>)typeof(SnapshotCopyQueries).GetMethod(nameof(CountRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entity.ClrType).Invoke(null, [data, ct])!;
    }
    private static Task<long> CountRowsAsync<T>(AkantiDbContext data, CancellationToken ct) where T : class => data.Set<T>().LongCountAsync(ct);
    internal static Task<long> CopyAsync(AkantiDbContext source, AkantiDbContext target, string table, string[] columns, string[] key, Action<IncrementalHash, object?> hash, Func<object?, object?> convert, CancellationToken ct)
    {
        var entity = source.Model.GetEntityTypes().SingleOrDefault(value => value.GetTableName() == table) ?? throw new InvalidOperationException("Snapshot entity is unavailable.");
        var targetEntity = target.Model.FindEntityType(entity.ClrType) ?? throw new InvalidOperationException("Snapshot target entity is unavailable.");
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var properties = entity.GetProperties().ToDictionary(value => value.GetColumnName(store)!, StringComparer.Ordinal);
        if (!properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(columns) || !entity.FindPrimaryKey()!.Properties.Select(value => value.GetColumnName(store)).SequenceEqual(key) || targetEntity.GetProperties().Count() != properties.Count) throw new InvalidOperationException("Snapshot model differs from the physical schema.");
        var method = typeof(SnapshotCopyQueries).GetMethod(nameof(CopyRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entity.ClrType);
        return (Task<long>)method.Invoke(null, [source, target, columns.Select(name => properties[name].PropertyInfo!).ToArray(), entity.FindPrimaryKey()!.Properties.Select(value => value.PropertyInfo!).ToArray(), hash, convert, ct])!;
    }
    private static async Task<long> CopyRowsAsync<T>(AkantiDbContext source, AkantiDbContext target, PropertyInfo[] columns, PropertyInfo[] key, Action<IncrementalHash, object?> hash, Func<object?, object?> convert, CancellationToken ct) where T : class
    {
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        T? cursor = null; long count = 0;
        while (true)
        {
            IQueryable<T> query = source.Set<T>().AsNoTracking();
            if (cursor is not null) query = query.Where(After(cursor, key));
            var rows = await Ordered(query, key).Take(128).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var row in rows) foreach (var column in columns) hash(sourceHash, convert(column.GetValue(row)));
            target.AddRange(rows); await target.SaveChangesAsync(ct); target.ChangeTracker.Clear();
            var copied = await target.Set<T>().AsNoTracking().Where(Keys(rows, key)).ToListAsync(ct);
            if (copied.Count != rows.Count) throw new InvalidOperationException("Snapshot batch count differs.");
            foreach (var row in rows)
            {
                var stored = copied.Single(value => key.All(property => Equals(property.GetValue(row), property.GetValue(value))));
                foreach (var column in columns) hash(targetHash, convert(column.GetValue(stored)));
            }
            count = checked(count + rows.Count); cursor = rows[^1];
        }
        if (await target.Set<T>().LongCountAsync(ct) != count || !CryptographicOperations.FixedTimeEquals(sourceHash.GetHashAndReset(), targetHash.GetHashAndReset())) throw new InvalidOperationException("Snapshot row count or content differs.");
        return count;
    }
    private static IQueryable<T> Ordered<T>(IQueryable<T> query, PropertyInfo[] key)
    {
        var row = Expression.Parameter(typeof(T), "row");
        for (int i = 0; i < key.Length; i++)
        {
            var property = Expression.Property(row, key[i]); var selector = Expression.Lambda(property, row);
            query = query.Provider.CreateQuery<T>(Expression.Call(typeof(Queryable), i == 0 ? nameof(Queryable.OrderBy) : nameof(Queryable.ThenBy), [typeof(T), property.Type], query.Expression, Expression.Quote(selector)));
        }
        return query;
    }
    private static Expression<Func<T, bool>> After<T>(T cursor, PropertyInfo[] key)
    {
        var row = Expression.Parameter(typeof(T), "row"); Expression result = Expression.Constant(false), prefix = Expression.Constant(true);
        foreach (var property in key)
        {
            var left = Expression.Property(row, property); var right = Expression.Constant(property.GetValue(cursor), property.PropertyType);
            Expression greater = property.PropertyType == typeof(string) || property.PropertyType == typeof(Guid) ? Expression.GreaterThan(Expression.Call(left, nameof(string.CompareTo), null, right), Expression.Constant(0)) : Expression.GreaterThan(left, right);
            result = Expression.OrElse(result, Expression.AndAlso(prefix, greater)); prefix = Expression.AndAlso(prefix, Expression.Equal(left, right));
        }
        return Expression.Lambda<Func<T, bool>>(result, row);
    }
    private static Expression<Func<T, bool>> Keys<T>(IReadOnlyList<T> rows, PropertyInfo[] key)
    {
        var parameter = Expression.Parameter(typeof(T), "row"); Expression result = Expression.Constant(false);
        foreach (var row in rows)
        {
            Expression match = Expression.Constant(true);
            foreach (var property in key) match = Expression.AndAlso(match, Expression.Equal(Expression.Property(parameter, property), Expression.Constant(property.GetValue(row), property.PropertyType)));
            result = Expression.OrElse(result, match);
        }
        return Expression.Lambda<Func<T, bool>>(result, parameter);
    }
}
