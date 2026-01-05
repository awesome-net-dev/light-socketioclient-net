using Light.SocketIoClient.Net.Options;

namespace Light.SocketIoClient.Net.Implementation;

public class SocketClientFactory
{
    public static ISocketIoClient Create(SocketClientOptions options)
    {
        return new SocketIoClient(options);
    }
}