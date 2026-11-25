using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using TcpToComport;

if (args.Contains("--probe-large-read", StringComparer.OrdinalIgnoreCase))
{
    using var probeClient = new TcpClient();
    using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await probeClient.ConnectAsync(IPAddress.Loopback, 502, probeTimeout.Token);
    await using var probeStream = probeClient.GetStream();
    var probeRequest = new byte[]
    {
        0x7F, 0xFE, 0x00, 0x00, 0x00, 0x06, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x64
    };
    await probeStream.WriteAsync(probeRequest, probeTimeout.Token);
    var probeResponse = new byte[260];
    var probeCount = await probeStream.ReadAsync(probeResponse, probeTimeout.Token);
    Console.WriteLine($"COUNT={probeCount} HEX={Convert.ToHexString(probeResponse.AsSpan(0, probeCount))}");
    return;
}

var targetListener = new TcpListener(IPAddress.Loopback, 0);
targetListener.Start();
var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
var bridgePort = ReservePort();
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await using var bridge = new WebSocketTcpBridge(bridgePort, (_, _) => { });

var echoTask = EchoOnceAsync(targetListener, cancellation.Token);
var bridgeTask = bridge.RunAsync(cancellation.Token);

try
{
    using var client = new ClientWebSocket();
    client.Options.SetRequestHeader("Origin", "https://modbus-mon.vercel.app");
    await client.ConnectAsync(
        new Uri($"ws://localhost:{bridgePort}/?host=127.0.0.1&port={targetPort}"),
        cancellation.Token);

    var request = new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x0A, 0x03, 0x00, 0x00, 0x00, 0x01 };
    await client.SendAsync(request, WebSocketMessageType.Binary, true, cancellation.Token);

    var response = new byte[request.Length];
    var result = await client.ReceiveAsync(response, cancellation.Token);
    if (result.MessageType != WebSocketMessageType.Binary ||
        result.Count != request.Length ||
        !response.SequenceEqual(request))
    {
        throw new InvalidOperationException("WebSocket bridge changed or lost the Modbus TCP frame.");
    }

    client.Abort();
    await echoTask;
    await VerifyChunkedRtuResponseAsync();
    Console.WriteLine("WebSocket bridge smoke test passed.");
}
finally
{
    cancellation.Cancel();
    targetListener.Stop();
    await bridge.DisposeAsync();
    await bridgeTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}

static int ReservePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    return port;
}

static async Task EchoOnceAsync(TcpListener listener, CancellationToken cancellationToken)
{
    using var client = await listener.AcceptTcpClientAsync(cancellationToken);
    await using var stream = client.GetStream();
    var buffer = new byte[256];
    var count = await stream.ReadAsync(buffer, cancellationToken);
    await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
}

static async Task VerifyChunkedRtuResponseAsync()
{
    var pdu = new byte[] { 0x03, 0x04, 0x00, 0x2A, 0x00, 0x2B };
    var frame = new byte[pdu.Length + 3];
    frame[0] = 0x0A;
    pdu.CopyTo(frame, 1);
    var crc = ModbusProtocol.ComputeCrc(frame.AsSpan(0, frame.Length - 2));
    frame[^2] = (byte)(crc & 0xFF);
    frame[^1] = (byte)(crc >> 8);

    await using var serial = new ChunkedSerialPort(frame);
    var options = new GatewayOptions(
        "COM3", 115200, SerialParity.None, 8, SerialStopBits.One,
        IPAddress.Loopback, 1502, 18765, 100, 1);
    await using var gateway = new ModbusGateway(options, serial, (_, _) => { });
    var request = ModbusProtocol.BuildRtuRequest(0x0A, new byte[] { 0x03, 0x00, 0x00, 0x00, 0x02 });
    var received = gateway.ExchangeRtu(request, CancellationToken.None);
    if (!received.SequenceEqual(frame))
    {
        throw new InvalidOperationException("Chunked RTU response was truncated at the USB read gap.");
    }
}

file sealed class ChunkedSerialPort(byte[] response) : ISerialPort
{
    private int _step;

    public void DiscardInput() => _step = 0;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_step == 0)
        {
            _step++;
            buffer[offset] = response[0];
            return 1;
        }

        if (_step == 1)
        {
            _step++;
            Thread.Sleep(3);
            return 0;
        }

        if (_step == 2)
        {
            _step++;
            var remaining = response.Length - 1;
            Array.Copy(response, 1, buffer, offset, remaining);
            return remaining;
        }

        return 0;
    }

    public void Write(ReadOnlySpan<byte> buffer)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
