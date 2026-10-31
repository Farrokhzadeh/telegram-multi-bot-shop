using System.Security.Cryptography;
using System.Text;
using NpgsqlTypes;
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
        byte[] fingerprint = command.Fingerprint(actor.AdminId);
        bool exists = await session.QuerySingleAsync(new DbStatement("""
            SELECT a.id,a.command_sha256 FROM (SELECT 1) singleton LEFT JOIN shop.audit_events a ON a.id=$1
            """, DbValue.Uuid(command.Id)), row =>
        {
            if (row.IsDBNull(0)) return false;
            if (row.IsDBNull(1) || !CryptographicOperations.FixedTimeEquals(fingerprint, row.GetBinary(1)))
                throw new FormatException("Audit command identity conflict.");
            return true;
        }, cancellationToken);
        if (exists) return false;
        await session.ExecuteAsync(new DbStatement("""
            INSERT INTO shop.audit_events(id,actor_kind,actor_admin_id,brand_id,action,entity_kind,entity_id,reason,
                previous_revision,new_revision,correlation_id,command_sha256,before_channel_enforcement,after_channel_enforcement)
            VALUES ($1,'admin',$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            """, DbValue.Uuid(command.Id), DbValue.Uuid(actor.AdminId), Uuid(command.BrandId), DbValue.Text(ActionCode(command.Action)),
            DbValue.Text(EntityCode(command.Entity)), Uuid(command.EntityId), DbValue.Text(ReasonCode(command.Reason)),
            Number(command.PreviousRevision), Number(command.NewRevision), DbValue.Uuid(command.CorrelationId), DbValue.Binary(fingerprint),
            Flag(command.BeforeChannelEnforcement), Flag(command.AfterChannelEnforcement)), cancellationToken);
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
    private static DbValue Uuid(Guid? value) => value.HasValue ? DbValue.Uuid(value.Value) : DbValue.Null(NpgsqlDbType.Uuid);
    private static DbValue Number(long? value) => value.HasValue ? DbValue.Bigint(value.Value) : DbValue.Null(NpgsqlDbType.Bigint);
    private static DbValue Flag(bool? value) => value.HasValue ? DbValue.Boolean(value.Value) : DbValue.Null(NpgsqlDbType.Boolean);
}
