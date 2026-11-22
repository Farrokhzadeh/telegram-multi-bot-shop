using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Platform.Security;

namespace Platform.Persistence;

public static class SqliteStageInventoryBridge
{
    private const int Port = 18082;
    private const int Maximum = 131072;

    public static async Task ForwardAsync(ReadOnlyMemory<byte> key, string token, Guid product,
        InventoryImportItem[] items, Guid command, CancellationToken ct = default)
    {
        if (key.Length != 32 || token.Length != 43 || product == Guid.Empty ||
            command == Guid.Empty || items.Length is < 1 or > 32)
            throw new UnauthorizedAccessException("Staging inventory import is unavailable.");
        byte[] message = Encode(key.Span, token, product, items, command);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, Port, ct);
            await using var stream = client.GetStream();
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, message.Length);
            await stream.WriteAsync(length, ct);
            await stream.WriteAsync(message, ct);
            await stream.FlushAsync(ct);
            byte[] result = new byte[1];
            await stream.ReadExactlyAsync(result, ct);
            if (result[0] != 0)
                throw result[0] == 1 ? new UnauthorizedAccessException("Staging import was rejected.") :
                    new InvalidOperationException("Staging import was not accepted; retry the same command identity.");
        }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    internal static byte[] Encode(ReadOnlySpan<byte> key, string token, Guid product,
        InventoryImportItem[] items, Guid command)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
        {
            writer.Write(key);
            writer.Write(token);
            writer.Write(product.ToByteArray());
            writer.Write(command.ToByteArray());
            writer.Write(items.Length);
            foreach (InventoryImportItem item in items)
            {
                if (item.Id == Guid.Empty || !item.Credential.StartsWith("synthetic-", StringComparison.Ordinal) ||
                    Encoding.UTF8.GetByteCount(item.Credential) is < 11 or > 1024)
                    throw new UnauthorizedAccessException("Only synthetic staging stock is supported.");
                writer.Write(item.Id.ToByteArray());
                byte[] content = Encoding.UTF8.GetBytes(item.Credential);
                try { writer.Write(content.Length); writer.Write(content); }
                finally { CryptographicOperations.ZeroMemory(content); }
            }
        }
        if (stream.Length is < 64 or > Maximum)
            throw new UnauthorizedAccessException("Staging inventory import exceeds the allowed size.");
        return stream.ToArray();
    }

    internal static async Task ProcessAsync(ReadOnlyMemory<byte> message, ReadOnlyMemory<byte> requiredKey,
        SqliteSessionFactory sessions, SecretSnapshot secrets, CancellationToken ct)
    {
        if (message.Length is < 64 or > Maximum || requiredKey.Length != 32)
            throw new UnauthorizedAccessException("Invalid staging inventory frame.");
        using var stream = new MemoryStream(message.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        byte[] key = reader.ReadBytes(32);
        try
        {
            if (key.Length != 32 || !CryptographicOperations.FixedTimeEquals(key, requiredKey.Span))
                throw new UnauthorizedAccessException("Staging inventory peer is unavailable.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        string bearer = reader.ReadString();
        if (bearer.Length != 43) throw new UnauthorizedAccessException();
        using var token = AdminToken.Parse(bearer);
        var actor = await SqliteAdminSessionStore.AuthenticateAsync(sessions, token, ct);
        byte[] productBytes = reader.ReadBytes(16), commandBytes = reader.ReadBytes(16);
        if (productBytes.Length != 16 || commandBytes.Length != 16) throw new UnauthorizedAccessException();
        Guid product = new(productBytes), command = new(commandBytes);
        int count = reader.ReadInt32();
        if (product == Guid.Empty || command == Guid.Empty || count is < 1 or > 32)
            throw new UnauthorizedAccessException();
        var items = new InventoryImportItem[count];
        for (int i = 0; i < count; i++)
        {
            byte[] identity = reader.ReadBytes(16);
            if (identity.Length != 16) throw new UnauthorizedAccessException();
            int length = reader.ReadInt32();
            if (length is < 11 or > 1024 || length > stream.Length - stream.Position)
                throw new UnauthorizedAccessException();
            byte[] bytes = reader.ReadBytes(length);
            try
            {
                string credential = new UTF8Encoding(false, true).GetString(bytes);
                if (!credential.StartsWith("synthetic-", StringComparison.Ordinal))
                    throw new UnauthorizedAccessException();
                items[i] = new InventoryImportItem(new Guid(identity), credential);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (stream.Position != stream.Length) throw new UnauthorizedAccessException();
        await new SqliteInventoryImportStore(sessions, secrets).ImportAsync(actor, product, items, command, ct);
    }

    public static async Task ServeAsync(SqliteSessionFactory sessions, SecretSnapshot secrets,
        ReadOnlyMemory<byte> requiredKey, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, Port);
        listener.Start(2);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await using var stream = client.GetStream();
                byte status = 2;
                byte[]? buffer = null;
                try
                {
                    byte[] header = new byte[4];
                    await stream.ReadExactlyAsync(header, timeout.Token);
                    int length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length is < 64 or > Maximum) throw new UnauthorizedAccessException();
                    buffer = new byte[length];
                    await stream.ReadExactlyAsync(buffer, timeout.Token);
                    await ProcessAsync(buffer, requiredKey, sessions, secrets, timeout.Token);
                    status = 0;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or AdminAuthenticationException)
                { status = 1; }
                catch (Exception error) when (error is InvalidOperationException or ArgumentException
                    or FormatException or IOException or OperationCanceledException or EndOfStreamException
                    or System.Data.Common.DbException)
                { status = 2; }
                finally { if (buffer is not null) CryptographicOperations.ZeroMemory(buffer); }
                try { await stream.WriteAsync(new byte[] { status }, timeout.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }
}
