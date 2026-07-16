using System;

// PatternGenerator：把 pattern 字串（例如 "H"、"line_5"、"square"）
// 轉成 2D bitmap（int[,]，0=空、1=需要放積木）。
// 純查表 / 純算術，不依賴任何場景狀態。
public static class PatternGenerator
{
    /// <summary>
    /// 依 pattern 名稱回傳 bitmap；未實作。
    /// </summary>
    public static int[,] GetBitmap(string patternText)
    {
        // TODO: 依 patternText 回傳對應 bitmap（line_N / 幾何形狀 / A-Z 字母）
        throw new NotImplementedException("PatternGenerator.GetBitmap not implemented yet.");
    }
}
