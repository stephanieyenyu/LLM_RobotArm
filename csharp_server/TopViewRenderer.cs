using OpenCvSharp;

/// <summary>
/// 純模擬沒開 Isaac Sim 時給 LLM 的畫面：依場景座標畫的俯視示意圖，方向跟相機畫面相同（上 = +Y、右 = +X）。
/// 畫出 QR1～QR4 圍成的工作區、手臂基座與可達範圍（離基座 0.20、0.48 m 的圓弧）、積木與場景 index。
/// 不是照片：沒有陰影、遮擋與量測誤差。
/// </summary>
public static class TopViewRenderer
{
    const double PxPerM = 1500;
    const double MarginM = 0.06;
    const double WorkspaceX = 0.805, WorkspaceY = 0.371;
    const double BaseX = 0.393, BaseY = 0.357, ReachMinM = 0.20, ReachMaxM = 0.48;
    const double CubeM = 0.025;

    public static byte[] Render(IReadOnlyList<SceneObject> objects)
    {
        double minX = -MarginM, maxX = WorkspaceX + MarginM, minY = -MarginM, maxY = WorkspaceY + MarginM;
        int width = (int)Math.Round((maxX - minX) * PxPerM), height = (int)Math.Round((maxY - minY) * PxPerM);
        using var image = new Mat(height, width, MatType.CV_8UC3, new Scalar(225, 225, 225));
        Point P(double x, double y) => new((int)Math.Round((x - minX) * PxPerM), (int)Math.Round((maxY - y) * PxPerM));
        int Px(double m) => (int)Math.Round(m * PxPerM);

        // 工作區（白底藍框）、QR1～QR4
        Cv2.Rectangle(image, P(0, WorkspaceY), P(WorkspaceX, 0), Scalar.White, -1);
        Cv2.Rectangle(image, P(0, WorkspaceY), P(WorkspaceX, 0), new Scalar(200, 120, 40), 2);
        var markers = new[] { ("QR1", 0.0, 0.0, Scalar.Red), ("QR2", WorkspaceX, 0.0, Scalar.Green),
                              ("QR3", 0.0, WorkspaceY, Scalar.Blue), ("QR4", WorkspaceX, WorkspaceY, Scalar.Magenta) };
        foreach (var (name, x, y, color) in markers)
        {
            Cv2.Rectangle(image, P(x - 0.01, y + 0.01), P(x + 0.01, y - 0.01), color, -1);
            Cv2.PutText(image, name, P(x - 0.012, y + (y > 0 ? 0.02 : -0.03)), HersheyFonts.HersheySimplex, 0.6, color, 2);
        }

        // 手臂基座與可達範圍
        Cv2.Circle(image, P(BaseX, BaseY), Px(ReachMinM), new Scalar(170, 170, 170), 1);
        Cv2.Circle(image, P(BaseX, BaseY), Px(ReachMaxM), new Scalar(170, 170, 170), 1);
        Cv2.Circle(image, P(BaseX, BaseY), Px(0.045), new Scalar(110, 110, 110), -1);
        Cv2.PutText(image, "arm base", P(BaseX + 0.05, BaseY), HersheyFonts.HersheySimplex, 0.6, new Scalar(80, 80, 80), 2);

        // 積木：cube 2.5 cm 見方；domino 沿長軸 5 cm（horizontal 沿 X、vertical 沿 Y）
        for (int i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            double halfX = CubeM / 2, halfY = CubeM / 2;
            if (o.Shape == "domino")
            {
                if (o.Orientation == "vertical") halfY = CubeM;
                else halfX = CubeM;
            }
            bool black = o.Name.StartsWith("black", StringComparison.OrdinalIgnoreCase);
            var fill = o.Name.StartsWith("yellow", StringComparison.OrdinalIgnoreCase) ? new Scalar(30, 200, 245)
                     : black ? new Scalar(40, 40, 40) : new Scalar(150, 150, 150);
            Cv2.Rectangle(image, P(o.X - halfX, o.Y + halfY), P(o.X + halfX, o.Y - halfY), fill, -1);
            Cv2.Rectangle(image, P(o.X - halfX, o.Y + halfY), P(o.X + halfX, o.Y - halfY), new Scalar(60, 60, 60), 1);
            string label = i.ToString();
            var size = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.5, 1, out _);
            var center = P(o.X, o.Y);
            Cv2.PutText(image, label, new Point(center.X - size.Width / 2, center.Y + size.Height / 2),
                HersheyFonts.HersheySimplex, 0.5, black ? Scalar.White : Scalar.Black, 1);
        }

        // 座標方向
        var origin = P(WorkspaceX + 0.005, -0.045);
        Cv2.ArrowedLine(image, new Point(origin.X - 70, origin.Y), origin, Scalar.Black, 2, tipLength: 0.25);
        Cv2.PutText(image, "+X", new Point(origin.X - 70, origin.Y - 10), HersheyFonts.HersheySimplex, 0.6, Scalar.Black, 2);
        var up = P(-0.045, WorkspaceY + 0.005);
        Cv2.ArrowedLine(image, new Point(up.X, up.Y + 70), up, Scalar.Black, 2, tipLength: 0.25);
        Cv2.PutText(image, "+Y", new Point(up.X + 8, up.Y + 40), HersheyFonts.HersheySimplex, 0.6, Scalar.Black, 2);

        Cv2.ImEncode(".jpg", image, out var bytes);
        return bytes;
    }
}
