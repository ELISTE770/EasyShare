using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// מנהל תקשורת Named Pipes בין מופע המופעל מהסייר לבין המופע הראשי שרץ ברקע.
/// מאפשר פעולה חלקה כמופע יחיד (Single Instance).
/// </summary>
public static class SingleInstanceIpcService
{
    internal static string PipeName { get; set; } = "EasyShare_SingleInstance_IPC_Pipe";
    private static CancellationTokenSource? _serverCts;

    /// <summary>
    /// אירוע המופעל כאשר מתקבלת בקשת שיתוף ממופע משני.
    /// הפרמטרים: (ערוץ שיתוף, נתיב הקובץ/תיקייה).
    /// </summary>
    public static event Action<string, string>? OnShareRequested;

    /// <summary>
    /// אירוע המופעל לקבלת בקשת שיתוף/הפעלה עם החזרת סטטוס הצלחה (bool).
    /// </summary>
    public static event Func<string, string, bool>? OnShareRequestedWithResult;

    /// <summary>
    /// מתחיל האזנה במופע הראשי לפקודות שיתוף ממופעים משניים.
    /// </summary>
    public static void StartIpcServer()
    {
        if (_serverCts != null) return;
        _serverCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            while (!_serverCts.Token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_serverCts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    string? payload = await reader.ReadLineAsync(_serverCts.Token);

                    bool success = false;
                    if (!string.IsNullOrEmpty(payload))
                    {
                        var parts = payload.Split('|', 2);
                        if (parts.Length == 2)
                        {
                            try
                            {
                                OnShareRequested?.Invoke(parts[0], parts[1]);
                                success = true;
                            }
                            catch { }

                            if (OnShareRequestedWithResult != null)
                            {
                                try
                                {
                                    success = OnShareRequestedWithResult.Invoke(parts[0], parts[1]);
                                }
                                catch { }
                            }
                        }
                    }

                    try
                    {
                        using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                        await writer.WriteLineAsync(success ? "OK" : "FAIL");
                        await writer.FlushAsync();
                    }
                    catch { }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(400); }
            }
        });
    }

    /// <summary>
    /// עוצר את שרת ה-IPC.
    /// </summary>
    public static void StopIpcServer()
    {
        _serverCts?.Cancel();
        _serverCts = null;
    }

    /// <summary>
    /// שולח פקודת שיתוף למופע הראשי.
    /// מחזיר true אם המופע הראשי היה פעיל וקיבל את הפקודה.
    /// </summary>
    public static async Task<bool> SendShareCommandAsync(string channel, string filePath)
    {
        try
        {
            using var cts = new CancellationTokenSource(1200);
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            await client.ConnectAsync(cts.Token);

            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync($"{channel}|{filePath}");
            await writer.FlushAsync();

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            string? ack = await reader.ReadLineAsync(cts.Token);
            return string.Equals(ack, "OK", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
