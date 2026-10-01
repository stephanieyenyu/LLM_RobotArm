using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// 虛擬場景檔（repo 的 sim_scenes/*.json）→ Isaac Sim POST /sim/load 的請求內容。規則跟 csharp_server
// SimScene.Load 相同：objects 原樣送出；camera 沒給就用同資料夾的 camera/default.json，那個檔也沒有就送 null
// （Isaac 沿用目前的相機）。積木格式不在這裡檢查：任務開始時 csharp_server 會驗證並重新載入。
public static class SimSceneFile
{
    public class Block
    {
        public string name, shape, orientation;
        public float x, y, z;   // QR 座標（公尺），z 是頂面高度
    }

    public static string BuildLoadRequest(string scenePath, out List<Block> blocks)
    {
        var file = JObject.Parse(File.ReadAllText(scenePath));
        if (!(file["objects"] is JArray objects) || objects.Count == 0)
            throw new InvalidDataException("objects 是空的");
        JToken camera = file["camera"];
        if (camera == null || camera.Type != JTokenType.Object)
        {
            string defaultCamera = Path.Combine(Path.GetDirectoryName(scenePath), "camera", "default.json");
            camera = File.Exists(defaultCamera) ? JObject.Parse(File.ReadAllText(defaultCamera)) : JValue.CreateNull();
        }
        blocks = new List<Block>();
        foreach (var o in objects)
        {
            blocks.Add(new Block
            {
                name = (string)o["name"], shape = (string)o["shape"], orientation = (string)o["orientation"],
                x = (float)o["x"], y = (float)o["y"], z = (float)o["z"],
            });
        }
        // if_idle：Isaac 正在 3D 驗證（任務還在跑）時拒絕重建，不會打斷任務
        var body = new JObject { ["scene"] = objects, ["camera"] = camera, ["if_idle"] = true };
        return body.ToString(Formatting.None);
    }
}
