using Light.SocketIoClient.Net.Options;
using System.Text.Json;

namespace Light.SocketIoClient.Net;

public interface ISocketIoClient
{
    event EventHandler Connected;
    event EventHandler<DisconnectedEventArgs> Disconnected;

    SocketClientOptions Options { get; }
    bool IsConnected { get; }
    Task Connect(CancellationToken cancellationToken);
    void On(string eventName, Func<JsonElement, Task> handler);
    ValueTask Send(string eventName, JsonElement payload);
    Task Disconnect();
}