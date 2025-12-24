using Light.SocketIoClient.Net.Options;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Light.SocketIoClient.Net.Implementation;

internal sealed class SocketIoClient : ISocketIoClient
{
    private readonly SocketClientOptions _options;
    private const string AuthHeaderName = "Authorization";

    private readonly Dictionary<string, Func<JsonElement, Task>> _receiveHandlers = new();
    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: 64 * 1024,
        resumeWriterThreshold: 32 * 1024,
        useSynchronizationContext: false
    ));

    private readonly Channel<ReadOnlyMemory<byte>> _sendChannel;
    private ClientWebSocket? _client;
    private Task? _receiveLoop;
    private Task? _parseLoop;
    private Task? _sendLoop;

    public SocketIoClient(IOptions<SocketClientOptions> clientOptions)
    {
        _options = clientOptions.Value;
        _sendChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(_options.SendMemoryBufferCapacity)
        {
            SingleReader = true, 
            SingleWriter = false, 
            AllowSynchronousContinuations = false, 
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    #region Implementation of ISocketIoClient

    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    public bool IsConnected => _client?.State == WebSocketState.Open;

    public async Task Connect(CancellationToken cancellationToken)
    {
        if (_client is not null)
            DisposeClient();

        _client = new ClientWebSocket();
        _client.Options.CollectHttpResponseDetails = true;

        if (!string.IsNullOrWhiteSpace(_options.Auth))
            _client.Options.SetRequestHeader(AuthHeaderName, _options.Auth);

        await _client.ConnectAsync(_options.BroadcastUri, cancellationToken);

        if (_client.State == WebSocketState.Open && _client.HttpStatusCode == HttpStatusCode.SwitchingProtocols)
        {
            _receiveLoop = Task.Run(() => ReceiveLoop(_client, cancellationToken), cancellationToken).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Disconnected?.Invoke(this, new DisconnectedEventArgs(DisconnectReason.ReceiveFail, t.Exception));
                });
            _parseLoop = Task.Run(() => ParseLoop(cancellationToken), cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Disconnected?.Invoke(this, new DisconnectedEventArgs(DisconnectReason.ParseFail, t.Exception));
            });
            _sendLoop = Task.Run(() => SendLoop(_client, cancellationToken), cancellationToken).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Disconnected?.Invoke(this, new DisconnectedEventArgs(DisconnectReason.SendFail, t.Exception));
            });
        }
        else
        {
            throw new HttpListenerException(101, $"Expected '101' but received '{_client.HttpStatusCode:D}'");
        }

        Connected?.Invoke(this, EventArgs.Empty);
    }

    public void On(string eventName, Func<JsonElement, Task> handler)
    {
        _receiveHandlers[eventName] = handler;
    }

    public ValueTask Send(string eventName, JsonElement payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new object[] { eventName, payload });
        var msgType = "42"u8;
        var message = new byte[msgType.Length + payloadBytes.Length];
        msgType.CopyTo(message);
        payloadBytes.CopyTo(message, msgType.Length);

        return _sendChannel.Writer.WriteAsync(message);
    }

    public async Task Disconnect()
    {
        var client = _client;
        if (client is { State: WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.Connecting })
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);

        DisposeClient();
        Disconnected?.Invoke(this, new DisconnectedEventArgs(DisconnectReason.User));
    }

    #endregion // Implementation of ISocketIoClient

    private void DisposeClient()
    {
        var client = _client;
        if (client == null)
            return;

        client.Abort();

        try
        {
            client.Dispose();
        }
        catch
        {
            // ignored
        }
        finally
        {
            _client = null;
        }

        _receiveLoop?.Dispose();
        _parseLoop?.Dispose();
        _sendLoop?.Dispose();
    }

    private async Task SendLoop(ClientWebSocket socket, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            if (!await _sendChannel.Reader.WaitToReadAsync(ct))
                break;

            while (_sendChannel.Reader.TryRead(out var message))
                await socket.SendAsync(message, WebSocketMessageType.Text, true, ct);
        }

        _sendChannel.Writer.TryComplete();
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
    {
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var memory = _pipe.Writer.GetMemory(_options.ReceiveMemoryBufferSizeHint);
            var result = await socket.ReceiveAsync(memory, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            _pipe.Writer.Advance(result.Count);

            var flush = await _pipe.Writer.FlushAsync(ct);
            if (flush.IsCompleted)
                break;
        }

        await _pipe.Writer.CompleteAsync();
    }

    private async Task ParseLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await _pipe.Reader.ReadAsync(ct);
            var buffer = result.Buffer;

            while (TryReadFrame(ref buffer, out var frame))
                if (TryReadMessage(frame, out var msgType, out var payload))
                    await HandleIncomingMessage(msgType, payload, ct);

            _pipe.Reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                break;
        }

        await _pipe.Reader.CompleteAsync();
    }

    private async Task HandleIncomingMessage(int msgType, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (msgType == 50) // ping
        {
            await SendPong(ct);
            return;
        }

        if (msgType == 51 // pong
            || msgType == 48 // opened
            || msgType == 40 // connect
            || msgType == 41) // disconnect
            return;

        if (msgType != 42) // event
            return;

        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        if (TryParseEvent(root, out var eventName, out var element))
            await DispatchEvent(eventName, element);
    }

    private ValueTask SendPong(CancellationToken ct)
    {
        return _sendChannel.Writer.WriteAsync(new ReadOnlyMemory<byte>([51]), ct);
    }

    private Task DispatchEvent(string eventName, JsonElement? element)
    {
        if (!_receiveHandlers.TryGetValue(eventName, out var handler))
            return Task.CompletedTask;

        var data = element!.Value;
        return handler.Invoke(data);
    }

    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> frame)
    {
        if (buffer.IsEmpty)
        {
            frame = default;
            return false;
        }

        frame = buffer.IsSingleSegment ? buffer.First : buffer.ToArray();
        buffer = buffer.Slice(buffer.End);
        return true;
    }

    private static bool TryReadMessage(ReadOnlyMemory<byte> message, out int msgType, out ReadOnlyMemory<byte> payload)
    {
        if (message.IsEmpty)
        {
            payload = default;
            msgType = default;
            return false;
        }

        var prefixLength = message.Length >= 2 ? 2 : 1;
        var msgTypeBytes = message.Span;
        if (prefixLength >= 2 && msgTypeBytes[1] >= (byte)'0' && msgTypeBytes[1] <= (byte)'9')
        {
            msgType = (msgTypeBytes[0] - (byte)'0') * 10 + (msgTypeBytes[1] - (byte)'0');
        }
        else
        {
            msgType = msgTypeBytes[0] - (byte)'0';
        }

        if (message.Length <= prefixLength)
            payload = default;
        else
            payload = message[prefixLength..];
        return true;
    }

    private static bool TryParseEvent(JsonElement root, out string eventName, out JsonElement? data)
    {
        eventName = null!;
        data = null;

        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 1)
            return false;

        var enumerator = root.EnumerateArray();

        if (!enumerator.MoveNext() || enumerator.Current.ValueKind != JsonValueKind.String)
            return false;

        eventName = enumerator.Current.GetString()!;

        if (enumerator.MoveNext())
            data = enumerator.Current;

        return true;
    }
}