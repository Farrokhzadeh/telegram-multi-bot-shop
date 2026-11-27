using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Platform.Persistence;

internal sealed class PlainTargetCopyDbContext(DbContextOptions<PlainTargetCopyDbContext> options) : PlainTargetDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        SnapshotCopyQueries.PreserveExplicitValues(builder);
    }
}
internal static class SnapshotCopyQueries
{
    internal static void PreserveExplicitValues(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
            {
                property.ValueGenerated = ValueGenerated.Never;
                property.SetBeforeSaveBehavior(PropertySaveBehavior.Save);
            }
    }
    internal static Task<long> CountAsync(DbContext data, string table, CancellationToken ct)
    {
        var entity = data.Model.GetEntityTypes().SingleOrDefault(value => value.GetTableName() == table) ?? throw new InvalidOperationException("Snapshot entity is unavailable.");
        return (Task<long>)typeof(SnapshotCopyQueries).GetMethod(nameof(CountRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entity.ClrType).Invoke(null, [data, ct])!;
    }
    private static Task<long> CountRowsAsync<T>(DbContext data, CancellationToken ct) where T : class => data.Set<T>().LongCountAsync(ct);
    internal static Task<long> CopyAsync(DbContext source, DbContext target, string table, string[] columns, string[] key, Action<IncrementalHash, object?> hash, Func<object?, object?> convert, CancellationToken ct)
    {
        var entity = source.Model.GetEntityTypes().SingleOrDefault(value => value.GetTableName() == table) ?? throw new InvalidOperationException("Snapshot entity is unavailable.");
        var targetEntity = target.Model.FindEntityType(entity.ClrType) ?? throw new InvalidOperationException("Snapshot target entity is unavailable.");
        var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
        var properties = entity.GetProperties().ToDictionary(value => value.GetColumnName(store)!, StringComparer.Ordinal);
        if (!properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(columns) || !entity.FindPrimaryKey()!.Properties.Select(value => value.GetColumnName(store)).SequenceEqual(key) || targetEntity.GetProperties().Count() != properties.Count) throw new InvalidOperationException("Snapshot model differs from the physical schema.");
        var method = typeof(SnapshotCopyQueries).GetMethod(nameof(CopyRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entity.ClrType);
        return (Task<long>)method.Invoke(null, [source, target, columns.Select(name => properties[name].PropertyInfo!).ToArray(), entity.FindPrimaryKey()!.Properties.Select(value => value.PropertyInfo!).ToArray(), hash, convert, ct])!;
    }
    private static async Task<long> CopyRowsAsync<T>(DbContext source, DbContext target, PropertyInfo[] columns, PropertyInfo[] key, Action<IncrementalHash, object?> hash, Func<object?, object?> convert, CancellationToken ct) where T : class
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
    internal static Task HashRowsAsync(DbContext source, IEntityType entity, IncrementalHash hash, CancellationToken ct, Action<object>? inspect = null)
    {
        var key = entity.FindPrimaryKey()!.Properties.Select(property => property.PropertyInfo!).ToArray();
        var properties = entity.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        var method = typeof(SnapshotCopyQueries).GetMethod(nameof(HashTableAsync), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entity.ClrType);
        SqliteStagingCopy.HashValue(hash, entity.GetTableName());
        foreach (var property in properties) SqliteStagingCopy.HashValue(hash, property.Name);
        return (Task)method.Invoke(null, [source, key, properties.Select(property => property.PropertyInfo!).ToArray(), hash, ct, inspect])!;
    }

    private static async Task HashTableAsync<T>(DbContext source, PropertyInfo[] key, PropertyInfo[] fields, IncrementalHash hash, CancellationToken ct, Action<object>? inspect) where T : class
    {
        T? cursor = null;
        while (true)
        {
            IQueryable<T> query = source.Set<T>().AsNoTracking();
            if (cursor is not null) query = query.Where(After(cursor, key));
            var rows = await Ordered(query, key).Take(128).ToListAsync(ct);
            foreach (T row in rows)
            {
                inspect?.Invoke(row);
                SqliteStagingCopy.HashValue(hash, "row");
                foreach (var field in fields)
                    SqliteStagingCopy.HashValue(hash, SqliteStagingCopy.ConvertValue(field.GetValue(row)));
            }
            if (rows.Count < 128) break;
            cursor = rows[^1];
        }
        SqliteStagingCopy.HashValue(hash, "end");
    }

    internal static Task<long> CopyOrVerifyAsync(DbContext source, PlainTargetCopyDbContext target, IEntityType entity,
        Func<object, object, CancellationToken, Task> transform, bool verifyOnly, CancellationToken ct,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? commitBatch = null)
    {
        var destination = target.Model.FindEntityType(entity.ClrType) ?? throw new InvalidOperationException("Snapshot entity is unavailable.");
        var key = entity.FindPrimaryKey()!.Properties.Select(property => property.PropertyInfo!).ToArray();
        if (!destination.FindPrimaryKey()!.Properties.Select(property => property.Name).SequenceEqual(key.Select(property => property.Name)))
            throw new InvalidOperationException("Snapshot primary key differs.");
        var fields = destination.GetProperties().Select(property => property.PropertyInfo!).ToArray();
        var method = typeof(SnapshotCopyQueries).GetMethod(nameof(CopyOrVerifyRowsAsync), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(entity.ClrType);
        return (Task<long>)method.Invoke(null, [source, target, key, fields, transform, verifyOnly, ct, commitBatch])!;
    }

    private static async Task<long> CopyOrVerifyRowsAsync<T>(DbContext source, PlainTargetCopyDbContext target,
        PropertyInfo[] key, PropertyInfo[] fields, Func<object, object, CancellationToken, Task> transform, bool verifyOnly, CancellationToken ct,
        Func<Func<CancellationToken, Task>, CancellationToken, Task>? commitBatch) where T : class, new()
    {
        T? cursor = null;
        long count = 0;
        while (true)
        {
            IQueryable<T> query = source.Set<T>().AsNoTracking();
            if (cursor is not null) query = query.Where(After(cursor, key));
            var rows = await Ordered(query, key).Take(128).ToListAsync(ct);
            if (rows.Count == 0) break;
            var expected = new List<T>(rows.Count);
            List<T>? actual = null;
            try
            {
                foreach (T original in rows)
                {
                    var row = new T();
                    expected.Add(row);
                    await transform(original, row, ct);
                }
                async Task ReconcileAsync(CancellationToken token)
                {
                    if (commitBatch is not null)
                    {
                        var previous = await target.Set<T>().AsNoTracking().Where(Keys(rows, key)).ToListAsync(token);
                        if (previous.Count != 0)
                        {
                            actual = previous;
                            Verify();
                            return;
                        }
                    }
                    if (!verifyOnly)
                    {
                        target.AddRange(expected);
                        await target.SaveChangesAsync(token);
                    }
                    actual = await target.Set<T>().AsNoTracking().Where(Keys(rows, key)).ToListAsync(token);
                    Verify();
                }
                void Verify()
                {
                    if (actual is null || actual.Count != expected.Count) throw new InvalidOperationException("Plaintext batch count differs.");
                    foreach (T row in expected)
                    {
                        var stored = actual.Single(value => key.All(property => Equals(property.GetValue(value), property.GetValue(row))));
                        if (fields.Any(field => !SameField(field.GetValue(row), field.GetValue(stored))))
                            throw new InvalidOperationException("Plaintext batch content differs.");
                    }
                }
                if (commitBatch is null) await ReconcileAsync(ct);
                else await commitBatch(ReconcileAsync, ct);
            }
            finally
            {
                target.ChangeTracker.Clear();
                foreach (var row in expected) ClearBytes(row, fields);
                if (actual is not null) foreach (var row in actual) ClearBytes(row, fields);
            }
            count = checked(count + rows.Count);
            cursor = rows[^1];
        }
        if (await target.Set<T>().LongCountAsync(ct) != count) throw new InvalidOperationException("Plaintext table count differs.");
        return count;
    }

    private static bool SameField(object? expected, object? actual) => expected is byte[] bytes
        ? actual is byte[] stored && CryptographicOperations.FixedTimeEquals(bytes, stored)
        : Equals(expected, actual);

    private static void ClearBytes<T>(T row, PropertyInfo[] fields)
    {
        foreach (var field in fields) if (field.GetValue(row) is byte[] bytes) CryptographicOperations.ZeroMemory(bytes);
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
