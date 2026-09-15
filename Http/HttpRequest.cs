using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Http;

/// <summary>
/// מייצג בקשת HTTP מנותחת שהתקבלה מעל חיבור Socket TCP ישיר.
/// </summary>
public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public string RawUrl { get; init; } = "/";
    public string HttpVersion { get; init; } = "HTTP/1.1";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string ClientIp { get; init; } = "127.0.0.1";
    public Stream BodyStream { get; init; } = Stream.Null;

    public (long Start, long? End)? ByteRange { get; private set; }
    public string? BasicAuthUser { get; private set; }
    public string? BasicAuthPassword { get; private set; }

    public string? GetHeader(string name) => Headers.TryGetValue(name, out var val) ? val : null;

    /// <summary>
    /// קורא ומנתח את בקשת ה-HTTP מתוך ה-Stream של ה-Socket.
    /// שומר את ה-Stream פתוח עבור קריאת גוף הבקשה (Payload / Multipart).
    /// </summary>
    public static async Task<HttpRequest?> ReadAsync(Stream stream, string clientIp, CancellationToken ct = default)
    {
        // קריאת כותרות הבקשה עד לקבלת רצף \r\n\r\n
        byte[] buffer = new byte[8192];
        int totalRead = 0;
        int headerEndIndex = -1;

        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0) return null; // הלקוח ניתק חיבור

            totalRead += read;
            headerEndIndex = FindHeaderEnd(buffer, totalRead);
            if (headerEndIndex != -1) break;
        }

        if (headerEndIndex == -1) return null;

        string headerText = Encoding.UTF8.GetString(buffer, 0, headerEndIndex);
        var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        // ניתוח שורת הבקשה (Request Line)
        var requestParts = lines[0].Split(' ');
        if (requestParts.Length < 2) return null;

        string method = requestParts[0].Trim().ToUpperInvariant();
        string rawUrl = requestParts[1].Trim();
        string version = requestParts.Length > 2 ? requestParts[2].Trim() : "HTTP/1.1";

        // חלוקת URL לנתיב ופרמטרי Query
        string path = rawUrl;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int qIdx = rawUrl.IndexOf('?');
        if (qIdx != -1)
        {
            path = rawUrl[..qIdx];
            string queryString = rawUrl[(qIdx + 1)..];
            ParseQueryString(queryString, query);
        }

        path = WebUtility.UrlDecode(path);

        // ניתוח כותרות (Headers)
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colonIdx = lines[i].IndexOf(':');
            if (colonIdx > 0)
            {
                string headerName = lines[i][..colonIdx].Trim();
                string headerValue = lines[i][(colonIdx + 1)..].Trim();
                headers[headerName] = headerValue;
            }
        }

        // יצירת זרם משולב עבור גוף הבקשה (הבייטים שכבר נקראו מעבר לכותרות + שאר ה-NetworkStream)
        int bodyPreambleLength = totalRead - headerEndIndex;
        Stream bodyStream;
        if (bodyPreambleLength > 0)
        {
            byte[] preamble = new byte[bodyPreambleLength];
            Array.Copy(buffer, headerEndIndex, preamble, 0, bodyPreambleLength);
            bodyStream = new PrefixedStream(preamble, stream);
        }
        else
        {
            bodyStream = stream;
        }

        long contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? clStr) && long.TryParse(clStr, out long parsedCl))
        {
            contentLength = parsedCl;
            bodyStream = new BoundedStream(bodyStream, contentLength);
        }

        var req = new HttpRequest
        {
            Method = method,
            Path = path,
            RawUrl = rawUrl,
            HttpVersion = version,
            Headers = headers,
            Query = query,
            ClientIp = clientIp,
            BodyStream = bodyStream,
            ContentLength = contentLength
        };

        req.ExtractRangeHeader();
        req.ExtractBasicAuth();

        return req;
    }

    public long ContentLength { get; private set; }

    public async Task<string> ReadBodyAsStringAsync(CancellationToken ct = default)
    {
        using var reader = new StreamReader(BodyStream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i < length - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i + 4;
            }
        }
        return -1;
    }

    private static void ParseQueryString(string queryString, Dictionary<string, string> query)
    {
        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in pairs)
        {
            int eqIdx = pair.IndexOf('=');
            if (eqIdx != -1)
            {
                string key = WebUtility.UrlDecode(pair[..eqIdx]);
                string val = WebUtility.UrlDecode(pair[(eqIdx + 1)..]);
                query[key] = val;
            }
            else
            {
                string key = WebUtility.UrlDecode(pair);
                query[key] = string.Empty;
            }
        }
    }

    private void ExtractRangeHeader()
    {
        if (Headers.TryGetValue("Range", out string? rangeVal) && !string.IsNullOrWhiteSpace(rangeVal))
        {
            // פורמט: bytes=0-1023 או bytes=1024-
            if (rangeVal.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                string spec = rangeVal["bytes=".Length..].Trim();
                int dashIdx = spec.IndexOf('-');
                if (dashIdx != -1)
                {
                    string sStart = spec[..dashIdx].Trim();
                    string sEnd = spec[(dashIdx + 1)..].Trim();

                    long start = 0;
                    long? end = null;

                    if (!string.IsNullOrEmpty(sStart) && long.TryParse(sStart, out long pStart))
                    {
                        start = pStart;
                    }

                    if (!string.IsNullOrEmpty(sEnd) && long.TryParse(sEnd, out long pEnd))
                    {
                        end = pEnd;
                    }

                    ByteRange = (start, end);
                }
            }
        }
    }

    private void ExtractBasicAuth()
    {
        if (Headers.TryGetValue("Authorization", out string? auth) && !string.IsNullOrWhiteSpace(auth))
        {
            if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string encoded = auth["Basic ".Length..].Trim();
                    byte[] credBytes = Convert.FromBase64String(encoded);
                    string decoded = Encoding.UTF8.GetString(credBytes);
                    int colon = decoded.IndexOf(':');
                    if (colon != -1)
                    {
                        BasicAuthUser = decoded[..colon];
                        BasicAuthPassword = decoded[(colon + 1)..];
                    }
                }
                catch
                {
                    // Basic Auth פגום
                }
            }
        }
    }

    /// <summary>
    /// Stream עוטף המאפשר קריאה מזיכרון ראשוני (Preamble) ואחריו המשך ישיר ל-Stream הרשת.
    /// </summary>
    private sealed class PrefixedStream(byte[] prefix, Stream mainStream) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < prefix.Length)
            {
                int toCopy = Math.Min(count, prefix.Length - _prefixOffset);
                Array.Copy(prefix, _prefixOffset, buffer, offset, toCopy);
                _prefixOffset += toCopy;
                return toCopy;
            }
            return mainStream.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_prefixOffset < prefix.Length)
            {
                int toCopy = Math.Min(count, prefix.Length - _prefixOffset);
                Array.Copy(prefix, _prefixOffset, buffer, offset, toCopy);
                _prefixOffset += toCopy;
                return toCopy;
            }
            return await mainStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixOffset < prefix.Length)
            {
                int toCopy = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
                prefix.AsMemory(_prefixOffset, toCopy).CopyTo(buffer);
                _prefixOffset += toCopy;
                return toCopy;
            }
            return await mainStream.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() => mainStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// מגביל את הקריאה ל-Content-Length המדויק שנשלח בבקשה ומחזיר EOF (0 בייטים) בסיומו,
    /// מה שמונע חסימה או תקיעה בחיבורי TCP מתמשכים (Keep-Alive).
    /// </summary>
    private sealed class BoundedStream(Stream innerStream, long maxBytes) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => maxBytes;
        public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bytesRead >= maxBytes) return 0;
            int toRead = (int)Math.Min(count, maxBytes - _bytesRead);
            int read = innerStream.Read(buffer, offset, toRead);
            _bytesRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_bytesRead >= maxBytes) return 0;
            int toRead = (int)Math.Min(count, maxBytes - _bytesRead);
            int read = await innerStream.ReadAsync(buffer.AsMemory(offset, toRead), cancellationToken);
            _bytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_bytesRead >= maxBytes) return 0;
            int toRead = (int)Math.Min(buffer.Length, maxBytes - _bytesRead);
            int read = await innerStream.ReadAsync(buffer.Slice(0, toRead), cancellationToken);
            _bytesRead += read;
            return read;
        }

        public override void Flush() => innerStream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
