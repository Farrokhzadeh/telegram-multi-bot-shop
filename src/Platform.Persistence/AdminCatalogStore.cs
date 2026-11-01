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
        var rows = await session.QueryManyAsync(new DbStatement("SELECT id,public_name,market,default_language,active,policy_version FROM shop.brands WHERE ($1 IS NULL OR id>$1) ORDER BY id LIMIT $2", AdminCommand.Id(after), DbValue.Integer(limit + 1)),
            r => new AdminBrand(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetInt64(5)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<BrandPolicyDetail> PolicyAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var row = await session.QuerySingleAsync(new DbStatement("""
            SELECT public_name,default_language,active,allow_wallet,allow_crypto,allow_bank,allow_wallet_topup,zero_balance_topup,support_enabled,customer_support_admin_id,fx_enabled,logo_asset_ref,require_channel_join,policy_version
            FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR SHARE
            """, DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => (Value: new BrandPolicyEdit(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetGuid(9), r.GetBoolean(10), r.IsDBNull(11) ? null : r.GetString(11)), Enforce: r.GetBoolean(12), Revision: r.GetInt64(13)), ct);
        var channels = await session.QueryManyAsync(new DbStatement("SELECT chat_id,title,invite_link FROM shop.brand_channels WHERE brand_id=$1 AND active AND required ORDER BY chat_id", DbValue.Uuid(context.BrandId)), r => new RequiredChannel(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), 1000, ct);
        return new(row.Value, row.Enforce, channels.ToArray(), row.Revision);
    }
    public async Task<ExchangeRateMetadata> FxAsync(AuthenticatedAdmin actor, BrandContext context, CancellationToken ct = default)
    {
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        return await session.QuerySingleAsync(new DbStatement("SELECT current_fx::text,fx_source,fx_fetched_at,fx_error_code FROM shop.brands WHERE id=$1 AND bot_id=$2", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)),
            r => new ExchangeRateMetadata(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetUtc(2), r.IsDBNull(3) ? null : r.GetString(3)), ct);
    }
    public async Task<AdminPage<AdminProduct>> ProductsAsync(AuthenticatedAdmin actor, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("SELECT id,title,fulfillment_kind,availability_policy,local_first,active,revision FROM shop.products WHERE ($1 IS NULL OR id>$1) ORDER BY id LIMIT $2",
            AdminCommand.Id(after), DbValue.Integer(limit + 1)), r => new AdminProduct(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), r.GetInt64(6)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
    }
    public async Task<AdminPage<InventoryMetadata>> InventoryAsync(AuthenticatedAdmin actor, Guid product, Guid? after, int limit, CancellationToken ct = default)
    {
        AdminCommand.Page(limit);
        await using var session = await sessions.BeginAsync(ct);
        await AdminSessionStore.RequireActiveAsync(session, actor, cancellationToken: ct);
        var rows = await session.QueryManyAsync(new DbStatement("SELECT id,product_id,state,origin_kind,created_at FROM shop.inventory_items WHERE product_id=$1 AND ($2 IS NULL OR id>$2) ORDER BY id LIMIT $3",
            DbValue.Uuid(product), AdminCommand.Id(after), DbValue.Integer(limit + 1)), r => new InventoryMetadata(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetUtc(4)), limit + 1, ct);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null);
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
            if (value.Support)
            {
                bool valid = await session.QuerySingleAsync(new DbStatement("SELECT EXISTS(SELECT 1 FROM shop.admins WHERE id=$1 AND disabled_at IS NULL AND telegram_verified_at IS NOT NULL AND public_contact_handle IS NOT NULL)", AdminCommand.Id(value.ContactAdminId)), r => r.GetBoolean(0), ct);
                if (!valid) throw new ArgumentException("A current verified public support contact is required.");
            }
            int changed = await session.ExecuteAsync(new DbStatement("""
                UPDATE shop.brands SET public_name=$1,default_language=$2,active=$3,allow_wallet=$4,allow_crypto=$5,allow_bank=$6,allow_wallet_topup=$7,
                    zero_balance_topup=$8,support_enabled=$9,customer_support_admin_id=$10,fx_enabled=$11,logo_asset_ref=$12,policy_version=policy_version+1,updated_at=clock_timestamp()
                WHERE id=$13 AND bot_id=$14 AND policy_version=$15
                """, DbValue.Text(value.PublicName), DbValue.Text(value.Language), DbValue.Boolean(value.Active), DbValue.Boolean(value.Wallet), DbValue.Boolean(value.Crypto), DbValue.Boolean(value.Bank), DbValue.Boolean(value.Topup),
                DbValue.Boolean(value.ZeroBalanceTopup), DbValue.Boolean(value.Support), AdminCommand.Id(value.ContactAdminId), DbValue.Boolean(value.Fx), AdminCommand.Text(value.Logo), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId), DbValue.Bigint(revision)), ct);
            if (changed != 1) throw new InvalidOperationException("Policy revision conflict.");
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
            int changed = await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.products(id,title,description,icon_ref,guidance,fulfillment_kind,availability_policy,local_first,active,revision)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,1)
                ON CONFLICT(id) DO UPDATE SET title=EXCLUDED.title,description=EXCLUDED.description,icon_ref=EXCLUDED.icon_ref,guidance=EXCLUDED.guidance,
                    fulfillment_kind=EXCLUDED.fulfillment_kind,availability_policy=EXCLUDED.availability_policy,local_first=EXCLUDED.local_first,active=EXCLUDED.active,
                    revision=shop.products.revision+1,updated_at=clock_timestamp() WHERE shop.products.revision=$10
                """, DbValue.Uuid(product), DbValue.Text(value.Title), AdminCommand.Text(value.Description), AdminCommand.Text(value.Icon), AdminCommand.Text(value.Guidance), DbValue.Text(value.Fulfillment), DbValue.Text(value.Availability), DbValue.Boolean(value.LocalFirst), DbValue.Boolean(value.Active), DbValue.Bigint(revision)), ct);
            if (changed != 1 || (revision != 0 && await session.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.products WHERE id=$1", DbValue.Uuid(product)), r => r.GetInt64(0), ct) != revision + 1)) throw new InvalidOperationException("Product revision conflict.");
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
            await session.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => r.GetInt64(0), ct);
            await session.QuerySingleAsync(new DbStatement("SELECT policy_version FROM shop.brands WHERE id=$1 AND bot_id=$2 FOR UPDATE", DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetInt64(0), ct);
            int changed = await session.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.brand_products(brand_id,product_id,visible,title_mode,title_override,description_mode,description_override,icon_mode,icon_override,guidance_mode,guidance_override,
                    price_usdt,price_toman,auto_price_toman,allow_wallet,allow_crypto,allow_bank,min_quantity,max_quantity,revision)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,1)
                ON CONFLICT(brand_id,product_id) DO UPDATE SET visible=EXCLUDED.visible,title_mode=EXCLUDED.title_mode,title_override=EXCLUDED.title_override,
                    description_mode=EXCLUDED.description_mode,description_override=EXCLUDED.description_override,icon_mode=EXCLUDED.icon_mode,icon_override=EXCLUDED.icon_override,
                    guidance_mode=EXCLUDED.guidance_mode,guidance_override=EXCLUDED.guidance_override,price_usdt=EXCLUDED.price_usdt,price_toman=EXCLUDED.price_toman,
                    auto_price_toman=EXCLUDED.auto_price_toman,allow_wallet=EXCLUDED.allow_wallet,allow_crypto=EXCLUDED.allow_crypto,allow_bank=EXCLUDED.allow_bank,
                    min_quantity=EXCLUDED.min_quantity,max_quantity=EXCLUDED.max_quantity,revision=shop.brand_products.revision+1,updated_at=clock_timestamp()
                WHERE shop.brand_products.revision=$20
                """, DbValue.Uuid(context.BrandId), DbValue.Uuid(product), DbValue.Boolean(value.Visible), DbValue.Text(value.TitleMode), AdminCommand.Text(value.Title), DbValue.Text(value.DescriptionMode), AdminCommand.Text(value.Description), DbValue.Text(value.IconMode), AdminCommand.Text(value.Icon), DbValue.Text(value.GuidanceMode), AdminCommand.Text(value.Guidance), DbValue.Numeric(usd.Amount), toman is Money price ? DbValue.Numeric(price.Amount) : DbValue.Null(NpgsqlTypes.NpgsqlDbType.Numeric),
                DbValue.Boolean(value.AutomaticToman), DbValue.Boolean(value.Wallet), DbValue.Boolean(value.Crypto), DbValue.Boolean(value.Bank), DbValue.Integer(value.Minimum), DbValue.Integer(value.Maximum), DbValue.Bigint(revision)), ct);
            long actual = await session.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.brand_products WHERE brand_id=$1 AND product_id=$2", DbValue.Uuid(context.BrandId), DbValue.Uuid(product)), r => r.GetInt64(0), ct);
            if (changed != 1 || actual != revision + 1) throw new InvalidOperationException("Listing revision conflict.");
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
            await session.QuerySingleAsync(new DbStatement("SELECT revision FROM shop.products WHERE id=$1 FOR UPDATE", DbValue.Uuid(product)), r => r.GetInt64(0), ct);
            await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock(739285126)"), ct);
            var keys = await session.QueryManyAsync(new DbStatement("SELECT DISTINCT credential_key_id FROM shop.inventory_items ORDER BY credential_key_id"), r => r.GetString(0), 32, ct);
            string key = secrets.CurrentInventoryKeyId;
            foreach (var item in items)
            {
                byte[] plain = Encoding.UTF8.GetBytes(item.Credential);
                try
                {
                    byte[] fingerprint = secrets.UseInventoryKey(key, k => HMACSHA256.HashData(k, plain));
                    bool exists = false;
                    foreach (string previous in keys.Append(key).Distinct())
                    {
                        byte[] digest = secrets.UseInventoryKey(previous, k => HMACSHA256.HashData(k, plain));
                        var duplicate = await session.QuerySingleAsync(new DbStatement("SELECT id,product_id FROM (SELECT 1) seed LEFT JOIN shop.inventory_items ON source_fingerprint=$1 LIMIT 1", DbValue.Binary(digest)), r => r.IsDBNull(0) ? null : new ExistingItem(r.GetGuid(0), r.GetGuid(1)), ct);
                        if (duplicate is null) continue;
                        if (duplicate.Id != item.Id || duplicate.Product != product) throw new InvalidOperationException("Duplicate inventory content.");
                        exists = true; break;
                    }
                    if (!exists) await session.ExecuteAsync(new DbStatement("INSERT INTO shop.inventory_items(id,product_id,credential_ciphertext,credential_key_id,source_fingerprint,origin_kind) VALUES ($1,$2,$3,$4,$5,'local_import')",
                        DbValue.Uuid(item.Id), DbValue.Uuid(product), DbValue.Binary(new InventoryCipher(secrets).Encrypt(item.Id, plain)), DbValue.Text(key), DbValue.Binary(fingerprint)), ct);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
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
    private sealed record ExistingItem(Guid Id, Guid Product);
}
