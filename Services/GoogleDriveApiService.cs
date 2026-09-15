using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// אינטגרציה ישירה ל-Google Drive API (v3) באמצעות Headless OAuth 2.0 Loopback מקומי.
/// מאבטח טוקנים באמצעות Windows DPAPI ומפיק קישורי שיתוף ציבוריים ישירים ללא תלות בדפדפן פתוח.
/// </summary>
public static class GoogleDriveApiService
{
    private static readonly string TokenStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EasyShare",
        "gdrive_token.dat");

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static bool IsAuthorized()
    {
        return File.Exists(TokenStorePath);
    }

    /// <summary>
    /// שומר טוקן מוצפן ברמת המשתמש הנוכחי (DPAPI).
    /// </summary>
    public static void SaveEncryptedToken(string tokenJson)
    {
        try
        {
            string? dir = Path.GetDirectoryName(TokenStorePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] plainBytes = Encoding.UTF8.GetBytes(tokenJson);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenStorePath, encrypted);
        }
        catch { }
    }

    /// <summary>
    /// קורא ומפענח את הטוקן השמור.
    /// </summary>
    public static string? LoadDecryptedToken()
    {
        try
        {
            if (!File.Exists(TokenStorePath)) return null;
            byte[] encrypted = File.ReadAllBytes(TokenStorePath);
            byte[] plainBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// מוחק את הטוקן השמור (התנתקות).
    /// </summary>
    public static void ClearToken()
    {
        try
        {
            if (File.Exists(TokenStorePath)) File.Delete(TokenStorePath);
        }
        catch { }
    }

    /// <summary>
    /// מבצע העלאה ישירה של קובץ לחשבון Google Drive.
    /// אם לא מחובר טוקן API, משתמש באינטגרציה המקומית (CloudDriveService).
    /// </summary>
    public static async Task<CloudShareResult> UploadAndShareFileAsync(
        string filePath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string? tokenJson = LoadDecryptedToken();
        if (string.IsNullOrEmpty(tokenJson))
        {
            // מעבר אוטומטי לסנכרון מקומי חכם של Google Drive
            return await CloudDriveService.ShareViaGoogleDriveAsync(filePath, ct);
        }

        try
        {
            using var doc = JsonDocument.Parse(tokenJson);
            string accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";

            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;

            // 1. יצירת מטא-דאטה לקובץ
            var metadata = new
            {
                name = fileInfo.Name,
                mimeType = Core.MimeTypes.GetMimeType(fileInfo.Extension)
            };

            string metaJson = JsonSerializer.Serialize(metadata);

            // 2. פתיחת Resumable Session ב-Google Drive API
            using var initRequest = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
            initRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            initRequest.Content = new StringContent(metaJson, Encoding.UTF8, "application/json");

            var initResponse = await _httpClient.SendAsync(initRequest, ct);
            if (!initResponse.IsSuccessStatusCode)
            {
                // נפילה בטוקן? נחזור לסנכרון מקומי
                return await CloudDriveService.ShareViaGoogleDriveAsync(filePath, ct);
            }

            string? sessionUri = initResponse.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(sessionUri))
            {
                return await CloudDriveService.ShareViaGoogleDriveAsync(filePath, ct);
            }

            // 3. הזרמת הקובץ ל-Session URI
            await using var fs = File.OpenRead(filePath);
            using var uploadContent = new StreamContent(fs);
            uploadContent.Headers.ContentType = new MediaTypeHeaderValue(Core.MimeTypes.GetMimeType(fileInfo.Extension));
            uploadContent.Headers.ContentLength = fileSize;

            var uploadResponse = await _httpClient.PutAsync(sessionUri, uploadContent, ct);
            string resultJson = await uploadResponse.Content.ReadAsStringAsync(ct);

            using var resDoc = JsonDocument.Parse(resultJson);
            string fileId = resDoc.RootElement.GetProperty("id").GetString() ?? "";

            // 4. הגדרת הרשאות ציבוריות לקובץ (anyoneWithLink -> reader)
            using var permRequest = new HttpRequestMessage(HttpMethod.Post, $"https://www.googleapis.com/drive/v3/files/{fileId}/permissions");
            permRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            permRequest.Content = new StringContent(JsonSerializer.Serialize(new { role = "reader", type = "anyone" }), Encoding.UTF8, "application/json");
            await _httpClient.SendAsync(permRequest, ct);

            string publicUrl = $"https://drive.google.com/file/d/{fileId}/view?usp=sharing";

            return new CloudShareResult
            {
                Success = true,
                ProviderName = "Google Drive (Direct API)",
                IsLocalSync = false,
                SavedPath = filePath,
                WebUrl = publicUrl,
                Message = $"הקובץ הועלה ישירות לחשבון Google Drive והופק קישור ציבורי ישיר!"
            };
        }
        catch
        {
            return await CloudDriveService.ShareViaGoogleDriveAsync(filePath, ct);
        }
    }
}
