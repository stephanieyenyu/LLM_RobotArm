"""
isaac_sim_server.py — 常駐 Isaac Sim HTTP 服務（直接操作已存在的 UR3e+夾爪 USD 資產）

跟 perception_server.py 同一種模式：常駐執行、開一個 Flask HTTP server，
讓 csharp_server（在另一台電腦，或同一台）呼叫。

這版不再用 isaacsim.robot.manipulators.examples（這個模組在 Isaac Sim
6.0.0 已經不存在，是本檔案上一版失敗的原因），改成直接參照你手動開過、
已經確認結構的資產檔案：

    D:/isaacsim/ur3_gripper_scene/ur3_gripper_scene/gripper_separate/
        ur3e_with_gripper_for_isaac_sim/ur3e_with_gripper_for_isaac_sim.usd

Stage 結構（在 Isaac Sim 裡實際點開確認過）：
    /ur3e_with_gripper (defaultPrim)
      /world                      ← 機器人 Articulation 根（確認過的完整路徑）
        /base_link, /shoulder_link, /upper_arm_link, /forearm_link,
        /wrist_1_link, /wrist_2_link, /wrist_3_link
        /left_finger, /right_finger
        /joints
          shoulder_pan_joint   (Revolute)
          shoulder_lift_joint  (Revolute)
          elbow_joint          (Revolute)
          wrist_1_joint        (Revolute)
          wrist_2_joint        (Revolute)
          wrist_3_joint        (Revolute)
          left_finger_joint    (Prismatic)
          right_finger_joint   (Prismatic)

不用 PickPlaceController（沒有這個 class 可用），改成自己寫：
  - IK：跟 unity_project/Assets/Scripts/UR3eKinematics.cs 完全一樣的 UR3e
    官方 DH 參數，數值 IK（damped least squares），只解「位置 + 固定朝下
    姿態」（假設永遠從正上方抓取，符合積木堆疊場景，不用解一般 6D 姿態）
  - 夾爪：兩根滑軌手指（prismatic），開合直接設兩個關節的 target 位置

用法（在 Isaac Sim 電腦上）：
  D:\\isaacsim\\python.bat isaac_sim_server.py [--gui]

Endpoint：
  POST /reset          {"scene": [SceneObject...]}
  POST /execute_step    {"source_index": int, "target": SceneObject, "actions": [...]}
  GET  /scene
  GET  /frame

重要 — 完全沒辦法在這個環境測試過，下面幾個地方是最大風險，第一次跑起來
最可能在這幾個地方出錯，麻煩把完整錯誤訊息貼回來：
  1. `from isaacsim.core.prims import Articulation` 這個 import 路徑跟
     class 名稱——不同 Isaac Sim 版本這個可能叫 SingleArticulation 或在
     別的模組底下。
  2. `Articulation` 物件的方法名稱（`.dof_names`、`.set_joint_position_targets()`
     等）——啟動時會把 `robot.dof_names` 印到 log，第一件事就是對照這份清單
     跟上面列的 8 個關節名稱是否一致。
  3. 把資產參照進我們自己建的 World 時，資產檔案裡本身可能也帶了一個
     PhysicsScene，跟 World() 預設建的 PhysicsScene 重複，可能需要手動
     處理（先看啟動時有沒有相關警告）。
  4. 夾爪滑軌的關節限位（開多開、合多合）我沒有你們實際量測的數值，先用
     保守的 ±1.5cm 猜，可能抓不緊或撞到，需要你們依實際夾爪調整
     GRIPPER_OPEN_M / GRIPPER_CLOSE_M。
"""

import argparse
import threading

import numpy as np


def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gui", action="store_true", help="顯示視窗（預設 headless，給 csharp_server 無人值守呼叫用）")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--host", default="0.0.0.0", help="0.0.0.0 讓另一台電腦連得到；只想本機測試可改 127.0.0.1")
    ap.add_argument("--robot_usd", default=r"D:\isaacsim\ur3_gripper_scene\ur3_gripper_scene\gripper_separate\ur3e_with_gripper_for_isaac_sim\ur3e_with_gripper_for_isaac_sim.usd",
                     help="UR3e+夾爪 USD 資產完整路徑，跟你手動開的那個檔案一樣")
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
from isaacsim.core.utils.stage import add_reference_to_stage
from isaacsim.core.prims import Articulation
from isaacsim.sensors.camera import Camera

# ============================================================
# UR3e 官方 DH 參數（跟 UR3eKinematics.cs 完全一致，來源同一份
# universal-robots.com 官方表格，Classical DH：Rz(θ)·Tz(d)·Tx(a)·Rx(α)）
# ============================================================
D1 = 0.15185
A2 = -0.24355
A3 = -0.21320
D4 = 0.13105
D5 = 0.08535
D6 = 0.09210

DH_A = [0.0, A2, A3, 0.0, 0.0, 0.0]
DH_ALPHA = [np.pi / 2, 0.0, 0.0, np.pi / 2, -np.pi / 2, 0.0]
DH_D = [D1, 0.0, 0.0, D4, D5, D6]

