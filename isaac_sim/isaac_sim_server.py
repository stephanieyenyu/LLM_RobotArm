"""
isaac_sim_server.py — 常駐 Isaac Sim HTTP 服務

跟 perception_server.py 同一種模式：常駐執行、開一個 Flask HTTP server，
讓 csharp_server（在另一台電腦）呼叫。不同的是這裡常駐的是 Isaac Sim +
UR3e，每次呼叫是真的模擬手臂抓放動作（不是像 stack_verifier.py 那樣把積木
瞬間擺到最終位置）。

用法（在 Isaac Sim 電腦上）：
  D:\\isaacsim\\python.bat isaac_sim_server.py [--gui]

Endpoint：
  POST /reset          {"scene": [SceneObject...]}
                        清空場景，依給定的積木清單重新擺好（索引順序保留，
                        之後 execute_step 用 source_index 找回同一顆）
  POST /execute_step    {"source_index": int, "target": SceneObject, "actions": [...]}
                        驅動 UR3e 執行這一步的 action_sequence
                        （move_above/descend/grasp/release/lift/wait/go_home）
  GET  /scene           目前每顆積木的位置（頂面高度慣例，跟全專案一致）
  GET  /frame           模擬相機目前畫面（JPEG）

重要 — 這整個檔案完全沒辦法在這個環境測試過，以下地方是照 phase3_pick_place.py
已經驗證過的「UR10 + 真空吸盤 + PickPlaceController」模式類推寫的，能不能動
要在 Isaac Sim 機器上實際跑過才能確認，特別是：

  1. UR3e class：假設 isaacsim.robot.manipulators.examples.universal_robots
     底下有 UR3e，用法跟 UR10 一樣（attach_gripper=True 直接帶內建夾爪
     preset）。如果 import 失敗，代表沒有這個現成 class，需要手動用 USD +
     RMPFlow 建（範圍會再放大，先回報錯誤訊息）。
  2. 夾爪類型：照抄 phase3 用真空吸盤 preset。如果你們實際 UR3e 是平行夾爪，
     PickPlaceController 的 gripper 介面（open/close）可能不能直接沿用，
     需要另外調整。
  3. world.scene.remove_object(name)：假設這是正確的 API 名稱，用來在
     /reset 時清掉上一輪的積木。
  4. Camera 類別 isaacsim.sensors.camera.Camera 建相機截圖：位置/角度沒有
     跟實際相機校準，純粹給 llm.Validate 一張「看得到最終結果」的畫面。
  5. Flask 用 threaded=False：確保所有 request 都在同一條（擁有 Isaac Sim
     context 的）主執行緒上依序處理，不會有跨執行緒呼叫 PhysX 的問題。

第一次跑起來，麻煩把完整的錯誤訊息貼回來，照 stack_verifier.py 那次的方式
一步步除錯。
"""

import argparse
import threading

import numpy as np


def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gui", action="store_true", help="顯示視窗（預設 headless，給 csharp_server 無人值守呼叫用）")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--host", default="0.0.0.0", help="0.0.0.0 讓另一台電腦連得到；只想本機測試可改 127.0.0.1")
    return ap.parse_known_args()[0]


args = parse_args()

# 必須第一步啟動 Isaac Sim
from isaacsim import SimulationApp
simulation_app = SimulationApp({"headless": not args.gui})

# ---- 只有 SimulationApp 啟動後才能 import isaac 相關模組 ----
import cv2
from flask import Flask, Response, jsonify, request
from isaacsim.core.api import World
from isaacsim.core.api.objects import DynamicCuboid
from isaacsim.robot.manipulators.examples.universal_robots import UR3e
from isaacsim.robot.manipulators.examples.universal_robots.controllers.pick_place_controller import (
    PickPlaceController,
)
from isaacsim.sensors.camera import Camera

CUBE_SIZE_M = 0.025
UR_HOME = np.array([0.0, -1.5708, 1.5708, -1.5708, -1.5708, 0.0], dtype=np.float32)
END_EFFECTOR_OFFSET = np.array([0.0, 0.0, 0.01], dtype=np.float32)
# 語義跟 phase3_pick_place.py 一樣：越小 = 該 phase 越慢/越多步
PLACE_EVENTS_DT = [0.008, 0.005, 0.1, 0.1, 0.05, 0.005, 0.0025, 1.0, 0.008, 0.08]
MAX_STEPS_PER_PICK_PLACE = 800

lock = threading.Lock()

world = None
robot = None
ctrl = None
camera = None
blocks = []  # [{"name": str, "cuboid": DynamicCuboid}]


def block_center(obj):
    """obj: dict 有 x/y/z（z 是全專案的頂面高度慣例），換成方塊中心點座標。"""
    return np.array([obj["x"], obj["y"], obj["z"] - CUBE_SIZE_M / 2], dtype=np.float32)


def build_world():
    global world, robot, ctrl, camera
    world = World(stage_units_in_meters=1.0)
    world.scene.add_default_ground_plane()
    robot = world.scene.add(UR3e(
        prim_path="/World/UR3e",
        name="ur3e",
        position=np.array([0.0, 0.0, 0.0]),
        attach_gripper=True,
    ))
    ctrl = PickPlaceController(
        name="pick_place",
        gripper=robot.gripper,
        robot_articulation=robot,
        events_dt=PLACE_EVENTS_DT,
    )
    camera = Camera(
        prim_path="/World/top_camera",
        position=np.array([0.3, 0.0, 0.8]),
        frequency=20,
        resolution=(640, 480),
    )
    world.reset()
    camera.initialize()
    robot.set_joint_positions(UR_HOME)
    for _ in range(10):
        world.step(render=False)


