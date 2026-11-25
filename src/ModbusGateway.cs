using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace TcpToComport;

internal sealed class ModbusGateway : IAsyncDisposable
{
    private readonly GatewayOptions _options;
    private readonly ISerialPort _serial;
    private readonly TcpListener _listener;
    private readonly SemaphoreSlim _serialLock = new(1, 1);
    private readonly Action<string, bool> _log;
    private readonly WebSocketTcpBridge _webSocketBridge;

    public ModbusGateway(GatewayOptions options, ISerialPort serial, Action<string, bool> log)
    {
        _options = options;
        _serial = serial;
        _log = log;
        _listener = new TcpListener(options.ListenAddress, options.ListenPort);
        _webSocketBridge = new WebSocketTcpBridge(options.WebSocketPort, log);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        using var stopListener = cancellationToken.Register(_listener.Stop);
        var webSocketBridgeTask = _webSocketBridge.RunAsync(cancellationToken);

        var clients = new HashSet<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    var acceptTask = _listener.AcceptTcpClientAsync(cancellationToken).AsTask();
                    var completed = await Task.WhenAny(acceptTask, webSocketBridgeTask);
                    if (completed == webSocketBridgeTask)
                    {
                        await webSocketBridgeTask;
                        throw new IOException("WebSocket 브리지가 예기치 않게 중지되었습니다.");
                    }
                    client = await acceptTask;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
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
            await _webSocketBridge.DisposeAsync();
            await webSocketBridgeTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Task[] remaining;
            lock (clients)
            {
                remaining = clients.ToArray();
            }

            await Task.WhenAll(remaining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            client.NoDelay = true;
            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            _log($"클라이언트 연결: {endpoint}", false);

            try
            {
                await using var stream = client.GetStream();
                while (!cancellationToken.IsCancellationRequested)
                {
                    var header = new byte[ModbusProtocol.MbapHeaderLength];
                    if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken))
                    {
                        break;
                    }

                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                    if (length is < 2 or > ModbusProtocol.MaxPduLength + 1)
                    {
                        throw new InvalidDataException($"Invalid MBAP length {length}.");
                    }

                    var pdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(pdu, cancellationToken);
                    var request = ModbusProtocol.ParseTcpRequest(header, pdu);
                    var response = await ProcessRequestAsync(request, endpoint, cancellationToken);
                    await stream.WriteAsync(response, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException ex)
            {
                _log($"{endpoint}: {ex.Message}", true);
            }
            catch (InvalidDataException ex)
            {
                _log($"{endpoint}: {ex.Message}; 연결을 종료했습니다.", true);
            }
            finally
            {
                _log($"클라이언트 연결 종료: {endpoint}", false);
            }
        }
    }

    private async Task<byte[]> ProcessRequestAsync(
        ModbusTcpRequest request, string endpoint, CancellationToken cancellationToken)
    {
        if (request.UnitId is < 1 or > 247)
        {
            _log($"{endpoint}: 지원하지 않는 RTU Unit ID {request.UnitId}.", true);
            return ModbusProtocol.BuildGatewayException(request);
        }

        await _serialLock.WaitAsync(cancellationToken);
        try
        {
            var rtuRequest = ModbusProtocol.BuildRtuRequest(request.UnitId, request.Pdu);
            var rtuResponse = await Task.Run(
                () => ExchangeRtu(rtuRequest, cancellationToken),
                CancellationToken.None);
            var responsePdu = ModbusProtocol.ValidateAndExtractResponsePdu(
                rtuResponse, request.UnitId, request.Pdu[0]);

            if ((responsePdu[0] & 0x80) != 0)
            {
                var exceptionCode = responsePdu.Length > 1 ? responsePdu[1] : (byte)0;
                _log(
                    $"{endpoint}: TID={request.TransactionId}, Unit={request.UnitId}, " +
                    $"Function=0x{request.Pdu[0]:X2}, RTU 예외=0x{exceptionCode:X2}", true);
            }
            else
            {
                _log(
                    $"{endpoint}: TID={request.TransactionId}, Unit={request.UnitId}, " +
                    $"Function=0x{request.Pdu[0]:X2}, 정상", false);
            }
            return ModbusProtocol.BuildTcpResponse(request.TransactionId, request.UnitId, responsePdu);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException)
        {
            _log(
                $"{endpoint}: TID={request.TransactionId}, Unit={request.UnitId}, 오류: {ex.Message}",
                true);
            return ModbusProtocol.BuildGatewayException(request);
        }
        finally
        {
            _serialLock.Release();
        }
    }

    internal byte[] ExchangeRtu(byte[] request, CancellationToken cancellationToken)
    {
        _serial.DiscardInput();
        _serial.Write(request);

        var response = new byte[ModbusProtocol.MaxRtuFrameLength];
        var count = 0;
        var total = Stopwatch.StartNew();
        var sinceLastByte = Stopwatch.StartNew();
        int? expectedLength = null;

        while (total.ElapsedMilliseconds < _options.ResponseTimeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = _serial.Read(response, count, response.Length - count);

            if (read > 0)
            {
                count += read;
                sinceLastByte.Restart();

                expectedLength = ModbusProtocol.TryGetExpectedRtuResponseLength(response.AsSpan(0, count));
                if (expectedLength is not null)
                {
                    if (count == expectedLength.Value)
                    {
                        return response[..count];
                    }

                    if (count > expectedLength.Value)
                    {
                        throw new InvalidDataException("RTU response contains trailing bytes.");
                    }
                }

                if (count == response.Length)
                {
                    throw new InvalidDataException("RTU response exceeds 256 bytes.");
                }
            }
            else if (count >= ModbusProtocol.MinimumRtuFrameLength && expectedLength is null &&
                     sinceLastByte.ElapsedMilliseconds >= _options.EffectiveFrameGapMs)
            {
                return response[..count];
            }
        }

        throw new TimeoutException(
            count == 0
                ? $"No RTU response within {_options.ResponseTimeoutMs} ms."
                : "RTU response did not complete before the timeout.");
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("Connection ended in the middle of an MBAP header.");
            }

            offset += read;
        }

        return true;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _ = _webSocketBridge.DisposeAsync();
        _serialLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
