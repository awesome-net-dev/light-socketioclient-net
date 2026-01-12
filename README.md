# Light.SocketIoClient.Net
.NetCore 8 lightweight client using socketIo protocol eio=4

Usage:
```text
var options = new SocketClientOptions
{
    BroadcastUri = "wss://your-domain.com/ws,
    Headers = { { "Authorization", "Bearer ..." } }
};

_client = SocketClientFactory.Create(options);
_client.On("your-event-name", async Task (response) => Console.WriteLine(response.ToString()));

await _client.Connect(cancellationToken);
```
