using System.Text.Json;

namespace Light.SocketIoClient.Net;

public interface ISocketIoClient
{
    event EventHandler Connected;
    event EventHandler<DisconnectedEventArgs> Disconnected;

    bool IsConnected { get; }
    Task Connect(CancellationToken cancellationToken);
    void On(string eventName, Func<JsonElement, Task> handler);
    ValueTask Send(string eventName, JsonElement payload);
    Task Disconnect();
}