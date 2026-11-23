using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Platform.Persistence;
using Platform.Persistence.Entities;
using Platform.Testing;
using EfFulfillment = global::Platform.Persistence.Entities.Fulfillment;
using Fixture = Platform.IntegrationTests.EfIntegrityTests.Fixture;

namespace Platform.IntegrationTests;

public sealed class EfCommerceIntegrityTests
{
    [Theory]
    [InlineData("order_release_state")]
    [InlineData("order_complete_manifest")]
    [InlineData("order_preorder_fifo")]
    [InlineData("order_preorder_whole_allocation")]
    [InlineData("order_unit_allocation")]
    [InlineData("order_split_quantity")]
    [InlineData("order_prepaid_no_goods")]
    [InlineData("order_release_generation")]
    public async Task RelatedOrderAndAllocationMutationsRejectBeforeTheLegacyBackstop(string rule)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var accepted = await SeedOrderAsync(fixture, ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
            switch (rule)
            {
                case "order_release_state": order.FulfillmentStatus = "ready"; break;
                case "order_complete_manifest": Paid(order); order.FulfillmentStatus = "ready"; break;
                case "order_preorder_fifo": Paid(order); order.IsPreorder = true; break;
                case "order_preorder_whole_allocation":
                    Paid(order); order.IsPreorder = true; order.FulfillmentStatus = "waiting_stock";
                    AddAllocation(session, order, 1);
                    break;
                case "order_unit_allocation": Paid(order); session.Data.Add(Unit(order, 1)); break;
                case "order_split_quantity":
                    Paid(order);
                    for (int number = 1; number <= order.Quantity + 1; number++) AddAllocation(session, order, number);
                    break;
                case "order_prepaid_no_goods":
                    order.OrderKind = "prepaid_units";
                    session.Data.Add(new EfFulfillment { Id = Guid.NewGuid(), OrderId = order.Id, UnitNo = 1, Status = "unfilled" });
                    break;
                case "order_release_generation":
                    Paid(order);
                    order.DeliveryGeneration = Guid.NewGuid();
                    order.ManifestAssetRef = "fixture-manifest";
                    order.ManifestDigest = new byte[32];
                    order.ManifestPreparedAt = DateTime.UtcNow;
                    await session.Data.SaveChangesAsync(token);
                    var released = Unit(order, 1);
                    released.Kind = "manual";
                    released.Status = "released";
                    released.ManifestGeneration = order.DeliveryGeneration;
                    released.CompletedAt = DateTime.UtcNow;
                    session.Data.Add(released);
                    break;
            }
        }, ct));
        Assert.Contains("EF commit rejected: " + rule, error.Message);
        await fixture.Sessions.RunEfAsync(false, fixture.Binding, async (session, token) =>
        {
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
            Assert.Equal("awaiting_payment", order.PaymentStatus);
            Assert.Equal("unfulfilled", order.FulfillmentStatus);
            Assert.Empty(await session.Data.Set<EfFulfillment>().ToArrayAsync(token));
            Assert.Empty(await session.Data.Set<InventoryItem>().ToArrayAsync(token));
            return true;
        }, ct);
    }

    [Fact]
    public async Task RemovingAnAllocationRevalidatesItsUnmodifiedFulfillment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var accepted = await SeedOrderAsync(fixture, ct);
        await fixture.WriteAsync(async (session, token) =>
        {
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
            Paid(order);
            AddAllocation(session, order, 1);
        }, ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            var item = await session.Data.Set<InventoryItem>().SingleAsync(token);
            item.State = "available";
            item.AssignedFulfillmentId = null;
            item.AllocatedAt = null;
        }, ct));
        Assert.Contains("EF commit rejected: order_unit_allocation", error.Message);
    }

    [Theory]
    [InlineData("coupon_revision_required")]
    [InlineData("coupon_redemption_count")]
    [InlineData("coupon_redeemed_obligation")]
    [InlineData("order_resolution_hold")]
    public async Task CouponAndResolutionChecksRequireTheirRelatedAtomicState(string rule)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var accepted = await SeedOrderAsync(fixture, ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
            var admin = new Admin { Id = Guid.NewGuid(), Login = "ef-coupon", AuthenticationMode = "external", ExternalSubject = "ef-coupon" };
            session.Data.Add(admin);
            var coupon = Coupon(session, admin.Id, CheckoutFixture.ResolveContexts()[0].BrandId, rule != "coupon_revision_required");
            if (rule == "coupon_redemption_count") coupon.Discount.RedeemedCount = 1;
            if (rule is "coupon_redeemed_obligation" or "order_resolution_hold")
            {
                bool redeem = rule == "coupon_redeemed_obligation";
                coupon.Discount.RedeemedCount = redeem ? 1 : 0;
                session.Data.Add(new DiscountUse
                {
                    DiscountId = coupon.Discount.Id,
                    DiscountRevisionId = coupon.Revision,
                    OrderId = order.Id,
                    UnitsCovered = 1,
                    SavedAmount = 0m,
                    SavedCurrency = "USD",
                    Status = redeem ? "redeemed" : "held"
                });
                if (!redeem)
                {
                    Paid(order);
                    order.IsPreorder = true;
                    order.FulfillmentStatus = "cancelled";
                    order.ResolutionKind = "local_preorder_cancellation";
                    order.ResolutionAdminId = admin.Id;
                    order.ResolutionAt = DateTime.UtcNow;
                    var audit = Audit(admin.Id, "cancel_preorder", "order", order.Id, CheckoutFixture.ResolveContexts()[0].BrandId);
                    session.Data.Add(audit);
                    order.ResolutionAuditEventId = audit.Id;
                }
            }
        }, ct));
        Assert.Contains("EF commit rejected: " + rule, error.Message);
    }

    [Theory]
    [InlineData("payment_status")]
    [InlineData("order_status")]
    [InlineData("received_amount")]
    public async Task ExistingCompensationIsRevalidatedWhenOnlyItsPaymentOrOrderChanges(string mutation)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var accepted = await SeedOrderAsync(fixture, ct);
        Guid paymentId = Guid.NewGuid(), entryId = Guid.NewGuid();
        await fixture.WriteAsync(async (session, token) =>
        {
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
            order.PaymentStatus = "expired";
            var payment = new Payment
            {
                Id = paymentId,
                OrderId = order.Id,
                PaymentMethod = "crypto",
                QuoteCurrency = "USD",
                AmountDue = 7m,
                AmountReceived = 7m,
                ProviderCode = "fixture",
                IdempotencyRef = "ef-late",
                NetworkAsset = "USDTTRC20",
                Status = "settled",
                ConfirmedAt = DateTime.UtcNow
            };
            session.Data.Add(payment);
            await session.Data.SaveChangesAsync(token);
            var wallet = await session.Data.Set<Wallet>().SingleAsync(token);
            wallet.Balance = 7m;
            wallet.Version = 1;
            session.Data.Add(new WalletEntry
            {
                Id = entryId,
                UserId = fixture.Member,
                SignedAmount = 7m,
                BalanceAfter = 7m,
                PostingVersion = 1,
                MovementKind = "late_crypto_credit",
                IsPosting = true,
                IdempotencyKey = "ef-late",
                OrderId = order.Id,
                PaymentId = payment.Id,
                Reason = "Synthetic late capture"
            });
            await session.Data.SaveChangesAsync(token);
            payment.CompensatingWalletEntryId = entryId;
        }, ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            if (mutation == "order_status")
            {
                var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
                Paid(order);
            }
            else
            {
                var payment = await session.Data.Set<Payment>().SingleAsync(token);
                if (mutation == "payment_status") payment.Status = "expired";
                else payment.AmountReceived = 6m;
            }
        }, ct));
        Assert.Contains("EF commit rejected: wallet_compensation_origin", error.Message);
        Assert.Equal(7m, await fixture.BalanceAsync(ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberOwnershipCannotBeReassignedByTrackedOrSetBasedWrites(bool setBased)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            if (setBased) await session.UpdateAsync<User>(value => value.Id == fixture.Member, setters => setters.SetProperty(value => value.TelegramId, 9001L), token);
            else (await session.Data.Set<User>().SingleAsync(token)).TelegramId = 9001;
        }, ct));
        await fixture.WriteAsync((_, _) => Task.CompletedTask, ct);
    }

    [Fact]
    public async Task StaticAuditReferencesRejectMissingTargetsAndPreventDeletingAnAuditedProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        Guid product = Guid.NewGuid();
        await fixture.WriteAsync((session, _) =>
        {
            session.Data.Add(new Product { Id = product, Title = "Audited", FulfillmentKind = "manual", AvailabilityPolicy = "immediate", Active = false });
            session.Data.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorKind = "system",
                Action = "create",
                EntityKind = "product",
                EntityId = product,
                Reason = "Synthetic product",
                CorrelationId = Guid.NewGuid()
            });
            return Task.CompletedTask;
        }, ct);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync((session, _) =>
        {
            session.Data.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                ActorKind = "system",
                Action = "update",
                EntityKind = "product",
                EntityId = Guid.NewGuid(),
                Reason = "Synthetic missing target",
                CorrelationId = Guid.NewGuid()
            });
            return Task.CompletedTask;
        }, ct));
        Assert.Contains("EF commit rejected: audit_entity_scope", missing.Message);
        var removed = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
            session.Data.Remove(await session.Data.Set<Product>().SingleAsync(token)), ct));
        Assert.Contains("EF commit rejected: audit_entity_scope", removed.Message);
    }

    [Fact]
    public async Task AResolutionCannotPointAtAnUnrelatedAcceptedPosting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var accepted = await SeedOrderAsync(fixture, ct);
        Guid posting = Guid.NewGuid();
        await fixture.WriteAsync(async (session, token) =>
        {
            var wallet = await session.Data.Set<Wallet>().SingleAsync(token);
            wallet.Balance = 7m;
            wallet.Version = 1;
            session.Data.Add(new WalletEntry
            {
                Id = posting,
                UserId = fixture.Member,
                SignedAmount = 7m,
                BalanceAfter = 7m,
                PostingVersion = 1,
                MovementKind = "opening_balance",
                IsPosting = true,
                IdempotencyKey = "resolution-opening",
                LegacyImportBatch = Guid.NewGuid(),
                Reason = "Synthetic opening"
            });
        }, ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
        {
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == accepted.Id, token);
            order.PaymentStatus = "expired";
            order.ResolutionKind = "late_crypto_credit";
            order.ResolutionAt = DateTime.UtcNow;
            order.ResolutionWalletEntryId = posting;
        }, ct));
        Assert.Contains("EF commit rejected: order_resolution_ledger", error.Message);
        Assert.Equal(7m, await fixture.BalanceAsync(ct));
    }

    [Fact]
    public async Task AChangedSupplierRequestRevalidatesItsUnmodifiedOrderQuantity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.CreateAsync(ct);
        var accepted = await SeedOrderAsync(fixture, ct);
        await fixture.WriteAsync((session, _) =>
        {
            session.Data.Add(new SupplierRequest
            {
                Id = Guid.NewGuid(),
                OrderId = accepted.Id,
                RequestedQuantity = 1,
                LocalIntentKey = "ef-blocked",
                RequestSha256 = new byte[32],
                Status = "blocked",
                ReviewCode = "cost_unverified"
            });
            return Task.CompletedTask;
        }, ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.WriteAsync(async (session, token) =>
            (await session.Data.Set<SupplierRequest>().SingleAsync(token)).RequestedQuantity = accepted.Quantity + 1, ct));
        Assert.Contains("EF commit rejected: order_split_quantity", error.Message);
    }

    [Fact]
    public async Task PostgresRunsTheFullCommerceAndAuditQueriesAgainstAcceptedState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await CheckoutFixture.CreateAsync(users: 1, stock: 1, ct: ct);
        var result = await fixture.Store.PurchaseLocalAsync(fixture.Context(0), CheckoutFixture.Actor(0), fixture.Request(), ct);
        Assert.Equal(CheckoutOutcome.Purchased, result.Outcome);
        var actor = await fixture.AdminAsync(ct);
        Guid couponId = Guid.Empty;
        await fixture.Sessions.RunEfAsync(true, async (session, token) =>
        {
            await session.RequireAdminAsync(actor, null, token);
            var order = await session.Data.Set<Order>().SingleAsync(value => value.Id == result.OrderId, token);
            order.Revision++;
            couponId = Coupon(session, actor.AdminId, fixture.Brands[0], true).Discount.Id;
            return true;
        }, ct);
        await fixture.Sessions.RunEfAsync(false, async (session, token) =>
        {
            Assert.False(await session.Data.Set<Discount>().Where(value => value.Id == couponId).Select(value => value.Active).SingleAsync(token));
            return true;
        }, ct);
    }

    private static async Task<Order> SeedOrderAsync(Fixture fixture, CancellationToken ct)
    {
        Guid product = Guid.NewGuid();
        var created = DateTime.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = fixture.Member,
            ProductId = product,
            Quantity = 2,
            OrderKind = "product_purchase",
            PaymentStatus = "awaiting_payment",
            FulfillmentStatus = "unfulfilled",
            Currency = "USDT",
            PaymentMethod = "crypto",
            UnitPriceUsdt = 1m,
            PricingRoundingStepSnapshot = 5000m,
            PriceCalculationVersion = "fixture",
            BrandPublicNameSnapshot = "EF",
            ProductTitleSnapshot = "Fixture",
            PaymentTermsSnapshot = "crypto",
            FulfillmentKindSnapshot = "local",
            LocalFirstSnapshot = true,
            CreatedAt = created,
            UpdatedAt = created,
            PaymentDueAt = created.AddMinutes(30)
        };
        await fixture.WriteAsync((session, _) =>
        {
            session.Data.Add(new Product { Id = product, Title = "Fixture", FulfillmentKind = "local", AvailabilityPolicy = "immediate" });
            session.Data.Add(order);
            return Task.CompletedTask;
        }, ct);
        return order;
    }
    private static void Paid(Order order) { order.PaymentStatus = "paid"; order.PaymentConfirmedAt = DateTime.UtcNow; }
    private static EfFulfillment Unit(Order order, int number) => new()
    {
        Id = Guid.NewGuid(),
        OrderId = order.Id,
        UnitNo = number,
        Kind = "local",
        Status = "acquired",
        DeliveredContent = [1],
        DeliveredContentKeyId = "fixture",
        ContentKind = "text"
    };
    private static void AddAllocation(EfSession session, Order order, int number)
    {
        var unit = Unit(order, number);
        session.Data.Add(unit);
        session.Data.Add(new InventoryItem
        {
            Id = Guid.NewGuid(),
            ProductId = order.ProductId!.Value,
            CredentialCiphertext = [1],
            CredentialKeyId = "fixture",
            SourceFingerprint = SHA256.HashData(unit.Id.ToByteArray()),
            OriginKind = "local_import",
            State = "allocated",
            AssignedFulfillmentId = unit.Id,
            AllocatedAt = DateTime.UtcNow
        });
    }
    private static AuditEvent Audit(Guid admin, string action, string kind, Guid target, Guid brand) => new()
    {
        Id = Guid.NewGuid(),
        ActorKind = "admin",
        ActorAdminId = admin,
        BrandId = brand,
        Action = action,
        EntityKind = kind,
        EntityId = target,
        Reason = "Synthetic integrity fixture",
        CorrelationId = Guid.NewGuid(),
        NewRevision = 0
    };
    private static (Discount Discount, Guid Revision) Coupon(EfSession session, Guid admin, Guid brand, bool revision)
    {
        var discount = new Discount
        {
            Id = Guid.NewGuid(),
            BrandId = brand,
            CodeDigest = SHA256.HashData(Guid.NewGuid().ToByteArray()),
            CodeCiphertext = [1],
            CodeKeyId = "fixture",
            DisplayPrefix = "EF",
            IssuanceBatchId = Guid.NewGuid(),
            Kind = "promotion",
            PercentOff = 10m,
            Active = false,
            Revision = 0,
            CreatedByAdminId = admin
        };
        var audit = Audit(admin, "coupon_issue", "discount", discount.Id, brand);
        session.Data.Add(discount);
        session.Data.Add(audit);
        Guid id = Guid.NewGuid();
        if (revision) session.Data.Add(new DiscountRevision { Id = id, DiscountId = discount.Id, RevisionNo = 0, AuditEventId = audit.Id, PercentOff = 10m, Active = false });
        return (discount, id);
    }
}
