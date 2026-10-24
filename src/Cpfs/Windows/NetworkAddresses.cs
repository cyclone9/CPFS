using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Cpfs.Core.Connect;

namespace Cpfs.Windows;

// The PC's names and the addresses of its connected adapters. Reads only; needs no administrator rights.
internal static class NetworkAddresses
{
    // The DNS host name, which mDNS announces as <name>.local; falls back to the NetBIOS name.
    public static string HostName()
    {
        try
        {
            var name = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }
        catch (SocketException ex)
        {
            AppLog.Write("connect", "Dns.GetHostName failed: " + ex.Message);
        }

        return Environment.MachineName;
    }

    public static List<AdapterAddress> Read()
    {
        var result = new List<AdapterAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException ex)
            {
                AppLog.Write("connect", "Adapter " + nic.Name + ": " + ex.Message);
                continue;
            }

            string? suffix = null;
            try
            {
                suffix = props.DnsSuffix;
            }
            catch (PlatformNotSupportedException)
            {
            }

            foreach (var unicast in props.UnicastAddresses)
            {
                result.Add(new AdapterAddress(nic.Name, unicast.Address, suffix));
            }
        }

        return result;
    }
}
