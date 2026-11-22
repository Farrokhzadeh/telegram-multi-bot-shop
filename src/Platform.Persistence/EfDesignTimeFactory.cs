using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Platform.Persistence;

public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAkantiDbContext>
{
    public SqliteAkantiDbContext CreateDbContext(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("Design-time factories accept no runtime connection arguments.");
        return new(new DbContextOptionsBuilder<SqliteAkantiDbContext>().UseSqlite("Data Source=:memory:").Options);
    }
}

public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresAkantiDbContext>
{
    public PostgresAkantiDbContext CreateDbContext(string[] args)
    {
        if (args.Length != 0) throw new ArgumentException("Design-time factories accept no runtime connection arguments.");
        return new(new DbContextOptionsBuilder<PostgresAkantiDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=shop_design_only;Username=shop_design_only;Pooling=false").Options);
    }
}
