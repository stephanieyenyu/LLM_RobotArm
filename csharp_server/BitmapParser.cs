using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// BitmapParser：
/// 把 LLM 產生的字串陣列轉成 int[,]。
///
/// 例如：
/// [
///   "10001",
///   "11111",
///   "10001"
/// ]
///
/// 轉成：
/// int[3,5]
///
/// 0 = 空白
/// 1 = 需要放置積木
/// </summary>
public static class BitmapParser
{
    public static int[,] Parse(List<string> bitmapRows)
    {
        if (bitmapRows == null)
        {
            throw new ArgumentNullException(
                nameof(bitmapRows),
                "Bitmap 不可以是 null。"
            );
        }

        if (bitmapRows.Count == 0)
        {
            throw new ArgumentException(
                "Bitmap 至少需要一列。",
                nameof(bitmapRows)
            );
        }

        // 去除每列前後空白
        List<string> rows = bitmapRows
            .Select(row => row?.Trim() ?? string.Empty)
            .ToList();

        int columnCount = rows[0].Length;

        if (columnCount == 0)
        {
            throw new ArgumentException(
                "Bitmap 的列不可以是空字串。",
                nameof(bitmapRows)
            );
        }

        // 限制最大圖案尺寸，防止 LLM 產生過大的圖案
        const int maxRows = 20;
        const int maxColumns = 20;

        if (rows.Count > maxRows)
        {
            throw new ArgumentException(
                $"Bitmap 高度不可超過 {maxRows} 列。"
            );
        }

        if (columnCount > maxColumns)
        {
            throw new ArgumentException(
                $"Bitmap 寬度不可超過 {maxColumns} 欄。"
            );
        }

        int[,] bitmap = new int[rows.Count, columnCount];

        for (int row = 0; row < rows.Count; row++)
        {
            string currentRow = rows[row];

            // 每一列長度必須相同
            if (currentRow.Length != columnCount)
            {
                throw new ArgumentException(
                    $"Bitmap 第 {row + 1} 列長度為 " +
                    $"{currentRow.Length}，但第一列長度為 {columnCount}。"
                );
            }

            for (int column = 0; column < columnCount; column++)
            {
                char value = currentRow[column];

                if (value == '0')
                {
                    bitmap[row, column] = 0;
                }
                else if (value == '1')
                {
                    bitmap[row, column] = 1;
                }
                else
                {
                    throw new ArgumentException(
                        $"Bitmap 第 {row + 1} 列、第 {column + 1} 欄" +
                        $"包含非法字元 '{value}'。只允許 0 或 1。"
                    );
                }
            }
        }

        return bitmap;
    }

    /// <summary>
    /// 計算 bitmap 中需要多少塊積木。
    /// </summary>
    public static int CountOccupiedCells(int[,] bitmap)
    {
        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        int count = 0;

        int rows = bitmap.GetLength(0);
        int columns = bitmap.GetLength(1);

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                if (bitmap[row, column] == 1)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 把 bitmap 印到 Console，方便測試。
    /// </summary>
    public static void Print(int[,] bitmap)
    {
        if (bitmap == null)
        {
            throw new ArgumentNullException(nameof(bitmap));
        }

        int rows = bitmap.GetLength(0);
        int columns = bitmap.GetLength(1);

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                Console.Write(
                    bitmap[row, column] == 1 ? "■" : "□"
                );
            }

            Console.WriteLine();
        }
    }
}