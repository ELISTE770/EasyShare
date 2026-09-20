using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Http;

/// <summary>
/// מנתח זרמי העלאת קבצים בפורמט multipart/form-data ישירות לדיסק (Streaming Multipart Parser).
/// מאפשר העלאת קבצים כבדים (וידאו, תמונות וארכיונים) ללא צריכת זיכרון RAM מיותרת.
/// </summary>
public static class MultipartParser
{
    public sealed record UploadedFile(string FileName, string SavedPath, long Size);

    /// <summary>
    /// מעבד את גוף הבקשה ומחלץ את הקבצים המועלים ישירות לתיקיית היעד.
    /// </summary>
    public static async Task<List<UploadedFile>> ParseAndSaveFilesAsync(
        HttpRequest request,
        string targetDirectory,
        CancellationToken ct = default)
    {
        var uploadedFiles = new List<UploadedFile>();

        string? contentType = request.GetHeader("Content-Type");
        if (string.IsNullOrEmpty(contentType) || !contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return uploadedFiles;
        }

        // חילוץ ה-Boundary
        string? boundary = ExtractBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
        {
            return uploadedFiles;
        }

        byte[] boundaryBytes = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        byte[] firstBoundaryBytes = Encoding.ASCII.GetBytes("--" + boundary);

        var stream = request.BodyStream;
        byte[] buffer = new byte[65536];
        int bufferCount = 0;

        // וידוא קיומה של תיקיית היעד
        Directory.CreateDirectory(targetDirectory);

        // דילוג ל-Boundary הראשון
        bool foundFirst = false;
        while (!foundFirst && !ct.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), ct);
            if (read == 0) break;
            bufferCount += read;