ARM_JOINT_NAMES = [
    "shoulder_pan_joint", "shoulder_lift_joint", "elbow_joint",
    "wrist_1_joint", "wrist_2_joint", "wrist_3_joint",
]
GRIPPER_JOINT_NAMES = ["left_finger_joint", "right_finger_joint"]

CUBE_SIZE_M = 0.025
# 猜測值，夾爪實際行程要你們依真實硬體調整
GRIPPER_OPEN_M = 0.015
GRIPPER_CLOSE_M = 0.001

ROBOT_MOUNT_PATH = "/World/ur3e_with_gripper"
ROBOT_ARTICULATION_PATH = ROBOT_MOUNT_PATH + "/world"

# 抓取時手臂固定從正上方垂直向下靠近（符合積木堆疊場景），不解一般姿態 IK
GRASP_ORIENTATION_RPY = (np.pi, 0.0, 0.0)  # 工具 Z 軸朝下

UR_HOME = np.array([0.0, -np.pi / 2, np.pi / 2, -np.pi / 2, -np.pi / 2, 0.0], dtype=np.float64)
END_EFFECTOR_OFFSET_M = 0.01  # 手指尖端到 wrist_3 flange 的額外距離，未實測，先猜
MOVE_STEPS = 120       # 每段動作用幾個 physics step 內插過去
GRIPPER_STEPS = 40

lock = threading.Lock()

world = None
robot: Articulation = None
camera = None
arm_indices = []
gripper_indices = []
blocks = []  # [{"name": str, "cuboid": DynamicCuboid}]


# ============================================================
# UR3e DH 運動學（跟 UR3eKinematics.cs 對應）
# ============================================================

def dh_matrix(a, alpha, d, theta):
    ct, st = np.cos(theta), np.sin(theta)
    ca, sa = np.cos(alpha), np.sin(alpha)
    return np.array([
        [ct, -st * ca,  st * sa, a * ct],
        [st,  ct * ca, -ct * sa, a * st],
        [0.0, sa,       ca,      d],
        [0.0, 0.0,      0.0,     1.0],
    ])


def fk(q):
    """q: 6 個關節角 → 4x4 末端（wrist_3 flange，未加 END_EFFECTOR_OFFSET_M）齊次矩陣"""
    t = np.eye(4)
    for i in range(6):
        t = t @ dh_matrix(DH_A[i], DH_ALPHA[i], DH_D[i], q[i])
    return t


def jacobian_numeric(q, delta=1e-6):
    """數值微分 Jacobian（6x6：前三列位置、後三列姿態的簡化角速度近似）"""
    base = fk(q)
    base_pos = base[:3, 3]
    base_rot = base[:3, :3]
    J = np.zeros((6, 6))
    for i in range(6):
        dq = q.copy()
        dq[i] += delta
        t = fk(dq)
        J[:3, i] = (t[:3, 3] - base_pos) / delta
        drot = (t[:3, :3] - base_rot) / delta
        # 角速度反對稱近似
        w = drot @ base_rot.T
        J[3, i] = (w[2, 1] - w[1, 2]) / 2
        J[4, i] = (w[0, 2] - w[2, 0]) / 2
        J[5, i] = (w[1, 0] - w[0, 1]) / 2
    return J


def rpy_to_matrix(roll, pitch, yaw):
    cr, sr = np.cos(roll), np.sin(roll)
    cp, sp = np.cos(pitch), np.sin(pitch)
    cy, sy = np.cos(yaw), np.sin(yaw)
    Rx = np.array([[1, 0, 0], [0, cr, -sr], [0, sr, cr]])
    Ry = np.array([[cp, 0, sp], [0, 1, 0], [-sp, 0, cp]])
    Rz = np.array([[cy, -sy, 0], [sy, cy, 0], [0, 0, 1]])
    return Rz @ Ry @ Rx


def solve_ik(target_pos_flange, seed_q, max_iters=200, tol=1e-4):
    """
    Damped least squares 數值 IK，只用位置誤差（姿態固定朝下，DOF 有餘裕
    就不特別約束，先求位置解得到能用的關節角）。
    target_pos_flange：wrist_3 flange 的目標位置（已經扣掉 END_EFFECTOR_OFFSET_M）。
    """
    q = seed_q.copy()
    target_rot = rpy_to_matrix(*GRASP_ORIENTATION_RPY)
    lam = 1e-2
    for _ in range(max_iters):
        t = fk(q)
        pos_err = target_pos_flange - t[:3, 3]
        rot_err_mat = target_rot @ t[:3, :3].T
        rot_err = 0.5 * np.array([
            rot_err_mat[2, 1] - rot_err_mat[1, 2],
            rot_err_mat[0, 2] - rot_err_mat[2, 0],
            rot_err_mat[1, 0] - rot_err_mat[0, 1],
        ])
        err = np.concatenate([pos_err, rot_err])
        if np.linalg.norm(pos_err) < tol:
            return q, True
        J = jacobian_numeric(q)
        JT = J.T
        dq = JT @ np.linalg.solve(J @ JT + (lam ** 2) * np.eye(6), err)
        q = q + dq
    return q, False


