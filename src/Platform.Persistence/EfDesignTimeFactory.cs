using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Platform.Persistence;

public sealed class SqlitePlainDesignTimeFactory : IDesignTimeDbContextFactory<SqlitePlainTargetDbContext>
{
    public SqlitePlainTargetDbContext CreateDbContext(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("Design-time factories accept no runtime connection arguments.");
        return new(new DbContextOptionsBuilder<SqlitePlainTargetDbContext>().UseSqlite("Data Source=:memory:").Options);
    }
}

public sealed class PostgresPlainDesignTimeFactory : IDesignTimeDbContextFactory<PostgresPlainTargetDbContext>
{
    public PostgresPlainTargetDbContext CreateDbContext(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("Design-time factories accept no runtime connection arguments.");
        return new(new DbContextOptionsBuilder<PostgresPlainTargetDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=shop_design_only;Username=shop_design_only;Pooling=false").Options);
    }
}
