using System.Globalization;
using System.Net;

namespace TcpToComport;

internal enum SerialParity : byte
{
    None = 0,
    Odd = 1,
    Even = 2,
    Mark = 3,
    Space = 4
}

internal enum SerialStopBits : byte
{
    One = 0,
    OnePointFive = 1,
    Two = 2
}

internal sealed record GatewayOptions(
    string ComPort,
    int BaudRate,
    SerialParity Parity,
    int DataBits,
    SerialStopBits StopBits,
    IPAddress ListenAddress,
    int ListenPort,
    int WebSocketPort,
    int ResponseTimeoutMs,
    int? FrameGapMs)
{
    public static GatewayOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for '{key}'.");
            }

            if (!values.TryAdd(key, args[++i]))
            {
                throw new ArgumentException($"Option '{key}' was specified more than once.");
            }
        }

        var comPort = Get(values, "--com", null);
        if (string.IsNullOrWhiteSpace(comPort))
        {
            throw new ArgumentException("--com is required, for example --com COM3.");
        }

        if (!comPort.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(comPort.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var comNumber) ||
            comNumber <= 0)
        {
            throw new ArgumentException("--com must be a Windows COM port such as COM3.");
        }

        var baudRate = GetInt(values, "--baud", 9600, 1, 4_000_000);
        var dataBits = GetInt(values, "--data-bits", 8, 5, 8);
        var listenPort = GetInt(values, "--listen-port", 502, 1, 65535);
        var webSocketPort = GetInt(values, "--websocket-port", 8765, 1, 65535);
        var timeout = GetInt(values, "--response-timeout-ms", 1000, 50, 60_000);

        var parityText = Get(values, "--parity", "None")!;
        if (!Enum.TryParse<SerialParity>(parityText, true, out var parity))
        {
            throw new ArgumentException("--parity must be None, Odd, Even, Mark, or Space.");
        }

        var stopBitsText = Get(values, "--stop-bits", "One")!;
        stopBitsText = stopBitsText switch
        {
            "1" => "One",
            "1.5" => "OnePointFive",
            "2" => "Two",
            _ => stopBitsText
        };
        if (!Enum.TryParse<SerialStopBits>(stopBitsText, true, out var stopBits))
        {
            throw new ArgumentException("--stop-bits must be 1, 1.5, or 2.");
        }

        var listenText = Get(values, "--listen-address", "0.0.0.0")!;
        if (!IPAddress.TryParse(listenText, out var listenAddress))
        {
            throw new ArgumentException("--listen-address must be an IPv4 or IPv6 address.");
        }

        int? frameGap = null;
        if (values.ContainsKey("--frame-gap-ms"))
        {
            frameGap = GetInt(values, "--frame-gap-ms", 2, 1, 1000);
        }

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--com", "--baud", "--parity", "--data-bits", "--stop-bits",
            "--listen-address", "--listen-port", "--response-timeout-ms", "--frame-gap-ms"
            , "--websocket-port"
        };
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
        {
            throw new ArgumentException($"Unknown option '{unknown}'.");
        }

        return new GatewayOptions(
            comPort.ToUpperInvariant(), baudRate, parity, dataBits, stopBits,
            listenAddress, listenPort, webSocketPort, timeout, frameGap);
    }

    public int EffectiveFrameGapMs => FrameGapMs ??
        (BaudRate <= 19_200
            ? Math.Max(2, (int)Math.Ceiling(3.5 * 11 * 1000 / BaudRate))
            : 2);

    public static void PrintHelp()
    {
        Console.WriteLine("""
TcpToComport - Modbus TCP to Modbus RTU gateway for Windows

Usage:
  TcpToComport --com COM3 [options]

Options:
  --com COM3                    Serial port (required)
  --baud 9600                   Baud rate (default: 9600)
  --parity None                 None, Odd, Even, Mark, Space (default: None)
  --data-bits 8                 5 to 8 (default: 8)
  --stop-bits 1                 1, 1.5, or 2 (default: 1)
  --listen-address 0.0.0.0      Address to bind (default: all interfaces)
  --listen-port 502             Modbus TCP port (default: 502)
  --websocket-port 8765         Local browser bridge port (default: 8765)
  --response-timeout-ms 1000    RTU response timeout (default: 1000)
  --frame-gap-ms 2              RTU end-of-frame silence; auto when omitted
  --self-test                   Run protocol checks without opening a COM port
  --help                        Show this help

Example:
  TcpToComport --com COM3 --baud 115200 --parity None --data-bits 8 --stop-bits 1
""");
    }

    private static string? Get(Dictionary<string, string> values, string key, string? fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static int GetInt(
        Dictionary<string, string> values, string key, int fallback, int minimum, int maximum)
    {
        var text = Get(values, key, fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum || value > maximum)
        {
            throw new ArgumentException($"{key} must be between {minimum} and {maximum}.");
        }

        return value;
    }
}
