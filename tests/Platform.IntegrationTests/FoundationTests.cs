using System.Security.Cryptography;
using Platform.Persistence;
using Platform.Security;
using Platform.Testing;

namespace Platform.IntegrationTests;

public sealed class FoundationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CanonicalSchemaAndWrongInventoryKeyRemainFailClosedAcrossModules()
    {
        FoundationQualification.VerifyIndependentInventoryKeys();
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        await Platform.Persistence.SchemaRunner.ApplyAsync(database.Data, SchemaManifest.ForQualification(database.Data.ConnectionString), Ct);
        await FoundationQualification.VerifySchemaAsync(database.Data, Ct);
    }

    [Fact]
    public async Task GlobalAdministratorCannotCommitMisattributedBrandAuditOrItsBusinessWrites()
    {
        await using var database = await DisposablePostgres.CreateAsync(Ct);
        await Platform.Persistence.SchemaRunner.ApplyPlaintextAsync(database.Data, Ct);
        var sessions = new DbSessionFactory(database.Data);
        Guid admin = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid(), user = Guid.NewGuid(), product = Guid.NewGuid();
        await using (var seed = await sessions.BeginAsync(Ct))
        {
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.admins(id,login,authentication_mode,password_verifier) VALUES ($1,'public-foundation','password',$2)",
                DbValue.Uuid(admin), DbValue.Text(AdminAuth.CreatePasswordVerifier(new string('s', 20)))), Ct);
            await seed.ExecuteAsync(new DbStatement("""
                INSERT INTO shop.brands(id,code,internal_name,public_name,market,default_language,bot_username,bot_id,bot_token)
                VALUES ($1,'public-first','public first','public first','international','en','public_first',1001,$3),
                    ($2,'public-second','public second','public second','international','en','public_second',1002,$3)
                """, DbValue.Uuid(first), DbValue.Uuid(second), DbValue.Binary([1])), Ct);
            await seed.ExecuteAsync(new DbStatement("INSERT INTO shop.users(id,brand_id,telegram_id,language) VALUES ($1,$2,1001,'en')", DbValue.Uuid(user), DbValue.Uuid(first)), Ct);
            await seed.CommitAsync(Ct);
        }
        IssuedAdminSession ticket;
        await using (var login = await sessions.BeginAsync(Ct))
        {
            ticket = await AdminSessionStore.LoginAsync(login, "public-foundation", new string('s', 20).AsMemory(), TimeSpan.FromHours(1), Ct);
            await login.CommitAsync(Ct);
        }
        using (ticket)
        {
            AuthenticatedAdmin actor;
            await using (var authenticate = await sessions.BeginAsync(Ct)) actor = await AdminSessionStore.AuthenticateAsync(authenticate, ticket.Token, Ct);
            await using (var wrongScope = await sessions.BeginAsync(Ct))
            {
                await AdminAuditStore.AppendAsync(wrongScope, actor, new AuditCommand(Guid.NewGuid(), AuditAction.Update, AuditEntity.User,
                    user, second, AuditReason.ConfigurationChange, Guid.NewGuid(), SHA256.HashData("public reviewed user change"u8)), Ct);
                await wrongScope.ExecuteAsync(new DbStatement("INSERT INTO shop.products(id,fulfillment_kind,availability_policy,title) VALUES ($1,'local','immediate','public atomic test')", DbValue.Uuid(product)), Ct);
                await Assert.ThrowsAsync<DbOperationException>(() => wrongScope.CommitAsync(Ct));
            }
            await using var verify = await sessions.BeginAsync(Ct);
            Assert.Equal(0L, await verify.QuerySingleAsync(new DbStatement("SELECT count(*) FROM shop.products WHERE id=$1", DbValue.Uuid(product)), row => row.GetInt64(0), Ct));
        }
    }
}
