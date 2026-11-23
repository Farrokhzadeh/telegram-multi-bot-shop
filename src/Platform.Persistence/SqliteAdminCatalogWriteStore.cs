using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Platform.Core;
using Platform.Security;

namespace Platform.Persistence;

public sealed class SqliteAdminCatalogWriteStore(SqliteSessionFactory sessions)
{
    public Task<long> PolicyAsync(AuthenticatedAdmin actor, BrandContext context,
        BrandPolicyEdit value, long revision, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(revision);
        Label(value.PublicName, 160);
        Label(value.Language, 160);
        byte[] hash = AdminCommand.Hash("brand-policy-v1", value.PublicName, value.Language,
            value.Active.ToString(), value.Wallet.ToString(), value.Crypto.ToString(),
            value.Bank.ToString(), value.Topup.ToString(), value.ZeroBalanceTopup.ToString(),
            value.Support.ToString(), value.ContactAdminId?.ToString("D"),
            value.Fx.ToString(), value.Logo);
        var audit = new AuditCommand(command, AuditAction.Update, AuditEntity.Brand,
            context.BrandId, context.BrandId, AuditReason.ConfigurationChange, command,
            hash, revision, checked(revision + 1));
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            await AdminSessionStore.RequireActiveAsync(data, actor, null, token);
            if (!await data.Data.Set<Entities.Brand>().AnyAsync(brand => brand.Id == context.BrandId && brand.BotId == context.BotId, token))
                throw new UnauthorizedAccessException("The brand policy scope is unavailable.");
            if (!await AdminAuditStore.AppendEfAsync(data, actor, audit, token)) return checked(revision + 1);
            return await BrandAdministrationQueries.WriteAsync(data, actor, context, value, revision, token);
        }, ct);
    }

    public Task<long> ProductAsync(AuthenticatedAdmin actor, Guid product, MasterProductEdit value,
        long revision, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(revision);
        Label(value.Title, 160);
        if (product == Guid.Empty || value.Fulfillment is not ("local" or "manual" or "supplier") ||
            value.Availability is not ("immediate" or "local_preorder") ||
            value.Availability == "local_preorder" && value.Fulfillment != "local")
            throw new ArgumentException("Invalid product fulfillment policy.");
        byte[] hash = AdminCommand.Hash("master-product-v1", value.Title, value.Description,
            value.Icon, value.Guidance, value.Fulfillment, value.Availability,
            value.LocalFirst.ToString(), value.Active.ToString());
        var audit = new AuditCommand(command, AuditAction.Update, AuditEntity.Product, product,
            null, AuditReason.ConfigurationChange, command, hash, revision, checked(revision + 1));
        return sessions.RunEfAsync(true, null, async (data, token) =>
        {
            if (!await AdminAuditStore.AppendEfAsync(data, actor, audit, token)) return checked(revision + 1);
            return await CatalogAdministration.ProductAsync(data, actor, product, value, revision, token);
        }, ct);
    }

    public Task<long> ListingAsync(AuthenticatedAdmin actor, BrandContext context, Guid product,
        ListingEdit value, long revision, Guid command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        AdminCommand.Revision(revision);
        Mode(value.TitleMode, value.Title, false);
        Mode(value.DescriptionMode, value.Description, true);
        Mode(value.IconMode, value.Icon, true);
        Mode(value.GuidanceMode, value.Guidance, true);
        Money usd = new(ExactDecimal.Parse(value.PriceUsd), Currency.Usd);
        Money? toman = value.PriceToman is null ? null :
            new(ExactDecimal.Parse(value.PriceToman), Currency.Toman);
        if (product == Guid.Empty || usd.Amount <= 0 || toman?.Amount <= 0 ||
            value.Minimum < 1 || value.Maximum < value.Minimum || value.Maximum > 10000)
            throw new ArgumentException("Invalid listing prices or quantities.");
        byte[] hash = AdminCommand.Hash("listing-v1", value.Visible.ToString(), value.TitleMode,
            value.Title, value.DescriptionMode, value.Description, value.IconMode, value.Icon,
            value.GuidanceMode, value.Guidance, ExactDecimal.Format(usd.Amount),
            toman is Money local ? ExactDecimal.Format(local.Amount) : null,
            value.AutomaticToman.ToString(), value.Wallet.ToString(), value.Crypto.ToString(),
            value.Bank.ToString(), value.Minimum.ToString(CultureInfo.InvariantCulture),
            value.Maximum.ToString(CultureInfo.InvariantCulture));
        var audit = new AuditCommand(command, AuditAction.Update, AuditEntity.BrandProduct, product,
            context.BrandId, AuditReason.ConfigurationChange, command, hash,
            revision, checked(revision + 1));
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            bool changed = await SqliteSessionFactory.StepEfAsync(tx, true, async (data, cancel) =>
            {
                await AdminSessionStore.RequireActiveAsync(data, actor, null, cancel);
                if (!await data.Data.Set<Entities.Product>().AnyAsync(row => row.Id == product, cancel) ||
                    !await data.Data.Set<Entities.Brand>().AnyAsync(row => row.Id == context.BrandId && row.BotId == context.BotId && row.Active, cancel))
                    data.RejectScope("The listing scope is unavailable.");
                if (!await AdminAuditStore.AppendEfAsync(data, actor, audit, cancel)) return false;
                await CatalogAdministration.ListingAsync(data, actor, context, product, value, revision, cancel);
                return true;
            }, token);
            if (!changed) return checked(revision + 1);
            await OutboxStore.EnqueueSqliteAsync(tx,
                JobIntent.Product(JobKind.EvaluateRestock, product),
                command, DateTime.UtcNow, token);
            return checked(revision + 1);
        }, ct);
    }

    private static void Label(string value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(char.IsControl)) throw new ArgumentException("Invalid display value.");
    }

    private static void Mode(string mode, string? value, bool clear)
    {
        if (mode is not ("inherit" or "override") && !(clear && mode == "clear") ||
            (mode == "override") != (value is not null))
            throw new ArgumentException("Invalid field override.");
    }
}
