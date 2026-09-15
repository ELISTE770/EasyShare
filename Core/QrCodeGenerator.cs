using System;
using System.Collections.Generic;
using System.Text;

namespace EasyShare.Core;

/// <summary>
/// מייצר קודי QR עצמאיים לחלוטין ללא כל תלות בספריות חיצוניות (Zero-Dependency QR Code Generator).
/// תומך בהפקה כ-ASCII עבור טרמינל הקונסול, כ-SVG עבור דפי אינטרנט, וכ-Data URI.
/// </summary>
public static class QrCodeGenerator
{
    /// <summary>
    /// מפיק מחרוזת ASCII של קוד ה-QR המתאימה להדפסה ישירה ב-Console.
    /// </summary>
    public static string GenerateAscii(string text)
    {
        bool[,] matrix = GenerateQrMatrix(text);
        int size = matrix.GetLength(0);
        var sb = new StringBuilder();

        // גבול שקט (Quiet Zone) עליון
        sb.AppendLine(new string(' ', (size + 4) * 2));
        sb.AppendLine(new string(' ', (size + 4) * 2));

        for (int r = 0; r < size; r++)
        {
            sb.Append("    "); // Quiet zone שמאל
            for (int c = 0; c < size; c++)
            {
                sb.Append(matrix[r, c] ? "██" : "  ");
            }
            sb.AppendLine("    ");
        }

        // גבול שקט תחתון
        sb.AppendLine(new string(' ', (size + 4) * 2));
        sb.AppendLine(new string(' ', (size + 4) * 2));

        return sb.ToString();
    }

