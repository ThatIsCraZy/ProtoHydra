using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataService.Core.Configuration;

namespace DataService.Infrastructure.Firewall;

/// <summary>
/// Creates temporary, port-scoped Windows Firewall allow rules through a short elevated
/// PowerShell helper. The helper runs exclusively via -EncodedCommand (no script file on
/// disk that could be tampered with), removes orphaned rules from earlier crashed sessions,
/// waits without polling for the owner process to exit or a stop signal, and confirms rule
/// creation through a state file so the caller does not have to guess.
/// </summary>
public sealed class WindowsTemporaryFirewallRuleService : IFirewallTemporaryRuleService, IDisposable
{
    private static readonly TimeSpan CreationConfirmationTimeout = TimeSpan.FromSeconds(90);

    private readonly object _lock = new();
    private EventWaitHandle? _stopEvent;

    public async Task<FirewallTemporaryRuleResult> StartTemporaryApplicationRuleAsync(
        string executablePath,
        int ownerProcessId,
        IReadOnlyCollection<FirewallPortProbe> ports,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            return new FirewallTemporaryRuleResult(false, "Temporary firewall fix is only available on Windows.");
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return new FirewallTemporaryRuleResult(false, "Executable path is not available.");
        }

        var tcpPorts = FormatPorts(ports, FirewallTransportProtocol.Tcp);
        var udpPorts = FormatPorts(ports, FirewallTransportProtocol.Udp);
        if (tcpPorts.Length == 0 && udpPorts.Length == 0)
        {
            return new FirewallTemporaryRuleResult(false, "No startable listener ports are available to allow.");
        }

