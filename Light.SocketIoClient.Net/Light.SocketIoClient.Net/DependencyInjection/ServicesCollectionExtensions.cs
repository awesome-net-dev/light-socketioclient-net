using Microsoft.Extensions.DependencyInjection;

namespace Light.SocketIoClient.Net.DependencyInjection;

public static class ServicesCollectionExtensions
{
    public static void AddSocketClient(this IServiceCollection serviceCollection)
    {
        serviceCollection.AddTransient<ISocketIoClient, Implementation.SocketIoClient>();
    }
}