    /// <summary>
    /// מפיק גרפיקת SVG וקטורית וקלת-משקל של קוד ה-QR.
    /// </summary>
    public static string GenerateSvg(string text, int moduleSize = 8, string darkColor = "#000000", string lightColor = "#ffffff")
    {
        bool[,] matrix = GenerateQrMatrix(text);
        int size = matrix.GetLength(0);
        int quietZone = 4;
        int totalModules = size + (quietZone * 2);
        int svgSize = totalModules * moduleSize;

        var sb = new StringBuilder();
        sb.Append($@"<svg xmlns=""http://www.w3.org/2000/svg"" version=""1.1"" viewBox=""0 0 {svgSize} {svgSize}"" width=""100%"" height=""100%"">");
        sb.Append($@"<rect width=""{svgSize}"" height=""{svgSize}"" fill=""{lightColor}"" />");

        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++)
            {
                if (matrix[r, c])
                {
                    int x = (c + quietZone) * moduleSize;
                    int y = (r + quietZone) * moduleSize;
                    sb.Append($@"<rect x=""{x}"" y=""{y}"" width=""{moduleSize}"" height=""{moduleSize}"" fill=""{darkColor}"" />");
                }
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// מפיק Data URI של SVG לשימוש מיידי בתגית img.
    /// </summary>
    public static string GenerateSvgDataUri(string text)
    {
        string svg = GenerateSvg(text);
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
        return $"data:image/svg+xml;base64,{base64}";
    }

    /// <summary>
    /// מחשב ומחזיר את מטריצת ה-QR הבסיסית (בוליאנית: true=שחור, false=לבן).
    /// מיושם עבור קידוד כתובות URL נפוצות ברמת תיקון שגיאות Medium (M).
    /// </summary>
    public static bool[,] GenerateQrMatrix(string text)
    {
        byte[] dataBytes = Encoding.UTF8.GetBytes(text);
        int version = GetMinimumVersion(dataBytes.Length);
        int size = 17 + (4 * version);

        bool[,] matrix = new bool[size, size];
        bool[,] isFunction = new bool[size, size];

        // 1. הוספת תבניות איתור (Finder Patterns) בשלושת הפינות
        AddFinderPattern(matrix, isFunction, 0, 0);
        AddFinderPattern(matrix, isFunction, size - 7, 0);
        AddFinderPattern(matrix, isFunction, 0, size - 7);

        // 2. מפרידים (Separators) ותבניות תזמון (Timing Patterns)
        AddTimingPatterns(matrix, isFunction, size);

        // 3. תבנית יישור (Alignment Pattern) עבור גרסאות מעל 1
        if (version >= 2)
        {
            AddAlignmentPattern(matrix, isFunction, size - 7, size - 7);
        }

        // 4. מידע פורמט זמני
        ReserveFormatInformation(isFunction, size);

        // 5. קידוד המידע ופולינום תיקון שגיאות (Reed-Solomon)
        byte[] encodedData = EncodeDataWithEcc(dataBytes, version);

        // 6. פריסת הבייטים במטריצה עם Mask Pattern 0 (i + j) % 2 == 0
        PlaceDataBits(matrix, isFunction, encodedData, size);

        // 7. כתיבת מידע פורמט סופי (Mask 0 + Error Correction M)
        WriteFormatInformation(matrix, size);

        return matrix;
    }

    private static int GetMinimumVersion(int dataLength)
    {
        // קיבולת בייטים עבור גרסאות 1 עד 6 ב-EC Level M
        int[] capacities = [14, 26, 42, 62, 84, 106, 122, 152, 180, 213];
        for (int v = 0; v < capacities.Length; v++)
        {
            if (dataLength + 3 <= capacities[v]) // +3 עבור קידומת מצב ואורך
            {
                return v + 1;
            }
        }
        return 10;
    }

    private static void AddFinderPattern(bool[,] matrix, bool[,] isFunction, int row, int col)
    {
        for (int r = -1; r <= 7; r++)
        {
            for (int c = -1; c <= 7; c++)
            {
                int currR = row + r;
                int currC = col + c;
                if (currR >= 0 && currR < matrix.GetLength(0) && currC >= 0 && currC < matrix.GetLength(1))
                {
                    isFunction[currR, currC] = true;
                    if (r >= 0 && r <= 6 && c >= 0 && c <= 6)
                    {
                        matrix[currR, currC] = (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
                    }
                    else
                    {
                        matrix[currR, currC] = false;
                    }
                }
            }
        }
    }

    private static void AddTimingPatterns(bool[,] matrix, bool[,] isFunction, int size)
    {
        for (int i = 8; i < size - 8; i++)
        {
            if (!isFunction[6, i])
            {
                matrix[6, i] = (i % 2 == 0);
                isFunction[6, i] = true;
            }
            if (!isFunction[i, 6])
            {
                matrix[i, 6] = (i % 2 == 0);
                isFunction[i, 6] = true;
            }
        }
    }

    private static void AddAlignmentPattern(bool[,] matrix, bool[,] isFunction, int row, int col)
    {
        int rCenter = row - 2;
        int cCenter = col - 2;
        for (int r = -2; r <= 2; r++)
        {
            for (int c = -2; c <= 2; c++)
            {
                int currR = rCenter + r;
                int currC = cCenter + c;
                isFunction[currR, currC] = true;
                matrix[currR, currC] = (Math.Abs(r) == 2 || Math.Abs(c) == 2 || (r == 0 && c == 0));
            }
        }
    }

    private static void ReserveFormatInformation(bool[,] isFunction, int size)
    {
        for (int i = 0; i < 9; i++)
        {
            isFunction[8, i] = true;
            isFunction[i, 8] = true;
            isFunction[8, size - 1 - i] = true;
            isFunction[size - 1 - i, 8] = true;
        }
        isFunction[size - 8, 8] = true; // Dark module
    }

    private static void WriteFormatInformation(bool[,] matrix, int size)
    {
        // עבור Mask 000 ו-EC M התוצאה הממודכת היא 0x5412
        int data = 0b101010000010010;

        for (int i = 0; i < 15; i++)
        {
            bool bit = ((data >> (14 - i)) & 1) == 1;

            // מסביב לפינה השמאלית עליונה
            if (i < 6) matrix[8, i] = bit;
            else if (i == 6) matrix[8, 7] = bit;
            else if (i == 7) matrix[8, 8] = bit;
            else if (i == 8) matrix[7, 8] = bit;
            else matrix[14 - i, 8] = bit;

            // בפינות השאר
            if (i < 8) matrix[size - 1 - i, 8] = bit;
            else matrix[8, size - 15 + i] = bit;
        }

        matrix[size - 8, 8] = true; // Dark module קבוע תמיד
    }

    private static byte[] EncodeDataWithEcc(byte[] data, int version)
    {
        int[] totalCodewords = [26, 44, 70, 100, 134, 172, 196, 242, 292, 346];
        int[] ecCodewords = [10, 16, 26, 36, 48, 64, 72, 88, 110, 130];

        int vIndex = Math.Min(version - 1, totalCodewords.Length - 1);
        int total = totalCodewords[vIndex];
        int ecc = ecCodewords[vIndex];
        int dataCapacity = total - ecc;

        var bitBuffer = new List<bool>();

        // קידוד Byte Mode: 0100
        bitBuffer.AddRange([false, true, false, false]);

        // אורך המידע (8 ביטים עבור גרסאות 1-9)
        int length = data.Length;
        for (int i = 7; i >= 0; i--)
        {
            bitBuffer.Add(((length >> i) & 1) == 1);
        }

        // בייטים של המידע
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--)
            {
                bitBuffer.Add(((b >> i) & 1) == 1);
            }
        }

        // סיום (Terminator) עד 4 אפסים
        int terminatorCount = Math.Min(4, (dataCapacity * 8) - bitBuffer.Count);
        for (int i = 0; i < terminatorCount; i++) bitBuffer.Add(false);

        // ריפוד לבייט מלא
        while (bitBuffer.Count % 8 != 0) bitBuffer.Add(false);

        // ריפוד בייטים (Pad Bytes: 0xEC, 0x11) עד קיבולת מלאה
        var dataBytes = new List<byte>();
        for (int i = 0; i < bitBuffer.Count; i += 8)
        {
            byte b = 0;
            for (int j = 0; j < 8; j++)
            {
                if (bitBuffer[i + j]) b |= (byte)(1 << (7 - j));
            }
            dataBytes.Add(b);
        }

        byte pad = 0xEC;
        while (dataBytes.Count < dataCapacity)
        {
            dataBytes.Add(pad);
            pad = (pad == 0xEC) ? (byte)0x11 : (byte)0xEC;
        }

        // חישוב Reed-Solomon Error Correction Codewords
        byte[] eccBytes = CalculateReedSolomonEcc(dataBytes.ToArray(), ecc);

        var result = new byte[total];
        dataBytes.CopyTo(result, 0);
        Array.Copy(eccBytes, 0, result, dataCapacity, ecc);
        return result;
    }

