using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace TcpToComport;

internal sealed class WebSocketTcpBridge : IAsyncDisposable
{
    private const string WebSocketMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string AllowedBrowserOrigin = "https://modbus-mon.vercel.app";

    private readonly TcpListener _listener;
    private readonly int _port;
    private readonly Action<string, bool> _log;

    public WebSocketTcpBridge(int port, Action<string, bool> log)
    {
        _port = port;
        _log = log;
        _listener = new TcpListener(IPAddress.IPv6Any, port);
        _listener.Server.DualMode = true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        _log($"웹 모니터 브리지 대기: ws://localhost:{_port}", false);
        using var stopListener = cancellationToken.Register(_listener.Stop);
        var clients = new HashSet<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (client.Client.RemoteEndPoint is not IPEndPoint peer ||
                    !IPAddress.IsLoopback(peer.Address))
                {
                    client.Dispose();
                    continue;
                }

                var task = HandleClientAsync(client, cancellationToken);
                lock (clients)
                {
                    clients.Add(task);
                }

                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (clients)
                        {
                            clients.Remove(completed);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            _listener.Stop();
            Task[] remaining;
            lock (clients)
            {
                remaining = clients.ToArray();
            }

            await Task.WhenAll(remaining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task HandleClientAsync(TcpClient browserClient, CancellationToken cancellationToken)
    {
        using (browserClient)
        {
            browserClient.NoDelay = true;
            await using var browserStream = browserClient.GetStream();

            try
            {
                var request = await ReadHandshakeAsync(browserStream, cancellationToken);
                if (!IsAllowedOrigin(request.Headers.GetValueOrDefault("Origin")))
                {
                    await WriteHttpErrorAsync(browserStream, "403 Forbidden", cancellationToken);
                    return;
                }

                if (!TryReadLocalTarget(request.Path, out var host, out var targetPort, out var error))
                {
                    await WriteHttpErrorAsync(browserStream, "400 Bad Request", cancellationToken);
                    _log($"WebSocket 브리지 요청 거부: {error}", true);
                    return;
                }

                var accept = Convert.ToBase64String(
                    SHA1.HashData(Encoding.ASCII.GetBytes(request.WebSocketKey + WebSocketMagic)));
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                await browserStream.WriteAsync(response, cancellationToken);

                using var webSocket = WebSocket.CreateFromStream(
                    browserStream,
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));
                using var target = new TcpClient { NoDelay = true };

                try
                {
                    await target.ConnectAsync(host, targetPort, cancellationToken);
                }
                catch (Exception ex) when (ex is SocketException or IOException)
                {
                    await SendTextAsync(webSocket, $"TCP error: {ex.Message}", cancellationToken);
                    await CloseWebSocketAsync(webSocket, cancellationToken);
                    _log($"WebSocket 브리지 대상 연결 실패: {host}:{targetPort} - {ex.Message}", true);
                    return;
                }

                _log($"웹 모니터 연결: {host}:{targetPort}", false);
                await using var targetStream = target.GetStream();
                using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var wsToTcp = PipeWebSocketToTcpAsync(webSocket, targetStream, connectionCancellation.Token);
                var tcpToWs = PipeTcpToWebSocketAsync(targetStream, webSocket, connectionCancellation.Token);
                await Task.WhenAny(wsToTcp, tcpToWs);
                connectionCancellation.Cancel();
                await Task.WhenAll(wsToTcp, tcpToWs).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await CloseWebSocketAsync(webSocket, CancellationToken.None);
                _log("웹 모니터 연결 종료", false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or WebSocketException)
            {
                _log($"WebSocket 브리지 오류: {ex.Message}", true);
            }
        }
    }

    private static async Task PipeWebSocketToTcpAsync(
        WebSocket webSocket, NetworkStream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return;
            }

            if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
        }
    }

    private static async Task PipeTcpToWebSocketAsync(
        NetworkStream target, WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            var count = await target.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return;
            }

            await webSocket.SendAsync(
                new ArraySegment<byte>(buffer, 0, count),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
    }

    private static async Task<HandshakeRequest> ReadHandshakeAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        const int maximumHeaderBytes = 16 * 1024;
        var bytes = new List<byte>(1024);
        var tail = 0;

        while (bytes.Count < maximumHeaderBytes)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                throw new EndOfStreamException("WebSocket handshake ended early.");
            }

            bytes.Add(one[0]);
            tail = ((tail << 8) | one[0]) & unchecked((int)0xFFFFFFFF);
            if (bytes.Count >= 4 && tail == 0x0D0A0D0A)
            {
                break;
            }
        }

        if (bytes.Count >= maximumHeaderBytes)
        {
            throw new InvalidDataException("WebSocket handshake header is too large.");
        }

        var text = Encoding.ASCII.GetString(bytes.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 3 || requestParts[0] != "GET")
        {
            throw new InvalidDataException("Invalid WebSocket HTTP request.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var webSocketKey) ||
            !headers.GetValueOrDefault("Upgrade", "").Equals("websocket", StringComparison.OrdinalIgnoreCase) ||
            !headers.GetValueOrDefault("Connection", "").Contains("upgrade", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Missing WebSocket upgrade headers.");
        }

        return new HandshakeRequest(requestParts[1], webSocketKey, headers);
    }

    private static bool TryReadLocalTarget(
        string requestPath, out string host, out int port, out string error)
    {
        host = string.Empty;
        port = 502;
        error = string.Empty;

        if (!Uri.TryCreate("http://localhost" + requestPath, UriKind.Absolute, out var requestUri))
        {
            error = "잘못된 요청 URL";
            return false;
        }

        var query = requestUri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);

        host = query.GetValueOrDefault("host", string.Empty);
        if (host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            error = "대상 host는 이 PC(127.0.0.1)만 허용됩니다.";
            return false;
        }

        if (!int.TryParse(query.GetValueOrDefault("port", "502"), out port) || port is < 1 or > 65535)
        {
            error = "대상 port가 올바르지 않습니다.";
            return false;
        }

        return true;
    }

    private static bool IsAllowedOrigin(string? origin) =>
        string.IsNullOrEmpty(origin) ||
        origin.Equals(AllowedBrowserOrigin, StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ||
        origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteHttpErrorAsync(
        Stream stream, string status, CancellationToken cancellationToken)
    {
        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }

    private static Task SendTextAsync(
        WebSocket webSocket, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return webSocket.SendAsync(
            new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task CloseWebSocketAsync(
        WebSocket webSocket, CancellationToken cancellationToken)
    {
        if (webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await webSocket.CloseOutputAsync(
                    WebSocketCloseStatus.NormalClosure, "", cancellationToken);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private sealed record HandshakeRequest(
        string Path,
        string WebSocketKey,
        Dictionary<string, string> Headers);
}
