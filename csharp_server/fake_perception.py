"""
fake_perception.py - 假 perception_server（不接相機）

用途：
  當你不想接實體相機、但仍要跑 csharp_server + Unity 完整 pipeline 時，
  這支程式假裝是 perception_server，讓 csharp_server 可以正常啟動。

它會：
  1. 監聽 http://localhost:5000
  2. /health   → 永遠回 OK
  3. /scene/mode → 回 {"mode": "idle"} (或最近 POST 進來的 mode)
  4. /scene   → 讀 Unity 寫的 manual_scene.json 回傳
  5. POST /scene/mode → 更新 mode 狀態

Unity 端（UIManager 的「＋ 黃色方塊」按鈕）會把手動生的方塊
自動寫到 unity_project/Assets/StreamingAssets/manual_scene.json，
這支 server 就會讀那個檔案回傳給 csharp_server。

用法：
    cd csharp_server
    C:\\Users\\ASUS\\isaac_env\\Scripts\\python.exe fake_perception.py

  然後照原本流程開 csharp_server + Unity。
"""

import json
import threading
from pathlib import Path
from flask import Flask, jsonify, request

app = Flask(__name__)

# 現在的 scene mode（idle / executing）
_mode_lock = threading.Lock()
_current_mode = "idle"

# 找 Unity 專案的 manual_scene.json（相對這支 script 位置）
BASE_DIR = Path(__file__).resolve().parent
UNITY_SCENE_FILE = BASE_DIR.parent / "unity_project" / "Assets" / "StreamingAssets" / "manual_scene.json"


def read_manual_scene():
    """讀 Unity 寫的 manual_scene.json；不存在就回空 scene"""
    if not UNITY_SCENE_FILE.exists():
        return {
            "image_width": 1280,
            "image_height": 720,
            "objects": [],
            "qrcodes": [],
            "timestamp": 0,
        }
    try:
        with open(UNITY_SCENE_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data
    except Exception as e:
        print(f"[fake_perception] 讀 {UNITY_SCENE_FILE} 失敗: {e}")
        return {"objects": [], "qrcodes": []}


@app.route("/health", methods=["GET"])
def health():
    return jsonify({
        "status": "ok",
        "mode": "fake",
        "message": "fake_perception (no camera)",
    })


@app.route("/scene", methods=["GET"])
def scene():
    return jsonify(read_manual_scene())


@app.route("/scene/mode", methods=["GET", "POST"])
def scene_mode():
    global _current_mode
    if request.method == "POST":
        try:
            data = request.get_json(force=True, silent=True) or {}
            new_mode = data.get("mode", _current_mode)
            with _mode_lock:
                _current_mode = new_mode
            print(f"[fake_perception] mode 切換為 {new_mode}")
        except Exception as e:
            print(f"[fake_perception] POST /scene/mode 失敗: {e}")
    with _mode_lock:
        return jsonify({"mode": _current_mode})


@app.route("/debug/frame", methods=["GET"])
def debug_frame():
    # csharp_server 不會用到，這裡回一個空 200 讓瀏覽器不會 404
    return "no frame in fake mode", 200


if __name__ == "__main__":
    print("=" * 60)
    print("  FAKE perception_server（無相機模式）")
    print("=" * 60)
    print(f"  Manual scene file: {UNITY_SCENE_FILE}")
    print(f"  Listen: http://localhost:5000")
    print("=" * 60)
    print()
    print("流程：")
    print("  1. Unity 按「＋ 黃色方塊」→ 寫 manual_scene.json")
    print("  2. csharp_server 開始跑 → 每次呼叫 /scene 都會讀最新的 json")
    print("  3. Ctrl+C 關掉")
    print()
    app.run(host="0.0.0.0", port=5000, threaded=True)
