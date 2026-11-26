using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Platform.Security;

namespace Platform.Persistence;

public enum AuditAction
{
    Create, Update, Disable, Enable, BindTelegram, RevokeTelegram, ChannelEnforcement, SelectSource, StockImport,
    ManualFulfillment, HoldPreorder, ResumePreorder, CancelPreorder, RetrySupplier, SupplierCredit, ApproveCost,
    CouponIssue, CouponEdit, WalletAdjustment, SupportAssign, CampaignApprove, DeliveryRecovery, LegacyImport, Maintenance
}
public enum AuditEntity
{
    Brand, User, Admin, Product, BrandProduct, InventoryItem, Wallet, WalletEntry, Order, Payment, Fulfillment,
    SupplierRequest, Campaign, TelegramDelivery, SupportTicket, SupportMessage, Discount, DiscountRevision,
    SupplierRequestAttempt, ProductSupplierSource, ProductStockSubscription, BotActivityEvent, Platform
}
public enum AuditReason { OperatorReview, ConfigurationChange, CredentialRotation, AccountRevocation, TelegramPossession, Reconciliation, CustomerRequest }

/// <summary>Typed metadata plus a server-produced commitment; never accepts arbitrary before/after text or credentials.</summary>
public sealed class AuditCommand
{
    public Guid Id { get; }
    public AuditAction Action { get; }
    public AuditEntity Entity { get; }
    public Guid? EntityId { get; }
    public Guid? BrandId { get; }
    public AuditReason Reason { get; }
    public Guid CorrelationId { get; }
    public long? PreviousRevision { get; }
    public long? NewRevision { get; }
    public bool? BeforeChannelEnforcement { get; }
    public bool? AfterChannelEnforcement { get; }
    private readonly byte[] commitment;

    public AuditCommand(Guid id, AuditAction action, AuditEntity entity, Guid? entityId, Guid? brandId, AuditReason reason,
        Guid correlationId, ReadOnlySpan<byte> commitment, long? previousRevision = null, long? newRevision = null,
        bool? beforeChannelEnforcement = null, bool? afterChannelEnforcement = null)
    {
        if (id == Guid.Empty || correlationId == Guid.Empty || commitment.Length != 32 || !Enum.IsDefined(action) ||
            !Enum.IsDefined(entity) || !Enum.IsDefined(reason) || previousRevision < 0 || newRevision < 0 ||
            (entity == AuditEntity.Platform ? entityId is not null : entityId is null || entityId == Guid.Empty))
            throw new ArgumentException("Invalid audit command.");
        bool global = entity is AuditEntity.Admin or AuditEntity.Product or AuditEntity.InventoryItem or AuditEntity.ProductSupplierSource or AuditEntity.Platform;
        if (global ? brandId is not null : brandId is null || brandId == Guid.Empty) throw new ArgumentException("Invalid audit scope.");
        if (entity == AuditEntity.Brand && entityId != brandId) throw new ArgumentException("Invalid audit scope.");
        if (entity == AuditEntity.Platform && action is not (AuditAction.Maintenance or AuditAction.LegacyImport))
            throw new ArgumentException("Invalid platform audit action.");
        if (action == AuditAction.ChannelEnforcement ? entity != AuditEntity.Brand || beforeChannelEnforcement is null || afterChannelEnforcement is null
            : beforeChannelEnforcement is not null || afterChannelEnforcement is not null)
            throw new ArgumentException("Invalid channel audit metadata.");
        Id = id; Action = action; Entity = entity; EntityId = entityId; BrandId = brandId; Reason = reason; CorrelationId = correlationId;
        PreviousRevision = previousRevision; NewRevision = newRevision;
        BeforeChannelEnforcement = beforeChannelEnforcement; AfterChannelEnforcement = afterChannelEnforcement;
        this.commitment = commitment.ToArray();
    }

