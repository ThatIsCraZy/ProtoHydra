using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataService.Core.Events;
using DataService.Protocols.Abstractions;

namespace DataService.App.ViewModels;

public sealed partial class FrontendViewModel : ObservableObject
{
    private readonly IProtocolAdapter? _adapter;
    private readonly Func<FrontendViewModel, ProtocolConfiguration> _createConfiguration;

    private ProtocolRuntimeState _state;
    private string _bindAddress;
    private int _port;
    private string _detail;
    private bool _isBusy;

    public FrontendViewModel(
        ProtocolKind protocol,
        string bindAddress,
        int port,
        ProtocolCapabilities capabilities,
        ProtocolRuntimeState state,
        string? detail,
        IProtocolAdapter? adapter,
        Func<FrontendViewModel, ProtocolConfiguration> createConfiguration)
    {
        Protocol = protocol;
        _bindAddress = bindAddress;
        _port = port;
        Capabilities = capabilities;
        _state = state;
        _detail = detail ?? "";
        _adapter = adapter;
        _createConfiguration = createConfiguration;
        StartCommand = new AsyncRelayCommand(() => StartAsync(), CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, CanStop);
    }

    public ProtocolKind Protocol { get; }

    public string ProtocolText => Protocol.ToString().ToUpperInvariant();

    public bool UsesSharedSshEndpoint => Protocol is ProtocolKind.Sftp or ProtocolKind.Scp;

    public string PortLabel => UsesSharedSshEndpoint ? "SSH Port" : "Port";

    public string BindAddressLabel => UsesSharedSshEndpoint ? "SSH Bind Address" : "Bind Address";

    public string BindAddress
    {
        get => _bindAddress;
        set => SetProperty(ref _bindAddress, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public ProtocolCapabilities Capabilities { get; }

    public ProtocolRuntimeState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(ActionLabel));
                RefreshCommands();
            }
        }
    }

    public string StateText => State.ToString();

    public string AvailabilityText => IsAvailable ? "Ready" : "Not implemented";

    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    public bool IsAvailable => _adapter is not null;

    public bool IsRunning => State == ProtocolRuntimeState.Running;

    public string ActionLabel => IsRunning ? $"Stop {ProtocolText}" : $"Start {ProtocolText}";

    public string StartLabel => $"Start {ProtocolText}";

    public string StopLabel => $"Stop {ProtocolText}";

    public IAsyncRelayCommand StartCommand { get; }

    public IAsyncRelayCommand StopCommand { get; }

    public string CapabilityText
    {
        get
        {
            var parts = new List<string>();
            if (Capabilities.SupportsDownload)
            {
                parts.Add("Download");
            }

            if (Capabilities.SupportsUpload)
            {
                parts.Add("Upload");
            }

            if (Capabilities.SupportsListing)
            {
                parts.Add("Listing");
            }

            if (Capabilities.SupportsAuthentication)
            {
                parts.Add("Accept Any");
            }

            if (Capabilities.UsesEncryption)
            {
                parts.Add("Encrypted");
            }

            return string.Join(", ", parts);
        }
    }

    public async Task StopIfRunningAsync(CancellationToken cancellationToken)
    {
        if (_adapter is not null && State is (ProtocolRuntimeState.Running or ProtocolRuntimeState.Starting))
        {
            await _adapter.StopAsync(cancellationToken);
            State = ProtocolRuntimeState.Stopped;
        }
    }

    public async Task StartIfStoppedAsync(CancellationToken cancellationToken)
    {
        if (_adapter is not null && State is (ProtocolRuntimeState.Stopped or ProtocolRuntimeState.Faulted))
        {
            await StartAsync(cancellationToken);
        }
    }

    private async Task StartAsync()
        => await StartAsync(CancellationToken.None);

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_adapter is null)
        {
            return;
        }

        _isBusy = true;
        RefreshCommands();
        State = ProtocolRuntimeState.Starting;
        Detail = "Starting...";

        try
        {
            var configuration = _createConfiguration(this);
            var validation = await _adapter.ValidateAsync(configuration, cancellationToken);
            if (!validation.IsValid)
            {
                State = ProtocolRuntimeState.Faulted;
                Detail = validation.Message ?? "Configuration validation failed.";
                return;
            }

            await _adapter.StartAsync(configuration, cancellationToken);
            State = _adapter.State;
            Detail = $"Listening on {BindAddress}:{Port.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex)
        {
            State = ProtocolRuntimeState.Faulted;
            Detail = ex.Message;
        }
        finally
        {
            _isBusy = false;
            RefreshCommands();
        }
    }

    private async Task StopAsync()
    {
        if (_adapter is null)
        {
            return;
        }

        _isBusy = true;
        RefreshCommands();
        State = ProtocolRuntimeState.Stopping;
        Detail = "Stopping...";

        try
        {
            await _adapter.StopAsync(CancellationToken.None);
            State = _adapter.State;
            Detail = "Stopped";
        }
        catch (Exception ex)
        {
            State = ProtocolRuntimeState.Faulted;
            Detail = ex.Message;
        }
        finally
        {
            _isBusy = false;
            RefreshCommands();
        }
    }

    private bool CanStart()
        => !_isBusy && IsAvailable && State is (ProtocolRuntimeState.Stopped or ProtocolRuntimeState.Faulted);

    private bool CanStop()
        => !_isBusy && IsAvailable && State == ProtocolRuntimeState.Running;

    private void RefreshCommands()
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
