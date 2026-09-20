using System;

namespace EasyShare.Models;

/// <summary>
/// מייצג עמית (מחשב או מכשיר מותקן אחר) ברשת המקומית או בענן.
/// </summary>
public sealed class PeerDevice
{
    public string PeerId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; } = 2121;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool IsOnline { get; set; } = true;
    public string OsDescription { get; set; } = string.Empty;
    public string DiscoverySource { get; set; } = "LAN"; // "LAN" or "Internet"
    public string? InternetUrl { get; set; } = null;

    public string EffectiveUrl => !string.IsNullOrWhiteSpace(InternetUrl) 
        ? InternetUrl.TrimEnd('/') 
        : $"http://{IpAddress}:{Port}";

    public string SourceBadge => DiscoverySource == "Internet" ? "אינטרנט 🌐" : "רשת מקומית 🏠";
    public string DisplayText => $"{DeviceName} ({PeerId}) [{(DiscoverySource == "Internet" ? "אינטרנט" : "LAN")}]";
    public string AddressText => !string.IsNullOrWhiteSpace(InternetUrl) ? InternetUrl : $"{IpAddress}:{Port}";
}
