using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Platform.Persistence.Migrations;

public sealed class AkantiSqliteMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, IRelationalAnnotationProvider annotations)
    : SqliteMigrationsSqlGenerator(dependencies, annotations)
{
    protected override void Generate(CreateTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        var entity = FindEntity(operation.Name, model);
        if (entity is not null)
        {
            foreach (var annotation in entity.GetAnnotations().Where(value => value.Name.StartsWith("Akanti:DatabaseForeignKey:", StringComparison.Ordinal)))
            {
                string name = annotation.Name["Akanti:DatabaseForeignKey:".Length..];
                if (operation.ForeignKeys.Any(value => value.Name == name)) continue;
                using var descriptor = JsonDocument.Parse((string)annotation.Value!);
                var root = descriptor.RootElement;
                if (root.GetProperty("onDelete").GetString() != "RESTRICT") throw new InvalidOperationException("Unsupported database foreign key action.");
                var foreign = new AddForeignKeyOperation
                {
                    Name = name,
                    Table = operation.Name,
                    Columns = root.GetProperty("columns").EnumerateArray().Select(value => value.GetString()!.Trim()).ToArray(),
                    PrincipalTable = root.GetProperty("principalTable").GetString()!,
                    PrincipalColumns = root.GetProperty("principalColumns").EnumerateArray().Select(value => value.GetString()!.Trim()).ToArray(),
                    OnDelete = ReferentialAction.Restrict
                };
                foreign["Akanti:Deferrability"] = root.GetProperty("deferrability").GetString();
                operation.ForeignKeys.Add(foreign);
            }
        }
        base.Generate(operation, model, builder, false);
        if (entity is not null) builder.Append(" STRICT");
        if (terminate) builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator).EndCommand();
    }

    protected override void ForeignKeyConstraint(AddForeignKeyOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        base.ForeignKeyConstraint(operation, model, builder);
        string? deferrability = operation["Akanti:Deferrability"] as string;
        if (deferrability is null && FindEntity(operation.Table, model) is { } entity)
        {
            deferrability = entity.GetForeignKeys().FirstOrDefault(value => value.GetConstraintName() == operation.Name)?["Akanti:Deferrability"] as string;
            if (deferrability is null) throw new InvalidOperationException("A business foreign key requires explicit deferrability.");
        }
        if (deferrability is null) return;
        if (deferrability is not ("DEFERRABLE INITIALLY IMMEDIATE" or "DEFERRABLE INITIALLY DEFERRED"))
            throw new InvalidOperationException("Unsupported foreign key deferrability.");
        builder.Append(" ").Append(deferrability);
    }

    private static IEntityType? FindEntity(string table, IModel? model)
    {
        string name = table.StartsWith("ef_temp_", StringComparison.Ordinal) ? table["ef_temp_".Length..] : table;
        return model?.GetEntityTypes().SingleOrDefault(entity => entity.GetTableName() == name);
    }
}
