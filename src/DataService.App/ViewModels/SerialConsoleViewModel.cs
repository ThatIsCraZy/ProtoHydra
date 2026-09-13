using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataService.Core.Configuration;
using DataService.Core.Terminal;
using DataService.Infrastructure.Serial;

namespace DataService.App.ViewModels;

/// <summary>
/// Drives the serial console tab: port discovery, connection parameters, the terminal
/// screen and the session log.
/// </summary>
public sealed class SerialConsoleViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromMilliseconds(300);

    private readonly ISerialPortEnumerator _portEnumerator;
    private readonly ISerialConsoleSession _session;
    private readonly SerialConsoleSettingsStore _settingsStore;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();
    private readonly object _pendingGate = new();
    private readonly object _logGate = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _statusTimer;

    private StreamWriter? _logWriter;
    private string? _logPath;
    private SerialPortDescriptor? _selectedPort;
    private string _preferredPortName = string.Empty;
    private int _selectedBaudRate = 115_200;
    private int _selectedDataBits = 8;
    private SerialOption<SerialParity> _selectedParity;
    private SerialOption<SerialStopBits> _selectedStopBits;
    private SerialOption<SerialFlowControl> _selectedFlowControl;
    private SerialOption<SerialNewLineMode> _selectedNewLine;
    private bool _isConnected;
    private bool _isBusy;
    private bool _localEcho;
    private double _fontSize = 13;
    private string _statusText = "Not connected";
    private string _connectionSummary = "";
    private string _remoteTitle = "";
    private SerialLineStatus _lineStatus = SerialLineStatus.None;
    private string _trafficText = "RX 0 B · TX 0 B";

    public SerialConsoleViewModel(
        ISerialPortEnumerator portEnumerator,
        ISerialConsoleSession session,
        SerialConsoleSettingsStore settingsStore)
    {
        _portEnumerator = portEnumerator;
        _session = session;
        _settingsStore = settingsStore;

        ParityOptions =
        [
            new("None", SerialParity.None),
            new("Even", SerialParity.Even),
            new("Odd", SerialParity.Odd),
            new("Mark", SerialParity.Mark),
            new("Space", SerialParity.Space)
        ];
        StopBitsOptions =
        [
            new("1", SerialStopBits.One),
            new("1.5", SerialStopBits.OnePointFive),
            new("2", SerialStopBits.Two)
        ];
        FlowControlOptions =
        [
            new("None", SerialFlowControl.None),
            new("XON/XOFF", SerialFlowControl.XOnXOff),
            new("RTS/CTS", SerialFlowControl.RtsCts),
            new("RTS/CTS + XON/XOFF", SerialFlowControl.RtsCtsXOnXOff)
        ];
        NewLineOptions =
        [
            new("CR", SerialNewLineMode.Cr),
            new("LF", SerialNewLineMode.Lf),
            new("CR LF", SerialNewLineMode.CrLf)
        ];

        _selectedParity = ParityOptions[0];
        _selectedStopBits = StopBitsOptions[0];
        _selectedFlowControl = FlowControlOptions[0];
        _selectedNewLine = NewLineOptions[0];

        Screen = new TerminalScreen();
        Screen.ResponseRequested += (_, response) => SendRaw(response);
        Screen.TitleChanged += (_, title) => Dispatcher.UIThread.Post(() => RemoteTitle = title);

        _session.DataReceived += OnDataReceived;
        _session.Faulted += OnFaulted;

        ConnectCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy);
        RefreshPortsCommand = new AsyncRelayCommand(RefreshPortsAsync);
        ClearCommand = new RelayCommand(() => Screen.ClearScreenAndScrollback());
        ResetTerminalCommand = new RelayCommand(() => Screen.Reset());
        SendBreakCommand = new AsyncRelayCommand(SendBreakAsync, () => IsConnected);
        ToggleLoggingCommand = new RelayCommand(ToggleLogging);
        CopyAllCommand = new AsyncRelayCommand(CopyAllAsync);
        ToggleDataTerminalReadyCommand = new RelayCommand(ToggleDataTerminalReady, () => IsConnected);
        ToggleRequestToSendCommand = new RelayCommand(
            ToggleRequestToSend,
            () => IsConnected && !_session.DriverOwnsRequestToSend);
        IncreaseFontSizeCommand = new RelayCommand(() => FontSize = Math.Min(28, FontSize + 1));
        DecreaseFontSizeCommand = new RelayCommand(() => FontSize = Math.Max(8, FontSize - 1));

        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += (_, _) => FlushPending();
        _statusTimer = new DispatcherTimer { Interval = StatusInterval };
        _statusTimer.Tick += (_, _) => RefreshRuntimeStatus();

        ApplyPreferences(_settingsStore.Load());
        WriteWelcomeBanner();
        _ = RefreshPortsAsync();
    }

    /// <summary>Set by the view; the clipboard lives on the window, not in the view model.</summary>
    public Func<string, Task>? CopyToClipboardAsync { get; set; }

    public TerminalScreen Screen { get; }

    public ObservableCollection<SerialPortDescriptor> AvailablePorts { get; } = new();

    public IReadOnlyList<int> BaudRateOptions { get; } =
    [
        300, 600, 1_200, 2_400, 4_800, 9_600, 14_400, 19_200, 38_400, 57_600,
        115_200, 128_000, 230_400, 256_000, 460_800, 921_600
    ];

    public IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];

    public IReadOnlyList<SerialOption<SerialParity>> ParityOptions { get; }

    public IReadOnlyList<SerialOption<SerialStopBits>> StopBitsOptions { get; }

    public IReadOnlyList<SerialOption<SerialFlowControl>> FlowControlOptions { get; }

    public IReadOnlyList<SerialOption<SerialNewLineMode>> NewLineOptions { get; }

    public SerialPortDescriptor? SelectedPort
    {
        get => _selectedPort;
        set => SetProperty(ref _selectedPort, value);
    }

    public int SelectedBaudRate
    {
        get => _selectedBaudRate;
        set => SetProperty(ref _selectedBaudRate, value);
    }

    public int SelectedDataBits
    {
        get => _selectedDataBits;
        set => SetProperty(ref _selectedDataBits, value);
    }

    public SerialOption<SerialParity> SelectedParity
    {
        get => _selectedParity;
        set => SetProperty(ref _selectedParity, value);
    }

    public SerialOption<SerialStopBits> SelectedStopBits
    {
        get => _selectedStopBits;
        set => SetProperty(ref _selectedStopBits, value);
    }

    public SerialOption<SerialFlowControl> SelectedFlowControl
    {
        get => _selectedFlowControl;
        set => SetProperty(ref _selectedFlowControl, value);
    }

    public SerialOption<SerialNewLineMode> SelectedNewLine
    {
        get => _selectedNewLine;
        set
        {
            if (SetProperty(ref _selectedNewLine, value))
            {
                OnPropertyChanged(nameof(NewLineSequence));
            }
        }
    }

    /// <summary>What the terminal control sends for Enter and for pasted line breaks.</summary>
    public string NewLineSequence => SelectedNewLine.Value switch
    {
        SerialNewLineMode.Lf => "\n",
        SerialNewLineMode.CrLf => "\r\n",
        _ => "\r"
    };

    public bool LocalEcho
    {
        get => _localEcho;
        set => SetProperty(ref _localEcho, value);
    }

    public double FontSize
    {
        get => _fontSize;
        set => SetProperty(ref _fontSize, Math.Clamp(value, 8, 28));
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(CanEditParameters));
                OnPropertyChanged(nameof(StatusBrush));
                SendBreakCommand.NotifyCanExecuteChanged();
                ToggleDataTerminalReadyCommand.NotifyCanExecuteChanged();
                ToggleRequestToSendCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEditParameters));
                ConnectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanEditParameters => !IsConnected && !IsBusy;

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public IBrush StatusBrush => IsConnected
        ? new SolidColorBrush(Color.FromRgb(0x3F, 0xBF, 0x6F))
        : new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3));

    public string ConnectionSummary
    {
        get => _connectionSummary;
        private set => SetProperty(ref _connectionSummary, value);
    }

    public string RemoteTitle
    {
        get => _remoteTitle;
        private set => SetProperty(ref _remoteTitle, value);
    }

    public string TrafficText
    {
        get => _trafficText;
        private set => SetProperty(ref _trafficText, value);
    }

    public bool ClearToSend => _lineStatus.ClearToSend;

    public bool DataSetReady => _lineStatus.DataSetReady;

    public bool CarrierDetect => _lineStatus.CarrierDetect;

    public bool DataTerminalReady => _lineStatus.DataTerminalReady;

    public bool RequestToSend => _lineStatus.RequestToSend;

    public bool IsLogging => _logWriter is not null;

    public string LogButtonText => IsLogging ? "● Logging…" : "Log to file";

    public string LogStatusText => _logPath is null ? "" : $"Session log: {_logPath}";

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand RefreshPortsCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public IRelayCommand ResetTerminalCommand { get; }

    public IAsyncRelayCommand SendBreakCommand { get; }

    public IRelayCommand ToggleLoggingCommand { get; }

    public IAsyncRelayCommand CopyAllCommand { get; }

    public IRelayCommand ToggleDataTerminalReadyCommand { get; }

    public IRelayCommand ToggleRequestToSendCommand { get; }

    public IRelayCommand IncreaseFontSizeCommand { get; }

    public IRelayCommand DecreaseFontSizeCommand { get; }

    /// <summary>Called by the terminal control for every keystroke, paste and reply.</summary>
    public void SendText(string text)
    {
        if (text.Length == 0 || !IsConnected)
        {
            return;
        }

        SendRaw(text);

        if (!LocalEcho)
        {
            return;
        }

        var echo = SelectedNewLine.Value switch
        {
            SerialNewLineMode.Cr => text.Replace("\r", "\r\n", StringComparison.Ordinal),
            SerialNewLineMode.Lf => text.Replace("\n", "\r\n", StringComparison.Ordinal),
            _ => text
        };
        Screen.Write(echo);
    }

    public async Task ShutdownAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }

        StopLogging();
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _statusTimer.Stop();
        _session.DataReceived -= OnDataReceived;
        _session.Faulted -= OnFaulted;
        _session.Dispose();
        StopLogging();
    }

    private void ApplyPreferences(SerialConsolePreferences preferences)
    {
        SelectedBaudRate = BaudRateOptions.Contains(preferences.Connection.BaudRate)
            ? preferences.Connection.BaudRate
            : 115_200;
        SelectedDataBits = DataBitsOptions.Contains(preferences.Connection.DataBits)
            ? preferences.Connection.DataBits
            : 8;
        SelectedParity = ParityOptions.First(option => option.Value == preferences.Connection.Parity);
        SelectedStopBits = StopBitsOptions.First(option => option.Value == preferences.Connection.StopBits);
        SelectedFlowControl = FlowControlOptions.First(option => option.Value == preferences.Connection.FlowControl);
        SelectedNewLine = NewLineOptions.First(option => option.Value == preferences.NewLineMode);
        LocalEcho = preferences.LocalEcho;
        FontSize = preferences.FontSize;
        _preferredPortName = preferences.Connection.PortName;
    }

    private void SavePreferences()
        => _settingsStore.Save(new SerialConsolePreferences(
            BuildSettings(SelectedPort?.PortName ?? _preferredPortName),
            SelectedNewLine.Value,
            LocalEcho,
            (int)Math.Round(FontSize)));

    private SerialConnectionSettings BuildSettings(string portName)
        => new(
            portName,
            SelectedBaudRate,
            SelectedDataBits,
            SelectedParity.Value,
            SelectedStopBits.Value,
            SelectedFlowControl.Value);

    private async Task RefreshPortsAsync()
    {
        IReadOnlyList<SerialPortDescriptor> ports;
        try
        {
            ports = await _portEnumerator.ListAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Port list unavailable: {exception.Message}";
            return;
        }

        var previous = SelectedPort?.PortName ?? _preferredPortName;
        AvailablePorts.Clear();
        foreach (var port in ports)
        {
            AvailablePorts.Add(port);
        }

        SelectedPort = AvailablePorts.FirstOrDefault(
                port => StringComparer.OrdinalIgnoreCase.Equals(port.PortName, previous))
            ?? AvailablePorts.FirstOrDefault();

        if (!IsConnected)
        {
            StatusText = AvailablePorts.Count == 0
                ? "No serial ports found"
                : $"{AvailablePorts.Count} port(s) available";
        }
    }

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        var port = SelectedPort;
        if (port is null)
        {
            StatusText = "Select a port first";
            return;
        }

        IsBusy = true;
        var settings = BuildSettings(port.PortName);
        try
        {
            await Task.Run(() => _session.Open(settings));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or InvalidOperationException)
        {
            var reason = exception is UnauthorizedAccessException
                ? $"{port.PortName} is in use by another application or not accessible"
                : exception.Message;
            StatusText = $"Connect failed: {reason}";
            WriteBanner($"Connect failed — {reason}", isError: true);
            IsBusy = false;
            return;
        }

        IsBusy = false;
        IsConnected = true;
        ConnectionSummary = settings.Summary;
        StatusText = $"Connected to {settings.Summary}";
        WriteBanner($"Connected to {settings.Summary}", isError: false);
        _flushTimer.Start();
        _statusTimer.Start();
        RefreshRuntimeStatus();
        SavePreferences();
    }

    private async Task DisconnectAsync()
    {
        IsBusy = true;
        await Task.Run(_session.Close);
        _flushTimer.Stop();
        _statusTimer.Stop();
        FlushPending();
        IsBusy = false;
        IsConnected = false;
        _lineStatus = SerialLineStatus.None;
        NotifyLineStatus();
        StatusText = "Disconnected";
        WriteBanner("Disconnected", isError: false);
        SavePreferences();
    }

    private async Task SendBreakAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        await _session.SendBreakAsync(BreakDuration, CancellationToken.None);
        StatusText = "Break sent";
    }

    private void ToggleDataTerminalReady()
    {
        if (!IsConnected)
        {
            return;
        }

        _session.SetDataTerminalReady(!_lineStatus.DataTerminalReady);
        RefreshRuntimeStatus();
    }

    private void ToggleRequestToSend()
    {
        if (!IsConnected || _session.DriverOwnsRequestToSend)
        {
            return;
        }

        _session.SetRequestToSend(!_lineStatus.RequestToSend);
        RefreshRuntimeStatus();
    }

    private void SendRaw(string text)
    {
        if (!_session.IsOpen)
        {
            return;
        }

        _session.Send(Encoding.UTF8.GetBytes(text));
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        var characters = new char[_decoder.GetCharCount(data, 0, data.Length, flush: false)];
        var count = _decoder.GetChars(data, 0, data.Length, characters, 0, flush: false);
        if (count == 0)
        {
            return;
        }

        var text = new string(characters, 0, count);
        lock (_pendingGate)
        {
            _pending.Append(text);
        }

        AppendToLog(text);
    }

    private void OnFaulted(object? sender, string message)
        => Dispatcher.UIThread.Post(() =>
        {
            _flushTimer.Stop();
            _statusTimer.Stop();
            FlushPending();
            IsConnected = false;
            IsBusy = false;
            _lineStatus = SerialLineStatus.None;
            NotifyLineStatus();
            StatusText = $"Connection lost: {message}";
            WriteBanner($"Connection lost — {message}", isError: true);
        });

    private void FlushPending()
    {
        string text;
        lock (_pendingGate)
        {
            if (_pending.Length == 0)
            {
                return;
            }

            text = _pending.ToString();
            _pending.Clear();
        }

        Screen.Write(text);
    }

    private void RefreshRuntimeStatus()
    {
        _lineStatus = _session.ReadLineStatus();
        NotifyLineStatus();
        TrafficText = string.Create(
            CultureInfo.InvariantCulture,
            $"RX {FormatBytes(_session.BytesReceived)} · TX {FormatBytes(_session.BytesSent)}");
        FlushLog();
    }

    private void NotifyLineStatus()
    {
        OnPropertyChanged(nameof(ClearToSend));
        OnPropertyChanged(nameof(DataSetReady));
        OnPropertyChanged(nameof(CarrierDetect));
        OnPropertyChanged(nameof(DataTerminalReady));
        OnPropertyChanged(nameof(RequestToSend));
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1_024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1_048_576 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.0} kB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576d:0.00} MB")
    };

    private void ToggleLogging()
    {
        if (IsLogging)
        {
            StopLogging();
            return;
        }

        try
        {
            var directory = ApplicationDataPaths.GetDirectory("Logs");
            Directory.CreateDirectory(directory);
            var portName = SelectedPort?.PortName ?? "serial";
            var path = Path.Combine(
                directory,
                string.Create(CultureInfo.InvariantCulture, $"serial-{portName}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log"));
            lock (_logGate)
            {
                _logWriter = new StreamWriter(path, append: true) { AutoFlush = false };
            }

            _logPath = path;
            StatusText = $"Logging to {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Log file could not be opened: {exception.Message}";
            return;
        }

        OnPropertyChanged(nameof(IsLogging));
        OnPropertyChanged(nameof(LogButtonText));
        OnPropertyChanged(nameof(LogStatusText));
    }

    private void StopLogging()
    {
        lock (_logGate)
        {
            if (_logWriter is null)
            {
                return;
            }

            try
            {
                _logWriter.Flush();
                _logWriter.Dispose();
            }
            catch (IOException)
            {
            }

            _logWriter = null;
        }

        OnPropertyChanged(nameof(IsLogging));
        OnPropertyChanged(nameof(LogButtonText));
    }

    private void AppendToLog(string text)
    {
        lock (_logGate)
        {
            try
            {
                _logWriter?.Write(text);
            }
            catch (IOException)
            {
            }
        }
    }

    private void FlushLog()
    {
        lock (_logGate)
        {
            try
            {
                _logWriter?.Flush();
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task CopyAllAsync()
    {
        var callback = CopyToClipboardAsync;
        if (callback is null)
        {
            return;
        }

        await callback(Screen.GetAllText());
        StatusText = "Console buffer copied to clipboard";
    }

    private void WriteWelcomeBanner()
    {
        // Lines stay under 70 columns: the banner is written before the control sizes the
        // grid, and a terminal wraps rather than reflows once the width grows.
        Screen.Write(
            "\x1b[38;5;244mProtoHydra serial console\x1b[0m\r\n"
            + "\x1b[38;5;244mPick a port and parameters above, then press Connect.\x1b[0m\r\n"
            + "\x1b[38;5;244mSelect text to copy · right-click pastes · Shift+PgUp scrolls\x1b[0m\r\n"
            + "\x1b[38;5;244mCtrl+Shift+C copies · Ctrl+Shift+V pastes\x1b[0m\r\n");
    }

    private void WriteBanner(string message, bool isError)
    {
        var color = isError ? "\x1b[38;5;203m" : "\x1b[38;5;244m";
        Screen.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"\r\n{color}*** {message} ***\x1b[0m\r\n"));
    }
}
