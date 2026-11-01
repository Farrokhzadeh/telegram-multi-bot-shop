using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Npgsql;
using Platform.Persistence;

namespace Platform.Testing;

// Synthetic loopback-only protocol fault: PostgreSQL confirms COMMIT to the proxy,
// then the proxy closes the client connection without forwarding that acknowledgement.
public sealed class DropCommitReplyProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<bool> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NpgsqlConnectionStringBuilder target;
    private readonly Task run;
    public Task CommitObserved => observed.Task;

    public DropCommitReplyProxy(string syntheticConnection)
    {
        _ = SchemaManifest.ForQualification(syntheticConnection); // Exact test DB/user and loopback validation.
        target = new NpgsqlConnectionStringBuilder(syntheticConnection);
        listener.Start();
        run = RunAsync();
    }

    public NpgsqlDataSource CreateClientDataSource()
    {
        var options = new NpgsqlConnectionStringBuilder(target.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            SslMode = SslMode.Disable, // Only this public-synthetic loopback probe; no real credentials/data.
            Pooling = false,
            Enlist = false,
            IncludeErrorDetail = false
        };
        return new NpgsqlSlimDataSourceBuilder(options.ConnectionString).Build();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested && !observed.Task.IsCompleted)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var backend = new TcpClient();
                await backend.ConnectAsync(target.Host == "localhost" ? "127.0.0.1" : target.Host!, target.Port, stop.Token);
                NetworkStream input = client.GetStream(), server = backend.GetStream();
                byte[] size = new byte[4];
                await input.ReadExactlyAsync(size, stop.Token);
                int length = BinaryPrimitives.ReadInt32BigEndian(size);
                if (length is < 8 or > 131072) throw new IOException("Invalid synthetic startup frame.");
                byte[] startup = new byte[length - 4];
                await input.ReadExactlyAsync(startup, stop.Token);
                await server.WriteAsync(size, stop.Token);
                await server.WriteAsync(startup, stop.Token);

                using var ending = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                Task upstream = ForwardAsync(input, server, false, ending.Token);
                Task downstream = ForwardAsync(server, input, true, ending.Token);
                await Task.WhenAny(upstream, downstream);
                ending.Cancel();
                client.Close(); backend.Close();
                try { await Task.WhenAll(upstream, downstream); }
                catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (!stop.IsCancellationRequested) observed.TrySetException(new IOException("Synthetic proxy stopped before commit acknowledgement."));
        }
        finally
        {
            if (!observed.Task.IsCompleted) observed.TrySetCanceled();
        }
    }

    private async Task ForwardAsync(NetworkStream source, NetworkStream destination, bool fromServer, CancellationToken ct)
    {
        byte[] header = new byte[5];
        while (!ct.IsCancellationRequested)
        {
            if (await source.ReadAsync(header.AsMemory(0, 1), ct) == 0) return;
            await source.ReadExactlyAsync(header.AsMemory(1, 4), ct);
            int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
            if (length is < 4 or > 1048576) throw new IOException("Invalid synthetic protocol frame.");
            byte[] body = new byte[length - 4];
            await source.ReadExactlyAsync(body, ct);
            if (fromServer && header[0] == (byte)'C' && body.AsSpan().SequenceEqual("COMMIT\0"u8))
            {
                observed.TrySetResult(true);
                throw new IOException("Synthetic commit acknowledgement dropped.");
            }
            await destination.WriteAsync(header, ct);
            await destination.WriteAsync(body, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        await run;
        stop.Dispose();
    }
}
