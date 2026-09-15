using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace EasyShare.Core;

/// <summary>
/// מנהל פתיחת פורטים אוטומטית בראוטר הביתי באמצעות פרוטוקול UPnP IGD (SSDP + SOAP).
/// מאפשר לחלץ את כתובת ה-WAN החיצונית ולפתוח את פורט 2121 לגישה ישירה מהאינטרנט ללא שרת ביניים.
/// </summary>
public static class UpnpPortForwarder
{
    private static string? _controlUrl;
    private static string? _serviceType;

    public static string? ExternalPublicIp { get; private set; }
    public static bool IsPortForwarded { get; private set; }

    /// <summary>
    /// מגלה את הראוטר ב-SSDP, פותח את הפורט המבוקש ומחלץ את ה-IP החיצוני.
    /// </summary>
    public static async Task<bool> ForwardPortAsync(int port, string description = "EasyShare PRO Server", int timeoutMs = 4000)
    {
        try
        {
            var (controlUrl, serviceType) = await DiscoverRouterControlUrlAsync(timeoutMs);
            if (string.IsNullOrEmpty(controlUrl) || string.IsNullOrEmpty(serviceType))
            {
                return false;
            }

            _controlUrl = controlUrl;
            _serviceType = serviceType;

            // 1. שליפת כתובת ה-IP הציבורית של הראוטר
            ExternalPublicIp = await GetExternalIpAsync(controlUrl, serviceType);

            // 2. מציאת כתובת ה-IP המקומית של המחשב
            string localIp = NetworkHelper.GetLocalIpAddress();

            // 3. שליחת בקשת SOAP AddPortMapping
            bool success = await SendAddPortMappingAsync(controlUrl, serviceType, localIp, port, description);
            IsPortForwarded = success;
            return success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// מוחק את מיפוי הפורט מהראוטר בעת סגירת התוכנה.
    /// </summary>
    public static async Task<bool> DeletePortMappingAsync(int port)
    {
        if (string.IsNullOrEmpty(_controlUrl) || string.IsNullOrEmpty(_serviceType)) return false;

        try
        {
            string soap =
                $"<?xml version=\"1.0\"?>\r\n" +
                $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                $"<s:Body>\r\n" +
                $"<u:DeletePortMapping xmlns:u=\"{_serviceType}\">\r\n" +
                $"<NewRemoteHost></NewRemoteHost>\r\n" +
                $"<NewExternalPort>{port}</NewExternalPort>\r\n" +
                $"<NewProtocol>TCP</NewProtocol>\r\n" +
                $"</u:DeletePortMapping>\r\n" +
                $"</s:Body>\r\n" +
                $"</s:Envelope>";

            await PostSoapAsync(_controlUrl, _serviceType, "DeletePortMapping", soap);
            IsPortForwarded = false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(string? ControlUrl, string? ServiceType)> DiscoverRouterControlUrlAsync(int timeoutMs)
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        udp.EnableBroadcast = true;

        string req =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n\r\n";

        byte[] reqBytes = Encoding.ASCII.GetBytes(req);
        var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        await udp.SendAsync(reqBytes, reqBytes.Length, ep);

        var cts = new CancellationTokenSource(timeoutMs);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var receiveTask = udp.ReceiveAsync();
                var completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, cts.Token));
                if (completedTask != receiveTask) break;

                var result = await receiveTask;
                string response = Encoding.ASCII.GetString(result.Buffer);

                string? location = ExtractHeader(response, "LOCATION");
                if (!string.IsNullOrEmpty(location))
                {
                    var (ctrl, st) = await ParseDeviceDescriptionAsync(location);
                    if (!string.IsNullOrEmpty(ctrl)) return (ctrl, st);
                }
            }
            catch { break; }
        }

        return (null, null);
    }

    private static async Task<(string? ControlUrl, string? ServiceType)> ParseDeviceDescriptionAsync(string locationUrl)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string xml = await http.GetStringAsync(locationUrl);
            var doc = XDocument.Parse(xml);

            var baseUri = new Uri(locationUrl);

            // חיפוש שירות WANIPConnection או WANPPPConnection
            foreach (var service in doc.Descendants())
            {
                string? sType = service.Element(service.Name.Namespace + "serviceType")?.Value;
                if (sType != null && (sType.Contains(":WANIPConnection:") || sType.Contains(":WANPPPConnection:")))
                {
                    string? ctrlPath = service.Element(service.Name.Namespace + "controlURL")?.Value;
                    if (!string.IsNullOrEmpty(ctrlPath))
                    {
                        var absoluteCtrlUrl = new Uri(baseUri, ctrlPath).ToString();
                        return (absoluteCtrlUrl, sType);
                    }
                }
            }
        }
        catch { }

        return (null, null);
    }

    private static async Task<string?> GetExternalIpAsync(string controlUrl, string serviceType)
    {
        try
        {
            string soap =
                $"<?xml version=\"1.0\"?>\r\n" +
                $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                $"<s:Body>\r\n" +
                $"<u:GetExternalIPAddress xmlns:u=\"{serviceType}\" />\r\n" +
                $"</s:Body>\r\n" +
                $"</s:Envelope>";

            string res = await PostSoapAsync(controlUrl, serviceType, "GetExternalIPAddress", soap);
            int start = res.IndexOf("<NewExternalIPAddress>");
            int end = res.IndexOf("</NewExternalIPAddress>");
            if (start > 0 && end > start)
            {
                return res.Substring(start + 22, end - (start + 22)).Trim();
            }
        }
        catch { }
        return null;
    }

    private static async Task<bool> SendAddPortMappingAsync(string controlUrl, string serviceType, string localIp, int port, string description)
    {
        string soap =
            $"<?xml version=\"1.0\"?>\r\n" +
            $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
            $"<s:Body>\r\n" +
            $"<u:AddPortMapping xmlns:u=\"{serviceType}\">\r\n" +
            $"<NewRemoteHost></NewRemoteHost>\r\n" +
            $"<NewExternalPort>{port}</NewExternalPort>\r\n" +
            $"<NewProtocol>TCP</NewProtocol>\r\n" +
            $"<NewInternalPort>{port}</NewInternalPort>\r\n" +
            $"<NewInternalClient>{localIp}</NewInternalClient>\r\n" +
            $"<NewEnabled>1</NewEnabled>\r\n" +
            $"<NewPortMappingDescription>{description}</NewPortMappingDescription>\r\n" +
            $"<NewLeaseDuration>0</NewLeaseDuration>\r\n" +
            $"</u:AddPortMapping>\r\n" +
            $"</s:Body>\r\n" +
            $"</s:Envelope>";

        string resp = await PostSoapAsync(controlUrl, serviceType, "AddPortMapping", soap);
        return !resp.Contains("errorCode");
    }

    private static async Task<string> PostSoapAsync(string url, string serviceType, string action, string soapBody)
    {
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url);
        request.Headers.Add("SOAPACTION", $"\"{serviceType}#{action}\"");
        request.Content = new System.Net.Http.StringContent(soapBody, Encoding.UTF8, "text/xml");

        var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static string? ExtractHeader(string text, string headerName)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring(headerName.Length + 1).Trim();
            }
        }
        return null;
    }
}
