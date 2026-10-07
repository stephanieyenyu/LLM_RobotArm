using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// 3D 批次的手臂自撞檢查：形狀直接取畫面上 UR3e 模型（含夾爪）的 mesh。底座與 6 個關節各自帶動的那一段各做成一個凸包，
// 依關節角算出各段的姿勢後兩兩檢查重疊（Physics.ComputePenetration），不用手打的膠囊半徑或門檻。
// 相鄰兩段在關節處本來就接在一起，不檢查；手臂直立（Home）時就已經重疊的組合是凸包構造上的接觸（例如手腕的墊塊），
// 也不檢查，建立時印在 Console。只有 3D 批次使用（JsonExecutor.ValidateApproximateRobotCollision），2D 照舊用膠囊。
public sealed class ArmMeshSelfCollision
{
    public static readonly string[] SegmentNames = { "底座", "肩部", "上臂", "前臂", "手腕 1", "手腕 2", "手腕 3／夾爪" };
    static readonly double[] HomeRad = { 0, -System.Math.PI / 2, 0, -System.Math.PI / 2, 0, 0 };

    readonly RobotArm arm;
    // 第 i 個關節的父物件相對第 i-1 個關節（i ≥ 1；中間的物件是固定的）
    readonly Matrix4x4[] parentFromPrevious = new Matrix4x4[6];
    // 0 = 底座（第 1 個關節的父物件底下、不屬於任何關節的 mesh），k = 第 k 個關節帶動的那一段（6 含夾爪）
    readonly MeshCollider[] hulls = new MeshCollider[7];
    readonly List<(int A, int B)> pairs = new List<(int, int)>();
    public string Error { get; }
    public string Summary { get; }

    /// <param name="exclude">不屬於手臂的物件（場景裡的方塊，可能被夾在夾爪底下）</param>
    public ArmMeshSelfCollision(RobotArm arm, IEnumerable<Transform> exclude)
    {
        this.arm = arm;
        Error = Build(exclude?.Where(t => t != null).ToList() ?? new List<Transform>(), out string summary);
        Summary = summary;
    }

