using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            if (!await AuditAsync(tx, actor, audit, token)) return checked(revision + 1);
            if (value.Support)
            {
                using var contact = SqliteWallet.Command(tx, """
                    SELECT 1 FROM admins WHERE id=$admin AND disabled_at IS NULL
                        AND telegram_verified_at IS NOT NULL AND public_contact_handle IS NOT NULL
                    """, ("$admin", value.ContactAdminId));
                if (await contact.ExecuteScalarAsync(token) is null)
                    throw new ArgumentException("A verified public support contact is required.");
            }
            using var save = SqliteWallet.Command(tx, """
                UPDATE brands SET public_name=$name,default_language=$language,active=$active,
                    allow_wallet=$wallet,allow_crypto=$crypto,allow_bank=$bank,
                    allow_wallet_topup=$topup,zero_balance_topup=$zero,support_enabled=$support,
                    customer_support_admin_id=$contact,fx_enabled=$fx,logo_asset_ref=$logo,
                    policy_version=policy_version+1,updated_at=$now
                WHERE id=$brand AND bot_id=$bot AND policy_version=$revision
                """, ("$name", value.PublicName), ("$language", value.Language),
                ("$active", value.Active), ("$wallet", value.Wallet),
                ("$crypto", value.Crypto), ("$bank", value.Bank),
                ("$topup", value.Topup), ("$zero", value.ZeroBalanceTopup),
                ("$support", value.Support), ("$contact", value.ContactAdminId),
                ("$fx", value.Fx), ("$logo", value.Logo),
                ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$brand", context.BrandId),
                ("$bot", context.BotId), ("$revision", revision));
            if (await save.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Policy revision conflict.");
            return checked(revision + 1);
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
        return sessions.RunAsync(true, async (_, tx, token) =>
        {
            if (!await AuditAsync(tx, actor, audit, token)) return checked(revision + 1);
            using var save = SqliteWallet.Command(tx, """
                INSERT INTO products(id,title,description,icon_ref,guidance,fulfillment_kind,
                    availability_policy,local_first,active,revision)
                VALUES ($product,$title,$description,$icon,$guidance,$fulfillment,$availability,
                    $first,$active,1)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title,description=excluded.description,
                    icon_ref=excluded.icon_ref,guidance=excluded.guidance,
                    fulfillment_kind=excluded.fulfillment_kind,availability_policy=excluded.availability_policy,
                    local_first=excluded.local_first,active=excluded.active,
                    revision=products.revision+1,updated_at=$now
                WHERE products.revision=$revision
                """, ("$product", product), ("$title", value.Title),
                ("$description", value.Description), ("$icon", value.Icon),
                ("$guidance", value.Guidance), ("$fulfillment", value.Fulfillment),
                ("$availability", value.Availability), ("$first", value.LocalFirst),
                ("$active", value.Active), ("$now", SqliteSchema.UtcText(DateTime.UtcNow)),
                ("$revision", revision));
            if (await save.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Product revision conflict.");
            return checked(revision + 1);
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
            if (!await AuditAsync(tx, actor, audit, token)) return checked(revision + 1);
            using (var identity = SqliteWallet.Command(tx, """
                SELECT EXISTS(SELECT 1 FROM products WHERE id=$product)
                    AND EXISTS(SELECT 1 FROM brands WHERE id=$brand AND bot_id=$bot AND active=1)
                """, ("$product", product), ("$brand", context.BrandId), ("$bot", context.BotId)))
                if ((long)(await identity.ExecuteScalarAsync(token))! != 1)
                    throw new UnauthorizedAccessException("The listing scope is unavailable.");
            using var save = SqliteWallet.Command(tx, """
                INSERT INTO brand_products(brand_id,product_id,visible,title_mode,title_override,
                    description_mode,description_override,icon_mode,icon_override,guidance_mode,
                    guidance_override,price_usdt,price_toman,auto_price_toman,allow_wallet,
                    allow_crypto,allow_bank,min_quantity,max_quantity,revision)
                VALUES ($brand,$product,$visible,$title_mode,$title,$desc_mode,$description,
                    $icon_mode,$icon,$guide_mode,$guidance,$usd,$toman,$automatic,$wallet,
                    $crypto,$bank,$minimum,$maximum,1)
                ON CONFLICT(brand_id,product_id) DO UPDATE SET visible=excluded.visible,
                    title_mode=excluded.title_mode,title_override=excluded.title_override,
                    description_mode=excluded.description_mode,description_override=excluded.description_override,
                    icon_mode=excluded.icon_mode,icon_override=excluded.icon_override,
                    guidance_mode=excluded.guidance_mode,guidance_override=excluded.guidance_override,
                    price_usdt=excluded.price_usdt,price_toman=excluded.price_toman,
                    auto_price_toman=excluded.auto_price_toman,allow_wallet=excluded.allow_wallet,
                    allow_crypto=excluded.allow_crypto,allow_bank=excluded.allow_bank,
                    min_quantity=excluded.min_quantity,max_quantity=excluded.max_quantity,
                    revision=brand_products.revision+1,updated_at=$now
                WHERE brand_products.revision=$revision
                """, ("$brand", context.BrandId), ("$product", product),
                ("$visible", value.Visible), ("$title_mode", value.TitleMode),
                ("$title", value.Title), ("$desc_mode", value.DescriptionMode),
                ("$description", value.Description), ("$icon_mode", value.IconMode),
                ("$icon", value.Icon), ("$guide_mode", value.GuidanceMode),
                ("$guidance", value.Guidance), ("$usd", ExactDecimal.Format(usd.Amount)),
                ("$toman", toman is Money local ? ExactDecimal.Format(local.Amount) : null),
                ("$automatic", value.AutomaticToman), ("$wallet", value.Wallet),
                ("$crypto", value.Crypto), ("$bank", value.Bank),
                ("$minimum", value.Minimum), ("$maximum", value.Maximum),
                ("$now", SqliteSchema.UtcText(DateTime.UtcNow)), ("$revision", revision));
            if (await save.ExecuteNonQueryAsync(token) != 1)
                throw new InvalidOperationException("Listing revision conflict.");
            await OutboxStore.EnqueueSqliteAsync(tx,
                JobIntent.Product(JobKind.EvaluateRestock, product),
                command, DateTime.UtcNow, token);
            return checked(revision + 1);
        }, ct);
    }

    private static async Task<bool> AuditAsync(SqliteTransaction tx, AuthenticatedAdmin actor,
        AuditCommand audit, CancellationToken ct)
    {
        await SqliteAdminSessionStore.RequireActiveAsync(tx, actor, ct);
        byte[] fingerprint = audit.Fingerprint(actor.AdminId);
        using (var previous = SqliteWallet.Command(tx, """
            SELECT command_sha256 FROM audit_events WHERE id=$command
            """, ("$command", audit.Id)))
        {
            object? existing = await previous.ExecuteScalarAsync(ct);
            if (existing is byte[] digest)
            {
                if (!CryptographicOperations.FixedTimeEquals(fingerprint, digest))
                    throw new UnauthorizedAccessException("Administrator command identity conflict.");
                return false;
            }
        }
        using var insert = SqliteWallet.Command(tx, """
            INSERT INTO audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,
                entity_id,reason,previous_revision,new_revision,correlation_id,command_sha256)
            VALUES ($id,'admin',$admin,$brand,$action,$entity,$target,'configuration_change',
                $old,$new,$id,$hash)
            """, ("$id", audit.Id), ("$admin", actor.AdminId),
            ("$brand", audit.BrandId), ("$action", AdminAuditStore.ActionCode(audit.Action)),
            ("$entity", AdminAuditStore.EntityCode(audit.Entity)), ("$target", audit.EntityId),
            ("$old", audit.PreviousRevision), ("$new", audit.NewRevision),
            ("$hash", fingerprint));
        if (await insert.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Administrator audit was not persisted.");
        return true;
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
