using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EasyShare.Core;

/// <summary>
/// מסייע לאיתור חכם של כתובת ה-IP המקומית של המחשב ברשת הפעילה,
/// תוך סינון מתאמי רשת וירטואליים (Hyper-V, VMware, VirtualBox, WSL).
/// </summary>
public static class NetworkHelper
{
    /// <summary>
    /// מאתר את כתובת ה-IPv4 האמיתית הפעילה של המחשב ברשת המקומית.
    /// </summary>
    public static string GetLocalIpAddress() => GetPreferredLocalIpAddress();

    /// <summary>
    /// מאתר את כתובת ה-IPv4 האמיתית הפעילה של המחשב ברשת המקומית.
    /// משתמש ראשית בבדיקת UDP Socket Probe מהירה, ונופל לבדיקת ממשקים במידת הצורך.
    /// </summary>
    public static string GetPreferredLocalIpAddress()
    {
        try
        {
            // UDP Probe אל עבר יעד ציבורי - ה-OS בוחר מיידית את ממשק הרשת עם הניתוב הפעיל (Default Gateway)
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint && !IPAddress.IsLoopback(endPoint.Address))
            {
                string ip = endPoint.Address.ToString();
                if (!string.IsNullOrWhiteSpace(ip) && !ip.StartsWith("127.") && !ip.StartsWith("169.254."))
                {
                    return ip;
                }
            }
        }
        catch
        {
            // במקרה של חוסר גישה ליעד חיצוני, נעבור לסריקה מושכלת של ממשקי הרשת
        }

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses.Count > 0)
                .ThenByDescending(nic => nic.Speed);

            foreach (var nic in interfaces)
            {
                string name = nic.Name.ToLowerInvariant();
                string desc = nic.Description.ToLowerInvariant();

                // סינון מתאמים וירטואליים ידועים
                if (IsVirtualAdapter(name, desc))
                {
                    continue;
                }

                var ipProps = nic.GetIPProperties();
                var ipv4Info = ipProps.UnicastAddresses
                    .FirstOrDefault(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                                           !IPAddress.IsLoopback(addr.Address) &&
                                           !addr.Address.ToString().StartsWith("169.254."));

                if (ipv4Info != null)
                {
                    return ipv4Info.Address.ToString();
                }
            }
        }
        catch
        {
            // שגיאת גישה לממשקי הרשת
        }

        return "127.0.0.1";
    }

    private static bool IsVirtualAdapter(string name, string description)
    {
        string[] virtualKeywords =
        [
            "virtual", "hyper-v", "vmware", "virtualbox", "vbox", "wsl", "vethernet",
            "tap-windows", "npcap", "pseudo", "loopback", "teredo", "docker", "tailscale", "zerotier"
        ];

        return virtualKeywords.Any(k => name.Contains(k) || description.Contains(k));
    }
}
