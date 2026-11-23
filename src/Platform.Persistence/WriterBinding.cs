using Platform.Security;

namespace Platform.Persistence;

public sealed record BrandWriterRegistration(BrandContext Context, long Revision);
internal sealed class WriterBinding
{
    internal WriterBinding(IEnumerable<BrandWriterRegistration> registrations, bool shared)
    {
        Entries = registrations.ToArray();
        if (Entries.Length is < 1 or > 1000 || Entries.Any(v => v is null || v.Context is null || v.Revision < 0) || Entries.Select(v => v.Context.BrandId).Distinct().Count() != Entries.Length ||
            Entries.Select(v => v.Context.BotId).Distinct().Count() != Entries.Length || Entries.Select(v => v.Context.DeploymentId).Distinct().Count() != 1 || (!shared && Entries.Length != 1)) throw new ArgumentException("Invalid writer registrations.");
        Shared = shared;
    }
    internal BrandWriterRegistration[] Entries { get; }
    internal bool Shared { get; }
    internal BrandWriterRegistration? Find(BrandContext context) => Entries.SingleOrDefault(v => v.Context.BrandId == context.BrandId && v.Context.BotId == context.BotId && v.Context.DeploymentId == context.DeploymentId && v.Context.ConfigurationRevision == context.ConfigurationRevision);
}

public sealed partial class DbSession
{
    private WriterBinding? writer;
    private bool writerAdmitted;
    public bool IsWriterScoped => writer is not null;
    public bool IsSharedWriterScoped => writer?.Shared == true;
    internal Guid? WriterBrand
    {
        get
        {
            if (writer is null) return null;
            if (writer.Shared) DenyWriter();
            return writer.Entries[0].Context.BrandId;
        }
    }
    internal void BindWriter(WriterBinding? binding) => writer = binding;
    internal void ValidateWriter(BrandContext context, long revision)
    {
        if (writer is null) return;
        if (context is null || writer.Find(context)?.Revision != revision) DenyWriter();
    }
    internal void MarkWriter(BrandContext context, long revision)
    {
        ValidateWriter(context, revision);
        if (writer is { Shared: false }) writerAdmitted = true;
    }
    public async Task AdmitWriterAsync(BrandContext context, CancellationToken ct = default)
    {
        if (writer is null) return;
        if (writer.Find(context) is null || (writer.Shared && !writerAdmitted)) DenyWriter();
        if (writerAdmitted) return;
        await BrandWriterStore.RequireAsync(this, context, writer.Entries[0].Revision, ct);
    }
    public async Task AdmitCustomerWriterAsync(BrandContext context, long actor, Guid? product = null, CancellationToken ct = default)
    {
        if (writer is null) return;
        if (writer.Shared || writer.Find(context) is null) DenyWriter();
        await BrandWriterStore.RequireCustomerAsync(this, context, actor, writer.Entries[0].Revision, product, ct);
    }
    public Task AdmitBrandWriterAsync(Guid brand, CancellationToken ct = default)
    {
        if (writer is null) return Task.CompletedTask;
        var registration = writer.Entries.SingleOrDefault(v => v.Context.BrandId == brand);
        if (registration is null) DenyWriter();
        return AdmitWriterAsync(registration.Context, ct);
    }
    public async Task<Guid?> AdmitClaimWriterAsync(bool brandOnly, CancellationToken ct = default)
    {
        if (writer is null) return null;
        if (writer.Shared)
        {
            if (brandOnly) DenyWriter();
            await AdmitSharedWriterAsync(ct);
            return null;
        }
        var registration = writer.Entries[0];
        await AdmitWriterAsync(registration.Context, ct);
        return registration.Context.BrandId;
    }
    internal async Task AdmitLeaseWriterAsync(JobLease lease, CancellationToken ct)
    {
        if (writer is null) return;
        if (lease.Intent.BrandId is Guid brand) await AdmitBrandWriterAsync(brand, ct);
        else await AdmitSharedWriterAsync(ct);
    }
    public async Task AdmitSharedWriterAsync(CancellationToken ct = default)
    {
        if (writer is null) return;
        if (!writer.Shared) DenyWriter();
        if (writerAdmitted) return;
        await RunEfStepAsync(false, async (data, token) => { await data.RequireSharedWritersAsync(token, true); return true; }, ct);
    }
    public void RequireOfflineImport() { if (writer is not null) DenyWriter(); }
    private void RequireWriterCommit()
    {
        if (writer is not null && !writerAdmitted && State == DbSessionState.Active) DenyWriter();
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void DenyWriter() { RejectApplication(); throw new UnauthorizedAccessException("Writer scope or admission is unavailable."); }
}
