using Light.SocketIoClient.Net;

namespace Light.SocketIoClient.Demo;

public class SocketClientWrapper
{
    private int _failedConnectAttempts;
    private readonly ISocketIoClient _client;
    private readonly ISocketClientsSentinel _socketClientsSentinel;
    private readonly ILogger<SocketClientWrapper> _logger;

    public SocketClientWrapper(ISocketIoClient client, ISocketClientsSentinel socketClientsSentinel, ILogger<SocketClientWrapper> logger)
    {
        _client = client;
        _socketClientsSentinel = socketClientsSentinel;
        _logger = logger;

        _client.Connected += OnClientConnected;
        _client.Disconnected += OnClientDisconnected;
    }

    public int FailedConnects => _failedConnectAttempts;
    public bool Connected => _client.IsConnected;

    public async Task Connect(CancellationToken cancellationToken)
    {
		try
		{
			await _client.Connect(cancellationToken);
            Interlocked.Exchange(ref _failedConnectAttempts, 0);
        }
		catch (Exception ex)
        {
            Interlocked.Increment(ref _failedConnectAttempts);
            _logger.LogError(ex, "connection error");
        }
    }

    public async Task Disconnect(CancellationToken cancellationToken)
    {
        try
        {
            await _client.Disconnect();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "disconnect error");
        }
    }

    private void OnClientConnected(object? sender, EventArgs e)
    {
        _logger.LogInformation("Client connected");
    }

    private void OnClientDisconnected(object? sender, DisconnectedEventArgs e)
    {
        _logger.LogInformation("Client disconnected, reason: {Reason}. {Desc}. {Exception}", e.Reason, e.Description, e.Exception);

        switch (e.Reason)
        {
            case DisconnectReason.User:
                return;
            case DisconnectReason.ReceiveFail:
            case DisconnectReason.ParseFail:
            case DisconnectReason.SendFail:
            default:
                _socketClientsSentinel.Reconnect(this);
                break;
        }
    }
}