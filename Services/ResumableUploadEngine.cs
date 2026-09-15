using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// מצב התקדמות העלאה מקוטעת (Resumable Upload Checkpoint).
/// </summary>
public class UploadCheckpoint
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long UploadedBytes { get; set; }
    public int ChunkSize { get; set; } = 8 * 1024 * 1024; // 8MB
    public string UploadUrl { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; } = DateTime.Now;
}

/// <summary>
/// מנוע העלאות מקוטע וחסין ניתוקים (Chunked Resumable Upload Engine).
/// מחלק קבצי ענק ל-Chunks של 8MB, שומר Checkpoints בדיסק,
/// ומאפשר חידוש מיידי מנקודת הניתוק ללא צורך בהעלאה מחודשת מההתחלה.
/// </summary>
public sealed class ResumableUploadEngine
{
    private const int DefaultChunkSize = 8 * 1024 * 1024; // 8MB
    private readonly HttpClient _httpClient;

    public ResumableUploadEngine(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>
    /// מעלה קובץ באופן מקוטע עם שמירת נקודת שחזור.
    /// </summary>
    public async Task<bool> UploadWithResumeAsync(
        string filePath,
        string uploadEndpointUrl,
        IProgress<(long BytesSent, long TotalBytes, double SpeedMbps)>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("File not found", filePath);

        var fileInfo = new FileInfo(filePath);
        long totalBytes = fileInfo.Length;
        string checkpointPath = filePath + ".upload_checkpoint.json";

        UploadCheckpoint checkpoint = LoadOrCreateCheckpoint(checkpointPath, filePath, totalBytes, uploadEndpointUrl);

        long currentOffset = checkpoint.UploadedBytes;
        byte[] buffer = new byte[checkpoint.ChunkSize];

        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        if (currentOffset > 0 && currentOffset < totalBytes)
        {
            fs.Seek(currentOffset, SeekOrigin.Begin);
        }

        var startTime = DateTime.Now;
        long bytesSentThisSession = 0;

        while (currentOffset < totalBytes && !ct.IsCancellationRequested)
        {
            int toRead = (int)Math.Min(buffer.Length, totalBytes - currentOffset);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0) break;

            long rangeStart = currentOffset;
            long rangeEnd = currentOffset + read - 1;

            using var content = new ByteArrayContent(buffer, 0, read);
            content.Headers.ContentRange = new ContentRangeHeaderValue(rangeStart, rangeEnd, totalBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var request = new HttpRequestMessage(HttpMethod.Post, uploadEndpointUrl)
            {
                Content = content
            };
            request.Headers.Add("X-Upload-FileName", Uri.EscapeDataString(fileInfo.Name));

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 308) // 308 Resume Incomplete
            {
                throw new HttpRequestException($"Chunk upload failed with status {response.StatusCode}");
            }

            currentOffset += read;
            bytesSentThisSession += read;

            // עדכון ושמירת ה-Checkpoint
            checkpoint.UploadedBytes = currentOffset;
            checkpoint.LastUpdated = DateTime.Now;
            SaveCheckpoint(checkpointPath, checkpoint);

            // חישוב מהירות
            double elapsedSec = Math.Max(0.1, (DateTime.Now - startTime).TotalSeconds);
            double speedMbps = Math.Round((bytesSentThisSession * 8.0) / (elapsedSec * 1024.0 * 1024.0), 2);

            progress?.Report((currentOffset, totalBytes, speedMbps));
        }

        // עם השלמת ההעלאה המלאה - מחיקת קובץ ה-Checkpoint
        if (currentOffset >= totalBytes && File.Exists(checkpointPath))
        {
            try { File.Delete(checkpointPath); } catch { }
        }

        return currentOffset >= totalBytes;
    }

    private static UploadCheckpoint LoadOrCreateCheckpoint(string checkpointPath, string filePath, long totalBytes, string uploadUrl)
    {
        if (File.Exists(checkpointPath))
        {
            try
            {
                string json = File.ReadAllText(checkpointPath);
                var cp = JsonSerializer.Deserialize<UploadCheckpoint>(json);
                if (cp != null && cp.FilePath == filePath && cp.TotalBytes == totalBytes)
                {
                    return cp;
                }
            }
            catch { }
        }

        return new UploadCheckpoint
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            TotalBytes = totalBytes,
            UploadedBytes = 0,
            UploadUrl = uploadUrl
        };
    }

    private static void SaveCheckpoint(string checkpointPath, UploadCheckpoint checkpoint)
    {
        try
        {
            string json = JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(checkpointPath, json);
        }
        catch { }
    }
}
