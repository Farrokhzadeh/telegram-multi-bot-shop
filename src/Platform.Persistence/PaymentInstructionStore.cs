using Platform.Security;

namespace Platform.Persistence;

public sealed partial class PaymentInstructionStore(DbSessionFactory sessions)
{
    internal static async Task<Guid> EnqueueAsync(DbSession session, BrandContext context, Guid order, string cause, CancellationToken ct)
    {
        if (cause is not ("created" or "expired" or "replaced" or "processing")) throw new ArgumentException("Invalid instruction cause.");
        Guid id = await session.QuerySingleAsync(new DbStatement("""
            INSERT INTO shop.telegram_deliveries(id,brand_id,recipient_kind,user_id,purpose,action,target_message_id,dedupe_key,order_id)
            SELECT gen_random_uuid(),u.brand_id,'customer_dm',u.id,'order_status',CASE WHEN a.message IS NULL THEN 'send' ELSE 'edit' END,a.message,$1,o.id
            FROM shop.orders o JOIN shop.users u ON u.id=o.user_id JOIN shop.brands b ON b.id=u.brand_id
            LEFT JOIN LATERAL (SELECT telegram_message_id message FROM shop.telegram_deliveries WHERE order_id=o.id AND purpose='order_status'
                AND status='succeeded' AND telegram_message_id IS NOT NULL ORDER BY created_at,id LIMIT 1) a ON true
            WHERE o.id=$2 AND u.brand_id=$3 AND b.bot_id=$4
            ON CONFLICT(dedupe_key) DO UPDATE SET dedupe_key=EXCLUDED.dedupe_key RETURNING id
            """, DbValue.Text("instruction-" + cause + ":" + order.ToString("D")), DbValue.Uuid(order), DbValue.Uuid(context.BrandId), DbValue.Bigint(context.BotId)), r => r.GetGuid(0), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }
}
