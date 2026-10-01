using System;
using System.IO;
using System.Linq;
using UnityEngine;

// 一鍵切換純模擬 / 實機（UIManager 的「模擬模式」按鈕）。狀態存在 StreamingAssets/run_mode.json，
// csharp_server 每個任務開始時讀同一個檔（csharp_server/RunModeConfig.cs），整個任務都用那個模式：
//   real（預設；沒有這個檔也是）：場景與照片來自相機（perception_server），動作送實體手臂。
//   sim：場景與照片來自 Isaac Sim，任務開始時用虛擬場景檔（repo 的 sim_scenes/*.json）重建 Isaac 世界；
//        csharp_server 把每一批都標 robot_target = "ursim"，只有 URSim 會動，Isaac 跟隨 URSim 做物理。
// Unity 這邊依模式切換：SceneSyncer / perception mode 的網址、Unity 手臂跟隨的對象、手動控制按鈕連的手臂。
// 切到純模擬或換場景的當下，SceneSyncer 先顯示場景檔的積木，同時把場景檔載入 Isaac（/sim/load）；
// 任務開始時 csharp_server 仍依 reset_each_task 再載入一次。
public static class RunMode
{
    [Serializable]
    class Data
    {
        public string mode = "real";
        public string scene = "sim_scenes/two_cubes.json";
        public bool reset_each_task = true;
        public string sim_perception_url = "http://localhost:6000/perception/";
    }

    static Data data;

    // 模式或場景改變時通知（在主執行緒，由 UI 按鈕觸發）
    public static event Action Changed;

    static string FilePath => Path.Combine(Application.streamingAssetsPath, "run_mode.json");

    static Data Current
    {
        get
        {
            if (data == null) Load();
            return data;
        }
    }

    public static bool IsSim => Current.mode == "sim";
    public static string Scene => Current.scene;

    public static string SimPerceptionUrl
    {
        get
        {
            string url = string.IsNullOrWhiteSpace(Current.sim_perception_url)
                ? "http://localhost:6000/perception/" : Current.sim_perception_url;
            return url.EndsWith("/") ? url : url + "/";
        }
    }

    // isaac_sim_server 的根網址（sim_perception_url 去掉 perception/），/sim/load 在這底下
    public static string SimServerUrl
    {
        get
        {
            const string suffix = "perception/";
            string url = SimPerceptionUrl;
            return url.EndsWith(suffix, StringComparison.Ordinal) ? url.Substring(0, url.Length - suffix.Length) : url;
        }
    }

    // 目前場景檔的完整路徑（跟 csharp_server RunModeConfig.ScenePath 相同）
    public static string ScenePath => Path.GetFullPath(Path.Combine(RepoRoot, Current.scene));

    static string RepoRoot => Path.Combine(Application.dataPath, "..", "..");

    public static void Load()
    {
        data = new Data();
        try
        {
            if (File.Exists(FilePath))
                JsonUtility.FromJsonOverwrite(File.ReadAllText(FilePath), data);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RunMode] 讀不懂 {FilePath}，Unity 先當實機模式：{ex.Message}");
            data = new Data();
        }
    }

    // 寫檔失敗就維持原狀並丟例外（Unity 跟 csharp_server 看到的模式必須一致）
    public static void SetSim(bool sim)
    {
        string previous = Current.mode;
        Current.mode = sim ? "sim" : "real";
        try { Save(); }
        catch { Current.mode = previous; throw; }
        Changed?.Invoke();
    }

    public static void SetScene(string scene)
    {
        string previous = Current.scene;
        Current.scene = scene;
        try { Save(); }
        catch { Current.scene = previous; throw; }
        Changed?.Invoke();
    }

    // 先寫暫存檔再換掉，csharp_server 不會讀到寫一半的檔
    static void Save()
    {
        Directory.CreateDirectory(Application.streamingAssetsPath);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonUtility.ToJson(Current, true));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    // repo 根目錄 sim_scenes/ 底下的虛擬場景檔（路徑相對 repo 根目錄，跟 run_mode.json 的 scene 同格式）
    public static string[] AvailableScenes()
    {
        string dir = Path.GetFullPath(Path.Combine(RepoRoot, "sim_scenes"));
        if (!Directory.Exists(dir)) return new string[0];
        return Directory.GetFiles(dir, "*.json")
            .Select(f => "sim_scenes/" + Path.GetFileName(f))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
    }
}
