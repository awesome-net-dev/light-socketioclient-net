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
    private readonly Dictionary<string, Func<JsonElement, Task>> _receiveHandlers = new();
    private readonly Channel<ReadOnlyMemory<byte>> _sendChannel;
    private readonly Pipe _pipe;
    private readonly SocketClientOptions _options;

    private ClientWebSocket? _client;
    private Task? _receiveLoop;
    private Task? _parseLoop;
    private Task? _sendLoop;

    public SocketIoClient(IOptions<SocketClientOptions> options)
        : this(options.Value)
    {

    }

    internal SocketIoClient(SocketClientOptions clientOptions)
    {
        _options = clientOptions;
        _sendChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(_options.SendMemoryBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: _options.PauseWriterThreshold,
            resumeWriterThreshold: _options.ResumeWriterThreshold,
            useSynchronizationContext: false
        ));
    }

    #region Implementation of ISocketIoClient

    public event EventHandler? Connected;
    public event EventHandler<DisconnectedEventArgs>? Disconnected;

    public SocketClientOptions Options => _options;

    public bool IsConnected => _client?.State == WebSocketState.Open;

    public async Task Connect(CancellationToken cancellationToken)
    {
        if (_client is not null)
            DisposeClient();

        _client = new ClientWebSocket();
        _client.Options.CollectHttpResponseDetails = true;

        if (_options.Headers.HasKeys())
        {
            foreach (var headerName in _options.Headers.AllKeys)
            {
                if (!string.IsNullOrWhiteSpace(headerName))
                    _client.Options.SetRequestHeader(headerName, _options.Headers[headerName]);
            }
        }
        
        try
        {
            await _client.ConnectAsync(_options.BroadcastUri, cancellationToken);
        }
        catch
        {
            DisposeClient();
            throw;
        }

        if (_client.State == WebSocketState.Open && _client.HttpStatusCode == HttpStatusCode.SwitchingProtocols)
        {
            _receiveLoop = Task.Run(() => ReceiveLoop(_client, cancellationToken), cancellationToken)
                .ContinueWith(t => HandleFailedTask(t, DisconnectReason.ReceiveFail), CancellationToken.None);
            _parseLoop = Task.Run(() => ParseLoop(cancellationToken), cancellationToken)
                .ContinueWith(t => HandleFailedTask(t, DisconnectReason.ParseFail), CancellationToken.None);
            _sendLoop = Task.Run(() => SendLoop(_client, cancellationToken), cancellationToken)
                .ContinueWith(t => HandleFailedTask(t, DisconnectReason.SendFail), CancellationToken.None);
        }
        else
        {
            throw new HttpListenerException(101, $"Expected '101' but received '{_client.HttpStatusCode:D}'");
        }
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
        FireDisconnectedEvent(DisconnectReason.User);
    }

    #endregion // Implementation of ISocketIoClient

    #region Implementation of IDisposable

    public void Dispose()
    {
        _receiveHandlers.Clear();
        _sendChannel.Writer.TryComplete();
        _pipe.Writer.Complete();
        _pipe.Reader.Complete();

        DisposeClient();
    }

    #endregion // Implementation of IDisposable

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

        if (_receiveLoop?.IsCompleted == true)
            _receiveLoop.Dispose();

        if (_parseLoop?.IsCompleted == true)
            _parseLoop.Dispose();

        if (_sendLoop?.IsCompleted == true)
            _sendLoop.Dispose();
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
        var msgBoundary = new ReadOnlyMemory<byte>([0x00]);
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var memory = _pipe.Writer.GetMemory(_options.ReceiveMemoryBufferSizeHint);
            var result = await socket.ReceiveAsync(memory, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            _pipe.Writer.Advance(result.Count);

            if (!result.EndOfMessage)
                continue;

            await _pipe.Writer.WriteAsync(msgBoundary, ct);

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
        if (msgType == SocketIoMessages.Open)
            return;

        if (msgType == SocketIoMessages.Ping)
        {
            await SendPong(ct);
            return;
        }

        if (msgType == SocketIoMessages.Pong)
            return;

        if (msgType == SocketIoMessages.Connect)
        {
            Connected?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (msgType == SocketIoMessages.Disconnect)
        {
            FireDisconnectedEvent(DisconnectReason.Server);
            return;
        }

        if (msgType != SocketIoMessages.Event)
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

    private void HandleFailedTask(Task t, DisconnectReason reason)
    {
        if (!t.IsFaulted)
            return;

        DisposeClient();
        FireDisconnectedEvent(reason, t.Exception);
    }

    private void FireDisconnectedEvent(DisconnectReason reason, Exception? ex = null, string? description = null)
    {
        Disconnected?.Invoke(this, new DisconnectedEventArgs(reason, ex, description));
    }

    private static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out ReadOnlyMemory<byte> frame)
    {
        // messages end with 0x00
        var position = buffer.PositionOf((byte)0x00);
        if (position == null)
        {
            frame = default;
            return false;
        }

        frame = buffer.Slice(0, position.Value).ToArray();
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
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

        System.Diagnostics.Debug.WriteLine($"new msg: {msgType}, {System.Text.Encoding.UTF8.GetString(payload.ToArray())}");
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