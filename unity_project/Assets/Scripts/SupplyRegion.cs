// QR1-frame object-center bounds in metres. Shared by planning and execution.
public static class SupplyRegion
{
    public const double MinX = 0.05;
    public const double MaxX = 0.35;
    public const double MinY = 0.02;
    public const double MaxY = 0.25;

    public const string Description = "一般補貨區以 QR1 為原點，積木中心須在 X=0.05～0.35 m、Y=0.02～0.25 m，含邊界；這只限制取料來源，不限制放置目標，其他距離與安全檢查仍須通過。";

    public static bool Contains(double x, double y) =>
        x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    // Unity JSON coordinates are floats; compare against bounds in that precision.
    public static bool Contains(float x, float y) =>
        x >= (float)MinX && x <= (float)MaxX &&
        y >= (float)MinY && y <= (float)MaxY;
}

// Candidate filters only: passing these never bypasses IK or motion safety checks.
public static class WorkspaceDistanceLimits
{
    public const double BaseX = 0.38824;
    public const double BaseY = 0.35473;
    public const double SourceMinM = 0.10;
    public const double SourceMaxM = 0.47;
    public const double TargetMinM = 0.16;
    public const double TargetMaxM = 0.48;
    public const string Description = "底座中心的 QR1 座標為 (0.38824, 0.35473) m；積木中心到基座中心的水平距離 r=sqrt((X-0.38824)^2+(Y-0.35473)^2)，取料須為 0.10～0.47 m，放置須為 0.16～0.48 m，含邊界。這只是候選篩選，仍須通過 IK、關節限制、奇異點、碰撞與路徑檢查。";

    // 0.1 micrometre accommodates JSON double-to-float boundary rounding in Unity.
    const double BoundaryRoundoffM = 0.0000001;
    public static double Radius(double x, double y)
    {
        double dx = x - BaseX, dy = y - BaseY;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }
    public static bool SourceContains(double x, double y) =>
        Within(Radius(x, y), SourceMinM, SourceMaxM);
    public static bool TargetContains(double x, double y) =>
        Within(Radius(x, y), TargetMinM, TargetMaxM);
    static bool Within(double radius, double min, double max) =>
        radius >= min - BoundaryRoundoffM && radius <= max + BoundaryRoundoffM;
}
