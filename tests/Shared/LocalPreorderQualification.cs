using System.Security.Cryptography;
using Npgsql;
using Platform.Fulfillment;
using Platform.Persistence;
using Platform.Telegram;

namespace Platform.Testing;

public static class LocalPreorderQualification
{
    public static async Task RunAsync(CancellationToken ct = default, bool lostCommit = false)
    {
        await using var f = await CheckoutFixture.CreateAsync(stock: 0, ct: ct);
        await using (var policy = await f.Sessions.BeginAsync(ct))
        {
            await policy.ExecuteAsync(new DbStatement("UPDATE shop.products SET availability_policy='local_preorder',revision=revision+1"), ct);
            await policy.CommitAsync(ct);
        }
        var service = new WalletCheckoutService(f.Sessions, f.Store, new CatalogStore(f.Sessions, [], (_, _) => throw new InvalidOperationException("Unexpected supplier transport.")));
        for (int user = 0; user < 2; user++)
        {
            var order = await service.PurchaseAsync(f.Context(user), CheckoutFixture.Actor(user), new(Guid.CreateVersion7(), f.Product, user == 0 ? 5 : 2), (_, _, _) => Task.FromResult(ChannelMembership.Member), ct);
            if (order.Outcome != CheckoutOutcome.Purchased) throw new InvalidOperationException("Native preorder acceptance failed.");
        }
        var worker = new LocalPreorderWorker(new(f.Sessions), f.Contexts);
        bool dropped = false;
        foreach (int count in new[] { 3, 3, 1 })
        {
            await new AdminCatalogStore(f.Sessions).ImportAsync(await f.AdminAsync(ct), f.Product, Enumerable.Range(0, count).Select(_ => new InventoryImportItem(Guid.NewGuid(), "Public native FIFO " + Guid.NewGuid())).ToArray(), Guid.NewGuid(), ct);
            while (await Claim(JobKind.DrainLocalPreorders) is JobLease lease)
            {
                if (!lostCommit || dropped || await f.CountAsync("SELECT count(*) FROM shop.inventory_items", ct) != 6) { await worker.RunAsync(lease, ct); continue; }
                var options = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHOP_TEST_POSTGRES_ADMIN") ?? throw new InvalidOperationException("Synthetic control identity is missing."))
                { Database = new NpgsqlConnectionStringBuilder(f.Database.Data.ConnectionString).Database };
                await using var proxy = new DropCommitReplyProxy(options.ConnectionString);
                await using var transport = proxy.CreateClientDataSource();
                bool unknown = false, fenced = false;
                try { await new LocalPreorderWorker(new(new(transport)), f.Contexts).RunAsync(lease, ct); }
                catch (DbOperationException error) when (error.Failure.Kind == DbFailureKind.OutcomeUnknown && error.Failure.Recovery == DbRecovery.ReconcileBeforeRetry) { unknown = true; }
                await proxy.CommitObserved.WaitAsync(TimeSpan.FromSeconds(10), ct);
                try { await worker.RunAsync(lease, ct); }
                catch (DbOperationException error) when (error.Failure.Kind == DbFailureKind.LeaseLost) { fenced = true; }
                if (!unknown || !fenced || await f.CountAsync("SELECT count(*) FROM shop.fulfillments", ct) != 5 || await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", ct) != 1)
                    throw new InvalidOperationException("Native lost FIFO commit was not reconciled.");
                dropped = true;
            }
            long expected = count == 1 ? 7 : await f.CountAsync("SELECT count(*) FROM shop.inventory_items", ct) == 3 ? 0 : 5;
            if (await f.CountAsync("SELECT count(*) FROM shop.fulfillments", ct) != expected) throw new InvalidOperationException("Native FIFO bypassed the whole head.");
        }
        var delivery = new DeliveryStore(f.Sessions);
        for (int user = 0; user < 2; user++)
        {
            JobLease lease = await Claim(JobKind.PrepareManifest) ?? throw new InvalidOperationException("Native manifest work missing.");
            await delivery.PrepareAsync(f.Context(user), lease, ct);
            byte[] content = await delivery.ReadManifestAsync(f.Context(user), CheckoutFixture.Actor(user), lease.Intent.OriginId!.Value, ct);
            CryptographicOperations.ZeroMemory(content);
        }
        if (lostCommit != dropped || await f.CountAsync("SELECT count(*) FROM shop.fulfillments WHERE status='ready'", ct) != 7 || await f.CountAsync("SELECT count(*) FROM shop.wallet_entries WHERE movement_kind='purchase_debit'", ct) != 2 || await f.CountAsync("SELECT count(*) FROM shop.jobs WHERE job_kind='prepare_manifest'", ct) != 2)
            throw new InvalidOperationException("Native FIFO manifest or debit failed.");
        async Task<JobLease?> Claim(JobKind kind)
        {
            await using var claim = await f.Sessions.BeginAsync(ct);
            JobLease? lease = await OutboxStore.ClaimAsync(claim, kind, Guid.NewGuid(), TimeSpan.FromMinutes(1), ct);
            await claim.CommitAsync(ct); return lease;
        }
    }
}
