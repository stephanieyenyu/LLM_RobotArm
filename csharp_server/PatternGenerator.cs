using System;
using System.Collections.Generic;

public static class PatternGenerator
{
    /// <summary>
    /// 回傳 0/1 bitmap
    /// 1 = 要放積木
    /// 0 = 空白
    /// </summary>
    public static int[,] GetBitmap(string patternText)
    {
        if (string.IsNullOrWhiteSpace(patternText))
            throw new ArgumentException("Pattern text is empty.");

        patternText = patternText.Trim().ToUpper();

        // 幾何圖形
        switch (patternText)
        {
            case "LINE_5":
                return Line(5);

            case "LINE_10":
                return Line(10);

            case "SQUARE":
                return Square();

            case "CROSS":
                return Cross();
        }

        // 單一字母
        if (Font5x7.ContainsKey(patternText))
            return Font5x7[patternText];

        // 多個字母，例如 HI、HELLO
        if (patternText.Length > 1)
            return CombineLetters(patternText);

        throw new NotSupportedException($"Pattern [{patternText}] not implemented.");
    }

    //------------------------------------------------------------
    // 幾何圖形
    //------------------------------------------------------------

    private static int[,] Line(int length)
    {
        int[,] bmp = new int[1, length];

        for (int i = 0; i < length; i++)
            bmp[0, i] = 1;

        return bmp;
    }

    private static int[,] Square()
    {
        return new int[,]
        {
            {1,1,1},
            {1,0,1},
            {1,1,1}
        };
    }

    private static int[,] Cross()
    {
        return new int[,]
        {
            {0,1,0},
            {1,1,1},
            {0,1,0}
        };
    }

    //------------------------------------------------------------
    // 字型
    //------------------------------------------------------------

    private static readonly Dictionary<string, int[,]> Font5x7 =
        new Dictionary<string, int[,]>
    {
        {
            "H",
            new int[,]
            {
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1}
            }
        },

        {
            "I",
            new int[,]
            {
                {1,1,1,1,1},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {1,1,1,1,1}
            }
        },

        {
            "L",
            new int[,]
            {
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,1,1,1,1}
            }
        },

        {
            "T",
            new int[,]
            {
                {1,1,1,1,1},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0}
            }
        }
    };

    //------------------------------------------------------------
    // 多字母組合
    //------------------------------------------------------------

    private static int[,] CombineLetters(string text)
    {
        const int gap = 1;

        List<int[,]> letters = new();

        foreach (char c in text)
        {
            string key = c.ToString();

            if (!Font5x7.ContainsKey(key))
                throw new NotSupportedException($"Letter [{key}] not implemented.");

            letters.Add(Font5x7[key]);
        }

        int rows = 7;

        int totalCols = 0;

        foreach (var bmp in letters)
            totalCols += bmp.GetLength(1);

        totalCols += gap * (letters.Count - 1);

        int[,] result = new int[rows, totalCols];

        int offset = 0;

        foreach (var bmp in letters)
        {
            int w = bmp.GetLength(1);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    result[r, offset + c] = bmp[r, c];
                }
            }

            offset += w + gap;
        }

        return result;
    }
}
