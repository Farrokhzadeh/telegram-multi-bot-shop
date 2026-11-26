using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Legacy = Platform.Persistence.Migrations.PostgreSql;

namespace Platform.Persistence.Migrations.PostgreSqlPlaintext;

[DbContext(typeof(PostgresPlainTargetDbContext))]
[Migration(PlainContentBaseline.MigrationId)]
public sealed class InitialPlaintext : Legacy.InitialEfBaseline
{
    protected override void Up(MigrationBuilder builder) => PlainContentBaseline.Create(builder,new Legacy.InitialEfBaseline(),false);

    protected override void BuildTargetModel(ModelBuilder builder)
    {
        base.BuildTargetModel(builder);
        PlainContentBaseline.UpdateModel(builder,false);
    }
}

[DbContext(typeof(PostgresPlainTargetDbContext))]
internal sealed class PlainTargetModelSnapshot : Legacy.PostgresAkantiDbContextModelSnapshot
{
    protected override void BuildModel(ModelBuilder builder)
    {
        base.BuildModel(builder);
        PlainContentBaseline.UpdateModel(builder,false);
    }
}
