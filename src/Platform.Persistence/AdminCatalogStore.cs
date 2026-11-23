using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed record BrandPolicyEdit(string PublicName, string Language, bool Active, bool Wallet, bool Crypto, bool Bank, bool Topup, bool ZeroBalanceTopup,
    bool Support, Guid? ContactAdminId, bool Fx, string? Logo);
public sealed record MasterProductEdit(string Title, string? Description, string? Icon, string? Guidance, string Fulfillment, string Availability, bool LocalFirst, bool Active);
public sealed record ListingEdit(bool Visible, string TitleMode, string? Title, string DescriptionMode, string? Description, string IconMode, string? Icon,
    string GuidanceMode, string? Guidance, string PriceUsd, string? PriceToman, bool AutomaticToman, bool Wallet, bool Crypto, bool Bank, int Minimum, int Maximum);
public sealed record InventoryImportItem(Guid Id, string Credential)
{
    public override string ToString() => "InventoryImportItem [redacted]";
}
public sealed record AdminBrand(Guid Id, string Name, string Market, string Language, bool Active, long Revision);
public sealed record BrandPolicyDetail(BrandPolicyEdit Value, bool RequireChannels, RequiredChannel[] Channels, long Revision);
public sealed record ExchangeRateMetadata(string? TomanPerUsd, string? Source, DateTime? ObservedAt, string? Error);
public sealed record AdminProduct(Guid Id, string Title, string Fulfillment, string Availability, bool LocalFirst, bool Active, long Revision);
public sealed record InventoryMetadata(Guid Id, Guid ProductId, string State, string Origin, DateTime CreatedAt);
public sealed class AdminCatalogStore(DbSessionFactory sessions, SecretSnapshot secrets)
{
    public async Task<AdminPage<AdminBrand>> BrandsAsync(AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.RunEfStepAsync(false, (data, token) => BrandAdministrationQueries.ListAsync(data, actor, after, limit, token), ct);
    }
    public async Task<BrandPolicyDetail> PolicyAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.RunEfStepAsync(false, (data, token) => BrandAdministrationQueries.ReadAsync(data, actor, context, token), ct);
    }

    public async Task<ExchangeRateMetadata> FxAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.RunEfStepAsync(false, (data, token) => CatalogAdministration.FxAsync(data, context, token), ct);
    }
    public async Task<AdminPage<AdminProduct>> ProductsAsync(AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.ListAdminProductsAsync(after, limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<AdminPage<InventoryMetadata>> InventoryAsync(AuthenticatedAdmin actor, Guid product, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.RunEfStepAsync(false, (data, token) => CatalogAdministration.InventoryAsync(data, product, after, limit, token), ct);
    }
    public async Task<long> PolicyAsync(AuthenticatedAdmin actor, BrandContext context, BrandPolicyEdit value, long revision, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(revision);
        Label(value.PublicName, 160); Label(value.Language, 160);
        byte[] hash = AdminCommand.Hash("brand-policy-v1", value.PublicName, value.Language, value.Active.ToString(), value.Wallet.ToString(), value.Crypto.ToString(), value.Bank.ToString(), value.Topup.ToString(), value.ZeroBalanceTopup.ToString(), value.Support.ToString(), value.ContactAdminId?.ToString("D"), value.Fx.ToString(), value.Logo);
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Update, AuditEntity.Brand, context.BrandId, context.BrandId, AuditReason.ConfigurationChange, command, hash, revision, revision + 1), ct)) return revision + 1;
            await session.AdmitWriterAsync(context, ct);
            await session.RunEfStepAsync(true, (data, token) => BrandAdministrationQueries.WriteAsync(data, actor, context, value, revision, token), ct);
            await session.CommitAsync(ct);
            return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<long> ProductAsync(AuthenticatedAdmin actor, Guid product, MasterProductEdit value, long revision, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(revision); Label(value.Title, 160);
        if (value.Fulfillment is not ("local" or "manual" or "supplier") || value.Availability is not ("immediate" or "local_preorder") || (value.Availability == "local_preorder" && value.Fulfillment != "local")) throw new ArgumentException("Invalid fulfillment policy.");
        byte[] hash = AdminCommand.Hash("master-product-v1", value.Title, value.Description, value.Icon, value.Guidance, value.Fulfillment, value.Availability, value.LocalFirst.ToString(), value.Active.ToString());
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Update, AuditEntity.Product, product, null, AuditReason.ConfigurationChange, command, hash, revision, revision + 1), ct)) return revision + 1;
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)", DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(product.ToByteArray(bigEndian: true)))), ct);
            await session.RunEfStepAsync(false, (data, token) => data.LockProductAsync(product, token), ct);
            await session.AdmitSharedWriterAsync(ct);
            await session.RunEfStepAsync(true, (data, token) => CatalogAdministration.ProductAsync(data, actor, product, value, revision, token), ct);
            await session.CommitAsync(ct); return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task<long> ListingAsync(AuthenticatedAdmin actor, BrandContext context, Guid product, ListingEdit value, long revision, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(revision);
        Mode(value.TitleMode, value.Title, false); Mode(value.DescriptionMode, value.Description, true); Mode(value.IconMode, value.Icon, true); Mode(value.GuidanceMode, value.Guidance, true);
        Money usd = new(ExactDecimal.Parse(value.PriceUsd), Currency.Usd);
        Money? toman = value.PriceToman is null ? null : new(ExactDecimal.Parse(value.PriceToman), Currency.Toman);
        if (usd.Amount <= 0 || toman?.Amount <= 0 || value.Minimum < 1 || value.Maximum < value.Minimum || value.Maximum > 10000) throw new ArgumentException("Invalid listing prices or quantities.");
        byte[] hash = AdminCommand.Hash("listing-v1", value.Visible.ToString(), value.TitleMode, value.Title, value.DescriptionMode, value.Description, value.IconMode, value.Icon, value.GuidanceMode, value.Guidance, ExactDecimal.Format(usd.Amount), toman is Money amount ? ExactDecimal.Format(amount.Amount) : null, value.AutomaticToman.ToString(), value.Wallet.ToString(), value.Crypto.ToString(), value.Bank.ToString(), value.Minimum.ToString(CultureInfo.InvariantCulture), value.Maximum.ToString(CultureInfo.InvariantCulture));
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.Update, AuditEntity.BrandProduct, product, context.BrandId, AuditReason.ConfigurationChange, command, hash, revision, revision + 1), ct)) return revision + 1;
            await session.RunEfStepAsync(false, (data, token) => data.LockProductAsync(product, token), ct);
            await session.AdmitWriterAsync(context, ct);
            await session.RunEfStepAsync(false, (data, token) => data.LockBrandAsync(context.BrandId, token), ct);
            await session.RunEfStepAsync(true, (data, token) => CatalogAdministration.ListingAsync(data, actor, context, product, value, revision, token), ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), command, DateTime.UtcNow, ct);
            await session.CommitAsync(ct); return revision + 1;
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    public async Task ImportAsync(AuthenticatedAdmin actor, Guid product, InventoryImportItem[] items, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length is < 1 or > 1000 || items.Any(i => i is null || i.Id == Guid.Empty || string.IsNullOrWhiteSpace(i.Credential) || Encoding.UTF8.GetByteCount(i.Credential) > 65536) || items.Select(i => i.Id).Distinct().Count() != items.Length) throw new ArgumentException("Invalid inventory import.");
        byte[] hash = AdminCommand.Hash("inventory-import-v1", items.OrderBy(i => i.Id).SelectMany(i => new[] { i.Id.ToString("D"), CredentialCommitment(i.Credential) }).ToArray());
        await using var session = await sessions.BeginAsync(ct);
        try
        {
            if (!await AdminAuditStore.AppendAsync(session, actor, new(command, AuditAction.StockImport, AuditEntity.Product, product, null, AuditReason.OperatorReview, command, hash), ct)) return;
            await session.RunEfStepAsync(false, (data, token) => data.LockProductAsync(product, token), ct);
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock(739285126)"), ct);
            await session.AdmitSharedWriterAsync(ct);
            await session.RunEfStepAsync(true, async (data, token) => { await CatalogAdministration.ImportAsync(data, actor, product, items, secrets, token); return true; }, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.DrainLocalPreorders, product), command, DateTime.UtcNow, ct);
            await OutboxStore.EnqueueAsync(session, JobIntent.Product(JobKind.EvaluateRestock, product), command, DateTime.UtcNow, ct);
            await session.CommitAsync(ct);
        }
        catch { if (session.State == DbSessionState.Active) session.RejectApplication(); throw; }
    }
    private static string CredentialCommitment(string credential)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(credential);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static void Label(string value, int maximum) { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Invalid display value."); }
    private static void Mode(string mode, string? value, bool clear) { if (mode is not ("inherit" or "override") && !(clear && mode == "clear") || ((mode == "override") != (value is not null))) throw new ArgumentException("Invalid field override."); }
}
