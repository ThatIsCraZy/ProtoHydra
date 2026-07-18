using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using DataService.Core.Events;
using DataService.Protocols.Abstractions;
using FxSsh;
using FxSsh.Services;
using Microsoft.Extensions.Options;
using SFTP;

namespace DataService.Protocols.Ssh;

public sealed class SharedSshServer : IDisposable
{
    private readonly ITransferEventBus _eventBus;
    private readonly SshHostKeyStore _hostKeyStore;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _sessionLock = new();
    private readonly List<SessionTokenRegistration> _sessionTokenSources = [];

    private SshServer? _server;
    private ProtocolConfiguration? _listenerConfiguration;
    private ProtocolConfiguration? _sftpConfiguration;
    private ProtocolConfiguration? _scpConfiguration;
    private bool _sftpEnabled;
    private bool _scpEnabled;
    private ProtocolRuntimeState _sftpState = ProtocolRuntimeState.Stopped;
    private ProtocolRuntimeState _scpState = ProtocolRuntimeState.Stopped;

    public SharedSshServer(
        ITransferEventBus eventBus,
        string hostKeyDirectory)
    {
        _eventBus = eventBus;
        _hostKeyStore = new SshHostKeyStore(hostKeyDirectory);
    }

    public ProtocolRuntimeState GetState(ProtocolKind protocol)
        => protocol switch
        {
            ProtocolKind.Sftp => _sftpState,
            ProtocolKind.Scp => _scpState,
            _ => ProtocolRuntimeState.Unavailable
        };

