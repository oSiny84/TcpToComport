using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace TcpToComport;

internal interface ISerialPort : IAsyncDisposable
{
    void DiscardInput();
    int Read(byte[] buffer, int offset, int count);
    void Write(ReadOnlySpan<byte> buffer);
}

internal sealed class Win32SerialPort : ISerialPort
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PurgeRxClear = 0x0008;
    private const uint PurgeTxClear = 0x0004;
    private const uint FBinary = 0x00000001;
    private const uint FParity = 0x00000002;

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;

    public Win32SerialPort(GatewayOptions options)
    {
        var deviceName = $@"\\.\{options.ComPort}";
        _handle = CreateFile(
            deviceName,
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            throw new IOException(
                $"Could not open {options.ComPort}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        try
        {
            Configure(options);
            _stream = new FileStream(_handle, FileAccess.ReadWrite, 256, isAsync: false);
            DiscardInput();
        }
        catch
        {
            _handle.Dispose();
            throw;
        }
    }

    public void DiscardInput()
    {
        if (!PurgeComm(_handle, PurgeRxClear | PurgeTxClear))
        {
            throw LastIoException("Could not clear the serial buffers");
        }
    }

    public int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

    public void Write(ReadOnlySpan<byte> buffer)
    {
        _stream.Write(buffer);
        _stream.Flush();
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Configure(GatewayOptions options)
    {
        var dcb = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(_handle, ref dcb))
        {
            throw LastIoException("Could not read serial port settings");
        }

        dcb.BaudRate = checked((uint)options.BaudRate);
        dcb.Flags = FBinary | (options.Parity == SerialParity.None ? 0u : FParity);
        dcb.ByteSize = checked((byte)options.DataBits);
        dcb.Parity = (byte)options.Parity;
        dcb.StopBits = (byte)options.StopBits;

        if (!SetCommState(_handle, ref dcb))
        {
            throw LastIoException("Could not apply serial port settings");
        }

        var readSliceMs = checked((uint)Math.Clamp(options.EffectiveFrameGapMs, 1, 50));
        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = readSliceMs,
            ReadTotalTimeoutMultiplier = 0,
            ReadTotalTimeoutConstant = readSliceMs,
            WriteTotalTimeoutMultiplier = 0,
            WriteTotalTimeoutConstant = checked((uint)options.ResponseTimeoutMs)
        };

        if (!SetCommTimeouts(_handle, ref timeouts))
        {
            throw LastIoException("Could not apply serial port timeouts");
        }
    }

    private static IOException LastIoException(string message) =>
        new($"{message}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint Length;
        public uint BaudRate;
        public uint Flags;
        public ushort Reserved;
        public ushort XonLimit;
        public ushort XoffLimit;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public sbyte XonChar;
        public sbyte XoffChar;
        public sbyte ErrorChar;
        public sbyte EofChar;
        public sbyte EventChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle fileHandle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommState(SafeFileHandle fileHandle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCommTimeouts(SafeFileHandle fileHandle, ref CommTimeouts timeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PurgeComm(SafeFileHandle fileHandle, uint flags);
}