# ============================================================
# 場景 / 機器人建置
# ============================================================

def block_center(obj):
    """obj: dict 有 x/y/z（z 是全專案的頂面高度慣例），換成方塊中心點座標。"""
    return np.array([obj["x"], obj["y"], obj["z"] - CUBE_SIZE_M / 2], dtype=np.float64)


def build_world():
    global world, robot, camera, arm_indices, gripper_indices
    world = World(stage_units_in_meters=1.0)
    world.scene.add_default_ground_plane()

    add_reference_to_stage(usd_path=args.robot_usd, prim_path=ROBOT_MOUNT_PATH)
    robot = Articulation(prim_paths_expr=ROBOT_ARTICULATION_PATH, name="ur3e")
    world.scene.add(robot)

    camera = Camera(
        prim_path="/World/top_camera",
        position=np.array([0.3, 0.0, 0.8]),
        frequency=20,
        resolution=(640, 480),
    )

    world.reset()
    camera.initialize()

    dof_names = list(robot.dof_names)
    print(f"[isaac_sim_server] robot dof_names = {dof_names}")
    arm_indices = [dof_names.index(n) for n in ARM_JOINT_NAMES]
    gripper_indices = [dof_names.index(n) for n in GRIPPER_JOINT_NAMES]

    goto_joint_positions(UR_HOME, gripper_open=True, steps=10)


def goto_joint_positions(arm_q, gripper_open, steps=MOVE_STEPS):
    """把手臂關節線性內插到 arm_q、夾爪設成開或合，跑 steps 個 physics step。"""
    current = robot.get_joint_positions()
    if current.ndim == 2:
        current = current[0]
    target = current.copy()
    target[arm_indices] = arm_q
    gripper_target = GRIPPER_OPEN_M if gripper_open else GRIPPER_CLOSE_M
    for idx in gripper_indices:
        target[idx] = gripper_target

    start = current.copy()
    for step in range(steps):
        alpha = (step + 1) / steps
        interp = start + (target - start) * alpha
        robot.set_joint_position_targets(interp.reshape(1, -1))
        world.step(render=True)


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
        scale = np.array([size, size, size], dtype=np.float64)
        if obj.get("shape") == "domino":
            if obj.get("orientation") == "vertical":
                scale = np.array([size, size * 2, size], dtype=np.float64)
            else:
                scale = np.array([size * 2, size, size], dtype=np.float64)
        cuboid = world.scene.add(DynamicCuboid(
            prim_path=f"/World/{name}",
            name=name,
            position=block_center(obj),
            scale=scale,
            color=np.array([0.6, 0.6, 0.6], dtype=np.float64),
            mass=0.05,
        ))
        blocks.append({"name": name, "cuboid": cuboid})
    world.reset()
    goto_joint_positions(UR_HOME, gripper_open=True, steps=10)


def pick_place(source_index, target):
    if source_index < 0 or source_index >= len(blocks):
        raise ValueError(f"source_index {source_index} 超出範圍（目前 {len(blocks)} 顆積木）")

    pick_center = np.array(blocks[source_index]["cuboid"].get_world_pose()[0], dtype=np.float64)
    place_center = block_center(target)

    approach_h = 0.06
    pick_flange = pick_center + np.array([0, 0, CUBE_SIZE_M / 2 + END_EFFECTOR_OFFSET_M])
    place_flange = place_center + np.array([0, 0, CUBE_SIZE_M / 2 + END_EFFECTOR_OFFSET_M])

    current = robot.get_joint_positions()
    seed = current[0][arm_indices] if current.ndim == 2 else current[arm_indices]

    def move_to(flange_pos, gripper_open):
        nonlocal seed
        q, ok = solve_ik(flange_pos, seed)
        if not ok:
            print(f"[isaac_sim_server] WARN: IK 沒收斂到目標 {flange_pos}，用最後一次迭代結果繼續")
        seed = q
        goto_joint_positions(q, gripper_open=gripper_open)

    move_to(pick_flange + [0, 0, approach_h], gripper_open=True)   # move_above source
    move_to(pick_flange, gripper_open=True)                        # descend
    move_to(pick_flange, gripper_open=False)                       # grasp（合爪，停在原地讓夾爪有時間夾住）
    for _ in range(GRIPPER_STEPS):
        world.step(render=True)
    move_to(pick_flange + [0, 0, approach_h], gripper_open=False)  # lift
    move_to(place_flange + [0, 0, approach_h], gripper_open=False) # move_above target
    move_to(place_flange, gripper_open=False)                      # descend
    move_to(place_flange, gripper_open=True)                       # release
    for _ in range(GRIPPER_STEPS):
        world.step(render=True)
    move_to(place_flange + [0, 0, approach_h], gripper_open=True)  # lift


def do_execute_step(source_index, target, actions):
    fn_names = {a.get("function") for a in actions}

    if fn_names == {"go_home"}:
        goto_joint_positions(UR_HOME, gripper_open=True, steps=10)
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

    pick_place(source_index, target)


# ============================================================
# Flask HTTP server
# ============================================================

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
