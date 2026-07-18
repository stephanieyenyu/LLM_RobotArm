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

        // line_N：N 為 2~10 的正整數，動態產生對應長度的線（取代原本寫死的 LINE_5 / LINE_10）
        if (patternText.StartsWith("LINE_"))
        {
            string lengthPart = patternText.Substring("LINE_".Length);
            if (int.TryParse(lengthPart, out int lineLength) && lineLength >= 2 && lineLength <= 10)
                return Line(lineLength);

            throw new NotSupportedException($"Pattern [{patternText}] not implemented.");
        }

        // 預定義幾何圖形（對齊 llm_planner.cs system prompt 宣稱支援的 square、O、X、triangle）
        switch (patternText)
        {
            case "SQUARE":
                return Square();

            case "CROSS":
                return Cross();

            case "O":
                return Circle();

            case "X":
                return XMark();

            case "TRIANGLE":
                return Triangle();
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

    private static int[,] Circle()
    {
        return new int[,]
        {
            {0,1,1,1,0},
            {1,0,0,0,1},
            {1,0,0,0,1},
            {1,0,0,0,1},
            {0,1,1,1,0}
        };
    }

    private static int[,] XMark()
    {
        return new int[,]
        {
            {1,0,0,0,1},
            {0,1,0,1,0},
            {0,0,1,0,0},
            {0,1,0,1,0},
            {1,0,0,0,1}
        };
    }

    private static int[,] Triangle()
    {
        return new int[,]
        {
            {0,0,1,0,0},
            {0,1,0,1,0},
            {1,0,0,0,1},
            {1,0,0,0,1},
            {1,1,1,1,1}
        };
    }

    //------------------------------------------------------------
    // 字型（5x7），補齊 A-Z 全部字母
    //------------------------------------------------------------

    private static readonly Dictionary<string, int[,]> Font5x7 =
        new Dictionary<string, int[,]>
    {
        {
            "A",
            new int[,]
            {
                {0,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1}
            }
        },
        {
            "B",
            new int[,]
            {
                {1,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,0}
            }
        },
        {
            "C",
            new int[,]
            {
                {0,1,1,1,1},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {0,1,1,1,1}
            }
        },
        {
            "D",
            new int[,]
            {
                {1,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,0}
            }
        },
        {
            "E",
            new int[,]
            {
                {1,1,1,1,1},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,1,1,1,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,1,1,1,1}
            }
        },
        {
            "F",
            new int[,]
            {
                {1,1,1,1,1},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,1,1,1,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0}
            }
        },
        {
            "G",
            new int[,]
            {
                {0,1,1,1,1},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,1,1,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {0,1,1,1,0}
            }
        },
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
            "J",
            new int[,]
            {
                {0,0,0,1,1},
                {0,0,0,0,1},
                {0,0,0,0,1},
                {0,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {0,1,1,1,0}
            }
        },
        {
            "K",
            new int[,]
            {
                {1,0,0,0,1},
                {1,0,0,1,0},
                {1,0,1,0,0},
                {1,1,0,0,0},
                {1,0,1,0,0},
                {1,0,0,1,0},
                {1,0,0,0,1}
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
            "M",
            new int[,]
            {
                {1,0,0,0,1},
                {1,1,0,1,1},
                {1,0,1,0,1},
                {1,0,1,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1}
            }
        },
        {
            "N",
            new int[,]
            {
                {1,0,0,0,1},
                {1,1,0,0,1},
                {1,0,1,0,1},
                {1,0,1,0,1},
                {1,0,0,1,1},
                {1,0,0,0,1},
                {1,0,0,0,1}
            }
        },
        {
            "O",
            new int[,]
            {
                {0,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {0,1,1,1,0}
            }
        },
        {
            "P",
            new int[,]
            {
                {1,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,0},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {1,0,0,0,0}
            }
        },
        {
            "Q",
            new int[,]
            {
                {0,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,1,0,1},
                {1,0,0,1,0},
                {0,1,1,0,1}
            }
        },
        {
            "R",
            new int[,]
            {
                {1,1,1,1,0},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,1,1,1,0},
                {1,0,1,0,0},
                {1,0,0,1,0},
                {1,0,0,0,1}
            }
        },
        {
            "S",
            new int[,]
            {
                {0,1,1,1,1},
                {1,0,0,0,0},
                {1,0,0,0,0},
                {0,1,1,1,0},
                {0,0,0,0,1},
                {0,0,0,0,1},
                {1,1,1,1,0}
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
        },
        {
            "U",
            new int[,]
            {
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {0,1,1,1,0}
            }
        },
        {
            "V",
            new int[,]
            {
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {0,1,0,1,0},
                {0,0,1,0,0}
            }
        },
        {
            "W",
            new int[,]
            {
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,0,0,1},
                {1,0,1,0,1},
                {1,0,1,0,1},
                {1,0,1,0,1},
                {0,1,0,1,0}
            }
        },
        {
            "X",
            new int[,]
            {
                {1,0,0,0,1},
                {0,1,0,1,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,1,0,1,0},
                {1,0,0,0,1}
            }
        },
        {
            "Y",
            new int[,]
            {
                {1,0,0,0,1},
                {1,0,0,0,1},
                {0,1,0,1,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0},
                {0,0,1,0,0}
            }
        },
        {
            "Z",
            new int[,]
            {
                {1,1,1,1,1},
                {0,0,0,0,1},
                {0,0,0,1,0},
                {0,0,1,0,0},
                {0,1,0,0,0},
                {1,0,0,0,0},
                {1,1,1,1,1}
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
