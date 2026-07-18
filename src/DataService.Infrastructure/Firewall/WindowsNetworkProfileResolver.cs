using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace DataService.Infrastructure.Firewall;

/// <summary>
/// Maps listener bind addresses to the firewall profile of the interface they belong to,
/// using the Network List Manager. A probe bound to a specific NIC can then be evaluated
/// against exactly that NIC's profile instead of guessing across all active profiles.
/// </summary>
public static class WindowsNetworkProfileResolver
{
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int NlmEnumNetworkConnected = 1;

    public static IReadOnlyList<FirewallProbeTarget> CreateTargets(IReadOnlyCollection<FirewallPortProbe> probes)
    {
        var targets = new List<FirewallProbeTarget>(probes.Count);
        IReadOnlyDictionary<Guid, FirewallProfile>? adapterProfiles = null;

        foreach (var probe in probes)
        {
            if (!IPAddress.TryParse(probe.BindAddress ?? "", out var address)
                || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.IPv6Any))
            {
                targets.Add(new FirewallProbeTarget(probe, IsLoopback: false, Profile: null));
                continue;
            }

            if (IPAddress.IsLoopback(address))
            {
                targets.Add(new FirewallProbeTarget(probe, IsLoopback: true, Profile: null));
                continue;
            }

            adapterProfiles ??= OperatingSystem.IsWindows()
                ? TryReadAdapterProfiles()
                : new Dictionary<Guid, FirewallProfile>();
            targets.Add(new FirewallProbeTarget(
                probe,
                IsLoopback: false,
                Profile: FindProfileForAddress(address, adapterProfiles)));
        }

        return targets;
    }

    private static FirewallProfile? FindProfileForAddress(
        IPAddress address,
        IReadOnlyDictionary<Guid, FirewallProfile> adapterProfiles)
    {
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                var hasAddress = networkInterface
                    .GetIPProperties()
                    .UnicastAddresses
                    .Any(unicast => unicast.Address.Equals(address));
                if (!hasAddress)
                {
                    continue;
                }

                if (Guid.TryParse(networkInterface.Id, out var adapterId)
                    && adapterProfiles.TryGetValue(adapterId, out var profile))
                {
                    return profile;
                }

                return null;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyDictionary<Guid, FirewallProfile> TryReadAdapterProfiles()
    {
        var map = new Dictionary<Guid, FirewallProfile>();
        try
        {
            var managerType = Type.GetTypeFromCLSID(NetworkListManagerClsid);
            if (managerType is null)
            {
                return map;
            }

            dynamic manager = Activator.CreateInstance(managerType)
                ?? throw new InvalidOperationException("Network List Manager could not be created.");
            foreach (dynamic network in manager.GetNetworks(NlmEnumNetworkConnected))
            {
                var profile = (int)network.GetCategory() switch
                {
                    2 => FirewallProfile.Domain,
                    1 => FirewallProfile.Private,
                    _ => FirewallProfile.Public
                };
                foreach (dynamic connection in network.GetNetworkConnections())
                {
                    map[(Guid)connection.GetAdapterId()] = profile;
                }
            }
        }
        catch (Exception)
        {
        }

        return map;
    }
}
