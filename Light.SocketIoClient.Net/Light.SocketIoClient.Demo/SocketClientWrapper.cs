using System.Text.Json;
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

    public async Task<bool> Connect(CancellationToken cancellationToken)
    {
		try
		{
            _client.On("error", OnErrorHandler);
			await _client.Connect(cancellationToken);
            Interlocked.Exchange(ref _failedConnectAttempts, 0);
            return _client.IsConnected;
        }
		catch (Exception ex)
        {
            Interlocked.Increment(ref _failedConnectAttempts);
            _logger.LogError(ex, "connection error");
            return false;
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
            case DisconnectReason.Server:
                return;
            case DisconnectReason.ReceiveFail:
            case DisconnectReason.ParseFail:
            case DisconnectReason.SendFail:
            default:
                _socketClientsSentinel.Reconnect(this);
                break;
        }
    }

    private Task OnErrorHandler(JsonElement details)
    {
        _logger.LogError(details.ToString());
        return Task.CompletedTask;
    }
}