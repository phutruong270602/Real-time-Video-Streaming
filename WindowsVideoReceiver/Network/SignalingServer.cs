using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WindowsVideoReceiver.Network;

internal sealed class SignalingServer : IDisposable
{
    private static readonly string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private CancellationTokenSource _shutdown = new();
    private readonly TcpListener _listener;
    private readonly object _clientLock = new();
    private readonly List<TcpClient> _clients = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private Task? _acceptLoop;

    private SignalingServer()
    {
        _listener = new TcpListener(IPAddress.Any, 8080);
    }

    public static SignalingServer Instance { get; } = new();

    public event EventHandler<string>? MessageReceived;

    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _acceptLoop is { IsCompleted: false };

    public Task StartAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        if (_shutdown.IsCancellationRequested)
        {
            _shutdown = new CancellationTokenSource();
        }

        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        StatusChanged?.Invoke(this, "Signaling server đang lắng nghe trên ws://localhost:8080");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        _shutdown.Cancel();
        _listener.Stop();

        lock (_clientLock)
        {
            foreach (var client in _clients)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // Ignore shutdown cleanup errors.
                }
            }

            _clients.Clear();
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop;
        }

        StatusChanged?.Invoke(this, "Signaling server đã dừng");
    }

    public async Task SendAsync(string message)
    {
        var payload = Utf8.GetBytes(message);

        await _sendLock.WaitAsync();
        try
        {
            var client = GetConnectedClients().FirstOrDefault();
            if (client is null)
            {
                throw new InvalidOperationException("Chưa có iPhone kết nối WebSocket signaling.");
            }

            try
            {
                var stream = client.GetStream();
                await SendWebSocketFrameAsync(stream, payload, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                client.Close();
                lock (_clientLock)
                {
                    _clients.Remove(client);
                }

                StatusChanged?.Invoke(this, "iPhone đã ngắt kết nối signaling");
                throw new IOException("Kết nối WebSocket với iPhone đã bị ngắt.", exception);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore shutdown errors during disposal.
        }

        _listener.Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                lock (_clientLock)
                {
                    foreach (var existing in _clients)
                    {
                        try
                        {
                            existing.Close();
                        }
                        catch
                        {
                            // Ignore close errors while replacing the active peer.
                        }
                    }

                    _clients.Clear();
                    _clients.Add(client);
                }

                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the server is stopping.
        }
        catch (SocketException)
        {
            // The listener is stopping; nothing to do here.
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var request = await ReadHttpRequestAsync(stream, cancellationToken);
            if (string.IsNullOrWhiteSpace(request))
            {
                client.Close();
                return;
            }

            if (!request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
            {
                await SendHttpResponseAsync(stream, "400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("Only websocket upgrade requests are accepted."));
                client.Close();
                return;
            }

            var key = ExtractWebSocketKey(request);
            if (string.IsNullOrWhiteSpace(key))
            {
                await SendHttpResponseAsync(stream, "400 Bad Request", "text/plain", Encoding.ASCII.GetBytes("Missing Sec-WebSocket-Key header."));
                client.Close();
                return;
            }

            var acceptValue = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
            var response = $"HTTP/1.1 101 Switching Protocols\r\n" +
                           "Upgrade: websocket\r\n" +
                           "Connection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {acceptValue}\r\n\r\n";
            var responseBytes = Utf8.GetBytes(response);
            await stream.WriteAsync(responseBytes, cancellationToken);
            StatusChanged?.Invoke(this, "iPhone đã kết nối đến signaling server");

            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                var frame = await ReadWebSocketFrameAsync(stream, cancellationToken);
                if (frame is null)
                {
                    break;
                }

                var text = Utf8.GetString(frame);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    MessageReceived?.Invoke(this, text);
                }
            }
        }
        catch (IOException)
        {
            // Client disconnected.
        }
        catch (OperationCanceledException)
        {
            // Server is shutting down.
        }
        finally
        {
            bool wasActiveClient;
            lock (_clientLock)
            {
                wasActiveClient = _clients.Remove(client) && _clients.Count == 0;
            }

            try
            {
                client.Close();
            }
            catch
            {
                // Ignore socket cleanup errors.
            }

            if (wasActiveClient)
            {
                StatusChanged?.Invoke(this, "iPhone đã ngắt kết nối signaling");
            }
        }
    }

    private static async Task<string> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();
        var bytesRead = 0;

        while (bytesRead < buffer.Length)
        {
            var chunk = new byte[1];
            var read = await stream.ReadAsync(chunk.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                break;
            }

            builder.Append((char)chunk[0]);
            if (builder.Length >= 4 && builder.ToString(builder.Length - 4, 4) == "\r\n\r\n")
            {
                break;
            }

            bytesRead += read;
        }

        return builder.ToString();
    }

    private static async Task SendHttpResponseAsync(NetworkStream stream, string statusCode, string contentType, byte[] payload)
    {
        var response = $"HTTP/1.1 {statusCode}\r\n" +
                       $"Content-Type: {contentType}\r\n" +
                       $"Content-Length: {payload.Length}\r\n" +
                       "Connection: close\r\n\r\n";
        var responseBytes = Utf8.GetBytes(response);
        await stream.WriteAsync(responseBytes, CancellationToken.None);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, CancellationToken.None);
        }
    }

    private static string ExtractWebSocketKey(string request)
    {
        var keyLine = request.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(keyLine))
        {
            return string.Empty;
        }

        return keyLine["Sec-WebSocket-Key:".Length..].Trim();
    }

    private static async Task<byte[]?> ReadWebSocketFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(0, 2), cancellationToken);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        var fin = (header[0] & 0x80) != 0;
        var opcode = header[0] & 0x0F;
        var masked = (header[1] & 0x80) != 0;
        var length = header[1] & 0x7F;

        if (opcode == 0x8)
        {
            return null;
        }

        var extendedLength = 0L;
        if (length == 126)
        {
            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes.AsMemory(0, 2), cancellationToken);
            extendedLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(lengthBytes, 0));
        }
        else if (length == 127)
        {
            var lengthBytes = new byte[8];
            await stream.ReadExactlyAsync(lengthBytes.AsMemory(0, 8), cancellationToken);
            extendedLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt64(lengthBytes, 0));
        }

        var totalLength = (int)(extendedLength == 0 ? length : extendedLength);
        var payload = new byte[totalLength];

        if (masked)
        {
            var mask = new byte[4];
            await stream.ReadExactlyAsync(mask.AsMemory(0, 4), cancellationToken);
            var bytesRead = 0;
            while (bytesRead < totalLength)
            {
                var readNow = await stream.ReadAsync(payload.AsMemory(bytesRead, totalLength - bytesRead), cancellationToken);
                if (readNow == 0)
                {
                    return null;
                }

                bytesRead += readNow;
            }

            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(payload[i] ^ mask[i % 4]);
            }
        }
        else
        {
            var bytesRead = 0;
            while (bytesRead < totalLength)
            {
                var readNow = await stream.ReadAsync(payload.AsMemory(bytesRead, totalLength - bytesRead), cancellationToken);
                if (readNow == 0)
                {
                    return null;
                }

                bytesRead += readNow;
            }
        }

        if (!fin)
        {
            return null;
        }

        return payload;
    }

    private static async Task SendWebSocketFrameAsync(NetworkStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var headerLength = payload.Length < 126 ? 2 : payload.Length < 65536 ? 4 : 10;
        var frame = new byte[headerLength + payload.Length];
        frame[0] = 0x81;

        if (payload.Length < 126)
        {
            frame[1] = (byte)payload.Length;
            Array.Copy(payload, 0, frame, 2, payload.Length);
        }
        else if (payload.Length < 65536)
        {
            frame[1] = 126;
            var len = (ushort)payload.Length;
            frame[2] = (byte)(len >> 8);
            frame[3] = (byte)len;
            Array.Copy(payload, 0, frame, 4, payload.Length);
        }
        else
        {
            frame[1] = 127;
            var len = (ulong)payload.Length;
            for (var i = 0; i < 8; i++)
            {
                frame[9 - i] = (byte)(len >> (8 * i));
            }

            Array.Copy(payload, 0, frame, 10, payload.Length);
        }

        await stream.WriteAsync(frame.AsMemory(0, frame.Length), cancellationToken);
    }

    private List<TcpClient> GetConnectedClients()
    {
        lock (_clientLock)
        {
            return _clients
                .Where(client => client.Connected)
                .ToList();
        }
    }
}
