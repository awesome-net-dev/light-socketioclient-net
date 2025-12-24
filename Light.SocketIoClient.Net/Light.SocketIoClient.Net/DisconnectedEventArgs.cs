namespace Light.SocketIoClient.Net;

public sealed record DisconnectedEventArgs(DisconnectReason Reason, Exception? Exception = null, string? Description = null);