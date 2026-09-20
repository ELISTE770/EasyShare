using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EasyShare.Services;

/// <summary>
/// מנגנון ויסות והגבלת מהירות תעבורה (Bandwidth Rate Limiter &amp; Throttler).
/// מונע עומסים וחנק של רשת ה-Wi-Fi הביתית בעת הורדת קבצים כבדים ע"י מספר מכשירים במקביל.
/// </summary>
public static class BandwidthThrottler
{
    private static int _maxBytesPerSecond = 0; // 0 = ללא הגבלה (Unlimited)
    private static long _accumulatedBytes = 0;
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static readonly SemaphoreSlim _throttleLock = new(1, 1);

    /// <summary>
    /// מגבלת המהירות המרבית בבייטים לשנייה. 0 מסמל ללא הגבלה.
    /// </summary>
    public static int MaxBytesPerSecond
    {
        get => Volatile.Read(ref _maxBytesPerSecond);
        set
        {
            Interlocked.Exchange(ref _maxBytesPerSecond, Math.Max(0, value));
            Interlocked.Exchange(ref _accumulatedBytes, 0);
        }
    }

    /// <summary>
    /// מגבלת המהירות בקילובייט לשנייה (KB/s).
    /// </summary>
    public static int MaxKbps
    {
        get => MaxBytesPerSecond / 1024;
        set => MaxBytesPerSecond = value * 1024;
    }

    /// <summary>
    /// מציין האם המהירות אינה מוגבלת.
    /// </summary>
    public static bool IsUnlimited => MaxBytesPerSecond <= 0;

    /// <summary>
    /// מבצע השהיה חכמה (Throttle) בהתאם לכמות הבייטים שנשלחו והמגבלה המוגדרת.
    /// אם לא הוגדרה מגבלה, חוזר מיידית ללא השהיה או עלות ביצועים.
    /// </summary>
    public static async Task ThrottleAsync(int bytesSent, CancellationToken ct = default)
    {
        int limit = MaxBytesPerSecond;
        if (limit <= 0 || bytesSent <= 0) return;

        long currentAccumulated = Interlocked.Add(ref _accumulatedBytes, bytesSent);
        long elapsedMs = _stopwatch.ElapsedMilliseconds;

        if (elapsedMs < 100) return;

        double expectedTimeMs = ((double)currentAccumulated / limit) * 1000.0;
        int delayNeeded = (int)(expectedTimeMs - elapsedMs);

        if (delayNeeded > 5)
        {
            await _throttleLock.WaitAsync(ct);
            try
            {
                // בדיקה חוזרת בתוך הנעילה למניעת השהיות כפולות
                long nowElapsed = _stopwatch.ElapsedMilliseconds;
                double nowExpected = ((double)Volatile.Read(ref _accumulatedBytes) / limit) * 1000.0;
                int actualDelay = (int)(nowExpected - nowElapsed);

                if (actualDelay > 5)
                {
                    if (actualDelay > 1000) actualDelay = 1000; // הגנת תקיעה מרבית
                    await Task.Delay(actualDelay, ct);
                }

                // איפוס תקופתי כל 2 שניות לשמירה על יציבות
                if (_stopwatch.ElapsedMilliseconds >= 2000)
                {
                    _stopwatch.Restart();
                    Interlocked.Exchange(ref _accumulatedBytes, 0);
                }
            }
            finally
            {
                _throttleLock.Release();
            }
        }
    }
}
