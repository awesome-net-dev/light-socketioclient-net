using System.Collections.Specialized;

namespace Light.SocketIoClient.Net.Options;

public sealed record SocketClientOptions
{
    public required Uri BroadcastUri { get; init; }
    public NameValueCollection Headers { get; init; } = new();

    public int ReceiveMemoryBufferSizeHint { get; init; } = 8128;
    public int SendMemoryBufferCapacity { get; init; } = 1000;
    
    public int PauseWriterThreshold { get; init; } = 64 * 1024;
    public int ResumeWriterThreshold { get; init; } = 32 * 1024;
}