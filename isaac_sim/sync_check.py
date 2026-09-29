"""
sync_check.py — 把目前真實場景投影到 Isaac Sim，存下模擬畫面與疊合圖，檢查對位是否正確

不需要 Isaac Sim 的 Python，一般 Python 3 就能跑（只用標準函式庫）：
    python isaac_sim/sync_check.py
    python isaac_sim/sync_check.py --isaac http://192.168.x.x:6000 --alpha 0.6

流程：
  1. perception GET /scene、/camera、/debug/frame?raw=1
  2. Isaac Sim POST /reset（積木 + 相機；手臂由 isaac_sim_server 跟隨 URSim，不是實體手臂）
  3. Isaac Sim GET /calibration、/frame，POST /overlay
  4. 存到 --out 資料夾：real.jpg、isaac.jpg、overlay.jpg、reset.json、isaac_scene.json

判讀 overlay.jpg：
  - 模擬積木整片平移      → QR1 偏移（--qr1 / JsonExecutor.cs QR1_X/Y/Z）不準
  - 旋轉、縮放、越遠越偏  → 相機位姿或內參不對（perception /camera）
"""

import argparse
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path


def get(url, timeout=10):
    with urllib.request.urlopen(url, timeout=timeout) as resp:
        return resp.read()


def post(url, body, content_type="application/json", timeout=300):
    req = urllib.request.Request(url, data=body, headers={"Content-Type": content_type}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.read()
    except urllib.error.HTTPError as ex:
        raise RuntimeError(f"{url} 回應 {ex.code}：{ex.read().decode('utf-8', 'replace')}") from ex


def scene_objects(scene):
    """跟 csharp_server Program.cs Scene() 一樣，把 perception 物件攤平成 SceneObject。"""
    objects = []
    for o in scene.get("objects", []):
        p = o.get("position")
        if not isinstance(p, dict):
            continue
        objects.append({
            "name": o.get("name") or "",
            "x": p["x"], "y": p["y"], "z": p["z"],
            "shape": o.get("shape") or "cube",
            "orientation": o.get("orientation"),
            "skew_deg": o.get("skew_deg") or 0.0,
        })
    return objects


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--perception", default="http://localhost:5000")
    ap.add_argument("--isaac", default="http://localhost:6000")
    ap.add_argument("--out", default=str(Path(__file__).resolve().parent / "sync_check_output"))
    ap.add_argument("--alpha", type=float, default=0.5, help="疊合圖中模擬畫面的比重")
    ap.add_argument("--camera_json", default=None, help="不從 perception 取相機，改讀這個 JSON 檔（格式同 /camera）")
    args = ap.parse_args()
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    perception, isaac = args.perception.rstrip("/"), args.isaac.rstrip("/")

    scene = json.loads(get(f"{perception}/scene"))
    objects = scene_objects(scene)
    if args.camera_json:
        camera = json.loads(Path(args.camera_json).read_text(encoding="utf-8"))
    else:
        try:
            camera = json.loads(get(f"{perception}/camera"))
        except Exception as ex:
            print(f"取不到 perception /camera（perception_server 是否已重啟到新版？）：{ex}")
            camera = None
    real = get(f"{perception}/debug/frame?raw=1")
    (out / "real.jpg").write_bytes(real)
    print(f"真實場景：{len(objects)} 個物件 → " + "、".join(o["name"] for o in objects))

    reset = json.loads(post(f"{isaac}/reset", json.dumps({"scene": objects, "camera": camera}).encode("utf-8")))
    (out / "reset.json").write_text(json.dumps(reset, ensure_ascii=False, indent=2), encoding="utf-8")
    calibration = json.loads(get(f"{isaac}/calibration"))
    print(f"Isaac 基座校正：yaw={calibration.get('base_yaw_deg')}°，殘差最大 {calibration.get('fk_residual_max_mm')} mm，"
          f"指尖距離 {calibration.get('flange_to_fingertip_m')} m")
    print(f"模擬積木 {reset.get('simulated')} 個；手臂關節來源：{reset.get('robot_joints_source')}；相機：{reset.get('camera')}")
    for w in reset.get("warnings", []):
        print("  警告：" + w)

    (out / "isaac_scene.json").write_text(get(f"{isaac}/scene").decode("utf-8"), encoding="utf-8")
    (out / "isaac.jpg").write_bytes(get(f"{isaac}/frame", timeout=60))
    (out / "overlay.jpg").write_bytes(post(f"{isaac}/overlay?alpha={args.alpha}", real, "image/jpeg"))
    print(f"已存：{out / 'overlay.jpg'}（另有 real.jpg、isaac.jpg、reset.json、isaac_scene.json）")


if __name__ == "__main__":
    try:
        main()
    except Exception as ex:
        print(f"失敗：{ex}")
        sys.exit(1)
