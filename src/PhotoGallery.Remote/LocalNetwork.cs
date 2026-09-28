using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PhotoGallery.Remote;

/// <summary>Which addresses count as "this network": the host only answers computers on the local network.</summary>
public static class LocalNetwork
{
    public static bool IsLocal(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 // 10.0.0.0/8
                || b[0] == 172 && b[1] is >= 16 and <= 31 // 172.16.0.0/12
                || b[0] == 192 && b[1] == 168 // 192.168.0.0/16
                || b[0] == 169 && b[1] == 254; // link-local
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                || (b[0] & 0xFE) == 0xFC; // unique local, fc00::/7
        }
        return false;
    }

    /// <summary>This computer's addresses on the local network (to show in Settings), IPv4 first.</summary>
    public static List<IPAddress> Addresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && IsLocal(address) && !IPAddress.IsLoopback(address)
                        && !(address.GetAddressBytes() is [169, 254, ..])) found.Add(address);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return found;
    }
}
