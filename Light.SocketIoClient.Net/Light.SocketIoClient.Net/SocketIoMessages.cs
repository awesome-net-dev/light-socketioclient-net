namespace Light.SocketIoClient.Net;

internal static class SocketIoMessages
{
    public static readonly int Open = 0;
    public static readonly int Ping = 2;
    public static readonly int Pong = 3;
    public static readonly int Connect = 40;
    public static readonly int Disconnect = 41;
    public static readonly int Event = 42;
}