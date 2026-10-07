using System;

// 3D 疊放專用的夾取幾何：只有 BatchEnvelope.layered_grasp = true 的批次（csharp_server 判定為 3D 疊放時，
// URSim 驗證批次與通過後的實機批次）會用到，2D 批次完全不呼叫這裡。
// 只用 System.Math、不依賴 UnityEngine：csharp_server 與 tests/ExperimentChecks 直接連結這個檔案，
// 三邊用同一份數字。isaac_sim/block_layers.py 是同一套層高規則的 Python 版，改這裡要一起改。
public static class LayeredGraspGeometry
{
    // 法蘭面 → 實體指尖（2026-09-29 實測 179 mm，跟 Unity / Isaac 的夾爪模型相同）。3D 批次算關節角、
    // 碰撞檢查與預覽都用這個長度，URSim、Isaac 與實機的指尖才會在同一個位置；
    // 2D 照舊用 RobotArm.toolOffsetZ。isaac_sim_server.py --fingertip_m 的預設值要跟這裡一樣。
    public const double FingertipLengthM = 0.179;
    // 3D 批次的桌面高度 = JsonExecutor.QR1_Z + 這個值（UR 基座座標）。2026-09-29 實測：同一組關節角下實機
    // 比模型高約 30 mm，法蘭與指尖一起高，代表桌面比 QR1_Z 低，不是夾爪長度的問題。2D 照舊用 QR1_Z；
    // isaac_sim_server.py --qr1 的 Z 預設要等於 QR1_Z + 這個值。
    public const double TableZCorrectionM = -0.030;
    // cube 與平放 domino 的高度都是 2.5 cm：積木頂面只可能落在 2.5 cm 的整數倍。
    public const double BlockLayerM = 0.025;
    // perception 頂面系統性偏低，對齊層高前先補這段。桌面上的積木一律是第 1 層（下限），所以這個值只影響
    // 疊起來的積木；黑色的偏差太大（實測 −32～+3 mm），層數主要靠 csharp_server LayeredHeights 的場景結構判斷。
    public const double LayerSnapOffsetM = 0.0075;
    // 指尖停在積木頂面下方 19 mm（離積木底面 6 mm）。放置用同一個深度：積木底面剛好落在下層頂面，
    // 指尖停在下層頂面上方 6 mm，不碰下層。
    public const double GraspDepthBelowTopM = 0.019;
    // 碰撞模型：夾爪本體（半徑 35 mm 的圓柱）只量到手指根部，手指這一段只檢查指尖離桌面的距離。
    // 前提：實體手指（指尖到夾爪本體底面）至少這麼長。
    public const double FingerLengthM = 0.030;
    public const double FingertipTableClearanceM = 0.003;

    /// <summary>perception 的頂面高度 → 對齊層高後的真實頂面高度（最少第 1 層）。</summary>
    public static double SnapTopToLayer(double perceivedTopM) =>
        Math.Max(1.0, Math.Floor((perceivedTopM + LayerSnapOffsetM) / BlockLayerM + 0.5)) * BlockLayerM;

    /// <summary>
    /// 手指根部：從指尖沿工具軸往手腕方向 FingerLengthM。wrist、fingertip 是 UR3eKinematics.LinkPoints
    /// 的最後兩點（兩點連線就是工具軸）。
    /// </summary>
    public static double[] FingerRoot(double[] wrist, double[] fingertip)
    {
        double dx = wrist[0] - fingertip[0], dy = wrist[1] - fingertip[1], dz = wrist[2] - fingertip[2];
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double k = length > FingerLengthM ? FingerLengthM / length : 1.0;
        return new[] { fingertip[0] + dx * k, fingertip[1] + dy * k, fingertip[2] + dz * k };
    }
}
