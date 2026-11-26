using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Legacy = Platform.Persistence.Migrations.Sqlite;

namespace Platform.Persistence.Migrations.SqlitePlaintext;

[DbContext(typeof(SqlitePlainTargetDbContext))]
[Migration(PlainContentBaseline.MigrationId)]
public sealed class InitialPlaintext : Legacy.InitialEfBaseline
{
    protected override void Up(MigrationBuilder builder) => PlainContentBaseline.Create(builder,new Legacy.InitialEfBaseline(),true);

    protected override void BuildTargetModel(ModelBuilder builder)
    {
        base.BuildTargetModel(builder);
        PlainContentBaseline.UpdateModel(builder,true);
    }
}

[DbContext(typeof(SqlitePlainTargetDbContext))]
internal sealed class PlainTargetModelSnapshot : Legacy.SqliteAkantiDbContextModelSnapshot
{
    protected override void BuildModel(ModelBuilder builder)
    {
        base.BuildModel(builder);
        PlainContentBaseline.UpdateModel(builder,true);
    }
}
