using System.Text.Json;

/// <summary>
/// 純模擬沒開 Isaac Sim 時的替代世界，只支援 2D：起點是虛擬場景檔，之後只在 2D 整批通過 Unity 預覽的 bitmap 比對、
/// 送 URSim 執行完成後，把放下的物件移到計畫的位置。沒有物理：不會被推動、碰撞或傾倒；3D 疊放仍需要 Isaac 驗證。
/// 世界同時寫到 StreamingAssets/sim_world.json（格式同 perception /scene），Unity 連不上 Isaac 時從這裡顯示積木、做預覽。
/// </summary>
public sealed class VirtualSimWorld
{
    public const string FileName = "sim_world.json";
    public const string Source = "virtual_world";
    readonly List<SceneObject> objects;

    public VirtualSimWorld(IEnumerable<SceneObject> sceneFileObjects) => objects = sceneFileObjects.Select(Copy).ToList();

    /// <summary>目前的物件（複本；順序固定，就是場景 index）。</summary>
    public List<SceneObject> Snapshot() => objects.Select(Copy).ToList();

    /// <summary>把場景 index 的物件放到 target：位置與方向照 target，頂面高度 topM。</summary>
    public void Place(int index, SceneObject target, double topM)
    {
        var o = objects[index];
        o.X = target.X;
        o.Y = target.Y;
        o.Z = topM;
        o.Orientation = target.Orientation ?? o.Orientation;
    }

    public void Write(string streamingAssets)
    {
        var body = new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            source = Source,
            objects = objects.Select(o => new
            {
                name = o.Name, confidence = 1.0, source = Source, shape = o.Shape, orientation = o.Orientation,
                skew_deg = o.SkewDeg, position = new { x = o.X, y = o.Y, z = o.Z, source = Source },
            }),
        };
        string path = Path.Combine(streamingAssets, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(body));
        File.Move(path + ".tmp", path, true);
    }

    public static void Delete(string streamingAssets)
    {
        string path = Path.Combine(streamingAssets, FileName);
        if (File.Exists(path)) File.Delete(path);
    }

    static SceneObject Copy(SceneObject o) => new()
    {
        Name = o.Name, X = o.X, Y = o.Y, Z = o.Z, Shape = o.Shape, Orientation = o.Orientation, SkewDeg = o.SkewDeg
    };
}