    public async Task<ProtocolValidationResult> ValidateAsync(
        ProtocolKind protocol,
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (protocol is not (ProtocolKind.Sftp or ProtocolKind.Scp))
        {
            return ProtocolValidationResult.Failure("Only SFTP and SCP use the shared SSH listener.");
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ValidateConfiguration(configuration, probePort: _server is null);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StartProtocolAsync(
        ProtocolKind protocol,
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (protocol is not (ProtocolKind.Sftp or ProtocolKind.Scp))
        {
            throw new InvalidOperationException("Only SFTP and SCP use the shared SSH listener.");
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (GetState(protocol) is ProtocolRuntimeState.Running or ProtocolRuntimeState.Starting)
            {
                return;
            }

            var validation = ValidateConfiguration(configuration, probePort: _server is null);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(validation.Message);
            }

            SetState(protocol, ProtocolRuntimeState.Starting);
            PublishListener(protocol, TransferEventKind.ListenerStarting, configuration, TransferResult.Success);

            try
            {
                if (_server is null)
                {
                    await StartListenerAsync(configuration, cancellationToken).ConfigureAwait(false);
                }

                EnableProtocol(protocol, configuration);
                SetState(protocol, ProtocolRuntimeState.Running);
                PublishListener(protocol, TransferEventKind.ListenerStarted, configuration, TransferResult.Success);
            }
            catch
            {
                SetState(protocol, ProtocolRuntimeState.Faulted);
                PublishListener(protocol, TransferEventKind.ListenerFaulted, configuration, TransferResult.Failed);
                DisableProtocol(protocol);
                if (!AnyProtocolEnabled())
                {
                    StopServer();
                }

                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopProtocolAsync(
        ProtocolKind protocol,
        CancellationToken cancellationToken)
    {
        if (protocol is not (ProtocolKind.Sftp or ProtocolKind.Scp))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (GetState(protocol) is ProtocolRuntimeState.Stopped or ProtocolRuntimeState.Stopping)
            {
                return;
            }

            SetState(protocol, ProtocolRuntimeState.Stopping);
            var configuration = GetProtocolConfiguration(protocol) ?? _listenerConfiguration;
            if (configuration is not null)
            {
                PublishListener(protocol, TransferEventKind.ListenerStopping, configuration, TransferResult.Success);
            }

            DisableProtocol(protocol);
            CancelSessions(protocol);

            if (!AnyProtocolEnabled())
            {
                StopServer();
            }

            SetState(protocol, ProtocolRuntimeState.Stopped);
            if (configuration is not null)
            {
                PublishListener(protocol, TransferEventKind.ListenerStopped, configuration, TransferResult.Success);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Dispose()
    {
        StopServer();
        _lifecycleLock.Dispose();
    }

    private ProtocolValidationResult ValidateConfiguration(
        ProtocolConfiguration configuration,
        bool probePort)
    {
        if (!Directory.Exists(configuration.RootPath))
        {
            return ProtocolValidationResult.Failure("Root folder does not exist.");
        }

        if (!IPAddress.TryParse(configuration.BindAddress, out var address))
        {
            return ProtocolValidationResult.Failure("Bind address is invalid.");
        }

        if (configuration.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            return ProtocolValidationResult.Failure("Port is outside the valid TCP port range.");
        }

        if (_listenerConfiguration is not null
            && !IsSameSharedEndpoint(_listenerConfiguration, configuration))
        {
            return ProtocolValidationResult.Failure(
                "SFTP and SCP share one SSH listener; bind address, port, and root folder must match.");
        }

        if (probePort)
        {
            try
            {
                using var listener = new TcpListener(address, configuration.Port);
                listener.Start();
                listener.Stop();
            }
            catch (SocketException ex)
            {
                return ProtocolValidationResult.Failure($"Port is not available: {ex.Message}");
            }
        }

        return ProtocolValidationResult.Success;
    }

    private async Task StartListenerAsync(
        ProtocolConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var address = IPAddress.Parse(configuration.BindAddress);
        var pem = await _hostKeyStore.GetOrCreateRsaKeyPemAsync(cancellationToken).ConfigureAwait(false);
        var server = new SshServer(new StartingInfo(address, configuration.Port, "SSH-2.0-ProtoHydra"));
        server.AddHostKey("rsa-sha2-256", pem);
        server.AddHostKey("rsa-sha2-512", pem);
        server.ConnectionAccepted += HandleConnectionAccepted;
        server.ExceptionRasied += HandleServerException;

        try
        {
            server.Start();
            _server = server;
            _listenerConfiguration = configuration;
        }
        catch
        {
            server.ConnectionAccepted -= HandleConnectionAccepted;
            server.ExceptionRasied -= HandleServerException;
            server.Stop();
            server.Dispose();
            _server = null;
            _listenerConfiguration = null;
            throw;
        }
    }

    // FxSsh 1.3.0 does not expose the remote endpoint publicly; the socket field is
    // the only way to attribute transfer events to a client address.
    private static readonly FieldInfo? SessionSocketField =
        typeof(Session).GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance);

    private static IPAddress? GetClientAddress(CommandRequestedArgs args)
    {
        try
        {
            return SessionSocketField?.GetValue(args.AttachedUserauthArgs.Session) is Socket socket
                && socket.RemoteEndPoint is IPEndPoint endpoint
                    ? endpoint.Address
                    : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void HandleConnectionAccepted(object? sender, Session session)
    {
        session.ServiceRegistered += HandleServiceRegistered;
    }

    private void HandleServiceRegistered(object? sender, SshService service)
    {
        if (service is UserauthService authService)
        {
            authService.Userauth += (_, args) => args.Result = true;
            return;
        }

        if (service is ConnectionService connectionService)
        {
            connectionService.CommandOpened += HandleCommandOpened;
        }
    }

    private void HandleCommandOpened(object? sender, CommandRequestedArgs args)
    {
        if (args.ShellType == "subsystem" && args.CommandText == "sftp")
        {
            HandleSftpSubsystem(args);
            return;
        }

        if (args.ShellType == "exec")
        {
            HandleScpExec(args);
            return;
        }

        if (args.ShellType == "shell")
        {
            HandleScpShell(args);
            return;
        }

        var command = args.ShellType == "subsystem"
            ? $"subsystem:{args.CommandText}"
            : args.ShellType;
        RejectChannel(
            args,
            ProtocolKind.Sftp,
            command,
            "Only subsystem:sftp, exec scp, and SCP-mode shell sessions are supported.");
    }

    private void HandleSftpSubsystem(CommandRequestedArgs args)
    {
        ProtocolConfiguration? configuration;
        lock (_sessionLock)
        {
            configuration = _sftpEnabled ? _sftpConfiguration : null;
        }

        if (configuration is null)
        {
            RejectChannel(
                args,
                ProtocolKind.Sftp,
                "subsystem:sftp",
                "SFTP subsystem is disabled.");
            return;
        }

        var clientAddress = GetClientAddress(args);
        PublishAuthentication(ProtocolKind.Sftp, args.AttachedUserauthArgs.Username, clientAddress);
        PublishCommand(
            ProtocolKind.Sftp,
            args.AttachedUserauthArgs.Username,
            "subsystem:sftp",
            TransferResult.Success,
            "SFTP subsystem accepted.",
            clientAddress);

        var input = new FxSshChannelInputStream();
        var output = new FxSshChannelOutputStream(args.Channel);
        args.Channel.DataReceived += (_, data) => input.OnData(data);
        args.Channel.EofReceived += (_, _) => input.Complete();
        args.Channel.CloseReceived += (_, _) => input.Complete();

        var sessionTokenSource = RegisterSession(ProtocolKind.Sftp);

        _ = Task.Run(async () =>
        {
            try
            {
                var options = Options.Create(new SFTPServerOptions
                {
                    Root = configuration.RootPath,
                    MaxMessageSize = 1024 * 1024
                });
                using var server = new SFTPServer(
                    options,
                    input,
                    output,
                    new RootedSftpHandler(
                        configuration.RootPath,
                        _eventBus,
                        args.AttachedUserauthArgs.Username,
                        clientAddress));
                await server.Run(sessionTokenSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionTokenSource.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Publish(
                    ProtocolKind.Sftp,
                    TransferEventKind.ListenerFaulted,
                    args.AttachedUserauthArgs.Username,
                    "subsystem:sftp",
                    null,
                    null,
                    null,
                    null,
                    TransferResult.Failed,
                    ex.Message,
                    clientAddress);
            }
            finally
            {
                // Proactively close the channel with exit-status 0, mirroring the SCP path.
                // FxSsh does not answer the client's CHANNEL_CLOSE on its own, so without
                // this a client that waits for the server to tear down the channel (e.g.
                // Brocade FOS firmwaredownload) stalls on a ~30s timeout per file.
                try
                {
                    args.Channel.SendEof();
                    args.Channel.SendClose(0);
                }
                catch (Exception)
                {
                    // Channel already gone (client disconnected or server stopping).
                }

                input.Dispose();
                output.Dispose();
                UnregisterSession(ProtocolKind.Sftp, sessionTokenSource);
                sessionTokenSource.Dispose();
            }
        }, CancellationToken.None);
    }

    private void HandleScpExec(CommandRequestedArgs args)
    {
        ProtocolConfiguration? configuration;
        lock (_sessionLock)
        {
            configuration = _scpEnabled ? _scpConfiguration : null;
        }

        ScpCommand command;
        try
        {
            command = ScpCommandParser.Parse(args.CommandText);
        }
        catch (Exception ex)
        {
            TrySendScpError(args, ex.Message);
            RejectChannel(args, ProtocolKind.Scp, args.CommandText, ex.Message);
            return;
        }

        if (configuration is null)
        {
            TrySendScpError(args, "Legacy SCP exec is disabled.");
            RejectChannel(args, ProtocolKind.Scp, args.CommandText, "Legacy SCP exec is disabled.");
            return;
        }

        var clientAddress = GetClientAddress(args);
        PublishAuthentication(ProtocolKind.Scp, args.AttachedUserauthArgs.Username, clientAddress);
        PublishCommand(
            ProtocolKind.Scp,
            args.AttachedUserauthArgs.Username,
            args.CommandText,
            TransferResult.Success,
            "SCP exec accepted.",
            clientAddress);

        var input = new FxSshChannelInputStream();
        var output = new FxSshChannelOutputStream(args.Channel);
        args.Channel.DataReceived += (_, data) => input.OnData(data);
        args.Channel.EofReceived += (_, _) => input.Complete();
        args.Channel.CloseReceived += (_, _) => input.Complete();

        var sessionTokenSource = RegisterSession(ProtocolKind.Scp);

        _ = Task.Run(async () =>
        {
            try
            {
                var session = new ScpServerSession(
                    input,
                    output,
                    configuration.RootPath,
                    _eventBus,
                    args.AttachedUserauthArgs.Username,
                    clientAddress);
                await session.RunAsync(command, sessionTokenSource.Token).ConfigureAwait(false);
                args.Channel.SendEof();
                args.Channel.SendClose(0);
            }
            catch (Exception ex)
            {
                try
                {
                    await new ScpProtocolStream(input, output)
                        .WriteErrorAsync(ex.Message, fatal: true, sessionTokenSource.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                }

                Publish(
                    ProtocolKind.Scp,
                    TransferEventKind.RequestRejected,
                    args.AttachedUserauthArgs.Username,
                    args.CommandText,
                    command.Path,
                    null,
                    null,
                    null,
                    TransferResult.Failed,
                    ex.Message,
                    clientAddress);
                args.Channel.SendClose(1);
            }
            finally
            {
                input.Dispose();
                output.Dispose();
                UnregisterSession(ProtocolKind.Scp, sessionTokenSource);
                sessionTokenSource.Dispose();
            }
        }, CancellationToken.None);
    }

    private void HandleScpShell(CommandRequestedArgs args)
    {
        ProtocolConfiguration? configuration;
        lock (_sessionLock)
        {
            configuration = _scpEnabled ? _scpConfiguration : null;
        }

        if (configuration is null)
        {
            RejectChannel(args, ProtocolKind.Scp, "shell", "SCP-mode shell is disabled.");
            return;
        }

        var clientAddress = GetClientAddress(args);
        PublishAuthentication(ProtocolKind.Scp, args.AttachedUserauthArgs.Username, clientAddress);
        PublishCommand(
            ProtocolKind.Scp,
            args.AttachedUserauthArgs.Username,
            "shell",
            TransferResult.Success,
            "SCP-mode shell accepted.",
            clientAddress);

        var input = new FxSshChannelInputStream();
        var output = new FxSshChannelOutputStream(args.Channel);
        args.Channel.DataReceived += (_, data) => input.OnData(data);
        args.Channel.EofReceived += (_, _) => input.Complete();
        args.Channel.CloseReceived += (_, _) => input.Complete();

        var sessionTokenSource = RegisterSession(ProtocolKind.Scp);

        _ = Task.Run(async () =>
        {
            try
            {
                var session = new ScpShellSession(
                    input,
                    output,
                    configuration.RootPath,
                    _eventBus,
                    args.AttachedUserauthArgs.Username,
                    clientAddress);
                await session.RunAsync(sessionTokenSource.Token).ConfigureAwait(false);
                args.Channel.SendEof();
                args.Channel.SendClose(0);
            }
            catch (OperationCanceledException) when (sessionTokenSource.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Publish(
                    ProtocolKind.Scp,
                    TransferEventKind.RequestRejected,
                    args.AttachedUserauthArgs.Username,
                    "shell",
                    null,
                    null,
                    null,
                    null,
                    TransferResult.Failed,
                    ex.Message,
                    clientAddress);
                args.Channel.SendClose(1);
            }
            finally
            {
                input.Dispose();
                output.Dispose();
                UnregisterSession(ProtocolKind.Scp, sessionTokenSource);
                sessionTokenSource.Dispose();
            }
        }, CancellationToken.None);
    }

    private CancellationTokenSource RegisterSession(ProtocolKind protocol)
    {
        var tokenSource = new CancellationTokenSource();
        lock (_sessionLock)
        {
            _sessionTokenSources.Add(new SessionTokenRegistration(protocol, tokenSource));
        }

        return tokenSource;
    }

    private void UnregisterSession(
        ProtocolKind protocol,
        CancellationTokenSource tokenSource)
    {
        lock (_sessionLock)
        {
            _sessionTokenSources.RemoveAll(registration =>
                registration.Protocol == protocol
                && ReferenceEquals(registration.TokenSource, tokenSource));
        }
    }

    private void CancelSessions(ProtocolKind? protocol)
    {
        List<CancellationTokenSource> tokenSources;
        lock (_sessionLock)
        {
            tokenSources = _sessionTokenSources
                .Where(registration => protocol is null || registration.Protocol == protocol.Value)
                .Select(registration => registration.TokenSource)
                .ToList();
        }

        foreach (var tokenSource in tokenSources)
        {
            tokenSource.Cancel();
        }
    }

    private void StopServer()
    {
        CancelSessions(protocol: null);

        if (_server is not null)
        {
            _server.ConnectionAccepted -= HandleConnectionAccepted;
            _server.ExceptionRasied -= HandleServerException;
            _server.Stop();
            _server.Dispose();
            _server = null;
        }

        _listenerConfiguration = null;
    }

    private void HandleServerException(object? sender, Exception exception)
    {
        if (IsBenignConnectionException(exception))
        {
            return;
        }

        bool sftpEnabled;
        bool scpEnabled;
        lock (_sessionLock)
        {
            sftpEnabled = _sftpEnabled;
            scpEnabled = _scpEnabled;
        }

        if (sftpEnabled)
        {
            PublishListenerIfConfigured(
                ProtocolKind.Sftp,
                TransferEventKind.ListenerFaulted,
                TransferResult.Failed,
                exception.Message);
        }

        if (scpEnabled)
        {
            PublishListenerIfConfigured(
                ProtocolKind.Scp,
                TransferEventKind.ListenerFaulted,
                TransferResult.Failed,
                exception.Message);
        }
    }

    private static bool IsBenignConnectionException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // A client dropping its connection surfaces as SshConnectionException/ConnectionLost;
            // that is a per-client disconnect, not a listener fault.
            if (current is SshConnectionException sshException
                && sshException.DisconnectReason is DisconnectReason.ConnectionLost
                    or DisconnectReason.ByApplication
                    or DisconnectReason.None)
            {
                return true;
            }

            if (current is SocketException socketException
                && socketException.SocketErrorCode is SocketError.ConnectionAborted
                    or SocketError.ConnectionReset
                    or SocketError.Interrupted
                    or SocketError.NotConnected
                    or SocketError.OperationAborted
                    or SocketError.Shutdown)
            {
                return true;
            }

            if (current is IOException
                && current.InnerException is SocketException)
            {
                continue;
            }
        }

        return false;
    }

    private void EnableProtocol(
        ProtocolKind protocol,
        ProtocolConfiguration configuration)
    {
        lock (_sessionLock)
        {
            if (protocol == ProtocolKind.Sftp)
            {
                _sftpEnabled = true;
                _sftpConfiguration = configuration;
            }
            else
            {
                _scpEnabled = true;
                _scpConfiguration = configuration;
            }
        }
    }

    private void DisableProtocol(ProtocolKind protocol)
    {
        lock (_sessionLock)
        {
            if (protocol == ProtocolKind.Sftp)
            {
                _sftpEnabled = false;
                _sftpConfiguration = null;
            }
            else
            {
                _scpEnabled = false;
                _scpConfiguration = null;
            }
        }
    }

    private ProtocolConfiguration? GetProtocolConfiguration(ProtocolKind protocol)
    {
        lock (_sessionLock)
        {
            return protocol == ProtocolKind.Sftp ? _sftpConfiguration : _scpConfiguration;
        }
    }

    private bool AnyProtocolEnabled()
    {
        lock (_sessionLock)
        {
            return _sftpEnabled || _scpEnabled;
        }
    }

    private void SetState(
        ProtocolKind protocol,
        ProtocolRuntimeState state)
    {
        if (protocol == ProtocolKind.Sftp)
        {
            _sftpState = state;
        }
        else
        {
            _scpState = state;
        }
    }

    private static void TrySendScpError(CommandRequestedArgs args, string message)
    {
        try
        {
            // SCP clients expect a fatal-error record (0x02) so they can show the reason.
            args.Channel.SendData([2, .. Encoding.UTF8.GetBytes($"scp: {message}\n")]);
        }
        catch
        {
        }
    }

    private void RejectChannel(
        CommandRequestedArgs args,
        ProtocolKind protocol,
        string? command,
        string message)
    {
        Publish(
            protocol,
            TransferEventKind.RequestRejected,
            args.AttachedUserauthArgs.Username,
            command,
            null,
            null,
            null,
            null,
            TransferResult.Rejected,
            message,
            GetClientAddress(args));
        args.Channel.SendClose(1);
    }

    private void PublishAuthentication(
        ProtocolKind protocol,
        string? username,
        IPAddress? sourceAddress)
        => Publish(
            protocol,
            TransferEventKind.AuthenticationAttempt,
            username,
            "ssh",
            null,
            null,
            null,
            null,
            TransferResult.Success,
            "Accepted by Accept-Any policy.",
            sourceAddress);

    private void PublishCommand(
        ProtocolKind protocol,
        string? username,
        string? command,
        TransferResult result,
        string? message,
        IPAddress? sourceAddress)
        => Publish(
            protocol,
            TransferEventKind.CommandReceived,
            username,
            command,
            null,
            null,
            null,
            null,
            result,
            message,
            sourceAddress);

    private void PublishListenerIfConfigured(
        ProtocolKind protocol,
        TransferEventKind eventKind,
        TransferResult result,
        string? message = null)
    {
        var configuration = GetProtocolConfiguration(protocol) ?? _listenerConfiguration;
        if (configuration is not null)
        {
            PublishListener(protocol, eventKind, configuration, result, message);
        }
    }

    private void PublishListener(
        ProtocolKind protocol,
        TransferEventKind eventKind,
        ProtocolConfiguration configuration,
        TransferResult result,
        string? message = null)
        => Publish(
            protocol,
            eventKind,
            null,
            null,
            null,
            null,
            null,
            null,
            result,
            message ?? $"{configuration.BindAddress}:{configuration.Port.ToString(CultureInfo.InvariantCulture)}");

    private void Publish(
        ProtocolKind protocol,
        TransferEventKind eventKind,
        string? username,
        string? command,
        string? relativePath,
        TransferDirection? direction,
        long? byteCount,
        TimeSpan? duration,
        TransferResult result,
        string? message,
        IPAddress? sourceAddress = null)
        => _eventBus.TryPublish(new TransferEvent(
            DateTimeOffset.UtcNow,
            protocol,
            eventKind,
            sourceAddress,
            username,
            command,
            relativePath,
            direction,
            byteCount,
            duration,
            result,
            message,
            null));

    private static bool IsSameSharedEndpoint(
        ProtocolConfiguration active,
        ProtocolConfiguration requested)
    {
        if (!IPAddress.Parse(active.BindAddress).Equals(IPAddress.Parse(requested.BindAddress))
            || active.Port != requested.Port)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            NormalizeRoot(active.RootPath),
            NormalizeRoot(requested.RootPath),
            comparison);
    }

    private static string NormalizeRoot(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record SessionTokenRegistration(
        ProtocolKind Protocol,
        CancellationTokenSource TokenSource);
}