    private static byte[] CalculateReedSolomonEcc(byte[] data, int eccCount)
    {
        // Galois Field GF(256) עם פולינום 0x11D (285)
        byte[] exp = new byte[512];
        byte[] log = new byte[256];
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            exp[i] = (byte)x;
            exp[i + 255] = (byte)x;
            log[x] = (byte)i;
            x = (x << 1) ^ (x >= 128 ? 0x11D : 0);
        }

        byte GfMult(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return exp[log[a] + log[b]];
        }

        // יצירת פולינום מחולל (Generator Polynomial)
        byte[] gen = [1];
        for (int i = 0; i < eccCount; i++)
        {
            byte[] next = new byte[gen.Length + 1];
            for (int j = 0; j < gen.Length; j++)
            {
                next[j] ^= GfMult(gen[j], exp[i]);
                next[j + 1] ^= gen[j];
            }
            gen = next;
        }

        // חלוקת פולינומים לקבלת השארית
        byte[] remainder = new byte[eccCount];
        foreach (byte d in data)
        {
            byte factor = (byte)(d ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, eccCount - 1);
            remainder[eccCount - 1] = 0;

            for (int j = 0; j < eccCount; j++)
            {
                remainder[j] ^= GfMult(gen[j], factor);
            }
        }

        return remainder;
    }

    private static void PlaceDataBits(bool[,] matrix, bool[,] isFunction, byte[] data, int size)
    {
        int byteIndex = 0;
        int bitIndex = 7;

        int row = size - 1;
        int col = size - 1;
        bool upward = true;

        while (col > 0)
        {
            if (col == 6) col--; // דילוג על עמודת התזמון

            for (int i = 0; i < size; i++)
            {
                int currRow = upward ? (size - 1 - i) : i;

                for (int c = 0; c < 2; c++)
                {
                    int currCol = col - c;
                    if (!isFunction[currRow, currCol])
                    {
                        bool bit = false;
                        if (byteIndex < data.Length)
                        {
                            bit = ((data[byteIndex] >> bitIndex) & 1) == 1;
                            bitIndex--;
                            if (bitIndex < 0)
                            {
                                bitIndex = 7;
                                byteIndex++;
                            }
                        }

                        // החלת מסכה: Mask Pattern 0 -> (row + col) % 2 == 0
                        bool mask = ((currRow + currCol) % 2) == 0;
                        matrix[currRow, currCol] = bit ^ mask;
                    }
                }
            }

            upward = !upward;
            col -= 2;
        }
    }
}
