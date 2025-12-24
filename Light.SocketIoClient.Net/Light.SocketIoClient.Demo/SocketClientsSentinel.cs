using System.Threading.Channels;

namespace Light.SocketIoClient.Demo;

internal class SocketClientsSentinel : BackgroundService, ISocketClientsSentinel
{
    private readonly Channel<SocketClientWrapper> _pendingReconnect = Channel.CreateUnbounded<SocketClientWrapper>();
    private int _maxConcurrentReconnects = 500;
    private int _maxReconnectPerClient = 10;
    private int _initialDelaySec = 5;

    public void Reconnect(SocketClientWrapper client)
    {
        _pendingReconnect.Writer.TryWrite(client);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!await _pendingReconnect.Reader.WaitToReadAsync(stoppingToken))
                break;

            var reconnects = new List<Task>();
            while (_pendingReconnect.Reader.TryRead(out var client))
            {
                reconnects.Add(ReconnectClient(client, stoppingToken));

                if (reconnects.Count >= _maxConcurrentReconnects)
                    break;
            }

            await Task.WhenAll(reconnects);
        }

        _pendingReconnect.Writer.TryComplete();
    }

    private async Task ReconnectClient(SocketClientWrapper client, CancellationToken ct)
    {
        if (client.FailedConnects >= _maxReconnectPerClient)
            return;

        var delay = _initialDelaySec + Math.Pow(2, client.FailedConnects);
        await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);

        if (client.Connected)
            return;

        await client.Disconnect(ct);
        await client.Connect(ct).ConfigureAwait(false);
    }
}