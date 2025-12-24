namespace Light.SocketIoClient.Net.Options;

public sealed class SocketClientOptions
{
    public required Uri BroadcastUri { get; set; }
    public string? Auth { get; set; }

    public int ReceiveMemoryBufferSizeHint { get; set; } = 8128;
    public int SendMemoryBufferCapacity { get; set; } = 1000;
}