def do_reset(scene):
    global blocks
    for b in blocks:
        try:
            world.scene.remove_object(b["name"])
        except Exception:
            pass
    blocks = []
    for i, obj in enumerate(scene):
        name = f"block_{i}"
        size = CUBE_SIZE_M
        scale = np.array([size, size, size], dtype=np.float32)
        if obj.get("shape") == "domino":
            if obj.get("orientation") == "vertical":
                scale = np.array([size, size * 2, size], dtype=np.float32)
            else:
                scale = np.array([size * 2, size, size], dtype=np.float32)
        cuboid = world.scene.add(DynamicCuboid(
            prim_path=f"/World/{name}",
            name=name,
            position=block_center(obj),
            scale=scale,
            color=np.array([0.6, 0.6, 0.6], dtype=np.float32),
            mass=0.05,
        ))
        blocks.append({"name": name, "cuboid": cuboid})
    world.reset()
    robot.set_joint_positions(UR_HOME)
    for _ in range(10):
        world.step(render=False)


def do_execute_step(source_index, target, actions):
    """
    actions 是這一步的 action_sequence（move_above/descend/grasp/release/
    lift/wait/go_home）。目前用「有沒有 grasp+release 同時出現」來判斷這步
    是不是一次完整的抓放，是的話直接交給 PickPlaceController 跑（跟
    phase3_pick_place.py 對單顆方塊的處理方式一樣）；純 wait 或純 go_home
    的步驟另外處理，不驅動抓放。
    """
    fn_names = {a.get("function") for a in actions}

    if fn_names == {"go_home"}:
        robot.set_joint_positions(UR_HOME)
        for _ in range(10):
            world.step(render=False)
        return

    if fn_names == {"wait"}:
        seconds = max((a.get("seconds") or 0) for a in actions)
        dt = world.get_physics_dt()
        for _ in range(int(seconds / dt)):
            world.step(render=False)
        return

    if "grasp" not in fn_names or "release" not in fn_names:
        # 沒有明確的抓放動作組合（例如只有 move_above/descend/lift），這裡
        # 先不驅動手臂，讓外層的 llm.Validate 用最終場景去判斷結果對不對。
        return

    if source_index < 0 or source_index >= len(blocks):
        raise ValueError(f"source_index {source_index} 超出範圍（目前 {len(blocks)} 顆積木）")

    picking = np.array(blocks[source_index]["cuboid"].get_world_pose()[0], dtype=np.float32)
    placing = block_center(target)

    ctrl.reset()
    robot.gripper.open()
    for _ in range(5):
        world.step(render=True)

    steps = 0
    while not ctrl.is_done():
        world.step(render=True)
        steps += 1
        if steps > MAX_STEPS_PER_PICK_PLACE:
            break
        action = ctrl.forward(
            picking_position=picking,
            placing_position=placing,
            current_joint_positions=robot.get_joint_positions(),
            end_effector_offset=END_EFFECTOR_OFFSET,
        )
        robot.apply_action(action)

    robot.gripper.open()
    for _ in range(20):
        world.step(render=True)


app = Flask(__name__)
import logging
logging.getLogger("werkzeug").setLevel(logging.WARNING)


@app.route("/reset", methods=["POST"])
def endpoint_reset():
    data = request.get_json(force=True)
    with lock:
        try:
            do_reset(data.get("scene", []))
        except Exception as ex:
            return jsonify({"error": str(ex)}), 500
    return jsonify({"ok": True, "count": len(blocks)})


@app.route("/execute_step", methods=["POST"])
def endpoint_execute_step():
    data = request.get_json(force=True)
    with lock:
        try:
            do_execute_step(data.get("source_index", -1), data.get("target") or {}, data.get("actions") or [])
        except Exception as ex:
            return jsonify({"error": str(ex)}), 500
    return jsonify({"ok": True})


@app.route("/scene", methods=["GET"])
def endpoint_scene():
    with lock:
        objects = []
        for b in blocks:
            pos, _ = b["cuboid"].get_world_pose()
            objects.append({
                "name": b["name"],
                "x": float(pos[0]), "y": float(pos[1]),
                "z": float(pos[2] + CUBE_SIZE_M / 2),  # 換回全專案的頂面高度慣例
                "shape": "cube", "orientation": None, "skew_deg": 0.0,
            })
    return jsonify({"objects": objects})


@app.route("/frame", methods=["GET"])
def endpoint_frame():
    with lock:
        try:
            world.render()
            rgba = camera.get_rgba()
        except Exception as ex:
            return f"frame not available: {ex}", 503
    if rgba is None or rgba.size == 0:
        return "frame not available", 503
    bgr = cv2.cvtColor(rgba[:, :, :3], cv2.COLOR_RGB2BGR)
    ok, buf = cv2.imencode(".jpg", bgr)
    if not ok:
        return "encode failed", 500
    return Response(buf.tobytes(), mimetype="image/jpeg")


def main():
    build_world()
    print(f"[isaac_sim_server] listening on http://{args.host}:{args.port}")
    print("  POST /reset         {'scene': [...]}")
    print("  POST /execute_step  {'source_index': int, 'target': {...}, 'actions': [...]}")
    print("  GET  /scene")
    print("  GET  /frame")
    app.run(host=args.host, port=args.port, threaded=False, use_reloader=False)


if __name__ == "__main__":
    main()
