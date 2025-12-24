using Microsoft.Extensions.DependencyInjection;

namespace Light.SocketIoClient.Net.DependencyInjection;

internal static class ServicesCollectionExtensions
{
    public static void AddSocketClient(IServiceCollection serviceCollection)
    {
        serviceCollection.AddTransient<ISocketIoClient, Implementation.SocketIoClient>();
    }
}