using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EasyShare.Services;

namespace EasyShare.Tests;

/// <summary>
/// מריץ בדיקות אינטגרציה פנימיות מקיפות של כל מודולי המערכת:
/// מנוע TcpListener, תמיכה ב-HTTP 206 Range, העלאת קבצים, הגנת Brute-Force ודחיסת ZIP ב-Streaming.
/// </summary>
public static class SelfTestRunner
{
    public static async Task<bool> RunAllTestsAsync()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("==========================================================");
        Console.WriteLine("   Running EasyShare Automated System & Protocol Tests    ");
        Console.WriteLine("==========================================================");
        Console.ResetColor();

        string tempTestDir = Path.Combine(Path.GetTempPath(), "EasyShare_Test_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempTestDir);

        string settingsFile = Path.Combine(AppContext.BaseDirectory, "settings.json");
        string? originalSettingsBackup = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : null;

        int testPort;
        using (var sock = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            sock.Start();
            testPort = ((System.Net.IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
        }

        using var server = new LocalWebServerService(tempTestDir, testPort, isReadOnly: false, pin: null);
        server.Start();

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        string baseUrl = $"http://127.0.0.1:{testPort}";

        int passed = 0;
        int failed = 0;

        var sbLog = new StringBuilder();
        void LogLine(string line)
        {
            Console.WriteLine(line);
            sbLog.AppendLine(line);
        }

        async Task AssertTest(string testName, Func<Task<bool>> testAction)
        {
            try
            {
                bool result = await testAction();
                if (result)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    LogLine($" [PASS] {testName}");
                    Console.ResetColor();
                    passed++;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    LogLine($" [FAIL] {testName}");
                    Console.ResetColor();
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                LogLine($" [FAIL] {testName} - Exception: {ex.Message}");
                Console.ResetColor();
                failed++;
            }
        }

        try
        {
            // Test 1: בדיקת שרת Web ודף SPA
            await AssertTest("1. Serves Embedded SPA (GET /)", async () =>
            {
                var res = await client.GetAsync(baseUrl + "/");
                string body = await res.Content.ReadAsStringAsync();
                return res.IsSuccessStatusCode && body.Contains("שיתוף קל");
            });

            // Test 2: בדיקת סטטוס שרת (GET /api/status)
            await AssertTest("2. Status Endpoint (GET /api/status)", async () =>
            {
                var res = await client.GetAsync(baseUrl + "/api/status");
                if (!res.IsSuccessStatusCode) return false;
                string json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("port").GetInt32() == testPort;
            });

            // Test 3: בדיקת הזרמת מדיה ו-HTTP 206 Partial Content (Range Requests)
            await AssertTest("3. HTTP 206 Partial Content (Range: bytes=100-299)", async () =>
            {
                string testFile = Path.Combine(tempTestDir, "test_video.mp4");
                byte[] sampleData = new byte[10000];
                for (int i = 0; i < sampleData.Length; i++) sampleData[i] = (byte)(i % 256);
                await File.WriteAllBytesAsync(testFile, sampleData);

                var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/download?path=test_video.mp4");
                req.Headers.Range = new RangeHeaderValue(100, 299);

                var res = await client.SendAsync(req);
                byte[] received = await res.Content.ReadAsByteArrayAsync();

                bool is206 = res.StatusCode == System.Net.HttpStatusCode.PartialContent;
                bool isCorrectLen = received.Length == 200;
                bool matchContent = true;
                for (int i = 0; i < 200; i++)
                {
                    if (received[i] != sampleData[100 + i]) { matchContent = false; break; }
                }

                return is206 && isCorrectLen && matchContent;
            });

            // Test 4: בדיקת יצירת תיקייה (POST /api/mkdir)
            await AssertTest("4. Create Directory (POST /api/mkdir)", async () =>
            {
                var content = new StringContent(JsonSerializer.Serialize(new { parentPath = "", name = "SubFolder" }), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(baseUrl + "/api/mkdir", content);
                return res.IsSuccessStatusCode && Directory.Exists(Path.Combine(tempTestDir, "SubFolder"));
            });

            // Test 5: בדיקת העלאת קובץ (Multipart Form-Data)
            await AssertTest("5. Multipart File Upload (POST /api/upload)", async () =>
            {
                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("Hello from automated test!"));
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
                form.Add(fileContent, "files", "uploaded_test.txt");

                var res = await client.PostAsync(baseUrl + "/api/upload?path=SubFolder", form);
                string uploadedPath = Path.Combine(tempTestDir, "SubFolder", "uploaded_test.txt");
                return res.IsSuccessStatusCode && File.Exists(uploadedPath);
            });

            // Test 6: בדיקת דחיסת תיקייה ל-ZIP ב-Streaming בזמן אמת
            await AssertTest("6. Streaming On-The-Fly ZIP Download", async () =>
            {
                var res = await client.GetAsync(baseUrl + "/api/download?path=SubFolder");
                if (!res.IsSuccessStatusCode) return false;

                await using var zipStream = await res.Content.ReadAsStreamAsync();
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                return archive.Entries.Count > 0;
            });

            // Test 7: בדיקת אבטחת Brute-Force של קוד PIN
            await AssertTest("7. PIN Brute-Force Rate Limiting (Block after 6 failures)", async () =>
            {
                server.SecurityService.SetCustomPin("99998888");

                // שליחת 6 ניסיונות שגויים
                for (int i = 0; i < 6; i++)
                {
                    var content = new StringContent(JsonSerializer.Serialize(new { pin = "00000000" }), Encoding.UTF8, "application/json");
                    await client.PostAsync(baseUrl + "/api/auth/pin", content);
                }

                // הניסיון השביעי חייב להיחסם מיידית עם 429 Too Many Requests
                var blockedReq = await client.GetAsync(baseUrl + "/api/status");
                bool isBlocked = (int)blockedReq.StatusCode == 429;

                // איפוס חסימה להמשך בדיקות
                server.SecurityService.ResetAllAttempts();
                server.SecurityService.SetCustomPin(null);
                return isBlocked;
            });

            // Test 8: בדיקת מצב Read-Only
            await AssertTest("8. Read-Only Mode Enforcement", async () =>
            {
                server.IsReadOnly = true;
                var content = new StringContent(JsonSerializer.Serialize(new { parentPath = "", name = "ShouldFail" }), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(baseUrl + "/api/mkdir", content);
                server.IsReadOnly = false;
                return res.StatusCode == System.Net.HttpStatusCode.Forbidden;
            });

            // Test 9: בדיקת סקירת כוננים ומערכת (GET /api/system/overview)
            await AssertTest("9. System Overview (Drives & Quick Folders)", async () =>
            {
                var res = await client.GetAsync(baseUrl + "/api/system/overview");
                if (!res.IsSuccessStatusCode) return false;
                string json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("drives", out var drives) && drives.GetArrayLength() > 0;
            });

            // Test 10: בדיקת עורך קבצי טקסט וקוד (POST /api/file/save & GET /api/file/content)
            await AssertTest("10. Built-in Text File Viewer & Editor", async () =>
            {
                string noteFile = Path.Combine(tempTestDir, "my_notes.txt");
                var saveContent = new StringContent(JsonSerializer.Serialize(new { path = noteFile, content = "EasyShare Pro Text Editor" }), Encoding.UTF8, "application/json");
                var saveRes = await client.PostAsync(baseUrl + "/api/file/save", saveContent);
                if (!saveRes.IsSuccessStatusCode) return false;

                var readRes = await client.GetAsync(baseUrl + "/api/file/content?path=" + Uri.EscapeDataString(noteFile));
                if (!readRes.IsSuccessStatusCode) return false;
                string readJson = await readRes.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(readJson);
                return doc.RootElement.GetProperty("content").GetString() == "EasyShare Pro Text Editor";
            });

            // Test 11: בדיקת הורדה מרובה כ-ZIP (POST /api/batch/download)
            await AssertTest("11. Multi-Item Batch Download as ZIP", async () =>
            {
                string f1 = Path.Combine(tempTestDir, "batch1.txt");
                string f2 = Path.Combine(tempTestDir, "batch2.txt");
                await File.WriteAllTextAsync(f1, "file 1");
                await File.WriteAllTextAsync(f2, "file 2");

                var payload = new StringContent(JsonSerializer.Serialize(new { paths = new[] { f1, f2 } }), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(baseUrl + "/api/batch/download", payload);
                if (!res.IsSuccessStatusCode) return false;

                await using var stream = await res.Content.ReadAsStreamAsync();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                return archive.Entries.Count == 2;
            });

            // Test 12: בדיקת שמירת הגדרות מתקדמות דינמיות (POST /api/settings)
            await AssertTest("12. Dynamic Advanced Settings Update", async () =>
            {
                var newSettings = new { sendToRecycleBin = false, defaultViewMode = "Grid", accessMode = "FullComputer" };
                var payload = new StringContent(JsonSerializer.Serialize(newSettings), Encoding.UTF8, "application/json");
                var res = await client.PostAsync(baseUrl + "/api/settings", payload);
                if (!res.IsSuccessStatusCode) return false;
                return server.Settings.SendToRecycleBin == false && server.Settings.DefaultViewMode == "Grid";
            });

            // Test 13: אימות טעינת ממשק משתמש WPF XAML, תבניות כרטיסיות ומעבר טאבים
            await AssertTest("13. WPF MainWindow XAML & All Tabs Navigation", () =>
            {
                var tcs = new TaskCompletionSource<bool>();
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        var win = new MainWindow();
                        win.MainTabControl.SelectedIndex = 0;
                        win.MainTabControl.SelectedIndex = 1;
                        win.MainTabControl.SelectedIndex = 2;
                        win.MainTabControl.SelectedIndex = 3;
                        win.Close();
                        tcs.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[WPF XAML ERROR] {ex}");
                        try { File.WriteAllText("wpf_error.txt", ex.ToString()); } catch { }
                        tcs.SetResult(false);
                    }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                return tcs.Task;
            });

            // Test 14: אימות דף הורדה מאובטח /secure, אימות PIN, והורדה עם תוקף ללא הגבלה
            await AssertTest("14. Secure Transfer (/secure) with Unlimited Expiration & PIN", async () =>
            {
                string secureFile = Path.Combine(tempTestDir, "secure_doc.txt");
                await File.WriteAllTextAsync(secureFile, "Confidential EasyShare Data");

                var item = await server.SecurityService.CreateTransferAsync(
                    targetPath: secureFile,
                    pinCode: "88776655",
                    expirationMinutes: 0, // ללא הגבלה
                    maxDownloads: 5,
                    channel: "LAN",
                    serverBaseUrl: baseUrl
                );

                if (item.ExpiresAt != DateTime.MaxValue) return false;
                if (!item.StatusDescription.Contains("ללא תפוגה")) return false;

                // 1. בדיקת קבלת דף ה-HTML של /secure
                var pageRes = await client.GetAsync($"{baseUrl}/secure?token={item.Token}");
                if (!pageRes.IsSuccessStatusCode) return false;
                string pageHtml = await pageRes.Content.ReadAsStringAsync();
                if (!pageHtml.Contains("secure_doc.txt") || !pageHtml.Contains("ללא תפוגה")) return false;

                // 2. בדיקת PIN שגוי
                var wrongPinRes = await client.GetAsync($"{baseUrl}/secure/download?token={item.Token}&pin=00000000");
                if (wrongPinRes.StatusCode != System.Net.HttpStatusCode.Unauthorized) return false;

                // 3. בדיקת הורדה תקינה עם PIN נכון
                var downloadRes = await client.GetAsync($"{baseUrl}/secure/download?token={item.Token}&pin=88776655");
                if (!downloadRes.IsSuccessStatusCode) return false;
                string downloadedContent = await downloadRes.Content.ReadAsStringAsync();
                return downloadedContent == "Confidential EasyShare Data" && item.DownloadCount == 1;
            });

            // Test 15: בדיקת נקודות קצה של PWA (GET /manifest.json & GET /sw.js)
            await AssertTest("15. PWA Manifest & Service Worker Delivery", async () =>
            {
                var manifestRes = await client.GetAsync(baseUrl + "/manifest.json");
                if (!manifestRes.IsSuccessStatusCode) return false;
                string manifest = await manifestRes.Content.ReadAsStringAsync();

                var swRes = await client.GetAsync(baseUrl + "/sw.js");
                if (!swRes.IsSuccessStatusCode) return false;
                string sw = await swRes.Content.ReadAsStringAsync();

                return manifest.Contains("שיתוף קל") && sw.Contains("easyshare-cache");
            });

            // Test 16: בדיקת לוח שיתוף מהיר (POST & GET /api/clipboard)
            await AssertTest("16. Quick Drop Clipboard Synchronization", async () =>
            {
                var postContent = new StringContent(JsonSerializer.Serialize(new { text = "EasyShare Quick Drop Test" }), Encoding.UTF8, "application/json");
                var postRes = await client.PostAsync(baseUrl + "/api/clipboard", postContent);
                if (!postRes.IsSuccessStatusCode) return false;

                var getRes = await client.GetAsync(baseUrl + "/api/clipboard");
                if (!getRes.IsSuccessStatusCode) return false;
                string json = await getRes.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("text").GetString() == "EasyShare Quick Drop Test";
            });

            // Test 17: בדיקת ניטור רשת חי ומעקב חיבורים (LiveNetworkMonitorService)
            await AssertTest("17. Live Network Monitor & Active Connections Tracking", () =>
            {
                var monitor = LiveNetworkMonitorService.Instance;
                monitor.ReportBytesSent(1024 * 1024);
                var conn = monitor.RegisterConnection("127.0.0.1", "test.mp4", 1024 * 1024);
                var active = monitor.GetActiveConnections();
                bool found = active.Exists(c => c.ConnectionId == conn.ConnectionId && c.ClientIp == "127.0.0.1");
                monitor.TerminateConnection(conn.ConnectionId);
                bool terminated = conn.Cts.IsCancellationRequested;
                monitor.UnregisterConnection(conn.ConnectionId);
                return Task.FromResult(found && terminated);
            });

            // Test 18: בדיקת מחיקה פיזית בטוחה (DoD Zero-Fill Shredding)
            await AssertTest("18. Secure Zero-Fill File Shredding", async () =>
            {
                string shredFile = Path.Combine(tempTestDir, "confidential_shred.bin");
                byte[] secretData = new byte[2048];
                Array.Fill(secretData, (byte)0xAA);
                await File.WriteAllBytesAsync(shredFile, secretData);

                SecureTransferService.SecureZeroFillShred(shredFile);
                return !File.Exists(shredFile);
            });

            // Test 19: בדיקת תקשורת IPC בין תהליכים (NamedPipe)
            await AssertTest("19. Single Instance Named Pipe IPC", async () =>
            {
                string originalPipe = SingleInstanceIpcService.PipeName;
                SingleInstanceIpcService.PipeName = "EasyShare_Test_Pipe_" + Guid.NewGuid().ToString("N")[..8];
                try
                {
                    string receivedMsg = "";
                    Action<string, string> handler = (channel, path) => receivedMsg = $"{channel}|{path}";
                    SingleInstanceIpcService.OnShareRequested += handler;
                    SingleInstanceIpcService.StartIpcServer();
                    await Task.Delay(200);

                    bool sent = await SingleInstanceIpcService.SendShareCommandAsync("LAN", "test_ipc_file.txt");
                    await Task.Delay(300);

                    SingleInstanceIpcService.OnShareRequested -= handler;
                    SingleInstanceIpcService.StopIpcServer();

                    return sent && receivedMsg.Contains("test_ipc_file.txt");
                }
                finally
                {
                    SingleInstanceIpcService.PipeName = originalPipe;
                }
            });

            // Test 20: בדיקת הגדרות מנהור Cloudflare ודומיין מותאם (Random vs CustomDomain)
            await AssertTest("20. Cloudflare Tunnel Settings & Custom Domain Configuration", () =>
            {
                var settingsMgr = new SettingsService();
                string prevMode = settingsMgr.Current.TunnelMode;
                string? prevDomain = settingsMgr.Current.CloudflareCustomDomain;
                string? prevToken = settingsMgr.Current.CloudflareTunnelToken;

                try
                {
                    settingsMgr.Current.TunnelMode = "CustomDomain";
                    settingsMgr.Current.CloudflareCustomDomain = "https://share.smartbinary.org";
                    settingsMgr.Current.CloudflareTunnelToken = "cf_token_secret_12345";
                    settingsMgr.Save(settingsMgr.Current);

                    var reloadedMgr = new SettingsService();
                    bool ok = reloadedMgr.Current.TunnelMode == "CustomDomain" &&
                              reloadedMgr.Current.CloudflareCustomDomain == "https://share.smartbinary.org" &&
                              reloadedMgr.Current.CloudflareTunnelToken == "cf_token_secret_12345";
                    return Task.FromResult(ok);
                }
                finally
                {
                    settingsMgr.Current.TunnelMode = prevMode;
                    settingsMgr.Current.CloudflareCustomDomain = prevDomain;
                    settingsMgr.Current.CloudflareTunnelToken = prevToken;
                    settingsMgr.Save(settingsMgr.Current);
                }
            });
        }
        finally
        {
            server.Stop();
            try { Directory.Delete(tempTestDir, recursive: true); } catch { }

            if (originalSettingsBackup != null)
            {
                try { File.WriteAllText(settingsFile, originalSettingsBackup); } catch { }
            }
        }

        Console.WriteLine("----------------------------------------------------------");
        string summary = $"Tests completed: {passed} passed, {failed} failed.";
        Console.WriteLine(summary);
        Console.WriteLine("==========================================================");
        sbLog.AppendLine(summary);

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test_summary.txt"), sbLog.ToString());
            File.WriteAllText("test_summary.txt", sbLog.ToString());
        }
        catch { }

        return failed == 0;
    }
}