    internal byte[] Fingerprint(Guid actor)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("admin-audit-v1"); writer.Write(actor.ToByteArray(bigEndian: true));
            writer.Write(AdminAuditStore.ActionCode(Action)); writer.Write(AdminAuditStore.EntityCode(Entity));
            writer.Write((EntityId ?? Guid.Empty).ToByteArray(bigEndian: true));
            writer.Write((BrandId ?? Guid.Empty).ToByteArray(bigEndian: true));
            writer.Write(AdminAuditStore.ReasonCode(Reason));
            writer.Write(PreviousRevision.HasValue); if (PreviousRevision.HasValue) writer.Write(PreviousRevision.Value);
            writer.Write(NewRevision.HasValue); if (NewRevision.HasValue) writer.Write(NewRevision.Value);
            writer.Write(BeforeChannelEnforcement.HasValue); if (BeforeChannelEnforcement.HasValue) writer.Write(BeforeChannelEnforcement.Value);
            writer.Write(AfterChannelEnforcement.HasValue); if (AfterChannelEnforcement.HasValue) writer.Write(AfterChannelEnforcement.Value);
            writer.Write(commitment);
        }
        // Correlation identifies a request, not the logical command; a retry may have a fresh correlation.
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }
    public override string ToString() => "Administrator audit command (redacted)";
}

