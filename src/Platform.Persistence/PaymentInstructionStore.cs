using Platform.Security;

namespace Platform.Persistence;

public sealed partial class PaymentInstructionStore(DbSessionFactory sessions)
{
    internal static async Task<Guid> EnqueueAsync(DbSession session, BrandContext context, Guid order, string cause, CancellationToken ct)
    {
        if (cause is not ("created" or "expired" or "replaced" or "processing" or "invoice")) throw new ArgumentException("Invalid instruction cause.");
        Guid id = await session.RunEfStepAsync(true, (data, token) => PaymentInstructionQueries.EnqueueAsync(data, context, order, cause, token), ct);
        await OutboxStore.EnqueueAsync(session, JobIntent.Telegram(context.BrandId, id), id, DateTime.UtcNow, ct);
        return id;
    }
}
