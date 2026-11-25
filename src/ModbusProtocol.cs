using System.Buffers.Binary;
using System.IO;

namespace TcpToComport;

internal readonly record struct ModbusTcpRequest(
    ushort TransactionId,
    byte UnitId,
    byte[] Pdu);

internal static class ModbusProtocol
{
    public const int MinimumRtuFrameLength = 5;
    public const int MbapHeaderLength = 7;
    public const int MaxPduLength = 253;
    public const int MaxRtuFrameLength = 256;

    public static ModbusTcpRequest ParseTcpRequest(ReadOnlySpan<byte> header, byte[] pdu)
    {
        if (header.Length != MbapHeaderLength)
        {
            throw new InvalidDataException("The MBAP header must be 7 bytes.");
        }

        var protocolId = BinaryPrimitives.ReadUInt16BigEndian(header[2..4]);
        if (protocolId != 0)
        {
            throw new InvalidDataException("Only Modbus protocol ID 0 is supported.");
        }

        var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(header[4..6]);
        if (declaredLength != pdu.Length + 1 || pdu.Length is < 1 or > MaxPduLength)
        {
            throw new InvalidDataException("Invalid Modbus TCP length field.");
        }

        return new ModbusTcpRequest(
            BinaryPrimitives.ReadUInt16BigEndian(header[..2]),
            header[6],
            pdu);
    }

    public static byte[] BuildRtuRequest(byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (unitId is < 1 or > 247)
        {
            throw new InvalidDataException("RTU unit ID must be between 1 and 247.");
        }

        if (pdu.Length is < 1 or > MaxPduLength)
        {
            throw new InvalidDataException("Invalid Modbus PDU length.");
        }

        var frame = new byte[pdu.Length + 3];
        frame[0] = unitId;
        pdu.CopyTo(frame.AsSpan(1));
        var crc = ComputeCrc(frame.AsSpan(0, frame.Length - 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), crc);
        return frame;
    }

    public static byte[] BuildTcpResponse(
        ushort transactionId, byte unitId, ReadOnlySpan<byte> responsePdu)
    {
        if (responsePdu.Length is < 1 or > MaxPduLength)
        {
            throw new InvalidDataException("Invalid response PDU length.");
        }

        var response = new byte[MbapHeaderLength + responsePdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4, 2), checked((ushort)(responsePdu.Length + 1)));
        response[6] = unitId;
        responsePdu.CopyTo(response.AsSpan(MbapHeaderLength));
        return response;
    }

    public static byte[] BuildGatewayException(ModbusTcpRequest request, byte exceptionCode = 0x0B)
    {
        var function = request.Pdu.Length == 0 ? (byte)0x80 : (byte)(request.Pdu[0] | 0x80);
        return BuildTcpResponse(request.TransactionId, request.UnitId, [function, exceptionCode]);
    }

    public static byte[] ValidateAndExtractResponsePdu(
        ReadOnlySpan<byte> frame, byte expectedUnitId, byte expectedFunction)
    {
        if (frame.Length < MinimumRtuFrameLength)
        {
            throw new InvalidDataException(
                $"RTU response is too short ({frame.Length} byte(s): {Convert.ToHexString(frame)})." );
        }

        var expectedCrc = BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]);
        var actualCrc = ComputeCrc(frame[..^2]);
        if (actualCrc != expectedCrc)
        {
            throw new InvalidDataException("RTU response CRC is invalid.");
        }

        if (frame[0] != expectedUnitId)
        {
            throw new InvalidDataException(
                $"RTU response unit ID {frame[0]} does not match request unit ID {expectedUnitId}.");
        }

        if (frame[1] != expectedFunction && frame[1] != (byte)(expectedFunction | 0x80))
        {
            throw new InvalidDataException("RTU response function does not match the request.");
        }

        return frame[1..^2].ToArray();
    }

    public static int? TryGetExpectedRtuResponseLength(ReadOnlySpan<byte> partialFrame)
    {
        if (partialFrame.Length < 2)
        {
            return null;
        }

        var function = partialFrame[1];
        if ((function & 0x80) != 0)
        {
            return 5;
        }

        return function switch
        {
            0x01 or 0x02 or 0x03 or 0x04 or 0x0C or 0x11 or 0x14 or 0x15 or 0x17
                when partialFrame.Length >= 3 => partialFrame[2] + 5,
            0x18 when partialFrame.Length >= 4 =>
                BinaryPrimitives.ReadUInt16BigEndian(partialFrame[2..4]) + 6,
            0x05 or 0x06 or 0x08 or 0x0B or 0x0F or 0x10 => 8,
            0x16 => 10,
            0x07 => 5,
            _ => null
        };
    }

    public static ushort ComputeCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0
                    ? (ushort)((crc >> 1) ^ 0xA001)
                    : (ushort)(crc >> 1);
            }
        }

        return crc;
    }
}

internal static class ProtocolSelfTest
{
    public static void Run()
    {
        var pdu = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x0A };
        var rtu = ModbusProtocol.BuildRtuRequest(1, pdu);
        Assert(rtu.SequenceEqual(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD }),
            "Known Modbus CRC vector failed.");

        var header = new byte[] { 0x12, 0x34, 0x00, 0x00, 0x00, 0x06, 0x01 };
        var request = ModbusProtocol.ParseTcpRequest(header, pdu);
        Assert(request.TransactionId == 0x1234 && request.UnitId == 1,
            "MBAP request parsing failed.");

        var responseFrame = new byte[] { 0x01, 0x03, 0x02, 0x00, 0x2A, 0x39, 0x9B };
        var responsePdu = ModbusProtocol.ValidateAndExtractResponsePdu(responseFrame, 1, 3);
        Assert(responsePdu.SequenceEqual(new byte[] { 0x03, 0x02, 0x00, 0x2A }),
            "RTU response conversion failed.");

        var tcpResponse = ModbusProtocol.BuildTcpResponse(0x1234, 1, responsePdu);
        Assert(tcpResponse.SequenceEqual(new byte[]
        {
            0x12, 0x34, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x00, 0x2A
        }), "TCP response framing failed.");

        Assert(ModbusProtocol.TryGetExpectedRtuResponseLength(responseFrame[..3]) == 7,
            "RTU response length detection failed.");
        Assert(ModbusProtocol.TryGetExpectedRtuResponseLength(new byte[] { 1, 0x16 }) == 10,
            "Mask-write response length detection failed.");
        Assert(ModbusProtocol.TryGetExpectedRtuResponseLength(new byte[] { 1, 0x18, 0, 6 }) == 12,
            "FIFO response length detection failed.");

        var badCrc = responseFrame.ToArray();
        badCrc[^1] ^= 0x01;
        try
        {
            _ = ModbusProtocol.ValidateAndExtractResponsePdu(badCrc, 1, 3);
            throw new InvalidOperationException("Bad CRC was accepted.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
