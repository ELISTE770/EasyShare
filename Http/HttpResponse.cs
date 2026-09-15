using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Http;

/// <summary>
/// אחראי על בנייה ושידור של תגובות HTTP ישירות ל-Stream של ה-Socket,
/// כולל תמיכה מלאה בהזרמת מדיה (HTTP 206 Partial Content / Range Requests).
/// </summary>
public static class HttpResponse
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static Task WriteTextAsync(
        Stream stream,
        string content,
        string contentType = "text/plain; charset=utf-8",
        int statusCode = 200,
        string statusText = "OK",
        CancellationToken ct = default)
        => WriteTextAsync(stream, content, contentType, statusCode, statusText, null, ct);

    public static async Task WriteTextAsync(
        Stream stream,
        string content,
        string contentType,
        int statusCode,
        string statusText,
        Dictionary<string, string>? customHeaders,
        CancellationToken ct = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        await WriteHeadersAsync(stream, statusCode, statusText, contentType, bytes.Length, customHeaders, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static Task WriteJsonAsync<T>(
        Stream stream,
        T data,
        int statusCode = 200,
        string statusText = "OK",
        CancellationToken ct = default)
        => WriteJsonAsync(stream, data, statusCode, statusText, null, ct);

    public static async Task WriteJsonAsync<T>(
        Stream stream,
        T data,
        int statusCode,
        string statusText,
        Dictionary<string, string>? customHeaders,
        CancellationToken ct = default)
    {
        string json = JsonSerializer.Serialize(data, JsonOpts);
        await WriteTextAsync(stream, json, "application/json; charset=utf-8", statusCode, statusText, customHeaders, ct);
    }

    public static async Task WriteStatusAsync(
        Stream stream,
        int statusCode,
        string statusText,
        string? message = null,
        CancellationToken ct = default)
    {
        string body = message ?? $"{statusCode} {statusText}";
        await WriteTextAsync(stream, body, "text/plain; charset=utf-8", statusCode, statusText, null, ct);
    }

    public static async Task WriteUnauthorizedBasicAsync(Stream stream, string realm = "EasyShare", CancellationToken ct = default)
    {
        var customHeaders = new Dictionary<string, string>
        {
            { "WWW-Authenticate", $@"Basic realm=""{realm}"", charset=""UTF-8""" }
        };
        byte[] body = "401 Unauthorized"u8.ToArray();
        await WriteHeadersAsync(stream, 401, "Unauthorized", "text/plain; charset=utf-8", body.Length, customHeaders, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static Task WriteFileAsync(
        Stream stream,
        string filePath,
        string contentType,
        (long Start, long? End)? rangeRequest,
        CancellationToken ct = default)
        => WriteFileAsync(stream, filePath, contentType, rangeRequest, null, ct);

    /// <summary>
    /// משדר קובץ מלא או קטע מקובץ (HTTP 206 Partial Content) בהתאם לדרישת ה-Range של הדפדפן.
    /// קריטי עבור Seeking / Scrubbing בקובצי וידאו ואודיו.
    /// </summary>
    public static async Task WriteFileAsync(
        Stream stream,
        string filePath,
        string contentType,
        (long Start, long? End)? rangeRequest,
        Dictionary<string, string>? additionalHeaders,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            await WriteStatusAsync(stream, 404, "Not Found", "File not found", ct);
            return;
        }

        long totalLength = fileInfo.Length;

        // טיפול בבקשת Range (206 Partial Content)
        if (rangeRequest.HasValue)
        {
            long start = rangeRequest.Value.Start;
            long end = rangeRequest.Value.End ?? (totalLength - 1);

            // בדיקת תקינות טווח
            if (start >= totalLength || end >= totalLength || start > end)
            {
                var errorHeaders = new Dictionary<string, string>
                {
                    { "Content-Range", $"bytes */{totalLength}" }
                };
                await WriteHeadersAsync(stream, 416, "Range Not Satisfiable", "text/plain", 0, errorHeaders, ct);
                return;
            }

            long rangeLength = end - start + 1;
            var rangeHeaders = new Dictionary<string, string>
            {
                { "Content-Range", $"bytes {start}-{end}/{totalLength}" },
                { "Accept-Ranges", "bytes" }
            };

            if (additionalHeaders != null)
            {
                foreach (var kvp in additionalHeaders)
                {
                    rangeHeaders[kvp.Key] = kvp.Value;
                }
            }

            await WriteHeadersAsync(stream, 206, "Partial Content", contentType, rangeLength, rangeHeaders, ct);

            // הזרמת הטווח המבוקש ישירות מהדיסק ל-Socket
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            fs.Seek(start, SeekOrigin.Begin);

            byte[] buffer = new byte[65536];
            long bytesRemaining = rangeLength;

            while (bytesRemaining > 0 && !ct.IsCancellationRequested)
            {
                int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
                if (bytesRead == 0) break;

                await stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                EasyShare.Services.LiveNetworkMonitorService.Instance.ReportBytesSent(bytesRead);
                bytesRemaining -= bytesRead;
            }
            await stream.FlushAsync(ct);
        }
        else
        {
            // שליחת קובץ מלא (200 OK) עם הצהרת Accept-Ranges כדי שהדפדפן ידע שאפשר לדלג
            var headers = new Dictionary<string, string>
            {
                { "Accept-Ranges", "bytes" }
            };

            if (additionalHeaders != null)
            {
                foreach (var kvp in additionalHeaders)
                {
                    headers[kvp.Key] = kvp.Value;
                }
            }

            await WriteHeadersAsync(stream, 200, "OK", contentType, totalLength, headers, ct);

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            byte[] buffer = new byte[65536];
            int read;
            while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                EasyShare.Services.LiveNetworkMonitorService.Instance.ReportBytesSent(read);
            }
            await stream.FlushAsync(ct);
        }
    }

    /// <summary>
    /// כותב כותרות HTTP סטנדרטיות (כולל CORS, Keep-Alive, ותאריך).
    /// </summary>
    public static async Task WriteHeadersAsync(
        Stream stream,
        int statusCode,
        string statusText,
        string contentType,
        long? contentLength = null,
        Dictionary<string, string>? customHeaders = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
        sb.Append($"Date: {DateTime.UtcNow:R}\r\n");
        sb.Append("Server: EasyShare/1.0 (SmartBinary)\r\n");
        if (customHeaders != null && customHeaders.TryGetValue("Connection", out string? connVal))
        {
            sb.Append($"Connection: {connVal}\r\n");
        }
        else
        {
            sb.Append("Connection: close\r\n");
        }
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Range, Authorization, Content-Type\r\n");

        if (!string.IsNullOrEmpty(contentType))
        {
            sb.Append($"Content-Type: {contentType}\r\n");
        }

        if (contentLength.HasValue)
        {
            sb.Append($"Content-Length: {contentLength.Value}\r\n");
        }

        if (customHeaders != null)
        {
            foreach (var kvp in customHeaders)
            {
                sb.Append($"{kvp.Key}: {kvp.Value}\r\n");
            }
        }

        sb.Append("\r\n");

        byte[] headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, ct);
    }
}
