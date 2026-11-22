using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Core;

namespace Platform.Persistence.Configurations;

internal sealed class ExactSqliteDecimalMapping : DecimalTypeMapping
{
    internal static ExactSqliteDecimalMapping Instance { get; } = new();
    private ExactSqliteDecimalMapping() : base("TEXT", System.Data.DbType.String) { }
    private ExactSqliteDecimalMapping(RelationalTypeMappingParameters parameters) : base(parameters) { }
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) => new ExactSqliteDecimalMapping(parameters);
    protected override void ConfigureParameter(DbParameter parameter)
    {
        parameter.DbType = System.Data.DbType.String;
        if (parameter.Value is decimal value) parameter.Value = ExactDecimal.Format(value);
    }
    protected override string GenerateNonNullSqlLiteral(object value) => "'" + ExactDecimal.Format(value switch { decimal number => number, int number => number, long number => number, short number => number, byte number => number, _ => throw new InvalidOperationException("Unsupported exact decimal literal.") }) + "'";
}
