using Microsoft.EntityFrameworkCore;

namespace Platform.Persistence;

public sealed partial class DbSession
{
    internal async Task<List<AdminProduct>> ListAdminProductsAsync(Guid? after, int count, CancellationToken ct)
    {
        EnterActive();
        try
        {
            ct.ThrowIfCancellationRequested();
            var config = new DbContextOptionsBuilder<AdminProductQuery>()
                .UseNpgsql(connection, provider => provider.CommandTimeout(options.ClientTimeoutSeconds))
                .Options;
            await using var catalog = new AdminProductQuery(config);
            await catalog.Database.UseTransactionAsync(transaction, ct);
            return await catalog.ListAsync(after, count, ct);
        }
        catch (Exception error) { throw RecordFailure(error, DbPhase.Executing, ct); }
        finally { Exit(); }
    }
}

internal sealed class AdminProductQuery(DbContextOptions<AdminProductQuery> options) : DbContext(options)
{
    internal Task<List<AdminProduct>> ListAsync(Guid? after, int count, CancellationToken ct)
    {
        var products = Set<AdminProduct>().AsNoTracking();
        if (after is { } cursor) products = products.Where(product => product.Id.CompareTo(cursor) > 0);
        return products.OrderBy(product => product.Id).Take(count).ToListAsync(ct);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var product = modelBuilder.Entity<AdminProduct>();
        product.ToTable("products", Database.IsSqlite() ? null : "shop");
        product.HasKey(p => p.Id);
        product.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        product.Property(p => p.Title).HasColumnName("title");
        product.Property(p => p.Fulfillment).HasColumnName("fulfillment_kind");
        product.Property(p => p.Availability).HasColumnName("availability_policy");
        product.Property(p => p.LocalFirst).HasColumnName("local_first");
        product.Property(p => p.Active).HasColumnName("active");
        product.Property(p => p.Revision).HasColumnName("revision");
    }
}
