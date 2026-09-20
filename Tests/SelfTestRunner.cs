using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EasyShare.Services;
using EasyShare.Models;

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
                        win.MainTabControl.SelectedIndex = 4;
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

            // Test 21: בדיקת יצירת חלון ראשי וטעינת עץ ה-XAML והמשאבים
            await AssertTest("21. WPF MainWindow & XAML Resource Tree", () =>
            {
                var tcs = new TaskCompletionSource<bool>();
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        if (System.Windows.Application.Current == null)
                        {
                            new EasyShare.App();
                        }
                        var win = new MainWindow();
                        tcs.SetResult(win != null && win.Title != null);
                    }
                    catch (Exception ex)
                    {
                        LogLine($"[FAIL] MainWindow XAML error: {ex}");
                        tcs.SetResult(false);
                    }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                return tcs.Task;
            });

            // Test 22: בדיקת מעבר דינמי של ערכות נושא (Dark <-> Light) ועדכון משאבי WPF
            await AssertTest("22. Dynamic Theme Switching (Dark <-> Light Palettes)", () =>
            {
                var tcs = new TaskCompletionSource<bool>();
                var thread = new System.Threading.Thread(() =>
                {
                    try
                    {
                        if (System.Windows.Application.Current == null)
                        {
                            new EasyShare.App();
                        }
                        var themeSvc = EasyShare.Core.ThemeService.Instance;

                        // בדיקת מצב בהיר
                        themeSvc.SetTheme(EasyShare.Core.ThemeService.ThemeLight);
                        var res = System.Windows.Application.Current?.Resources;
                        if (res == null)
                        {
                            tcs.SetResult(false);
                            return;
                        }

                        var bgLight = res["BrushBackground"] as System.Windows.Media.SolidColorBrush;
                        var cardLight = res["BrushCard"] as System.Windows.Media.SolidColorBrush;
                        var textLight = res["BrushText"] as System.Windows.Media.SolidColorBrush;

                        bool lightOk = bgLight != null && bgLight.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F8FAFC") &&
                                       cardLight != null && cardLight.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF") &&
                                       textLight != null && textLight.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F172A");

                        // בדיקת חזרה למצב כהה
                        themeSvc.SetTheme(EasyShare.Core.ThemeService.ThemeDark);
                        var bgDark = res["BrushBackground"] as System.Windows.Media.SolidColorBrush;
                        var cardDark = res["BrushCard"] as System.Windows.Media.SolidColorBrush;
                        var textDark = res["BrushText"] as System.Windows.Media.SolidColorBrush;

                        bool darkOk = bgDark != null && bgDark.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0B0F14") &&
                                      cardDark != null && cardDark.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#16202B") &&
                                      textDark != null && textDark.Color == (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F1F5F9");

                        tcs.SetResult(lightOk && darkOk);
                    }
                    catch (Exception ex)
                    {
                        LogLine($"[FAIL] Theme switching test error: {ex}");
                        tcs.SetResult(false);
                    }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                return tcs.Task;
            });

            // Test 23: בדיקת העלאת קבצים בחלקים (Chunked / Resumable Upload)
            await AssertTest("23. Chunked Upload Assembly & Integrity", async () =>
            {
                string uploadId = "test_chunk_" + Guid.NewGuid().ToString("N")[..8];
                byte[] part0 = "Hello ".Select(c => (byte)c).ToArray();
                byte[] part1 = "World from Chunked Upload!".Select(c => (byte)c).ToArray();
                string fileName = "chunked_test_file.txt";

                var res0 = await client.PostAsync($"{baseUrl}/api/upload-chunk?uploadId={uploadId}&chunkIndex=0&totalChunks=2&fileName={fileName}", new ByteArrayContent(part0));
                if (!res0.IsSuccessStatusCode) return false;

                var res1 = await client.PostAsync($"{baseUrl}/api/upload-chunk?uploadId={uploadId}&chunkIndex=1&totalChunks=2&fileName={fileName}", new ByteArrayContent(part1));
                if (!res1.IsSuccessStatusCode) return false;

                string targetPath = Path.Combine(tempTestDir, fileName);
                if (!File.Exists(targetPath)) return false;
                string content = await File.ReadAllTextAsync(targetPath);
                return content == "Hello World from Chunked Upload!";
            });

            // Test 24: בדיקת צ'אט ופתקים מהירים (Quick Text / Chat Drop)
            await AssertTest("24. Quick Text / Chat Drop Feed", async () =>
            {
                string testMsg = "EasyShare Quick Note " + Guid.NewGuid().ToString("N")[..6];
                var postContent = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new { sender = "UnitTester", text = testMsg }),
                    Encoding.UTF8,
                    "application/json");

                var postRes = await client.PostAsync($"{baseUrl}/api/messages", postContent);
                if (!postRes.IsSuccessStatusCode) return false;

                var getRes = await client.GetAsync($"{baseUrl}/api/messages");
                if (!getRes.IsSuccessStatusCode) return false;
                string getJson = await getRes.Content.ReadAsStringAsync();
                return getJson.Contains(testMsg) && getJson.Contains("UnitTester");
            });

            // Test 25: בדיקת חיפוש רקורסיבי עמוק (Instant Recursive Deep Search)
            await AssertTest("25. Instant Recursive Deep Search", async () =>
            {
                string deepDir = Path.Combine(tempTestDir, "DeepSubFolder", "Level2");
                Directory.CreateDirectory(deepDir);
                string deepFileName = "DeepSecretFile9876.txt";
                await File.WriteAllTextAsync(Path.Combine(deepDir, deepFileName), "Secret Content");

                var searchRes = await client.GetAsync($"{baseUrl}/api/search?q=DeepSecret");
                if (!searchRes.IsSuccessStatusCode) return false;
                string json = await searchRes.Content.ReadAsStringAsync();
                return json.Contains(deepFileName) && json.Contains("DeepSubFolder");
            });

            // Test 26: בדיקת חסימת IP ואכיפת גישה (IP Blacklisting & Enforcement)
            await AssertTest("26. IP Blacklisting & Access Enforcement", () =>
            {
                string testIp = "192.168.99.99";
                server.BlacklistIp(testIp);
                bool isListed = server.IsIpBlacklisted(testIp);
                if (!isListed) return Task.FromResult(false);

                var ips = server.GetBlacklistedIps();
                if (!ips.Contains(testIp)) return Task.FromResult(false);

                server.UnblacklistIp(testIp);
                bool unblocked = !server.IsIpBlacklisted(testIp);
                return Task.FromResult(unblocked);
            });

            // Test 27: בדיקת סנכרון ערכת נושא של המערכת (System Theme Sync)
            await AssertTest("27. System Theme Sync & Detection", () =>
            {
                var themeSvc = EasyShare.Core.ThemeService.Instance;
                themeSvc.SetTheme(EasyShare.Core.ThemeService.ThemeSystem);
                bool isSystem = themeSvc.ConfiguredTheme == EasyShare.Core.ThemeService.ThemeSystem;
                bool validEffective = themeSvc.CurrentTheme == EasyShare.Core.ThemeService.ThemeLight ||
                                      themeSvc.CurrentTheme == EasyShare.Core.ThemeService.ThemeDark;

                themeSvc.SetTheme(EasyShare.Core.ThemeService.ThemeDark);
                themeSvc.ToggleTheme();
                bool isLight = themeSvc.ConfiguredTheme == EasyShare.Core.ThemeService.ThemeLight;
                themeSvc.ToggleTheme();
                bool isSysAgain = themeSvc.ConfiguredTheme == EasyShare.Core.ThemeService.ThemeSystem;
                themeSvc.ToggleTheme();
                bool isDarkAgain = themeSvc.ConfiguredTheme == EasyShare.Core.ThemeService.ThemeDark;

                return Task.FromResult(isSystem && validEffective && isLight && isSysAgain && isDarkAgain);
            });

            // Test 28: בדיקת הגבלת קצב תעבורה (Bandwidth Throttling Engine)
            await AssertTest("28. Bandwidth Throttler Engine & Clamping", () =>
            {
                EasyShare.Services.BandwidthThrottler.MaxKbps = 0;
                bool isUnlimited = EasyShare.Services.BandwidthThrottler.IsUnlimited;
                EasyShare.Services.BandwidthThrottler.MaxKbps = 5120; // 5 MB/s
                bool isLimited = !EasyShare.Services.BandwidthThrottler.IsUnlimited && EasyShare.Services.BandwidthThrottler.MaxKbps == 5120;
                EasyShare.Services.BandwidthThrottler.MaxKbps = 0; // החזרה למצב רגיל
                return Task.FromResult(isUnlimited && isLimited);
            });

            // Test 29: בדיקת סנכרון לוח אוניברסלי (Universal Clipboard Synchronization)
            await AssertTest("29. Universal Clipboard Sync API", () =>
            {
                string testClip = "TestClip_" + Guid.NewGuid().ToString("N");
                server.AddClipboardItem(testClip);
                var latest = server.GetLatestClipboardItem();
                bool matches = latest != null && latest.Content == testClip;
                return Task.FromResult(matches);
            });

            // Test 30: מזהה התקנה ייחודי בפורמט SB ושירות מידע (Peer ID Format & My-Info API)
            await AssertTest("30. Peer ID Generation (SB Prefix) & My-Info API", async () =>
            {
                string peerId = server.PeerDiscovery.LocalPeerId;
                bool validFormat = !string.IsNullOrWhiteSpace(peerId) && 
                                   peerId.StartsWith("SB-", StringComparison.OrdinalIgnoreCase) && 
                                   peerId.Length == 10;
                
                var res = await client.GetAsync($"{baseUrl}/api/peer/my-info");
                if (!res.IsSuccessStatusCode) return false;
                
                string json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                string returnedId = doc.RootElement.GetProperty("peerId").GetString() ?? "";
                string visibility = doc.RootElement.GetProperty("internetVisibility").GetString() ?? "";
                
                return validFormat && returnedId == peerId && !string.IsNullOrEmpty(visibility);
            });

            // Test 31: קבלת הודעות צ'אט ישירות מעמית (Peer Direct Messaging)
            await AssertTest("31. Peer Direct Messaging API", async () =>
            {
                string testMsg = "Hello_Peer_" + Guid.NewGuid().ToString("N")[..6];
                var payload = new
                {
                    senderPeerId = "SB-777-888",
                    senderName = "PeerLaptop",
                    text = testMsg,
                    timestamp = DateTime.UtcNow
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var res = await client.PostAsync($"{baseUrl}/api/peer/message", content);
                if (!res.IsSuccessStatusCode) return false;

                var messages = server.GetRecentMessages();
                bool found = messages.Any(m => m.Text.Contains(testMsg));
                return found;
            });

            // Test 32: דרופ קבצים ישיר מעמית ושמירה בתיקיית Received_Drops
            await AssertTest("32. Direct Peer File Drop & Save to Received_Drops", async () =>
            {
                string testFileName = "peer_drop_test_" + Guid.NewGuid().ToString("N")[..6] + ".txt";
                byte[] testBytes = Encoding.UTF8.GetBytes("EasyShare Direct Peer Drop File Content Test!");

                using var form = new MultipartFormDataContent();
                var byteContent = new ByteArrayContent(testBytes);
                byteContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                form.Add(byteContent, "files", testFileName);

                var reqMsg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/peer/drop")
                {
                    Content = form
                };
                reqMsg.Headers.Add("X-Sender-PeerId", "SB-777-888");
                reqMsg.Headers.Add("X-Sender-DeviceName", Uri.EscapeDataString("PeerLaptop"));

                var res = await client.SendAsync(reqMsg);
                if (!res.IsSuccessStatusCode) return false;

                string dropsDir = Path.Combine(server.RootDirectory, "Received_Drops");
                string expectedFilePath = Path.Combine(dropsDir, testFileName);
                if (File.Exists(expectedFilePath))
                {
                    string content = await File.ReadAllTextAsync(expectedFilePath);
                    return content.Contains("EasyShare Direct Peer Drop");
                }
                return false;
            });

            // Test 33: עדכון מצב נראות באינטרנט (גלוי מול מוסתר)
            await AssertTest("33. Internet Peer Visibility Mode (Hidden <-> Visible)", async () =>
            {
                // העברה למצב Visible
                var contentVis = new StringContent(JsonSerializer.Serialize(new { visibility = "Visible" }), Encoding.UTF8, "application/json");
                var resVis = await client.PostAsync($"{baseUrl}/api/peer/visibility", contentVis);
                if (!resVis.IsSuccessStatusCode) return false;
                if (server.PeerDiscovery.InternetDiscovery.VisibilityMode != "Visible") return false;

                // העברה חזרה למצב Hidden
                var contentHid = new StringContent(JsonSerializer.Serialize(new { visibility = "Hidden" }), Encoding.UTF8, "application/json");
                var resHid = await client.PostAsync($"{baseUrl}/api/peer/visibility", contentHid);
                if (!resHid.IsSuccessStatusCode) return false;
                if (server.PeerDiscovery.InternetDiscovery.VisibilityMode != "Hidden") return false;

                return true;
            });

            // Test 34: איתור עמית באינטרנט לפי מזהה מדויק (Exact Peer ID Internet Lookup)
            await AssertTest("34. Exact Peer ID Internet Lookup & Hidden Discovery", async () =>
            {
                server.PeerDiscovery.InternetDiscovery.TestMode = true;
                string remoteTargetId = "SB-999-111";
                server.PeerDiscovery.InternetDiscovery.RegisterPeerForTesting(new PeerDevice
                {
                    PeerId = remoteTargetId,
                    DeviceName = "RemoteDevPC",
                    DiscoverySource = "Internet",
                    InternetUrl = "https://remotedev.trycloudflare.com",
                    IpAddress = "10.20.30.40",
                    Port = 2121,
                    IsOnline = true,
                    LastSeen = DateTime.UtcNow
                });

                var lookupPayload = new { targetPeerId = remoteTargetId };
                var content = new StringContent(JsonSerializer.Serialize(lookupPayload), Encoding.UTF8, "application/json");
                var res = await client.PostAsync($"{baseUrl}/api/peer/search-internet", content);
                if (!res.IsSuccessStatusCode) return false;

                string json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                bool found = doc.RootElement.GetProperty("found").GetBoolean();
                if (!found) return false;

                var peerElem = doc.RootElement.GetProperty("peer");
                string foundId = peerElem.GetProperty("peerId").GetString() ?? "";
                string devName = peerElem.GetProperty("deviceName").GetString() ?? "";
                string iUrl = peerElem.GetProperty("internetUrl").GetString() ?? "";

                return foundId == remoteTargetId && devName == "RemoteDevPC" && iUrl.Contains("trycloudflare.com");
            });

            // Test 35: בדיקת רישום ושליפת היסטוריית צ'אט עמיתים (Peer Chat History Storage & Retrieval)
            await AssertTest("35. Peer Chat History Engine & Attachments", () =>
            {
                string peerA = "SB-123-456";
                var chatMsg1 = new EasyShare.Models.PeerChatMessage
                {
                    SenderPeerId = peerA,
                    SenderName = "DeviceA",
                    RecipientPeerId = server.PeerDiscovery.LocalPeerId,
                    Text = "Hello from DeviceA!",
                    Timestamp = DateTime.UtcNow,
                    IsOutgoing = false
                };

                var chatMsg2 = new EasyShare.Models.PeerChatMessage
                {
                    SenderPeerId = server.PeerDiscovery.LocalPeerId,
                    SenderName = server.PeerDiscovery.LocalDeviceName,
                    RecipientPeerId = peerA,
                    Text = "Replying with attachment",
                    AttachedFileName = "photo.png",
                    AttachedFilePath = @"C:\Fake\photo.png",
                    AttachedFileSize = 1024 * 1024 * 2, // 2MB
                    Timestamp = DateTime.UtcNow,
                    IsOutgoing = true
                };

                server.PeerDiscovery.AddChatMessage(chatMsg1);
                server.PeerDiscovery.AddChatMessage(chatMsg2);

                var history = server.PeerDiscovery.GetChatHistory(peerA);
                if (history.Count < 2) return Task.FromResult(false);

                bool foundText = history.Any(m => m.Text == "Hello from DeviceA!" && !m.IsOutgoing);
                bool foundAttachment = history.Any(m => m.AttachedFileName == "photo.png" && m.IsOutgoing && m.HasAttachment);

                server.PeerDiscovery.ClearChatHistory(peerA);
                var clearedHistory = server.PeerDiscovery.GetChatHistory(peerA);

                return Task.FromResult(foundText && foundAttachment && clearedHistory.Count == 0);
            });

            // Test 36: בדיקת נקודות קצה REST API עבור היסטוריית צ'אט (Peer Chat REST API Endpoints)
            await AssertTest("36. Peer Chat REST API (/api/peer/chat & /api/peer/chat/clear)", async () =>
            {
                string targetPeerId = "SB-888-999";
                server.PeerDiscovery.AddChatMessage(new EasyShare.Models.PeerChatMessage
                {
                    SenderPeerId = targetPeerId,
                    SenderName = "RemotePartner",
                    RecipientPeerId = server.PeerDiscovery.LocalPeerId,
                    Text = "Special Chat Message For API Test",
                    Timestamp = DateTime.UtcNow,
                    IsOutgoing = false
                });

                var getRes = await client.GetAsync($"{baseUrl}/api/peer/chat?targetPeerId={targetPeerId}");
                if (!getRes.IsSuccessStatusCode) return false;

                string json = await getRes.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                int count = doc.RootElement.GetProperty("count").GetInt32();
                if (count < 1) return false;

                var clearPayload = new { targetPeerId };
                var clearContent = new StringContent(JsonSerializer.Serialize(clearPayload), Encoding.UTF8, "application/json");
                var postClearRes = await client.PostAsync($"{baseUrl}/api/peer/chat/clear", clearContent);
                if (!postClearRes.IsSuccessStatusCode) return false;

                var getResAfter = await client.GetAsync($"{baseUrl}/api/peer/chat?targetPeerId={targetPeerId}");
                if (!getResAfter.IsSuccessStatusCode) return false;

                string jsonAfter = await getResAfter.Content.ReadAsStringAsync();
                using var docAfter = JsonDocument.Parse(jsonAfter);
                int countAfter = docAfter.RootElement.GetProperty("count").GetInt32();

                return countAfter == 0;
            });

            // Test 37: ניטור קישורים והשמדת קישור שיתוף (אימות ביטול קישור ושמירה מוחלטת על הקובץ במחשב)
            await AssertTest("37. Monitoring Shared Links & Safe Link Revocation (/api/monitor/shares)", async () =>
            {
                // 1. יצירת קובץ מקורי בדיסק שהשמדת הקישור אסור שתפגע בו
                string testProtectFilePath = Path.Combine(tempTestDir, "original_file_safe_keep.txt");
                string fileOriginalContent = "Critical User Data: Must Never Be Deleted By Link Revocation!";
                await File.WriteAllTextAsync(testProtectFilePath, fileOriginalContent);

                // 2. יצירת שיתוף מאובטח
                var shareItem = await server.SecurityService.CreateTransferAsync(
                    targetPath: testProtectFilePath,
                    pinCode: "88889999",
                    expirationMinutes: 60,
                    maxDownloads: 5,
                    channel: "LAN",
                    serverBaseUrl: baseUrl
                );

                if (shareItem == null || string.IsNullOrWhiteSpace(shareItem.Token)) return false;

                // 3. אימות קריאת רשימת שיתופים פעילים דרך ה-REST API של הניטור
                var getSharesRes = await client.GetAsync($"{baseUrl}/api/monitor/shares");
                if (!getSharesRes.IsSuccessStatusCode) return false;

                string sharesJson = await getSharesRes.Content.ReadAsStringAsync();
                using var sharesDoc = JsonDocument.Parse(sharesJson);
                var sharesArray = sharesDoc.RootElement.GetProperty("shares");
                bool foundInShares = false;
                foreach (var el in sharesArray.EnumerateArray())
                {
                    if (el.GetProperty("token").GetString() == shareItem.Token)
                    {
                        foundInShares = true;
                        break;
                    }
                }
                if (!foundInShares) return false;

                // 4. אימות שהקישור עובד ונגיש כעת (HTTP 200)
                var pageRes = await client.GetAsync($"{baseUrl}/secure?token={shareItem.Token}");
                if (!pageRes.IsSuccessStatusCode) return false;

                // 5. השמדת קישור השיתוף דרך נקודת הקצה /api/monitor/shares/revoke
                var revokePayload = new { token = shareItem.Token };
                var revokeContent = new StringContent(JsonSerializer.Serialize(revokePayload), Encoding.UTF8, "application/json");
                var revokeRes = await client.PostAsync($"{baseUrl}/api/monitor/shares/revoke", revokeContent);
                if (!revokeRes.IsSuccessStatusCode) return false;

                // 6. אימות שהקישור אינו נגיש עוד (HTTP 404 או 410)
                var pageResAfter = await client.GetAsync($"{baseUrl}/secure?token={shareItem.Token}");
                if (pageResAfter.StatusCode != System.Net.HttpStatusCode.NotFound && pageResAfter.StatusCode != System.Net.HttpStatusCode.Gone)
                {
                    return false;
                }

                // 7. בדיקת הבטיחות הקריטית ביותר: הקובץ המקורי במחשב נשאר שלם וללא פגע!
                if (!File.Exists(testProtectFilePath))
                {
                    return false; // שגיאה חמורה: הקובץ במחשב נמחק!
                }
                string textOnDisk = await File.ReadAllTextAsync(testProtectFilePath);
                if (textOnDisk != fileOriginalContent)
                {
                    return false;
                }

                // 8. אימות שהשיתוף הוסר מרשימת הניטור הפעילה
                var getSharesAfter = await client.GetAsync($"{baseUrl}/api/monitor/shares");
                string sharesAfterJson = await getSharesAfter.Content.ReadAsStringAsync();
                using var sharesAfterDoc = JsonDocument.Parse(sharesAfterJson);
                foreach (var el in sharesAfterDoc.RootElement.GetProperty("shares").EnumerateArray())
                {
                    if (el.GetProperty("token").GetString() == shareItem.Token)
                    {
                        return false;
                    }
                }

                return true;
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
