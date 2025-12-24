namespace Light.SocketIoClient.Demo;

public interface ISocketClientsSentinel : IHostedService
{
    void Reconnect(SocketClientWrapper client);
}