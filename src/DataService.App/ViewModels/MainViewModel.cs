using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataService.Core.Configuration;
using DataService.Core.Diagnostics;
using DataService.Core.Events;
using DataService.Infrastructure.Firewall;
using DataService.Protocols.Abstractions;

namespace DataService.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogEvents = 1_000;
    private const int MaxTrafficSamples = 60;

    private readonly AppConfiguration _configuration;
    private readonly ITransferEventBus _eventBus;
    private readonly IFirewallStatusService _firewallStatusService;
    private readonly IFirewallTemporaryRuleService _firewallTemporaryRuleService;
    private readonly IoErrorLog _ioErrorLog;
    private readonly Dictionary<ProtocolKind, IProtocolAdapter> _adapters;
    private readonly Dictionary<string, Queue<double>> _trafficHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProtocolKind, long> _currentBytes = new();
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly Task _eventPumpTask;
    private readonly Task _trafficSamplerTask;

    private string _rootPath;
    private string _pendingRootPath;
    private string _selectedTrafficProtocol = "All";
    private string _trafficRateText = "0 B/s";
    private IReadOnlyList<double> _trafficSamples = Array.Empty<double>();
    private string _firewallStatusText = "Checking firewall";
    private string _firewallDetailText = "";
    private IBrush _firewallStatusBrush = Brushes.Goldenrod;
    private bool _isLogPaused;
    private bool _autoScrollLog = true;
    private bool _isDarkTheme = true;
    private bool _isFirewallFixActive;
    private bool _isCapturing;
    private string _captureStatusText = "";
    private TransferCaptureSession? _captureSession;
    private bool _hasIoErrors;

    public MainViewModel(
        AppConfiguration configuration,
        ITransferEventBus eventBus,
        IFirewallStatusService firewallStatusService,
        IFirewallTemporaryRuleService firewallTemporaryRuleService,
        IoErrorLog ioErrorLog,
        IEnumerable<IProtocolAdapter> adapters)
    {
        _configuration = configuration;
        _eventBus = eventBus;
        _firewallStatusService = firewallStatusService;
        _firewallTemporaryRuleService = firewallTemporaryRuleService;
        _ioErrorLog = ioErrorLog;
        _rootPath = configuration.RootPath;
        _pendingRootPath = configuration.RootPath;
        _adapters = adapters.ToDictionary(adapter => adapter.Protocol);

        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            _currentBytes[protocol] = 0;
            _trafficHistory[protocol.ToString()] = new Queue<double>();
        }

        _trafficHistory["All"] = new Queue<double>();
        TrafficProtocolOptions = new ObservableCollection<string>(
        [
            "All",
            ProtocolKind.Http.ToString(),
            ProtocolKind.Https.ToString(),
            ProtocolKind.Ftp.ToString(),
            ProtocolKind.Ftps.ToString(),
            ProtocolKind.Tftp.ToString(),
            ProtocolKind.Sftp.ToString(),
            ProtocolKind.Scp.ToString()
        ]);

        Frontends = new ObservableCollection<FrontendViewModel>(
        [
            CreateFrontend(ProtocolKind.Http, configuration.Protocols.Http, new(true, false, true, false, false), "Ready"),
            CreateFrontend(ProtocolKind.Https, configuration.Protocols.Https, new(true, false, true, false, true), "Ready"),
            CreateFrontend(ProtocolKind.Ftp, configuration.Protocols.Ftp, new(true, true, true, true, false), "Ready"),
            CreateFrontend(ProtocolKind.Ftps, configuration.Protocols.Ftps, new(true, true, true, true, true), "Ready"),
            CreateFrontend(ProtocolKind.Tftp, configuration.Protocols.Tftp, new(true, true, false, false, false), "Ready"),
            CreateFrontend(ProtocolKind.Sftp, configuration.Protocols.Sftp, new(true, true, true, true, true), "Shared SSH listener; accepts subsystem:sftp when enabled"),
            CreateFrontend(ProtocolKind.Scp, configuration.Protocols.Scp, new(true, true, false, true, true), "Shared SSH listener; accepts scp exec and WinSCP SCP (shell) mode when enabled; no arbitrary shell execution")
        ]);

        ClearLogCommand = new RelayCommand(ClearLog);
        ToggleLogPauseCommand = new RelayCommand(() => IsLogPaused = !IsLogPaused);
        ApplyRootPathCommand = new RelayCommand(ApplyRootPath, CanApplyRootPath);
        ExportLogCommand = new RelayCommand(ExportLog);
        StartAllCommand = new AsyncRelayCommand(StartAllAsync);
        StopAllCommand = new AsyncRelayCommand(StopAllAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        RefreshFirewallStatusCommand = new AsyncRelayCommand(RefreshFirewallStatusAsync);
        StartTemporaryFirewallFixCommand = new AsyncRelayCommand(ToggleTemporaryFirewallFixAsync);
        ToggleCaptureCommand = new RelayCommand(ToggleCapture);
        ClearIoErrorsCommand = new RelayCommand(ClearIoErrors);

        foreach (var frontend in Frontends)
        {
            frontend.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(FrontendViewModel.State) or nameof(FrontendViewModel.StateText))
                {
                    OnPropertyChanged(nameof(ServiceStatus));
                    OnPropertyChanged(nameof(RootStatus));
                    ApplyRootPathCommand.NotifyCanExecuteChanged();
                }

                if (args.PropertyName is nameof(FrontendViewModel.Port) or nameof(FrontendViewModel.BindAddress))
                {
                    SynchronizeSharedSshEndpoint(frontend, args.PropertyName);
                    FirewallDetailText = "Firewall status may be stale. Refresh after changing ports.";
                }
            };
        }

        _eventPumpTask = Task.Run(ReadEventsAsync);
        _trafficSamplerTask = Task.Run(SampleTrafficAsync);
        _ = RefreshFirewallStatusAsync();
    }

    public string RootPath
    {
        get => _rootPath;
        private set
        {
            if (SetProperty(ref _rootPath, value))
            {
                OnPropertyChanged(nameof(RootStatus));
                ApplyRootPathCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string PendingRootPath
    {
        get => _pendingRootPath;
        set
        {
            if (SetProperty(ref _pendingRootPath, value))
            {
                OnPropertyChanged(nameof(RootStatus));
                ApplyRootPathCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string RootStatus
        => !Directory.Exists(PendingRootPath)
            ? "Selected folder does not exist"
            : PathsEqual(PendingRootPath, RootPath)
                ? "Selected folder is active"
                : HasActiveListeners
                    ? "Stop all services before applying a new root folder"
                    : "Selected folder is ready to apply";

    public string ServiceStatus
    {
        get
        {
            var running = Frontends.Count(frontend => frontend.IsRunning);
            return running == 0
                ? "Stopped"
                : $"{running.ToString(CultureInfo.InvariantCulture)} listener running";
        }
    }

    public string AuthenticationWarning => "Accept-Any authentication is not access control. Every supplied username and password is accepted.";

    public ObservableCollection<FrontendViewModel> Frontends { get; }

    public ObservableCollection<LogEventViewModel> LiveLog { get; } = new();

    public ObservableCollection<string> TrafficProtocolOptions { get; }

    public string SelectedTrafficProtocol
    {
        get => _selectedTrafficProtocol;
        set
        {
            if (SetProperty(ref _selectedTrafficProtocol, value))
            {
                PublishTrafficView();
            }
        }
    }

    public string TrafficRateText
    {
        get => _trafficRateText;
        private set => SetProperty(ref _trafficRateText, value);
    }

    public IReadOnlyList<double> TrafficSamples
    {
        get => _trafficSamples;
        private set => SetProperty(ref _trafficSamples, value);
    }

    public string FirewallStatusText
    {
        get => _firewallStatusText;
        private set => SetProperty(ref _firewallStatusText, value);
    }

    public string FirewallDetailText
    {
        get => _firewallDetailText;
        private set => SetProperty(ref _firewallDetailText, value);
    }

    public IBrush FirewallStatusBrush
    {
        get => _firewallStatusBrush;
        private set => SetProperty(ref _firewallStatusBrush, value);
    }

    public bool IsLogPaused
    {
        get => _isLogPaused;
        set
        {
            if (SetProperty(ref _isLogPaused, value))
            {
                OnPropertyChanged(nameof(LogPauseText));
            }
        }
    }

    public string LogPauseText => IsLogPaused ? "Resume" : "Pause";

    public bool AutoScrollLog
    {
        get => _autoScrollLog;
        set => SetProperty(ref _autoScrollLog, value);
    }

    public string ThemeToggleText => _isDarkTheme ? "Light Mode" : "Dark Mode";

    public IRelayCommand ClearLogCommand { get; }

    public IRelayCommand ToggleLogPauseCommand { get; }

    public IRelayCommand ApplyRootPathCommand { get; }

    public IRelayCommand ExportLogCommand { get; }

    public IAsyncRelayCommand StartAllCommand { get; }

    public IAsyncRelayCommand StopAllCommand { get; }

    public IRelayCommand ToggleThemeCommand { get; }

    public IAsyncRelayCommand RefreshFirewallStatusCommand { get; }

    public IAsyncRelayCommand StartTemporaryFirewallFixCommand { get; }

    public string FirewallFixButtonText => _isFirewallFixActive ? "Undo Fix" : "Temp-Fix";

    public IRelayCommand ToggleCaptureCommand { get; }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(CaptureButtonText));
            }
        }
    }

    public string CaptureButtonText => IsCapturing ? "● Capturing…" : "Capture";

    public string CaptureStatusText
    {
        get => _captureStatusText;
        private set => SetProperty(ref _captureStatusText, value);
    }

    public IRelayCommand ClearIoErrorsCommand { get; }

    public bool HasIoErrors
    {
        get => _hasIoErrors;
        private set
        {
            if (SetProperty(ref _hasIoErrors, value))
            {
                OnPropertyChanged(nameof(IoStatusBrush));
                OnPropertyChanged(nameof(IoStatusText));
            }
        }
    }

    public IBrush IoStatusBrush => _hasIoErrors ? Brushes.Red : Brushes.LimeGreen;

    public string IoStatusText => _hasIoErrors ? $"{_ioErrorLog.Count} errors" : "OK";

    public IReadOnlyList<IoErrorEntryViewModel> IoErrorEntries
        => _ioErrorLog.Snapshot().Select(entry => new IoErrorEntryViewModel(entry)).ToArray();

    public void Dispose()
    {
        _disposeTokenSource.Cancel();
        _disposeTokenSource.Dispose();
        _captureSession?.Dispose();
        _captureSession = null;
        GC.SuppressFinalize(this);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await StopAllAsync();
        _disposeTokenSource.Cancel();
    }

    private FrontendViewModel CreateFrontend(
        ProtocolKind protocol,
        ProtocolEndpointSettings endpoint,
        ProtocolCapabilities capabilities,
        string detail)
    {
        _adapters.TryGetValue(protocol, out var adapter);
        return new FrontendViewModel(
            protocol,
            endpoint.BindAddress,
            endpoint.Port,
            capabilities,
            adapter is null ? ProtocolRuntimeState.Unavailable : ProtocolRuntimeState.Stopped,
            adapter is null ? detail : detail,
            adapter,
            frontend => new ProtocolConfiguration(frontend.BindAddress, frontend.Port, RootPath, Enabled: true));
    }

    private void SynchronizeSharedSshEndpoint(
        FrontendViewModel changedFrontend,
        string? propertyName)
    {
        if (changedFrontend.Protocol is not (ProtocolKind.Sftp or ProtocolKind.Scp))
        {
            return;
        }

        var counterpartProtocol = changedFrontend.Protocol == ProtocolKind.Sftp
            ? ProtocolKind.Scp
            : ProtocolKind.Sftp;
        var counterpart = Frontends.FirstOrDefault(frontend => frontend.Protocol == counterpartProtocol);
        if (counterpart is null)
        {
            return;
        }

        if (propertyName == nameof(FrontendViewModel.Port) && counterpart.Port != changedFrontend.Port)
        {
            counterpart.Port = changedFrontend.Port;
        }

        if (propertyName == nameof(FrontendViewModel.BindAddress)
            && !StringComparer.OrdinalIgnoreCase.Equals(counterpart.BindAddress, changedFrontend.BindAddress))
        {
            counterpart.BindAddress = changedFrontend.BindAddress;
        }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            await foreach (var transferEvent in _eventBus.Reader.ReadAllAsync(_disposeTokenSource.Token))
            {
                if (transferEvent.ByteCount is > 0)
                {
                    lock (_currentBytes)
                    {
                        _currentBytes[transferEvent.Protocol] += transferEvent.ByteCount.Value;
                    }
                }

                // Capture also records while the live log is paused.
                _captureSession?.Record(transferEvent);

                if (transferEvent.IoError is { } ioCategory)
                {
                    _ioErrorLog.Report(new IoErrorEntry(
                        transferEvent.Timestamp,
                        transferEvent.Protocol,
                        ioCategory,
                        transferEvent.RelativePath,
                        transferEvent.Message ?? ""));
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        HasIoErrors = true;
                        OnPropertyChanged(nameof(IoStatusText));
                        OnPropertyChanged(nameof(IoErrorEntries));
                    });
                }

                if (IsLogPaused)
                {
                    continue;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LiveLog.Insert(0, new LogEventViewModel(transferEvent));
                    while (LiveLog.Count > MaxLogEvents)
                    {
                        LiveLog.RemoveAt(LiveLog.Count - 1);
                    }

                    OnPropertyChanged(nameof(ServiceStatus));
                });
            }
        }
        catch (OperationCanceledException) when (_disposeTokenSource.IsCancellationRequested)
        {
        }
    }

    private async Task SampleTrafficAsync()
    {
        try
        {
            while (!_disposeTokenSource.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _disposeTokenSource.Token);

                Dictionary<ProtocolKind, long> snapshot;
                lock (_currentBytes)
                {
                    snapshot = _currentBytes.ToDictionary(pair => pair.Key, pair => pair.Value);
                    foreach (var protocol in snapshot.Keys)
                    {
                        _currentBytes[protocol] = 0;
                    }
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var total = 0L;
                    foreach (var pair in snapshot)
                    {
                        total += pair.Value;
                        AddTrafficSample(pair.Key.ToString(), pair.Value);
                    }

                    AddTrafficSample("All", total);
                    PublishTrafficView();
                });
            }
        }
        catch (OperationCanceledException) when (_disposeTokenSource.IsCancellationRequested)
        {
        }
    }

    private void AddTrafficSample(string key, double bytes)
    {
        var queue = _trafficHistory[key];
        queue.Enqueue(bytes);
        while (queue.Count > MaxTrafficSamples)
        {
            queue.Dequeue();
        }
    }

    private void PublishTrafficView()
    {
        if (!_trafficHistory.TryGetValue(SelectedTrafficProtocol, out var queue))
        {
            return;
        }

        TrafficSamples = queue.ToArray();
        TrafficRateText = TrafficSamples.Count == 0
            ? "0 B/s"
            : $"{FormatBytes(TrafficSamples[^1])}/s";
    }

    private void ClearLog()
        => LiveLog.Clear();

    private bool HasActiveListeners
        => Frontends.Any(frontend => frontend.State is
            ProtocolRuntimeState.Starting or
            ProtocolRuntimeState.Running or
            ProtocolRuntimeState.Stopping);

    private bool CanApplyRootPath()
        => Directory.Exists(PendingRootPath)
            && !PathsEqual(PendingRootPath, RootPath)
            && !HasActiveListeners;

    private void ApplyRootPath()
    {
        RootPath = Path.GetFullPath(PendingRootPath);
        PendingRootPath = RootPath;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void ExportLog()
    {
        var exportDirectory = string.IsNullOrWhiteSpace(_configuration.LogSettings.DirectoryPath)
            ? ApplicationDataPaths.GetDirectory("Logs")
            : _configuration.LogSettings.DirectoryPath;
        Directory.CreateDirectory(exportDirectory);
        var exportPath = Path.Combine(exportDirectory, $"live-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.tsv");
        var lines = LiveLog.Select(item => item.ExportLine).Reverse();
        File.WriteAllLines(exportPath, ["Time\tProtocol\tEvent\tSource\tUser\tCommand\tPath\tDirection\tBytes\tResult\tDuration\tMessage", .. lines]);
    }

    private async Task StartAllAsync()
    {
        foreach (var frontend in Frontends)
        {
            await frontend.StartIfStoppedAsync(CancellationToken.None);
        }

        OnPropertyChanged(nameof(ServiceStatus));
    }

    private async Task StopAllAsync()
    {
        foreach (var frontend in Frontends)
        {
            await frontend.StopIfRunningAsync(CancellationToken.None);
        }

        OnPropertyChanged(nameof(ServiceStatus));
    }

    private FirewallPortProbe[] BuildFirewallProbes()
        => Frontends
            .Where(frontend => frontend.IsAvailable)
            .Select(frontend => new FirewallPortProbe(
                frontend.ProtocolText,
                frontend.Port,
                frontend.Protocol == ProtocolKind.Tftp ? FirewallTransportProtocol.Udp : FirewallTransportProtocol.Tcp,
                frontend.BindAddress))
            .GroupBy(probe => $"{probe.TransportProtocol}:{probe.BindAddress}:{probe.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

    private static string GetExecutablePath()
        => Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private async Task RefreshFirewallStatusAsync()
    {
        try
        {
            FirewallStatusText = "Checking firewall";
            FirewallDetailText = "Evaluating Windows Firewall rules for this executable and configured listener ports.";
            FirewallStatusBrush = Brushes.Goldenrod;

            var snapshot = await _firewallStatusService.CheckAsync(
                GetExecutablePath(),
                BuildFirewallProbes(),
                _disposeTokenSource.Token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FirewallStatusText = snapshot.Summary;
                FirewallDetailText = snapshot.Detail;
                FirewallStatusBrush = snapshot.Level switch
                {
                    FirewallStatusLevel.Green => Brushes.LimeGreen,
                    FirewallStatusLevel.Red => Brushes.Red,
                    _ => Brushes.Goldenrod
                };
            });
        }
        catch (OperationCanceledException) when (_disposeTokenSource.IsCancellationRequested)
        {
        }
    }

    private async Task ToggleTemporaryFirewallFixAsync()
    {
        if (_isFirewallFixActive)
        {
            var stopResult = await _firewallTemporaryRuleService.StopTemporaryApplicationRuleAsync(_disposeTokenSource.Token);
            FirewallDetailText = stopResult.Message;
            _isFirewallFixActive = false;
            OnPropertyChanged(nameof(FirewallFixButtonText));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _disposeTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RefreshFirewallStatusAsync();
            return;
        }

        FirewallStatusText = "Starting firewall fix";
        FirewallDetailText = "A Windows elevation prompt may appear. Only the firewall helper is elevated.";
        FirewallStatusBrush = Brushes.Goldenrod;

        var result = await _firewallTemporaryRuleService.StartTemporaryApplicationRuleAsync(
            GetExecutablePath(),
            Environment.ProcessId,
            BuildFirewallProbes(),
            _disposeTokenSource.Token);

        FirewallDetailText = result.Message;
        if (!result.Started)
        {
            FirewallStatusText = "Firewall fix failed";
            FirewallStatusBrush = Brushes.Red;
            return;
        }

        _isFirewallFixActive = true;
        OnPropertyChanged(nameof(FirewallFixButtonText));
        FirewallStatusText = "Temporary firewall fix active";
        FirewallStatusBrush = Brushes.Goldenrod;
        await RefreshFirewallStatusAsync();
        FirewallDetailText = result.Message + "\n" + FirewallDetailText;
    }

    private void ClearIoErrors()
    {
        _ioErrorLog.Clear();
        HasIoErrors = false;
        OnPropertyChanged(nameof(IoErrorEntries));
    }

    private void ToggleCapture()
    {
        if (_captureSession is not null)
        {
            var session = _captureSession;
            _captureSession = null;
            session.RecordInfo("Capture stopped by user.");
            session.Dispose();
            IsCapturing = false;
            CaptureStatusText = $"Capture saved: {Path.GetFileName(session.FilePath)}";
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(GetExecutablePath());
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Executable directory is not available.");
            }

            var session = TransferCaptureSession.Start(directory, BuildCaptureHeader());
            _captureSession = session;
            IsCapturing = true;
            CaptureStatusText = $"Recording to {Path.GetFileName(session.FilePath)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            IsCapturing = false;
            CaptureStatusText = $"Capture failed: {ex.Message}";
        }
    }

    private IEnumerable<string> BuildCaptureHeader()
    {
        yield return $"Executable: {GetExecutablePath()}";
        yield return $"Root folder: {RootPath}";
        yield return $"Firewall status: {FirewallStatusText}";
        foreach (var frontend in Frontends)
        {
            yield return $"Protocol {frontend.ProtocolText}: state={frontend.StateText} bind={frontend.BindAddress} port={frontend.Port} capability={frontend.CapabilityText}";
        }
    }

    private void ToggleTheme()
    {
        _isDarkTheme = !_isDarkTheme;
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = _isDarkTheme
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }

        OnPropertyChanged(nameof(ThemeToggleText));
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:N1} {units[unit]}";
    }

}