            int idx = IndexOfSequence(buffer, bufferCount, firstBoundaryBytes);
            if (idx != -1)
            {
                // דילוג על ה-Boundary הראשון
                int startAfter = idx + firstBoundaryBytes.Length;
                ShiftBuffer(buffer, ref bufferCount, startAfter);
                foundFirst = true;
            }
        }

        if (!foundFirst) return uploadedFiles;

        while (!ct.IsCancellationRequested)
        {
            if (bufferCount >= 2 && buffer[0] == '-' && buffer[1] == '-')
            {
                // סיום ה-Multipart (הגעה לסימון -- הסופי)
                break;
            }

            // קריאת כותרות החלק הנוכחי (עד \r\n\r\n)
            var (partHeaders, consumed, updatedCount) = await ReadPartHeadersAsync(stream, buffer, bufferCount, ct);
            bufferCount = updatedCount;
            if (partHeaders == null) break;

            ShiftBuffer(buffer, ref bufferCount, consumed);

            string? disposition = partHeaders.TryGetValue("content-disposition", out var disp) ? disp : null;
            string? fileName = ExtractFileName(disposition);

            if (!string.IsNullOrEmpty(fileName))
            {
                // מניעת Path Traversal ותמיכה בתיקיות משנה
                string sanitizedRel = fileName.Replace('/', Path.DirectorySeparatorChar)
                                              .Replace('\\', Path.DirectorySeparatorChar)
                                              .TrimStart(Path.DirectorySeparatorChar);
                if (sanitizedRel.Contains(".."))
                {
                    sanitizedRel = Path.GetFileName(sanitizedRel);
                }
                string destinationPath = Path.Combine(targetDirectory, sanitizedRel);
                string? destDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                // אם הקובץ קיים, הוספת מספר ייחודי
                destinationPath = GetUniqueFilePath(destinationPath);

                long savedBytes = 0;
                await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    // הזרמת תוכן הקובץ עד ל-Boundary הבא
                    bool reachedNextBoundary = false;
                    while (!reachedNextBoundary && !ct.IsCancellationRequested)
                    {
                        int boundaryIdx = IndexOfSequence(buffer, bufferCount, boundaryBytes);
                        if (boundaryIdx != -1)
                        {
                            // מצאנו את סיום הקובץ
                            if (boundaryIdx > 0)
                            {
                                await fileStream.WriteAsync(buffer.AsMemory(0, boundaryIdx), ct);
                                savedBytes += boundaryIdx;
                            }

                            int nextStart = boundaryIdx + boundaryBytes.Length;
                            ShiftBuffer(buffer, ref bufferCount, nextStart);
                            reachedNextBoundary = true;
                        }
                        else
                        {
                            // שמירת שולי ביטחון באורך ה-Boundary למקרה שהוא נחצה בין שני Reads
                            int safeWrite = Math.Max(0, bufferCount - boundaryBytes.Length);
                            if (safeWrite > 0)
                            {
                                await fileStream.WriteAsync(buffer.AsMemory(0, safeWrite), ct);
                                savedBytes += safeWrite;
                                ShiftBuffer(buffer, ref bufferCount, safeWrite);
                            }

                            int read = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), ct);
                            if (read == 0) break;
                            bufferCount += read;
                        }
                    }
                }

                uploadedFiles.Add(new UploadedFile(fileName, destinationPath, savedBytes));
            }
            else
            {
                // חלק שאינו קובץ (שדה רגיל) - דילוג עד ל-Boundary הבא
                bool skipped = false;
                while (!skipped && !ct.IsCancellationRequested)
                {
                    int boundaryIdx = IndexOfSequence(buffer, bufferCount, boundaryBytes);
                    if (boundaryIdx != -1)
                    {
                        ShiftBuffer(buffer, ref bufferCount, boundaryIdx + boundaryBytes.Length);
                        skipped = true;
                    }
                    else
                    {
                        ShiftBuffer(buffer, ref bufferCount, Math.Max(0, bufferCount - boundaryBytes.Length));
                        int read = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), ct);
                        if (read == 0) break;
                        bufferCount += read;
                    }
                }
            }

            // בדיקת סיום כל החלקים (סיומת --)
            if (bufferCount >= 2 && buffer[0] == '-' && buffer[1] == '-')
            {
                break;
            }
        }

        return uploadedFiles;
    }

    private static string? ExtractBoundary(string contentType)
    {
        int bIdx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (bIdx == -1) return null;

        string boundary = contentType[(bIdx + 9)..].Trim();
        if (boundary.StartsWith('\"') && boundary.EndsWith('\"'))
        {
            boundary = boundary[1..^1];
        }
        return boundary;
    }

    private static string? ExtractFileName(string? disposition)
    {
        if (string.IsNullOrEmpty(disposition)) return null;

        int fIdx = disposition.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
        if (fIdx == -1) return null;

        string val = disposition[(fIdx + 9)..].Trim();
        if (val.StartsWith('\"'))
        {
            int endQuote = val.IndexOf('\"', 1);
            if (endQuote != -1) return val[1..endQuote];
        }
        else
        {
            int semi = val.IndexOf(';');
            return semi != -1 ? val[..semi].Trim() : val;
        }
        return null;
    }

    private static async Task<(Dictionary<string, string>?, int, int)> ReadPartHeadersAsync(
        Stream stream,
        byte[] buffer,
        int bufferCount,
        CancellationToken ct)
    {
        while (true)
        {
            for (int i = 0; i < bufferCount - 3; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    string headerString = Encoding.UTF8.GetString(buffer, 0, i);
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var lines = headerString.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        int cIdx = line.IndexOf(':');
                        if (cIdx != -1)
                        {
                            dict[line[..cIdx].Trim()] = line[(cIdx + 1)..].Trim();
                        }
                    }
                    return (dict, i + 4, bufferCount);
                }
            }

            if (bufferCount >= buffer.Length) break;

            int read = await stream.ReadAsync(buffer.AsMemory(bufferCount, buffer.Length - bufferCount), ct);
            if (read == 0) break;
            bufferCount += read;
        }

        return (null, 0, bufferCount);
    }

    private static int IndexOfSequence(byte[] buffer, int length, byte[] sequence)
    {
        for (int i = 0; i <= length - sequence.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (buffer[i + j] != sequence[j])
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    private static void ShiftBuffer(byte[] buffer, ref int count, int shiftAmount)
    {
        int remaining = count - shiftAmount;
        if (remaining > 0)
        {
            Array.Copy(buffer, shiftAmount, buffer, 0, remaining);
            count = remaining;
        }
        else
        {
            count = 0;
        }
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        string dir = Path.GetDirectoryName(filePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(filePath);
        string ext = Path.GetExtension(filePath);
        int counter = 1;

        while (File.Exists(filePath))
        {
            filePath = Path.Combine(dir, $"{name}_{counter}{ext}");
            counter++;
        }
        return filePath;
    }
}
