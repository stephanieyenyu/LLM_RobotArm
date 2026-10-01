"""
save_sim_camera.py — 把實機相機（perception_server /camera）的內參與位姿存成純模擬的預設相機
（sim_scenes/camera/default.json）。虛擬場景檔沒有指定 camera 時，Isaac Sim 用這個相機出圖，
模擬畫面就跟實拍同一個視角。相機或 QR 貼紙移動過後重跑一次。

perception_server 啟動、看得到 QR1-3 之後執行（一般 Python 3 即可）：
    python isaac_sim/save_sim_camera.py [http://localhost:5000/camera]
"""

import json
import sys
import urllib.request
from pathlib import Path


def main():
    url = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5000/camera"
    cam = json.loads(urllib.request.urlopen(url, timeout=5).read())
    missing = [k for k in ("intrinsics", "pose_in_qr") if k not in cam]
    if missing:
        sys.exit(f"{url} 缺少 {missing}：{cam}")
    cam.pop("timestamp", None)
    out = Path(__file__).resolve().parent.parent / "sim_scenes" / "camera" / "default.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(cam, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"已存 {out}：相機在 QR 座標 {cam['pose_in_qr']['position']}，"
          f"解析度 {cam['intrinsics']['width']}x{cam['intrinsics']['height']}")


if __name__ == "__main__":
    main()
