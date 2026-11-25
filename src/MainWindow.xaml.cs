using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TcpToComport;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private ModbusGateway? _gateway;
    private ISerialPort? _serial;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshComPorts();
        RefreshListenAddresses();
        UpdateEndpointPreview();
        AppendLog("설정을 확인한 뒤 '게이트웨이 시작'을 누르세요.", false);
    }

    private void RefreshComPorts()
    {
        var previous = ComPortCombo.Text;
        var ports = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is not null)
            {
                ports.AddRange(key.GetValueNames()
                    .Select(name => key.GetValue(name)?.ToString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.ToUpperInvariant()));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AppendLog($"COM 포트 목록을 읽지 못했습니다: {ex.Message}", true);
        }

        ports = ports.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(PortSortKey)
            .ToList();

        ComPortCombo.ItemsSource = ports;
        if (!string.IsNullOrWhiteSpace(previous))
        {
            ComPortCombo.Text = previous;
        }
        else if (ports.Count > 0)
        {
            ComPortCombo.SelectedIndex = 0;
        }
        else
        {
            ComPortCombo.Text = "COM3";
        }
    }

    private void RefreshListenAddresses()
    {
        var addresses = new List<string> { "0.0.0.0" };
        try
        {
            addresses.AddRange(Dns.GetHostAddresses(Dns.GetHostName())
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                  !IPAddress.IsLoopback(address))
                .Select(address => address.ToString()));
        }
        catch (SocketException ex)
        {
            AppendLog($"로컬 IP 주소를 읽지 못했습니다: {ex.Message}", true);
        }

        ListenAddressCombo.ItemsSource = addresses.Distinct().ToArray();
        ListenAddressCombo.SelectedIndex = 0;
    }

    private static int PortSortKey(string port) =>
        int.TryParse(port.AsSpan(3), out var number) ? number : int.MaxValue;

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        SetStatus("시작 중", "#F59E0B");

        try
        {
            var options = ReadOptions();
            _serial = new Win32SerialPort(options);
            _gateway = new ModbusGateway(options, _serial, AppendLog);
            _runCancellation = new CancellationTokenSource();

            SetRunningUi(true);
            AppendLog(
                $"게이트웨이 시작: {options.ListenAddress}:{options.ListenPort} → " +
                $"{options.ComPort} ({options.BaudRate}, {options.DataBits}-{ParityShort(options.Parity)}-{StopBitsShort(options.StopBits)})",
                false);

            _runTask = RunGatewayAsync(_gateway, _runCancellation.Token);
            await Task.Yield();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            if (_serial is not null)
            {
                await _serial.DisposeAsync();
                _serial = null;
            }

            AppendLog($"시작 실패: {ex.Message}", true);
            SetRunningUi(false);
            MessageBox.Show(this, ex.Message, "게이트웨이를 시작할 수 없습니다", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RunGatewayAsync(ModbusGateway gateway, CancellationToken cancellationToken)
    {
        try
        {
            await gateway.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"게이트웨이 중단: {ex.Message}", true);
            await Dispatcher.InvokeAsync(() =>
                MessageBox.Show(this, ex.Message, "게이트웨이 오류", MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            await gateway.DisposeAsync();
            if (_serial is not null)
            {
                await _serial.DisposeAsync();
            }

            _gateway = null;
            _serial = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            await Dispatcher.InvokeAsync(() => SetRunningUi(false));
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopGatewayAsync();
    }

    private async Task StopGatewayAsync()
    {
        if (_runCancellation is null)
        {
            return;
        }

        StopButton.IsEnabled = false;
        SetStatus("중지 중", "#F59E0B");
        _runCancellation.Cancel();

        if (_runTask is not null)
        {
            await _runTask;
            _runTask = null;
        }

        AppendLog("게이트웨이가 중지되었습니다.", false);
    }

    private GatewayOptions ReadOptions()
    {
        var args = new[]
        {
            "--com", ComPortCombo.Text.Trim(),
            "--baud", ComboValue(BaudRateCombo),
            "--parity", ComboValue(ParityCombo),
            "--data-bits", ComboValue(DataBitsCombo),
            "--stop-bits", ComboValue(StopBitsCombo),
            "--listen-address", ListenAddressCombo.Text.Trim(),
            "--listen-port", ListenPortTextBox.Text.Trim(),
            "--websocket-port", BridgePortTextBox.Text.Trim(),
            "--response-timeout-ms", ResponseTimeoutTextBox.Text.Trim()
        };
        return GatewayOptions.Parse(args);
    }

    private static string ComboValue(ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? string.Empty
            : comboBox.Text.Trim();

    private void SetRunningUi(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        RefreshPortsButton.IsEnabled = !running;
        ComPortCombo.IsEnabled = !running;
        BaudRateCombo.IsEnabled = !running;
        ParityCombo.IsEnabled = !running;
        DataBitsCombo.IsEnabled = !running;
        StopBitsCombo.IsEnabled = !running;
        ListenAddressCombo.IsEnabled = !running;
        ListenPortTextBox.IsEnabled = !running;
        BridgePortTextBox.IsEnabled = !running;
        ResponseTimeoutTextBox.IsEnabled = !running;
        SetStatus(running ? "실행 중" : "중지됨", running ? "#22C55E" : "#94A3B8");
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void AppendLog(string message, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AppendLog(message, isError));
            return;
        }

        var marker = isError ? "ERROR" : "INFO ";
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {marker}  {message}{Environment.NewLine}");
        if (LogTextBox.Text.Length > 100_000)
        {
            LogTextBox.Text = LogTextBox.Text[^60_000..];
        }
        LogTextBox.ScrollToEnd();
    }

    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshComPorts();
        AppendLog("COM 포트 목록을 새로고침했습니다.", false);
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogTextBox.Clear();

    private void EndpointField_Changed(object sender, EventArgs e) => UpdateEndpointPreview();

    private void UpdateEndpointPreview()
    {
        if (!IsLoaded || EndpointPreviewText is null)
        {
            return;
        }

        var address = ListenAddressCombo.Text == "0.0.0.0" || string.IsNullOrWhiteSpace(ListenAddressCombo.Text)
            ? "이 PC의 IP"
            : ListenAddressCombo.Text;
        var port = string.IsNullOrWhiteSpace(ListenPortTextBox.Text) ? "502" : ListenPortTextBox.Text;
        var bridgePort = string.IsNullOrWhiteSpace(BridgePortTextBox.Text) ? "8765" : BridgePortTextBox.Text;
        EndpointPreviewText.Text = $"{address} : {port}";
        BridgePreviewText.Text = $"웹 모니터: ws://localhost:{bridgePort}";
    }

    private static string ParityShort(SerialParity parity) => parity switch
    {
        SerialParity.None => "N",
        SerialParity.Even => "E",
        SerialParity.Odd => "O",
        SerialParity.Mark => "M",
        SerialParity.Space => "S",
        _ => "?"
    };

    private static string StopBitsShort(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => "1",
        SerialStopBits.OnePointFive => "1.5",
        SerialStopBits.Two => "2",
        _ => "?"
    };

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _runCancellation is null)
        {
            return;
        }

        e.Cancel = true;
        await StopGatewayAsync();
        _allowClose = true;
        Close();
    }
}
