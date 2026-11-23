namespace Platform.Persistence;

internal sealed class EfIntegrityScope
{
    internal readonly HashSet<Guid> Wallets = [], Postings = [], Audits = [], Users = [], Orders = [], Discounts = [], Allocations = [], Payments = [], Entities = [];
    internal readonly HashSet<Guid> AuditUsers = [];
    internal bool Rejected;
    internal bool Empty => Wallets.Count + Postings.Count + Audits.Count + Users.Count + Orders.Count + Discounts.Count + Allocations.Count + Payments.Count + Entities.Count == 0;
}
