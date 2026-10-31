using Npgsql;
using NpgsqlTypes;
using Platform.Core;

namespace Platform.Persistence;

public readonly struct DbValue
{
    private readonly NpgsqlDbType type;
    private readonly object? value;
    private DbValue(NpgsqlDbType type, object? value) { this.type = type; this.value = value; }
    public static DbValue Uuid(Guid value) => new(NpgsqlDbType.Uuid, value);
    public static DbValue Integer(int value) => new(NpgsqlDbType.Integer, value);
    public static DbValue Bigint(long value) => new(NpgsqlDbType.Bigint, value);
    public static DbValue Boolean(bool value) => new(NpgsqlDbType.Boolean, value);
    public static DbValue Text(string value) => new(NpgsqlDbType.Text, value ?? throw new ArgumentNullException(nameof(value)));
    public static DbValue Numeric(decimal value) => new(NpgsqlDbType.Numeric, value);
    public static DbValue Binary(ReadOnlySpan<byte> value) => new(NpgsqlDbType.Bytea, value.ToArray());
    public static DbValue Currency(Currency currency) => Text(currency.ToCode());

    public static DbValue Money(Money value, Currency expectedCurrency)
    {
        if (value.Currency != expectedCurrency) throw new ArgumentException("Money parameter currency mismatch.");
        return Numeric(value.Amount);
    }

    public static DbValue Utc(DateTime value) => value.Kind == DateTimeKind.Utc && value != DateTime.MinValue && value != DateTime.MaxValue
        ? new(NpgsqlDbType.TimestampTz, value)
        : throw new ArgumentException("Database timestamps must be UTC.");

    public static DbValue Null(NpgsqlDbType type)
    {
        RequireType(type);
        return new(type, null);
    }

    public static Money ReadMoney(DbRow row, int ordinal, Currency currency) =>
        Platform.Core.Money.Parse(row.GetString(ordinal), currency); // Project numeric::text; never use a rounding conversion.

    internal NpgsqlParameter ToParameter()
    {
        RequireType(type); // Reject default(DbValue), arrays and unregistered object/JSON mappings.
        return new NpgsqlParameter { NpgsqlDbType = type, Value = value ?? DBNull.Value };
    }

    private static void RequireType(NpgsqlDbType type)
    {
        if (type is not (NpgsqlDbType.Uuid or NpgsqlDbType.Integer or NpgsqlDbType.Bigint or NpgsqlDbType.Boolean or
            NpgsqlDbType.Text or NpgsqlDbType.Numeric or NpgsqlDbType.Bytea or NpgsqlDbType.TimestampTz))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}

// A projection can read typed fields, but cannot obtain the owning connection/transaction.
// Escaped row handles become unusable as soon as the synchronous projection returns.
public sealed class DbRow
{
    private readonly NpgsqlDataReader reader;
    private bool valid = true;
    internal DbRow(NpgsqlDataReader reader) => this.reader = reader;
    internal void Invalidate() => valid = false;
    private void RequireActive() { if (!valid) throw new InvalidOperationException("Database row projection is complete."); }
    public bool IsDBNull(int ordinal) { RequireActive(); return reader.IsDBNull(ordinal); }
    public string GetString(int ordinal) { RequireActive(); return reader.GetString(ordinal); }
    public int GetInt32(int ordinal) { RequireActive(); return reader.GetInt32(ordinal); }
    public long GetInt64(int ordinal) { RequireActive(); return reader.GetInt64(ordinal); }
    public bool GetBoolean(int ordinal) { RequireActive(); return reader.GetBoolean(ordinal); }
    public Guid GetGuid(int ordinal) { RequireActive(); return reader.GetGuid(ordinal); }
    public byte[] GetBinary(int ordinal) { RequireActive(); return reader.GetFieldValue<byte[]>(ordinal); }
    public DateTime GetUtc(int ordinal)
    {
        RequireActive();
        DateTime value = reader.GetDateTime(ordinal);
        _ = DbValue.Utc(value);
        return value;
    }
}

public sealed class DbStatement
{
    internal string Text { get; }
    private readonly DbValue[] values;

    // SQL is a repository-authored constant. Every variable value uses an explicit positional parameter.
    // This deliberately small surface excludes scripts, comments, dollar quoting and transaction control.
    public DbStatement(string sql, params DbValue[] values)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(values);
        string start = sql.TrimStart();
        int wordEnd = 0;
        while (wordEnd < start.Length && char.IsAsciiLetter(start[wordEnd])) wordEnd++;
        string first = start[..wordEnd].ToUpperInvariant();
        if (first is not ("SELECT" or "INSERT" or "UPDATE" or "DELETE" or "WITH"))
        {
            throw new ArgumentException("Only one data statement is allowed.");
        }

        var seen = new HashSet<int>();
        char quote = '\0';
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (quote != '\0')
            {
                if (c == '\\') throw new ArgumentException("Escaped SQL literals are not supported; bind the value.");
                if (c == quote)
                {
                    if (i + 1 < sql.Length && sql[i + 1] == quote) i++;
                    else quote = '\0';
                }
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == ';' || (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') ||
                (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*'))
            {
                throw new ArgumentException("SQL scripts and comments are not supported.");
            }
            if (c == '$')
            {
                int end = i + 1;
                while (end < sql.Length && char.IsAsciiDigit(sql[end])) end++;
                if (end == i + 1 || !int.TryParse(sql.AsSpan(i + 1, end - i - 1), out int ordinal) || ordinal < 1 || ordinal > values.Length)
                {
                    throw new ArgumentException("Invalid positional SQL parameter.");
                }
                seen.Add(ordinal); i = end - 1;
            }
        }
        if (quote != '\0' || seen.Count != values.Length) throw new ArgumentException("SQL quote or parameter mismatch.");
        this.values = values.ToArray();
        foreach (DbValue value in this.values) _ = value.ToParameter();
        Text = sql;
    }

    internal NpgsqlCommand CreateCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, int timeoutSeconds)
    {
        var command = new NpgsqlCommand(Text, connection, transaction) { CommandTimeout = timeoutSeconds };
        foreach (DbValue value in values) command.Parameters.Add(value.ToParameter());
        return command;
    }
}
