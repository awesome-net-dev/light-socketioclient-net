namespace Light.SocketIoClient.Net;

public enum DisconnectReason
{
    ReceiveFail,
    ParseFail,
    SendFail,
    User,
    Server
}