    string Build(List<Transform> exclude, out string summary)
    {
        summary = null;
        if (arm == null || arm.Transforms == null || arm.Transforms.Length < 6 || arm.Transforms.Take(6).Any(t => t == null) ||
            arm.RotationAxis == null || arm.RotationAxis.Length < 6 || arm.RotationOffsets == null || arm.RotationOffsets.Length < 6)
            return "RobotArm 的 Transforms / RotationAxis / RotationOffsets 不完整";
        Transform frame = arm.Transforms[0].parent;
        if (frame == null) return "第 1 個關節沒有父物件";
        for (int i = 1; i < 6; i++)
        {
            if (!arm.Transforms[i].IsChildOf(arm.Transforms[i - 1]))
                return $"第 {i + 1} 個關節不在第 {i} 個關節底下";
            parentFromPrevious[i] = arm.Transforms[i - 1].worldToLocalMatrix * arm.Transforms[i].parent.localToWorldMatrix;
            if (parentFromPrevious[i].determinant <= 0f) return $"第 {i} 到第 {i + 1} 個關節之間有鏡射";
        }
        for (int i = 0; i < 6; i++)
        {
            Vector3 s = arm.Transforms[i].localScale;
            if (s.x <= 0f || s.y <= 0f || s.z <= 0f) return $"第 {i + 1} 個關節的 scale 有負值";
        }

        var vertices = Enumerable.Range(0, 7).Select(_ => new List<Vector3>()).ToArray();
        var triangles = Enumerable.Range(0, 7).Select(_ => new List<int>()).ToArray();
        foreach (var mf in frame.GetComponentsInChildren<MeshFilter>(false))
        {
            var renderer = mf.GetComponent<MeshRenderer>();
            Mesh mesh = mf.sharedMesh;
            if (renderer == null || !renderer.enabled || mesh == null) continue;
            if (exclude.Any(t => mf.transform.IsChildOf(t))) continue;
            if (!mesh.isReadable)
                return $"模型 mesh「{mesh.name}」的匯入設定沒開 Read/Write，讀不到外型（手臂與夾爪的 .dae / .obj 都要開）";
            int segment = 0;
            for (int k = 5; k >= 0; k--)
                if (mf.transform.IsChildOf(arm.Transforms[k])) { segment = k + 1; break; }
            Transform segmentFrame = segment == 0 ? frame : arm.Transforms[segment - 1];
            Matrix4x4 toSegment = segmentFrame.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            Vector3[] source;
            int[] sourceTriangles;
            try
            {
                source = mesh.vertices;
                sourceTriangles = mesh.triangles;
            }
            catch (System.Exception ex)
            {
                return $"讀不到 mesh {mesh.name} 的頂點（{ex.Message}）";
            }
            int offset = vertices[segment].Count;
            vertices[segment].AddRange(source.Select(v => toSegment.MultiplyPoint3x4(v)));
            triangles[segment].AddRange(sourceTriangles.Select(t => t + offset));
        }

        var holder = new GameObject("_ArmSelfCollisionHulls_") { hideFlags = HideFlags.HideAndDontSave };
        // 只拿來做 ComputePenetration（姿勢由參數給），放遠一點、不擋射線，不會碰到場景裡的東西
        holder.transform.position = new Vector3(0f, -10000f, 0f);
        holder.layer = 2;
        for (int k = 0; k < 7; k++)
        {
            if (vertices[k].Count < 4) continue;
            var mesh = new Mesh { name = $"ArmHull_{k}", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices[k]);
            mesh.SetTriangles(triangles[k], 0);
            var go = new GameObject($"Hull_{SegmentNames[k]}") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
            go.transform.SetParent(holder.transform, false);
            var collider = go.AddComponent<MeshCollider>();
            collider.convex = true;
            collider.sharedMesh = mesh;
            hulls[k] = collider;
        }
        if (hulls.Count(h => h != null) < 3)
            return $"有 mesh 的段太少（{string.Join("、", Enumerable.Range(0, 7).Where(k => hulls[k] != null).Select(k => SegmentNames[k]))}）";

        var home = Poses(HomeRad);
        var excluded = new List<string>();
        for (int a = 0; a < 7; a++)
            for (int b = a + 2; b < 7; b++)
            {
                if (hulls[a] == null || hulls[b] == null) continue;
                if (Overlap(a, b, home)) excluded.Add($"{SegmentNames[a]}↔{SegmentNames[b]}");
                else pairs.Add((a, b));
            }
        summary = $"用手臂模型外型檢查 {pairs.Count} 組" +
                  (excluded.Count > 0 ? $"；直立時就重疊而排除的：{string.Join("、", excluded)}" : "；直立時沒有重疊的組合");
        return null;
    }

    /// <summary>依關節角（UR 順序，rad）回傳第一組重疊的兩段，沒有重疊回傳 false。</summary>
    public bool Collides(double[] qRad, out string description)
    {
        var poses = Poses(qRad);
        foreach (var (a, b) in pairs)
        {
            if (!Overlap(a, b, poses)) continue;
            description = $"{SegmentNames[a]}碰到{SegmentNames[b]}（依手臂模型外型）";
            return true;
        }
        description = null;
        return false;
    }

    // 各段相對第 1 個關節父物件的姿勢，算法跟 RobotArm.ApplyAnglesToTransforms 相同（Angles 是 UR 關節角，度）
    Matrix4x4[] Poses(double[] qRad)
    {
        var poses = new Matrix4x4[7];
        poses[0] = Matrix4x4.identity;
        for (int i = 0; i < 6; i++)
        {
            Transform t = arm.Transforms[i];
            Matrix4x4 parent = i == 0 ? Matrix4x4.identity : poses[i] * parentFromPrevious[i];
            Quaternion rotation = arm.RestRotation(i) * Quaternion.AngleAxis(
                (float)(qRad[i] * Mathf.Rad2Deg) + arm.RotationOffsets[i], RobotArm.AxisVector(arm.RotationAxis[i]));
            poses[i + 1] = parent * Matrix4x4.TRS(t.localPosition, rotation, t.localScale);
        }
        return poses;
    }

    bool Overlap(int a, int b, Matrix4x4[] poses) =>
        Physics.ComputePenetration(hulls[a], poses[a].GetColumn(3), poses[a].rotation,
                                   hulls[b], poses[b].GetColumn(3), poses[b].rotation, out _, out _);
}
