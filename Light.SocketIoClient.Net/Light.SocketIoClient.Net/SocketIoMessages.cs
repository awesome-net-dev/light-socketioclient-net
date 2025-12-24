namespace Light.SocketIoClient.Net;

internal static class SocketIoMessages
{
    public static readonly int Session = 0;
    public static readonly int Ping = 50;
    public static readonly int Pong = 51;
    public static readonly int Open = 48;
    public static readonly int Connect = 40;
    public static readonly int Disconnect = 41;
    public static readonly int Event = 42;
}