public static class AdminAuditStore
{
    // Returns false for an already committed command. Caller MUST return its original result without repeating writes.
    // Append before business mutations/locks; commit both together, and abandon the transaction on any error.
    public static async Task<bool> AppendAsync(DbSession session, AuthenticatedAdmin actor, AuditCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await AdminSessionStore.RequireActiveAsync(session, actor, command.Entity == AuditEntity.Admin ? command.EntityId : null, cancellationToken);
        await session.ExecuteAsync(new DbStatement("SELECT pg_advisory_xact_lock($1)",
            DbValue.Bigint(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(command.Id.ToByteArray(bigEndian: true)))), cancellationToken);
        return await session.RunEfStepAsync(true, (data, ct) => AppendEfAsync(data, actor, command, ct), cancellationToken);
    }
    internal static async Task<bool> AppendEfAsync(EfSession data, AuthenticatedAdmin actor, AuditCommand command, CancellationToken ct)
    {
        await AdminSessionStore.RequireActiveAsync(data, actor, command.Entity == AuditEntity.Admin ? command.EntityId : null, ct);
        byte[] fingerprint = command.Fingerprint(actor.AdminId);
        var previous = await data.Data.Set<Entities.AuditEvent>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == command.Id, ct);
        if (previous is not null)
        {
            if (previous.CommandSha256 is null || !CryptographicOperations.FixedTimeEquals(fingerprint, previous.CommandSha256))
            {
                if (data.Data.Database.IsSqlite()) throw new UnauthorizedAccessException("Administrator command identity conflict.");
                throw new FormatException("Audit command identity conflict.");
            }
            return false;
        }
        data.Data.Add(new Entities.AuditEvent
        {
            Id = command.Id,
            ActorKind = "admin",
            ActorAdminId = actor.AdminId,
            BrandId = command.BrandId,
            Action = ActionCode(command.Action),
            EntityKind = EntityCode(command.Entity),
            EntityId = command.EntityId,
            Reason = ReasonCode(command.Reason),
            PreviousRevision = command.PreviousRevision,
            NewRevision = command.NewRevision,
            CorrelationId = command.CorrelationId,
            CommandSha256 = fingerprint,
            BeforeChannelEnforcement = command.BeforeChannelEnforcement,
            AfterChannelEnforcement = command.AfterChannelEnforcement
        });
        return true;
    }
    internal static Task<bool> AppendSqliteAsync(Microsoft.Data.Sqlite.SqliteTransaction transaction, AuthenticatedAdmin actor, AuditCommand command, CancellationToken ct) =>
        SqliteSessionFactory.StepEfAsync(transaction, true, (data, token) => AppendEfAsync(data, actor, command, token), ct);
    internal static async Task<bool> ReplayEfAsync(EfSession data, AuthenticatedAdmin actor, AuditCommand command, CancellationToken ct)
    {
        await AdminSessionStore.RequireActiveAsync(data, actor, null, ct);
        var previous = await data.Data.Set<Entities.AuditEvent>().AsNoTracking().SingleOrDefaultAsync(value => value.Id == command.Id, ct);
        if (previous is null) return false;
        if (previous.CommandSha256 is null || !CryptographicOperations.FixedTimeEquals(previous.CommandSha256, command.Fingerprint(actor.AdminId)))
            throw new UnauthorizedAccessException("Administrator command identity conflict.");
        return true;
    }

    internal static string ActionCode(AuditAction action) => action switch
    {
        AuditAction.Create => "create",
        AuditAction.Update => "update",
        AuditAction.Disable => "disable",
        AuditAction.Enable => "enable",
        AuditAction.BindTelegram => "bind_telegram",
        AuditAction.RevokeTelegram => "revoke_telegram",
        AuditAction.ChannelEnforcement => "channel_enforcement",
        AuditAction.SelectSource => "select_source",
        AuditAction.StockImport => "stock_import",
        AuditAction.ManualFulfillment => "manual_fulfillment",
        AuditAction.HoldPreorder => "hold_preorder",
        AuditAction.ResumePreorder => "resume_preorder",
        AuditAction.CancelPreorder => "cancel_preorder",
        AuditAction.RetrySupplier => "retry_supplier",
        AuditAction.SupplierCredit => "supplier_credit",
        AuditAction.ApproveCost => "approve_cost",
        AuditAction.CouponIssue => "coupon_issue",
        AuditAction.CouponEdit => "coupon_edit",
        AuditAction.WalletAdjustment => "wallet_adjustment",
        AuditAction.SupportAssign => "support_assign",
        AuditAction.CampaignApprove => "campaign_approve",
        AuditAction.DeliveryRecovery => "delivery_recovery",
        AuditAction.LegacyImport => "legacy_import",
        AuditAction.Maintenance => "maintenance",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
    internal static string EntityCode(AuditEntity entity) => entity switch
    {
        AuditEntity.Brand => "brand",
        AuditEntity.User => "user",
        AuditEntity.Admin => "admin",
        AuditEntity.Product => "product",
        AuditEntity.BrandProduct => "brand_product",
        AuditEntity.InventoryItem => "inventory_item",
        AuditEntity.Wallet => "wallet",
        AuditEntity.WalletEntry => "wallet_entry",
        AuditEntity.Order => "order",
        AuditEntity.Payment => "payment",
        AuditEntity.Fulfillment => "fulfillment",
        AuditEntity.SupplierRequest => "supplier_request",
        AuditEntity.Campaign => "campaign",
        AuditEntity.TelegramDelivery => "telegram_delivery",
        AuditEntity.SupportTicket => "support_ticket",
        AuditEntity.SupportMessage => "support_message",
        AuditEntity.Discount => "discount",
        AuditEntity.DiscountRevision => "discount_revision",
        AuditEntity.SupplierRequestAttempt => "supplier_request_attempt",
        AuditEntity.ProductSupplierSource => "product_supplier_source",
        AuditEntity.ProductStockSubscription => "product_stock_subscription",
        AuditEntity.BotActivityEvent => "bot_activity_event",
        AuditEntity.Platform => "platform",
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };
    internal static string ReasonCode(AuditReason reason) => reason switch
    {
        AuditReason.OperatorReview => "operator_review",
        AuditReason.ConfigurationChange => "configuration_change",
        AuditReason.CredentialRotation => "credential_rotation",
        AuditReason.AccountRevocation => "account_revocation",
        AuditReason.TelegramPossession => "telegram_possession",
        AuditReason.Reconciliation => "reconciliation",
        AuditReason.CustomerRequest => "customer_request",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}
