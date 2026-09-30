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

夾爪長度校正（模擬手臂擺成跟實體手臂同一組關節角，比對兩邊的夾爪）：
    python isaac_sim/sync_check.py --robot_ip 192.168.50.204     # 從實體手臂唯讀埠讀目前關節角
    python isaac_sim/sync_check.py --joints_deg -66.48 -93.14 -98.75 -78.11 90 113.52   # 指尖 179 mm 時離桌 50 mm
  Isaac 這時要不帶 --ursim_ip 啟動，否則手臂會被 URSim 拉走。overlay.jpg 裡實體與模擬的夾爪要重合；
  terminal 印出的「指尖離桌」要跟尺量的一樣。
"""

import argparse
import json
import math
import socket
import struct
import sys
import urllib.error
import urllib.request
from pathlib import Path

# UR3e DH（同 UR3eKinematics.cs / isaac_sim_server.py）
DH_A = [0.0, -0.24355, -0.21320, 0.0, 0.0, 0.0]
DH_ALPHA = [math.pi / 2, 0.0, 0.0, math.pi / 2, -math.pi / 2, 0.0]
DH_D = [0.15185, 0.0, 0.0, 0.13105, 0.08535, 0.09210]


def fk(q):
    """6 個關節角 → (法蘭位置, 工具 z 軸)，UR 基座座標。"""
    t = [[1.0 if i == j else 0.0 for j in range(4)] for i in range(4)]
    for k in range(6):
        ct, st = math.cos(q[k]), math.sin(q[k])
        ca, sa = math.cos(DH_ALPHA[k]), math.sin(DH_ALPHA[k])
        m = [[ct, -st * ca, st * sa, DH_A[k] * ct], [st, ct * ca, -ct * sa, DH_A[k] * st], [0, sa, ca, DH_D[k]], [0, 0, 0, 1]]
        t = [[sum(t[i][n] * m[n][j] for n in range(4)) for j in range(4)] for i in range(4)]
    return [t[i][3] for i in range(3)], [t[i][2] for i in range(3)]


def read_robot_joints(ip, port=30013):
    """讀 UR realtime 唯讀埠的一個封包，取 q_actual（跟 isaac_sim_server UrsimFollower 同一個位置）。只讀不寫。"""
    with socket.create_connection((ip, port), timeout=3) as sock:
        def exact(n):
            buf = b""
            while len(buf) < n:
                chunk = sock.recv(n - len(buf))
                if not chunk:
                    raise ConnectionError("連線中斷")
                buf += chunk
            return buf
        size = struct.unpack(">i", exact(4))[0]
        body = exact(size - 4)
        return list(struct.unpack(">6d", body[248:296]))


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
    ap.add_argument("--robot_ip", default=None, help="夾爪校正：從這台 UR 的唯讀埠 30013 讀目前關節角，模擬手臂擺成一樣")
    ap.add_argument("--joints_deg", type=float, nargs=6, default=None, help="夾爪校正：直接指定 6 個關節角（度）")
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

    joints = None
    if args.robot_ip:
        joints = read_robot_joints(args.robot_ip)
        print(f"實體手臂 {args.robot_ip} 目前關節角（度）：{[round(math.degrees(v), 2) for v in joints]}")
    elif args.joints_deg:
        joints = [math.radians(v) for v in args.joints_deg]
    calibration = json.loads(get(f"{isaac}/calibration"))
    if joints is not None:
        status = json.loads(get(f"{isaac}/status"))
        if (status.get("following") or {}).get("connected"):
            print("警告：Isaac 正在跟隨 URSim，擺好的姿勢會馬上被拉走；校正時請不帶 --ursim_ip 重開 Isaac。")
        flange, tool_z = fk(joints)
        table_z = calibration["qr1_offset_m"][2]
        tip = calibration["flange_to_fingertip_m"]
        print(f"這組關節角下，Isaac 的指尖（法蘭下 {tip * 1000:.0f} mm）離桌 {(flange[2] + tool_z[2] * tip - table_z) * 1000:.1f} mm；"
              f"請用尺量實體指尖離桌多少來對照")

    body = {"scene": objects, "camera": camera}
    if joints is not None:
        body["joints"] = joints
    reset = json.loads(post(f"{isaac}/reset", json.dumps(body).encode("utf-8")))
    (out / "reset.json").write_text(json.dumps(reset, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"Isaac 基座校正：yaw={calibration.get('base_yaw_deg')}°，殘差最大 {calibration.get('fk_residual_max_mm')} mm，"
          f"指尖距離 {calibration.get('flange_to_fingertip_m')} m（夾爪資產本身 {calibration.get('asset_flange_to_fingertip_m')} m）")
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
