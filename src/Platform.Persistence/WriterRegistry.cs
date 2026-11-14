using Platform.Security;

namespace Platform.Persistence;

public sealed class WriterRegistry
{
    private readonly DbSessionFactory control;
    private readonly BrandWriterRegistration[] registrations;
    private readonly Dictionary<Guid, DbSessionFactory> brands;
    public WriterRegistry(DbSessionFactory control, IEnumerable<BrandWriterRegistration> registrations)
    {
        if (control.IsWriterScoped) throw new ArgumentException("A control factory is required.");
        this.control = control;
        this.registrations = registrations.ToArray();
        Shared = control.ForSharedWriters(this.registrations);
        brands = this.registrations.ToDictionary(v => v.Context.BrandId, v => control.ForBrandWriter(v.Context, v.Revision));
    }
    public DbSessionFactory Shared { get; }
    public BrandWriterStore Controls => new(control);
    public BrandContext? Resolve(Guid brand) => registrations.SingleOrDefault(v => v.Context.BrandId == brand)?.Context;
    public DbSessionFactory ForBrand(BrandContext context)
    {
        if (!brands.TryGetValue(context.BrandId, out var factory) || factory.ExpectedWriterRevision(context) is null) throw new UnauthorizedAccessException();
        return factory;
    }
}