        var helperDirectory = ApplicationDataPaths.GetDirectory("Firewall");
        Directory.CreateDirectory(helperDirectory);
        var logPath = Path.Combine(helperDirectory, "temporary-firewall-rule.log");
        var statePath = Path.Combine(helperDirectory, "temporary-firewall-rule.state");
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }

        var ruleBaseName = BuildRuleBaseName(executablePath, ownerProcessId);
        var stopEventName = $"Local\\ProtoHydra.FirewallFix.Stop.{ownerProcessId.ToString(CultureInfo.InvariantCulture)}";
        lock (_lock)
        {
            _stopEvent?.Dispose();
            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName);
            _stopEvent.Reset();
        }

        File.AppendAllText(
            logPath,
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] Launching elevated firewall helper for {executablePath} (TCP: {tcpPorts}; UDP: {udpPorts}).{Environment.NewLine}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = string.Join(
                " ",
                "-NoProfile",
                "-ExecutionPolicy Bypass",
                "-WindowStyle Hidden",
                "-EncodedCommand",
                BuildEncodedHelperCommand(
                    ownerProcessId,
                    executablePath,
                    ruleBaseName,
                    tcpPorts,
                    udpPorts,
                    logPath,
                    statePath,
                    stopEventName)),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DisposeStopEvent();
            return new FirewallTemporaryRuleResult(false, $"Temporary firewall fix failed: {ex.Message}");
        }

        var state = await WaitForStateAsync(statePath, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new FirewallTemporaryRuleResult(
                false,
                "The elevated helper did not confirm the rule in time. If you approved the prompt late, refresh the firewall status.");
        }

        if (!state.StartsWith("created", StringComparison.OrdinalIgnoreCase))
        {
            DisposeStopEvent();
            return new FirewallTemporaryRuleResult(false, $"Temporary firewall fix failed: {state}");
        }

        var message = $"Temporary allow rules active (TCP: {FormatOrDash(tcpPorts)}; UDP: {FormatOrDash(udpPorts)}). "
            + "They are removed automatically when ProtoHydra exits or when the fix is undone.";
        var policyWarning = TryGetPolicyWarning();
        if (policyWarning is not null)
        {
            message = policyWarning + "\n" + message;
        }

        return new FirewallTemporaryRuleResult(true, message);
    }

    public Task<FirewallTemporaryRuleResult> StopTemporaryApplicationRuleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_stopEvent is null)
            {
                return Task.FromResult(new FirewallTemporaryRuleResult(false, "No temporary firewall fix is active."));
            }

            _stopEvent.Set();
            _stopEvent.Dispose();
            _stopEvent = null;
        }

        return Task.FromResult(new FirewallTemporaryRuleResult(
            true,
            "Stop signal sent. The temporary firewall rules are being removed."));
    }

    public void Dispose()
        => DisposeStopEvent();

    private void DisposeStopEvent()
    {
        lock (_lock)
        {
            _stopEvent?.Dispose();
            _stopEvent = null;
        }
    }

    private static async Task<string?> WaitForStateAsync(string statePath, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(CreationConfirmationTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(statePath))
            {
                try
                {
                    var state = (await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false)).Trim();
                    if (state.Length > 0)
                    {
                        return state;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static string? TryGetPolicyWarning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
            {
                return null;
            }

            dynamic policy = Activator.CreateInstance(policyType)!;
            return (int)policy.LocalPolicyModifyState switch
            {
                1 => "⚠ Group Policy overrides local firewall rules — the temporary rules may have no effect.",
                2 => "⚠ 'Block all incoming connections' is active — the temporary rules are ignored until that setting is turned off.",
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FormatPorts(IReadOnlyCollection<FirewallPortProbe> ports, FirewallTransportProtocol transport)
        => string.Join(
            ",",
            ports
                .Where(probe => probe.TransportProtocol == transport)
                .Select(probe => probe.Port)
                .Where(port => port is > 0 and <= 65535)
                .Distinct()
                .OrderBy(port => port)
                .Select(port => port.ToString(CultureInfo.InvariantCulture)));

    private static string FormatOrDash(string ports)
        => ports.Length == 0 ? "—" : ports;

    private static string BuildRuleBaseName(string executablePath, int ownerProcessId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executablePath)))[..12];
        return $"ProtoHydra Temporary Allow {ownerProcessId.ToString(CultureInfo.InvariantCulture)} {hash}";
    }

    private static string Quote(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string BuildEncodedHelperCommand(
        int ownerProcessId,
        string executablePath,
        string ruleBaseName,
        string tcpPorts,
        string udpPorts,
        string logPath,
        string statePath,
        string stopEventName)
    {
        var command = string.Join(
            Environment.NewLine,
            "$OwnerProcessId = " + ownerProcessId.ToString(CultureInfo.InvariantCulture),
            "$ExecutablePath = " + Quote(executablePath),
            "$RuleBaseName = " + Quote(ruleBaseName),
            "$TcpPorts = " + Quote(tcpPorts),
            "$UdpPorts = " + Quote(udpPorts),
            "$LogPath = " + Quote(logPath),
            "$StateFilePath = " + Quote(statePath),
            "$StopEventName = " + Quote(stopEventName),
            HelperScriptBody);
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private const string HelperScriptBody = """
$ErrorActionPreference = 'Stop'

function Write-HelperLog {
    param([string]$Message)
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'
    Add-Content -LiteralPath $LogPath -Value "[$timestamp] $Message"
}

function Remove-RuleQuiet {
    param($Policy, [string]$Name)
    try {
        $Policy.Rules.Remove($Name)
        Write-HelperLog "Removed rule '$Name'."
    }
    catch {
    }
}

function Add-HydraRule {
    param($Policy, [string]$Name, [int]$Protocol, [string]$Ports)
    $rule = New-Object -ComObject HNetCfg.FWRule
    $rule.Name = $Name
    $rule.Description = 'Temporary ProtoHydra allow rule. Removed automatically when the owner process exits.'
    $rule.ApplicationName = $ExecutablePath
    $rule.Protocol = $Protocol
    $rule.LocalPorts = $Ports
    $rule.Direction = 1
    $rule.Action = 1
    $rule.Enabled = $true
    $rule.Profiles = 2147483647
    $Policy.Rules.Add($rule)
    Write-HelperLog "Created rule '$Name' (protocol $Protocol, ports $Ports)."
}

try {
    Write-HelperLog "Elevated temporary firewall rule helper started (owner PID $OwnerProcessId)."
    $policy = New-Object -ComObject HNetCfg.FwPolicy2

    $staleNames = @($policy.Rules | Where-Object { $_.Name -like 'ProtoHydra Temporary Allow *' } | ForEach-Object { $_.Name })
    foreach ($staleName in $staleNames) {
        # Rule name layout: "ProtoHydra Temporary Allow <pid> <hash> <TCP|UDP>" -> PID is token index 3.
        $parts = $staleName.Split(' ')
        $rulePid = 0
        if ($parts.Length -ge 4 -and [int]::TryParse($parts[3], [ref]$rulePid)) {
            if (-not (Get-Process -Id $rulePid -ErrorAction SilentlyContinue)) {
                Write-HelperLog "Removing orphaned rule from PID $rulePid."
                Remove-RuleQuiet $policy $staleName
            }
        }
    }

    Remove-RuleQuiet $policy "$RuleBaseName TCP"
    Remove-RuleQuiet $policy "$RuleBaseName UDP"
    if ($TcpPorts) { Add-HydraRule $policy "$RuleBaseName TCP" 6 $TcpPorts }
    if ($UdpPorts) { Add-HydraRule $policy "$RuleBaseName UDP" 17 $UdpPorts }
    Set-Content -LiteralPath $StateFilePath -Value 'created'

    $waitHandles = New-Object 'System.Collections.Generic.List[System.Threading.WaitHandle]'
    $ownerProcess = [System.Diagnostics.Process]::GetProcessById($OwnerProcessId)
    $ownerWait = New-Object System.Threading.ManualResetEvent $false
    $ownerWait.SafeWaitHandle = New-Object Microsoft.Win32.SafeHandles.SafeWaitHandle($ownerProcess.Handle, $false)
    $waitHandles.Add($ownerWait)
    try {
        $waitHandles.Add([System.Threading.EventWaitHandle]::OpenExisting($StopEventName))
    }
    catch {
        Write-HelperLog "Stop event not available: $($_.Exception.Message)"
    }
    [void][System.Threading.WaitHandle]::WaitAny($waitHandles.ToArray())
    Write-HelperLog "Owner process exited or stop was requested."
}
catch {
    Write-HelperLog "Temporary firewall helper failed: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    try { Set-Content -LiteralPath $StateFilePath -Value "failed: $($_.Exception.Message)" } catch { }
}
finally {
    try {
        $policy = New-Object -ComObject HNetCfg.FwPolicy2
        Remove-RuleQuiet $policy "$RuleBaseName TCP"
        Remove-RuleQuiet $policy "$RuleBaseName UDP"
        Write-HelperLog "Temporary firewall rule cleanup finished."
    }
    catch {
        Write-HelperLog "Cleanup failed: $($_.Exception.Message)"
    }
}
""";
}
