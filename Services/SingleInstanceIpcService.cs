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
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_serverCts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? payload = await reader.ReadLineAsync(_serverCts.Token);

                    if (!string.IsNullOrEmpty(payload))
                    {
                        var parts = payload.Split('|', 2);
                        if (parts.Length == 2)
                        {
                            OnShareRequested?.Invoke(parts[0], parts[1]);
                        }
                    }
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
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            await client.ConnectAsync(900); // 900ms timeout
            await using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync($"{channel}|{filePath}");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
