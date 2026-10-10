"""
isaac_sim_server.py — 常駐 Isaac Sim HTTP 服務：3D 疊放的物理驗證（手臂跟隨 URSim）

整體流程（3D 疊放；2D 平面移動不經過這裡，照舊由 Unity 模擬驗證）：
    Unity/LLM 產生動作 → csharp_server 先把真實場景投影到這裡（/verify/begin）
    → Unity 把關節軌跡送到 URSim 執行 → 本服務的手臂即時跟隨 URSim 的關節角與夾爪（DO4），
      積木用真實物理被夾起、放下
    → URSim 跑完，csharp_server 呼叫 /verify/end：等積木靜止後做幾何檢查（位置、層高、傾倒、
      撞動其他積木、穩定度、指尖有沒有低於桌面），再由 LLM 看模擬畫面
    → 都通過才由 Unity 送真實手臂。
    本服務只連 URSim，不連真實手臂；只讀 URSim 的唯讀 realtime 埠（預設 30013），不送任何指令。

機器人資產（在 Isaac Sim 裡實際點開確認過）：
    D:/isaacsim/ur3_gripper_scene/ur3_gripper_scene/gripper_separate/
        ur3e_with_gripper_for_isaac_sim/ur3e_with_gripper_for_isaac_sim.usd
    /ur3e_with_gripper/world 為 Articulation 根；6 個手臂關節 + left/right_finger_joint（prismatic，無限位）
    實體夾爪指尖在法蘭下 179 mm（2026-09-29 實測），3D 批次在 Unity 也用這個長度算關節角
    （LayeredGraspGeometry.FingertipLengthM）。啟動時量資產的指尖位置，跟 --fingertip_m 不同就把整支夾爪
    （手指 + 本體）沿工具軸移過去（資產較短就往外延伸），讓模擬的指尖跟實物一致。
    資產手指（指尖單指厚 21 mm、寬 20 mm、末端外張）也在啟動時換成實測大小的直方塊（--finger_thickness_m /
    --finger_width_m，預設各 15 mm），張開寬度（--gripper_open_m）與指尖位置不變。

座標系（全部以公尺為單位）：
    QR 座標系    perception /scene 與 csharp_server 使用；QR1 為原點，X = QR1→QR2，Y = QR1→QR3，Z 向上，
                 物件 z 是頂面高度（perception 量測值，系統性偏低，偏多少依顏色而定）。投影時頂面對齊 2.5 cm
                 層高（同 Unity LayeredGraspGeometry.SnapTopToLayer，--layer_snap_offset）；對外（/scene、驗證報告）
                 每塊積木加回自己投影時的量測偏差，維持 perception 慣例（沒動過的積木回報值跟輸入相同）。
    UR 基座座標  控制器 / DH 運動學使用。base = QR + --qr1；X/Y 跟 Unity JsonExecutor.cs QR1_X/Y 一樣，
                 Z 是 3D 批次用的實測桌面高度（QR1_Z + LayeredGraspGeometry.TableZCorrectionM）。
    Isaac 世界   USD 資產的 base_link 跟 UR 控制器 base 差 180°（實測），啟動時用 FK 自動校正。

投影（真實 → 模擬）：
    - 積木：QR 座標、名稱（顏色）、形狀、方向；高度對齊到 2.5 cm 的層數。
    - 相機：perception /camera 的內參 + 相機位姿，模擬畫面跟真實照片同一視角。
    - 桌面：QR1-4 標記、QR1→QR2→QR4→QR3 藍色框線，桌面比標記外緣再大 --table_margin。
    - 手臂：跟隨 URSim（--ursim_ip）；沒給時停在 Unity 的 Home（直立）。

純模擬（Unity「模擬模式」按鈕；不接相機與實體手臂）：
    本服務就是世界：csharp_server 每個任務開始時用虛擬場景檔（repo 的 sim_scenes/*.json）重建積木
    （/sim/load），之後只靠物理改變；/perception/* 跟 perception_server 同格式，csharp_server 與 Unity
    從這裡拿場景和相機畫面。所有動作都送 URSim，手臂跟隨 URSim。3D 驗證不重新投影（/verify/begin 帶
    use_current_world），驗證前 /sim/snapshot、驗證後 /sim/restore，正式執行從驗證前的狀態開始。

用法（在 Isaac Sim 電腦上）：
  D:\\isaacsim\\python.bat isaac_sim_server.py --ursim_ip 192.168.50.221 [--gui]

Endpoint：
  POST /verify/begin   {"scene": [SceneObject...], "camera": {...}?, "steps": [{source_index, target, actions}],
                        "use_current_world": bool?}
  POST /verify/end     → {"pass": bool, "reasons": [...], "checks": [...], "objects": [...], ...}
  POST /reset          {"scene": [...], "camera": {...}?, "joints": [6 rad]?}（只投影，不驗證）
  POST /overlay?alpha=0.5   body = 真實相機 JPEG → 疊合 JPEG
  GET  /scene  /frame  /calibration  /camera  /status
  純模擬：POST /sim/load {"scene": [...], "camera": {...}?, "if_idle": true?}、POST /sim/snapshot、POST /sim/restore
          GET /perception/scene、GET|POST /perception/scene/mode、GET /perception/debug/frame、GET /perception/camera
"""

import argparse
import base64
import collections
import queue
import socket
import struct
import threading
import time
from concurrent.futures import Future

import numpy as np

import block_layers   # 同資料夾；層高規則跟 csharp_server LayeredHeights 相同


def parse_args():
    ap = argparse.ArgumentParser()
    ap.add_argument("--gui", action="store_true", help="顯示視窗（預設 headless，給 csharp_server 無人值守呼叫用）")
    ap.add_argument("--port", type=int, default=6000)
    ap.add_argument("--host", default="0.0.0.0", help="0.0.0.0 讓另一台電腦連得到；只想本機測試可改 127.0.0.1")
    ap.add_argument("--robot_usd", default=r"D:\isaacsim\ur3_gripper_scene\ur3_gripper_scene\gripper_separate\ur3e_with_gripper_for_isaac_sim\ur3e_with_gripper_for_isaac_sim.usd",
                    help="UR3e+夾爪 USD 資產完整路徑")
    # 下面三個必須跟 Unity 一致（JsonExecutor.cs、LayeredGraspGeometry.cs）
    ap.add_argument("--qr1", type=float, nargs=3, default=[-0.38637, -0.35747, 0.030 - 0.030],
                    metavar=("X", "Y", "Z"),
                    help="QR1 在 UR 基座座標的位置：X/Y = JsonExecutor.cs QR1_X/Y（2026-10-10 起 X 不再減 7 mm）；Z = 3D 批次的實測桌面高度 "
                         "= QR1_Z + LayeredGraspGeometry.TableZCorrectionM（2026-09-29 實測桌面比 QR1_Z 低 30 mm）")
    ap.add_argument("--layer_snap_offset", type=float, default=0.0075,
                    help="perception 頂面對齊 2.5 cm 層高前先加的補償（LayeredGraspGeometry.cs LayerSnapOffsetM）")
    ap.add_argument("--ready_q", type=float, nargs=6,
                    default=[-2.35, -1.5708, -1.0472, -1.5708, 1.5708, 0.0], metavar="Q",
                    help="Unity Inspector 的 Ready Joints Rad；URSim 到這個姿勢之後才開始記錄指尖最低點"
                         "（之前是 URSim 上一次停留的姿勢移到 Ready 的過程，不屬於這次計畫）")
    ap.add_argument("--gripper_do", type=int, default=4,
                    help="夾爪用的標準數位輸出編號（JsonExecutor.cs set_standard_digital_out(4, True) = 夾）")
    ap.add_argument("--fingertip_m", type=float, default=0.179,
                    help="實體夾爪法蘭面 → 指尖的實測距離（公尺，2026-09-29 實測 179 mm）；要跟 Unity "
                         "LayeredGraspGeometry.FingertipLengthM 相同（3D 批次算關節角用的長度）。"
                         "資產量到的長度不同時，整支夾爪沿工具軸移到這個位置")
    ap.add_argument("--gripper_open_m", type=float, default=0.035,
                    help="夾爪張開時兩指內側間距（公尺，2026-09-30 實測實體夾爪 3.5 cm）；Isaac 的手指張到同樣寬，"
                         "旁邊積木會不會被手指碰到才跟實物一致")
    ap.add_argument("--finger_thickness_m", type=float, default=0.015,
                    help="實體夾爪單根手指沿閉合方向的厚度（公尺，2026-09-30 實測約 1.5 cm）；啟動時把資產手指"
                         "（指尖厚 21 mm、寬 20 mm）換成這個大小的直方塊。0 = 用資產原本的手指")
    ap.add_argument("--finger_width_m", type=float, default=0.015,
                    help="實體夾爪單根手指垂直閉合方向的寬度（公尺，2026-09-30 實測約 1.5 cm）")
    ap.add_argument("--ursim_ip", default=None, help="URSim IP；手臂即時跟隨它（只讀唯讀埠，不送指令）")
    ap.add_argument("--ursim_port", type=int, default=30013, help="URSim 唯讀 realtime 埠")
    ap.add_argument("--skew_sign", type=float, default=-1.0,
                    help="perception skew_deg（影像座標，y 向下）轉 QR 座標 yaw 的正負號")
    ap.add_argument("--table_margin", type=float, default=0.08, help="桌面比 QR1-4 標記外緣多出的寬度（公尺）")
    return ap.parse_known_args()[0]


args = parse_args()

# 必須第一步啟動 Isaac Sim
from isaacsim import SimulationApp
simulation_app = SimulationApp({"headless": not args.gui})

# ---- 只有 SimulationApp 啟動後才能 import isaac 相關模組 ----
import cv2
from flask import Flask, Response, jsonify, request
from isaacsim.core.api import World
from isaacsim.core.api.materials import PhysicsMaterial
from isaacsim.core.api.objects import DynamicCuboid, GroundPlane
from isaacsim.core.utils.stage import add_reference_to_stage
from isaacsim.core.prims import Articulation
from isaacsim.sensors.camera import Camera
import omni.replicator.core as rep
import omni.usd
from pxr import Gf, PhysxSchema, Usd, UsdGeom, UsdLux, UsdPhysics, UsdShade

# ============================================================
# UR3e 官方 DH 參數（跟 UR3eKinematics.cs 完全一致，Classical DH：Rz(θ)·Tz(d)·Tx(a)·Rx(α)）
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
FLANGE_LINK = "wrist_3_link"   # 實測：wrist_3_link 原點 = DH flange，軸向也一致
FINGER_LINKS = ["left_finger", "right_finger"]

CUBE_SIZE_M = 0.025
BLOCK_MASS_KG = 0.015
# 實測資產（量手指 Mesh 頂點）：手指末端往外張，指尖（最低 15 mm）在關節 0 時內側間距 57.5 mm，
# 上半段最靠內處才 40 mm；指尖單指厚 21 mm、寬 20 mm。關節值每 +1 mm 兩指各往外 1 mm（開）。
# 開 = 指尖內側間距 --gripper_open_m（預設 3.5 cm：2026-09-30 實測實體夾爪張開只有 3.5 cm，只跨得住 2.5 cm 的邊，
# 跨不住 domino 長邊）→ 關節 −11.25 mm；合的目標設在關節 −20 mm（指尖間距 17.5 mm），靠接觸力夾住。
# 實體手指是單指厚、寬各約 15 mm 的直條（2026-09-30 實測），啟動時把資產手指換成這個大小的直方塊（replace_fingers）：
# 內側面整條放在資產指尖最靠內處（關節 0 時間距仍是 57.5 mm），下緣 = 資產指尖，張開寬度與 flange → 指尖距離都不變。
GRIPPER_ZERO_SPACING_M = 0.0575
FINGERTIP_WINDOW_M = 0.015       # 上面說的「指尖」= 手指最低的這一段
FINGER_BOX_NAME = "measured_finger"
GRIPPER_OPEN_POS = (args.gripper_open_m - GRIPPER_ZERO_SPACING_M) / 2
GRIPPER_CLOSE_POS = -0.020
GRIPPER_STIFFNESS = 2000.0
GRIPPER_DAMPING = 20.0

# 跟 Unity JsonExecutor 的 go_home / 收尾 Home 相同（手臂直立）
UR_HOME = np.array([-np.pi / 2, -np.pi / 2, 0.0, -np.pi / 2, 0.0, 0.0], dtype=np.float64)
# 工具朝下的標準姿勢：FK 校正與量指尖距離用
TOOL_DOWN_Q = np.array([0.0, -np.pi / 2, np.pi / 2, -np.pi / 2, -np.pi / 2, 0.0], dtype=np.float64)

# QR1-4 中心在 QR 座標系的位置（目前實測 0.805 × 0.371 m）；收到 perception /camera 的 qr_markers 後以實測為準
DEFAULT_QR_MARKERS = {"QR1": [0.0, 0.0], "QR2": [0.805, 0.0], "QR3": [0.0, 0.371], "QR4": [0.805, 0.371]}
DEFAULT_QR_SIZE_M = 0.073
TABLE_THICKNESS_M = 0.04
FLOOR_BELOW_TABLE_M = 0.75
WORKSPACE_PATH = "/World/workspace"
BLOCK_MATERIAL_PATH = "/World/Physics/block_material"

# 驗證門檻
VERIFY_XY_TOL_M = 0.020          # 放置位置 XY 誤差（含 perception 抖動與夾取偏移）
VERIFY_Z_TOL_M = 0.008           # 頂面高度與預期層高的誤差
VERIFY_TILT_TOL_DEG = 10.0       # 傾斜角
VERIFY_MOVED_TOL_M = 0.010       # 沒被搬的積木位移超過這個就算被撞動
VERIFY_STABLE_TOL_M = 0.002      # 靜止觀察 1 秒內的最大位移
VERIFY_OVERLAP_THRESHOLD = 0.9   # 畫面重疊率要大於這個（跟 Unity JsonExecutor.bitmapOverlapThreshold 相同）
OVERLAP_PIXEL_M = 0.001          # 畫面重疊率 1 mm 一像素（同 Unity FigureImageOverlap）
OVERLAP_ALIGN_M = 0.005          # 算重疊率前，實際圖形整體平移對齊的搜尋範圍（±5 mm）
VERIFY_SUPPORT_RADIUS_M = 0.020  # 上下兩塊中心 XY 距離在這之內才算疊在上面
VERIFY_TIP_TABLE_TOL_M = 0.003   # 指尖（依 URSim 關節角算）低於桌面超過這個就算撞桌
VERIFY_SETTLE_MAX_S = 3.0
VERIFY_STABLE_WINDOW_S = 1.0
URSIM_STALE_S = 1.0
READY_TOL_RAD = 0.04             # 跟 Unity 判定「到達」的 HOME_JOINT_TOLERANCE_RAD 相同
LAYER_EDGE_WARN_M = 0.009        # 對齊層高的殘差超過這個（離兩層分界 < 3.5 mm）就提醒可能判錯層

READY_Q = np.array(args.ready_q, dtype=np.float64)

ROBOT_MOUNT_PATH = "/World/ur3e_with_gripper"
ROBOT_ARTICULATION_PATH = ROBOT_MOUNT_PATH + "/world"
CAMERA_PATH = "/World/sync_camera"

QR1_OFFSET = np.array(args.qr1, dtype=np.float64)

COLORS = {
    "yellow": np.array([0.95, 0.75, 0.05]),
    "black": np.array([0.05, 0.05, 0.05]),
}
DEFAULT_COLOR = np.array([0.6, 0.6, 0.6])

world = None
robot: Articulation = None
camera = None
arm_indices = []
gripper_indices = []
blocks = []           # [{"index": int, "source": dict, "cuboid": DynamicCuboid | None, "dims": np.ndarray}]
block_material = None
calibration = {}
camera_state = {"source": "default"}
camera_pose_world = None   # (位置, ROS 慣例旋轉) 最後一次設定的模擬相機位姿，出圖前放回去
qr_markers = dict(DEFAULT_QR_MARKERS)
qr_size_m = DEFAULT_QR_SIZE_M
workspace_signature = None
follower = None
verify_state = {"active": False}
follow_tick_count = 0
# 純模擬：Unity 透過 /perception/scene/mode 通知執行中 / 閒置（跟 perception_server 相同）；驗證前的積木位姿快照
perception_mode = "idle"
world_snapshot = None

# UR 基座 → Isaac 世界（啟動時由 FK 校正覆寫）
R_WORLD_FROM_BASE = np.eye(3)
T_WORLD_FROM_BASE = np.zeros(3)
BASE_YAW = 0.0
TCP_OFFSET_M = 0.179   # flange → 指尖（啟動時由資產幾何量測覆寫）


def log(msg):
    print(msg, flush=True)   # stdout 導到檔案時是區塊緩衝，不 flush 會看不到


# ============================================================
# 數學工具
# ============================================================

def rot_z(a):
    c, s = np.cos(a), np.sin(a)
    return np.array([[c, -s, 0.0], [s, c, 0.0], [0.0, 0.0, 1.0]])


def quat_wxyz_from_matrix(m):
    m = np.asarray(m, dtype=np.float64)
    tr = np.trace(m)
    if tr > 0:
        s = np.sqrt(tr + 1.0) * 2
        q = [0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = [(m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s]
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = [(m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s]
    else:
        s = np.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = [(m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s]
    q = np.array(q)
    return q / np.linalg.norm(q)


def matrix_from_quat_wxyz(q):
    w, x, y, z = np.asarray(q, dtype=np.float64) / np.linalg.norm(q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def qr_to_base(p):
    return np.asarray(p, dtype=np.float64) + QR1_OFFSET


def base_to_qr(p):
    return np.asarray(p, dtype=np.float64) - QR1_OFFSET


def base_to_world(p):
    return R_WORLD_FROM_BASE @ np.asarray(p, dtype=np.float64) + T_WORLD_FROM_BASE


def world_to_base(p):
    return R_WORLD_FROM_BASE.T @ (np.asarray(p, dtype=np.float64) - T_WORLD_FROM_BASE)


def qr_to_world(p):
    return base_to_world(qr_to_base(p))


def world_to_qr(p):
    return base_to_qr(world_to_base(p))


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
    """q: 6 個關節角 → 4x4 flange（wrist_3）在 UR 基座座標的齊次矩陣"""
    t = np.eye(4)
    for i in range(6):
        t = t @ dh_matrix(DH_A[i], DH_ALPHA[i], DH_D[i], q[i])
    return t


def fingertip_height_qr(q):
    """依關節角算指尖最低點相對 QR 平面（桌面）的高度；跟實體接觸無關，是指令姿勢本身的高度。"""
    t = fk(q)
    tip_base = t[:3, 3] + t[:3, 2] * TCP_OFFSET_M
    return float(tip_base[2] - QR1_OFFSET[2])


# ============================================================
# 機器人狀態
# ============================================================

def current_full_q():
    q = robot.get_joint_positions()
    q = np.asarray(q, dtype=np.float64)
    return q[0] if q.ndim == 2 else q


def current_arm_q():
    return current_full_q()[arm_indices]


def link_positions():
    view = robot._physics_view
    t = view.get_link_transforms()
    t = t.numpy() if hasattr(t, "numpy") else np.asarray(t)
    t = t.reshape(view.count, view.max_links, 7)[0]
    return {name: t[i] for i, name in enumerate(robot.body_names)}


# ============================================================
# 手臂自撞（3D 驗證）
# ============================================================
# 形狀直接取機器人 USD 裡每個連桿的碰撞體（collisions 底下的 Mesh、換上的實測手指方塊），用 physics 的連桿位姿擺好，
# 兩兩做凸包相交測試（GJK），沒有手打的形狀或門檻。6 個手臂關節把機器人切成 7 段（底座、肩部、上臂、前臂、
# 手腕 1、手腕 2、手腕 3 與夾爪）：同一段的連桿（夾爪本體與手指）、相鄰兩段（在關節處本來就接在一起）不檢查；
# 手臂直立（UR_HOME）時就已經相交的連桿組合是凸包構造上的接觸，啟動時印出來並排除。
SEGMENT_NAMES = ["底座", "肩部", "上臂", "前臂", "手腕 1", "手腕 2", "手腕 3／夾爪"]
self_collision = {"links": {}, "pairs": [], "excluded": [], "error": "還沒建立"}


def _gjk_support(a, b, d):
    return a[np.argmax(a @ d)] - b[np.argmax(b @ -d)]


def _gjk_line(pts):
    a, b = pts
    ab, ao = b - a, -a
    if ab @ ao > 0:
        return [a, b], np.cross(np.cross(ab, ao), ab)
    return [a], ao


def _gjk_triangle(pts):
    a, b, c = pts
    ab, ac, ao = b - a, c - a, -a
    abc = np.cross(ab, ac)
    if np.cross(abc, ac) @ ao > 0:
        if ac @ ao > 0:
            return [a, c], np.cross(np.cross(ac, ao), ac)
        return _gjk_line([a, b])
    if np.cross(ab, abc) @ ao > 0:
        return _gjk_line([a, b])
    if abc @ ao > 0:
        return [a, b, c], abc
    return [a, c, b], -abc


def _gjk_tetrahedron(pts):
    a, b, c, d = pts
    ab, ac, ad, ao = b - a, c - a, d - a, -a
    if np.cross(ab, ac) @ ao > 0:
        return _gjk_triangle([a, b, c])
    if np.cross(ac, ad) @ ao > 0:
        return _gjk_triangle([a, c, d])
    if np.cross(ad, ab) @ ao > 0:
        return _gjk_triangle([a, d, b])
    return None, None


def convex_hulls_touch(a, b, max_iterations=64):
    """兩組點的凸包是否相交（含剛好碰到）：GJK，看 Minkowski 差是否包含原點。沒收斂時當作相交（寧可誤報）。"""
    d = a.mean(axis=0) - b.mean(axis=0)
    if not d.any():
        d = np.array([1.0, 0.0, 0.0])
    p = _gjk_support(a, b, d)
    simplex, d = [p], -p
    for _ in range(max_iterations):
        if d @ d < 1e-20:
            return True
        p = _gjk_support(a, b, d)
        if p @ d <= 0:
            return False
        simplex = [p] + simplex
        if len(simplex) == 2:
            simplex, d = _gjk_line(simplex)
        elif len(simplex) == 3:
            simplex, d = _gjk_triangle(simplex)
        else:
            simplex, d = _gjk_tetrahedron(simplex)
            if simplex is None:
                return True
    return True


def collision_points(link_prim):
    """連桿底下所有啟用中的碰撞體頂點（連桿自身座標）：Mesh 取頂點，Cube 取 8 個角，其他形狀不支援（略過）。"""
    inverse = UsdGeom.Xformable(link_prim).ComputeLocalToWorldTransform(Usd.TimeCode.Default()).GetInverse()
    pts = []
    for prim in Usd.PrimRange(link_prim, Usd.TraverseInstanceProxies()):   # 碰撞 Mesh 在 instance 底下
        if not prim.HasAPI(UsdPhysics.CollisionAPI):
            continue
        enabled = prim.GetAttribute("physics:collisionEnabled")
        if enabled and enabled.Get() is False:
            continue
        for shape in Usd.PrimRange(prim, Usd.TraverseInstanceProxies()):
            if shape.IsA(UsdGeom.Mesh):
                raw = UsdGeom.Mesh(shape).GetPointsAttr().Get()
                local = np.asarray(raw, dtype=np.float64) if raw is not None else np.zeros((0, 3))
            elif shape.IsA(UsdGeom.Cube):
                h = float(UsdGeom.Cube(shape).GetSizeAttr().Get()) / 2.0
                local = np.array(np.meshgrid([-h, h], [-h, h], [-h, h])).T.reshape(-1, 3)
            else:
                continue
            if not len(local):
                continue
            rel = np.array(UsdGeom.Xformable(shape).ComputeLocalToWorldTransform(Usd.TimeCode.Default()) * inverse)
            pts.append((np.c_[local, np.ones(len(local))] @ rel)[:, :3])
    return np.unique(np.vstack(pts), axis=0) if pts else np.zeros((0, 3))


def touching_links(pairs):
    """目前 physics 姿勢下第一組凸包相交的連桿 (a, b)，沒有回傳 None。先用外接球排除離很遠的組合。"""
    poses, placed = link_positions(), {}

    def place(name):
        if name not in placed:
            pose = poses[name].astype(np.float64)
            rot = matrix_from_quat_wxyz([pose[6], pose[3], pose[4], pose[5]])   # physics 是 xyzw
            link = self_collision["links"][name]
            placed[name] = (rot, pose[:3], rot @ link["center"] + pose[:3])
        return placed[name]

    for a, b in pairs:
        (ra, ta, ca), (rb, tb, cb) = place(a), place(b)
        la, lb = self_collision["links"][a], self_collision["links"][b]
        if np.linalg.norm(ca - cb) > la["radius"] + lb["radius"]:
            continue
        if convex_hulls_touch(la["points"] @ ra.T + ta, lb["points"] @ rb.T + tb):
            return a, b
    return None


def build_self_collision_model():
    """啟動時、手臂在 UR_HOME（夾爪張開）時呼叫：各連桿碰撞體的頂點、所屬的段，以及要檢查的連桿組合。"""
    stage = omni.usd.get_context().get_stage()
    root = stage.GetPrimAtPath(ROBOT_MOUNT_PATH)
    group = {}

    def find(name):
        while group.setdefault(name, name) != name:
            group[name] = group[group[name]]
            name = group[name]
        return name

    # 手臂關節以外的關節（固定、夾爪滑軌）連在一起的連桿屬於同一段；手臂關節依序把段接起來
    arm_joints = {}
    for prim in Usd.PrimRange(root):
        if not prim.IsA(UsdPhysics.Joint):
            continue
        joint = UsdPhysics.Joint(prim)
        body0, body1 = joint.GetBody0Rel().GetTargets(), joint.GetBody1Rel().GetTargets()
        if not body0 or not body1:
            continue
        if prim.GetName() in ARM_JOINT_NAMES:
            arm_joints[prim.GetName()] = (body0[0].name, body1[0].name)
        else:
            group[find(body0[0].name)] = find(body1[0].name)
    missing = [n for n in ARM_JOINT_NAMES if n not in arm_joints]
    if missing:
        raise RuntimeError(f"USD 裡找不到手臂關節 {missing}")
    segment = {find(arm_joints[ARM_JOINT_NAMES[0]][0]): 0}
    for k, name in enumerate(ARM_JOINT_NAMES):
        segment[find(arm_joints[name][1])] = k + 1

    links = {}
    for name in robot.body_names:
        prim = stage.GetPrimAtPath(f"{ROBOT_MOUNT_PATH}/{name}")
        seg = segment.get(find(name))
        if not prim or seg is None:
            continue
        pts = collision_points(prim)
        if len(pts):
            lo, hi = pts.min(axis=0), pts.max(axis=0)
            center = (lo + hi) / 2
            links[name] = {"segment": seg, "points": pts, "center": center,
                           "radius": float(np.linalg.norm(pts - center, axis=1).max())}
    if len({link["segment"] for link in links.values()}) < 3:
        raise RuntimeError(f"有碰撞體的連桿太少（{sorted(links)}），無法檢查")
    self_collision["links"] = links
    names = sorted(links, key=lambda n: (links[n]["segment"], n))
    pairs = [(a, b) for i, a in enumerate(names) for b in names[i + 1:]
             if abs(links[a]["segment"] - links[b]["segment"]) > 1]
    excluded = []
    while True:
        hit = touching_links([p for p in pairs if p not in excluded])
        if hit is None:
            break
        excluded.append(hit)
    self_collision["pairs"] = [p for p in pairs if p not in excluded]
    self_collision["excluded"] = excluded
    self_collision["error"] = None
    calibration["self_collision"] = {
        "links": {n: SEGMENT_NAMES[links[n]["segment"]] for n in names},
        "checked_pairs": len(self_collision["pairs"]),
        "excluded_at_home": [f"{a}↔{b}" for a, b in excluded],
    }
    log(f"[isaac_sim_server] 手臂自撞檢查：{len(links)} 個連桿有碰撞體，檢查 {len(self_collision['pairs'])} 組；"
        + ("直立時就相交而排除的：" + "、".join(f"{a}↔{b}" for a, b in excluded) if excluded else "直立時沒有相交的組合"))


def describe_self_collision(hit, q):
    a, b = hit
    links = self_collision["links"]
    return (f"{SEGMENT_NAMES[links[a]['segment']]}（{a}）碰到 {SEGMENT_NAMES[links[b]['segment']]}（{b}），"
            f"URSim 關節角 [{', '.join(f'{v:.1f}' for v in np.degrees(q))}]°")


def full_target(arm_q, gripper_pos):
    target = current_full_q().copy()
    target[arm_indices] = arm_q
    target[gripper_indices] = gripper_pos
    return target


def apply_gripper_gains():
    """資產的手指驅動剛性只有 200 N/m（約 1.4 N 夾持力），加上手指內側面往指尖外擴約 8°，
    積木一抬就滑出去；實測提高到 2000 N/m 才夾得住（world.reset 後重新套用）。"""
    kps, kds = robot.get_gains()
    kps, kds = np.array(kps, dtype=np.float32), np.array(kds, dtype=np.float32)
    kps[..., gripper_indices] = GRIPPER_STIFFNESS
    kds[..., gripper_indices] = GRIPPER_DAMPING
    robot.set_gains(kps=kps, kds=kds)


def teleport(arm_q, gripper_pos=GRIPPER_OPEN_POS, settle_steps=5):
    """直接把關節設到指定角度（不經過內插）：對齊 URSim 目前姿勢 / 校正用。"""
    target = full_target(np.asarray(arm_q, dtype=np.float64), gripper_pos)
    robot.set_joint_positions(target.reshape(1, -1))
    robot.set_joint_position_targets(target.reshape(1, -1))
    robot.set_joint_velocities(np.zeros_like(target).reshape(1, -1))
    for _ in range(settle_steps):
        world.step(render=False)


# ============================================================
# 跟隨 URSim（唯讀 realtime 埠，只讀不寫）
# ============================================================

URSIM_GAP_LOG_S = 0.3   # 兩次收到 URSim 資料的間隔超過這個就記一筆並印出來


class UrsimFollower:
    """背景執行緒持續讀 URSim realtime 封包：q_actual（第 252 byte 起 6 個 double）與
    Digital outputs（第 1044 byte 的 double，位元 --gripper_do = 夾爪）。斷線會自動重連。
    每次把 socket 裡累積的資料一次讀完、只留最新一包（2026-10-07）：URSim 每秒送 500 包，以前逐包讀，
    這個執行緒搶不到 GIL（主執行緒在跑物理與畫面）時會越積越多，手臂跟得越來越慢、URSim 那端也可能因為送不出去而停住。
    資料中斷（間隔超過 URSIM_GAP_LOG_S）時記下並印出：中斷期間 Isaac 的手臂停在原地，資料回來時會一口氣追上
    URSim，夾著的積木會被甩掉。印出的「這次收到幾包」「控制器時鐘走了多久」用來分辨是哪一邊停住：
    收到很多包 = Isaac 這邊太久沒讀；只收到一兩包、控制器時鐘也幾乎沒走 = URSim（VirtualBox）那邊停住。"""

    def __init__(self, ip, port):
        self.ip, self.port = ip, port
        self.lock = threading.Lock()
        self.q = None
        self.gripper_closed = False
        self.stamp = 0.0
        self.ctrl_time = None
        self.packets = 0
        self.gaps = 0
        self.max_gap_s = 0.0
        self.reconnects = 0
        self.gap_log = collections.deque(maxlen=1000)   # (收到資料的時間, 中斷秒數)
        self.error = None
        threading.Thread(target=self._run, daemon=True, name="ursim_follower").start()

    def gaps_since(self, t):
        """t 之後的資料中斷（秒數清單），驗證結束時列在「URSim 資料完整」的說明裡。"""
        with self.lock:
            return [gap for stamp, gap in self.gap_log if stamp >= t]

    def _run(self):
        while True:
            try:
                with socket.create_connection((self.ip, self.port), timeout=3) as sock:
                    sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 20)
                    sock.settimeout(3)
                    buf = bytearray()
                    while True:
                        chunk = sock.recv(1 << 16)
                        if not chunk:
                            raise ConnectionError("連線中斷")
                        buf += chunk
                        latest, count = None, 0
                        while len(buf) >= 4:
                            size = struct.unpack_from(">i", buf, 0)[0]
                            if size < 300 or size > 10000:
                                raise ValueError(f"realtime 封包長度異常：{size}")
                            if len(buf) < size:
                                break
                            latest = bytes(buf[4:size])   # 去掉開頭 4 byte 的長度（跟以前逐包讀到的 body 相同）
                            del buf[:size]
                            count += 1
                        if latest is not None:
                            self._accept(latest, count)
            except Exception as ex:
                text = f"{type(ex).__name__}: {ex}"
                with self.lock:
                    repeated = self.error == text   # URSim 沒開時一直重連，同樣的錯誤只印一次
                    self.error = text
                    self.reconnects += 1
                if not repeated:
                    log(f"[isaac_sim_server] URSim 連線中斷（{text}），重新連線")
                time.sleep(0.5)

    def _accept(self, body, count):
        q = np.array(struct.unpack(">6d", body[248:296]), dtype=np.float64)
        ctrl_time = struct.unpack(">d", body[0:8])[0]
        now = time.time()
        with self.lock:
            closed = self.gripper_closed
            if len(body) >= 1048:
                bits = int(struct.unpack(">d", body[1040:1048])[0])
                closed = bool((bits >> args.gripper_do) & 1)
            gap = now - self.stamp if self.stamp else 0.0
            ctrl_step = ctrl_time - self.ctrl_time if self.ctrl_time is not None else None
            self.q, self.gripper_closed, self.stamp, self.ctrl_time, self.error = q, closed, now, ctrl_time, None
            self.packets += count
            if gap > URSIM_GAP_LOG_S:
                self.gaps += 1
                self.max_gap_s = max(self.max_gap_s, gap)
                self.gap_log.append((now, gap))
        if gap > URSIM_GAP_LOG_S:
            ctrl_text = f"控制器時鐘走了 {ctrl_step:.2f} s" if ctrl_step is not None else "控制器時鐘不明"
            log(f"[isaac_sim_server] URSim 資料中斷 {gap:.2f} s（這次收到 {count} 包，{ctrl_text}）")

    def latest(self):
        """回傳 (q 或 None（超過 URSIM_STALE_S 沒更新）, 夾爪是否夾緊)。"""
        with self.lock:
            fresh = self.q is not None and time.time() - self.stamp < URSIM_STALE_S
            return (self.q.copy() if fresh else None), self.gripper_closed

    def status(self):
        with self.lock:
            age = time.time() - self.stamp if self.stamp else None
            return {
                "ursim": f"{self.ip}:{self.port}",
                "connected": self.q is not None and age is not None and age < URSIM_STALE_S,
                "last_update_s_ago": round(age, 2) if age is not None else None,
                "packets": self.packets,
                "gaps_over_0_3s": self.gaps,
                "max_gap_s": round(self.max_gap_s, 2),
                "reconnects": self.reconnects,
                "joints_deg": np.degrees(self.q).round(2).tolist() if self.q is not None else None,
                "gripper_closed": self.gripper_closed,
                "error": self.error,
            }


def at_ready(q):
    """URSim 關節角是否已到 Unity 的 Ready（每軸差 ≤ READY_TOL_RAD，角度差取 ±π 以內）。"""
    return float(np.max(np.abs(np.angle(np.exp(1j * (q - READY_Q)))))) <= READY_TOL_RAD


def follow_step(render):
    """把手臂關節與夾爪目標設成 URSim 目前狀態，跑一個 physics step。URSim 沒資料時照樣推進物理。
    驗證期間同時記錄指尖最低高度與 URSim 斷線狀況。Unity 每一批都先把手臂送到 Ready 才開始計畫的軌跡，
    所以指尖最低點從 URSim 第一次到 Ready 開始記；之前是 URSim 從上一次停留的姿勢移到 Ready，不屬於這次計畫。"""
    q, closed = follower.latest() if follower else (None, False)
    if q is not None:
        robot.set_joint_position_targets(full_target(q, GRIPPER_CLOSE_POS if closed else GRIPPER_OPEN_POS).reshape(1, -1))
    world.step(render=render)
    if verify_state.get("active"):
        if q is None:
            verify_state["stale_steps"] += 1
        else:
            verify_state["follow_steps"] += 1
            tip = fingertip_height_qr(q)
            verify_state["min_tip_all_m"] = min(verify_state["min_tip_all_m"], tip)
            if not verify_state["ready_reached"] and at_ready(q):
                verify_state["ready_reached"] = True
            if verify_state["ready_reached"]:
                verify_state["min_tip_m"] = min(verify_state["min_tip_m"], tip)
            # 手臂自撞：每一步都用 physics 目前的連桿位姿檢查；跟指尖最低點一樣分「到 Ready 之後」與「整段」記第一次
            if self_collision["error"] is None and verify_state["self_hit"] is None:
                hit = touching_links(self_collision["pairs"])
                if hit is not None:
                    text = describe_self_collision(hit, q)
                    if verify_state["self_hit_all"] is None:
                        verify_state["self_hit_all"] = text
                        log(f"[isaac_sim_server] ✗ 手臂自撞：{text}" +
                            ("" if verify_state["ready_reached"] else "（URSim 還沒到 Ready）"))
                    if verify_state["ready_reached"]:
                        verify_state["self_hit"] = text
        if verify_state["follow_steps"] % 6 == 0:       # 每 0.1 秒記一次每塊積木的最高頂面，判斷有沒有被夾起
            for b in blocks:
                if b["cuboid"] is not None:
                    top = block_pose_qr(b)[1]
                    verify_state["max_top"][b["index"]] = max(verify_state["max_top"].get(b["index"], top), top)


def sim_seconds(seconds):
    """在工作裡推進物理 seconds 秒，手臂持續跟隨 URSim。"""
    for _ in range(max(1, int(round(seconds / world.get_physics_dt())))):
        follow_step(render=args.gui)


# ============================================================
# 啟動校正：UR 基座 ↔ Isaac 世界、指尖距離
# ============================================================

def calibrate_robot_frame():
    global R_WORLD_FROM_BASE, T_WORLD_FROM_BASE, BASE_YAW, TCP_OFFSET_M, calibration
    poses = [TOOL_DOWN_Q,
             np.array([0.6, -1.2, 1.3, -1.7, -1.57, 0.3]),
             np.array([-0.9, -1.9, 1.8, -1.5, -1.57, -0.7]),
             np.array([2.0, -1.0, 1.0, -1.5, -1.2, 1.0])]
    base_pts, world_pts = [], []
    for q in poses:
        teleport(q, settle_steps=10)
        base_pts.append(fk(q)[:3, 3])
        world_pts.append(link_positions()[FLANGE_LINK][:3].astype(np.float64))
    base_pts, world_pts = np.array(base_pts), np.array(world_pts)

    # 平面 Procrustes：world = Rz(yaw)·base + t
    bc, wc = base_pts.mean(axis=0), world_pts.mean(axis=0)
    b, w = base_pts - bc, world_pts - wc
    yaw = np.arctan2(np.sum(b[:, 0] * w[:, 1] - b[:, 1] * w[:, 0]), np.sum(b[:, 0] * w[:, 0] + b[:, 1] * w[:, 1]))
    R = rot_z(yaw)
    t = wc - R @ bc
    residuals = np.linalg.norm((R @ base_pts.T).T + t - world_pts, axis=1)

    ok = bool(residuals.max() < 0.005)
    if ok:
        R_WORLD_FROM_BASE, T_WORLD_FROM_BASE, BASE_YAW = R, t, float(yaw)

    TCP_OFFSET_M = measure_fingertip() or TCP_OFFSET_M
    calibration = {
        "base_yaw_deg": round(float(np.degrees(yaw)), 3),
        "base_translation_m": [round(float(v), 5) for v in t],
        "fk_residual_max_mm": round(float(residuals.max()) * 1000, 2),
        "fk_residuals_mm": [round(float(r) * 1000, 2) for r in residuals],
        "applied": ok,
        "asset_flange_to_fingertip_m": round(TCP_OFFSET_M, 4),
        "flange_to_fingertip_m": round(TCP_OFFSET_M, 4),
        "qr1_offset_m": QR1_OFFSET.tolist(),
        "layer_snap_offset_m": args.layer_snap_offset,
        "ready_q": [round(v, 4) for v in args.ready_q],
    }
    log(f"[isaac_sim_server] FK 校正：UR 基座 → Isaac 世界 yaw={calibration['base_yaw_deg']}°, "
        f"t={calibration['base_translation_m']}, 殘差最大 {calibration['fk_residual_max_mm']} mm"
        f"{'' if ok else '（殘差過大，未套用！）'}")
    log(f"[isaac_sim_server] 資產模型 flange → 指尖 {TCP_OFFSET_M:.4f} m")


def measure_fingertip():
    """手指幾何（手指自身座標）配上 physics 的連桿位姿，沿工具 z 投影取最遠點 = flange → 指尖距離。
    不用 USD 的連桿 transform，那個在模擬中不一定即時更新。"""
    teleport(TOOL_DOWN_Q, settle_steps=10)
    links = link_positions()
    flange = links[FLANGE_LINK][:3].astype(np.float64)
    tool_z = R_WORLD_FROM_BASE @ fk(TOOL_DOWN_Q)[:3, 2]
    stage = omni.usd.get_context().get_stage()
    cache = UsdGeom.BBoxCache(Usd.TimeCode.Default(), [UsdGeom.Tokens.default_, UsdGeom.Tokens.render, UsdGeom.Tokens.proxy])
    tips = []
    for name in FINGER_LINKS:
        prim = stage.GetPrimAtPath(f"{ROBOT_MOUNT_PATH}/{name}")
        if not prim or name not in links:
            continue
        rng = cache.ComputeUntransformedBound(prim).ComputeAlignedRange()
        lo, hi = np.array(rng.GetMin(), dtype=np.float64), np.array(rng.GetMax(), dtype=np.float64)
        pose = links[name].astype(np.float64)
        R_link = matrix_from_quat_wxyz([pose[6], pose[3], pose[4], pose[5]])   # physics 是 xyzw
        for corner in np.array(np.meshgrid(*zip(lo, hi))).T.reshape(-1, 3):
            tips.append(float(np.dot(R_link @ corner + pose[:3] - flange, tool_z)))
    tip = max(tips) if tips else None
    return tip if tip is not None and 0.05 < tip < 0.4 else None


def shift_gripper(delta_m):
    """整支夾爪沿工具 z（wrist_3 本地 z，朝指尖）移動 delta_m；負值 = 往法蘭縮。
    手指：prismatic 關節在 wrist_3 上的掛點；本體：wrist_3 visuals / collisions 底下的 GripperCenter。
    只移手指的話本體（原本到法蘭下 126 mm）會比指尖還低，下降時本體先頂到積木。要在模擬停止時改，
    下一次 world.reset() 生效。回傳本體移動後的下緣（法蘭下多少公尺）。"""
    stage = omni.usd.get_context().get_stage()
    for name in GRIPPER_JOINT_NAMES:
        attr = stage.GetPrimAtPath(f"{ROBOT_MOUNT_PATH}/joints/{name}").GetAttribute("physics:localPos0")
        pos = attr.Get()
        attr.Set(Gf.Vec3f(float(pos[0]), float(pos[1]), float(pos[2]) + float(delta_m)))
    link = stage.GetPrimAtPath(f"{ROBOT_MOUNT_PATH}/{FLANGE_LINK}")
    body_bottom = None
    for sub in ("visuals", "collisions"):
        holder = stage.GetPrimAtPath(f"{ROBOT_MOUNT_PATH}/{FLANGE_LINK}/{sub}")
        if not holder:
            continue
        if holder.IsInstanceable():          # instance proxy 不能改，先取消 instance
            holder.SetInstanceable(False)
        body = stage.GetPrimAtPath(f"{holder.GetPath()}/GripperCenter")
        if not body:
            log(f"[isaac_sim_server] WARN: 找不到 {holder.GetPath()}/GripperCenter，夾爪本體沒有移動")
            continue
        # (0, 0, delta) 是 wrist_3 座標，換到 GripperCenter 的父座標（visuals / collisions）再當成最外層平移
        v = UsdGeom.Xformable(holder).GetLocalTransformation().GetInverse().TransformDir(Gf.Vec3d(0.0, 0.0, float(delta_m)))
        xf = UsdGeom.Xformable(body)
        ops = list(xf.GetOrderedXformOps())
        shift = xf.AddTranslateOp(UsdGeom.XformOp.PrecisionDouble, "gripperShift")
        shift.Set(v)
        xf.SetXformOpOrder([shift] + ops)
        if sub == "collisions":
            cache = UsdGeom.BBoxCache(Usd.TimeCode.Default(), [UsdGeom.Tokens.default_, UsdGeom.Tokens.render,
                                                              UsdGeom.Tokens.proxy, UsdGeom.Tokens.guide])
            body_bottom = float(cache.ComputeRelativeBound(body, link).ComputeAlignedRange().GetMax()[2])
    return body_bottom


def finger_box_specs(thickness_m, width_m):
    """量資產手指（Mesh 頂點，工具朝下時 physics 的連桿位姿），算出換成實測直方塊的位置（手指自身座標）：
    內側面 = 資產指尖（最低 FINGERTIP_WINDOW_M）最靠內處，下緣 = 資產指尖，上緣 = 資產手指頂端，
    垂直閉合方向置中，厚度往外長。張開寬度與 flange → 指尖距離因此跟資產相同。模擬執行中才量得到。"""
    teleport(TOOL_DOWN_Q, settle_steps=10)
    links = link_positions()
    stage = omni.usd.get_context().get_stage()
    down = R_WORLD_FROM_BASE @ fk(TOOL_DOWN_Q)[:3, 2]
    local, world_pts, rot = {}, {}, {}
    for name in FINGER_LINKS:
        root = stage.GetPrimAtPath(f"{ROBOT_MOUNT_PATH}/{name}")
        root_inv = UsdGeom.Xformable(root).ComputeLocalToWorldTransform(Usd.TimeCode.Default()).GetInverse()
        pts = []
        for prim in Usd.PrimRange(root, Usd.TraverseInstanceProxies()):   # 手指 Mesh 在 instance 底下
            raw = UsdGeom.Mesh(prim).GetPointsAttr().Get() if prim.IsA(UsdGeom.Mesh) else None
            if raw is not None and len(raw):
                rel = np.array(UsdGeom.Xformable(prim).ComputeLocalToWorldTransform(Usd.TimeCode.Default()) * root_inv)
                pts.append((np.c_[np.asarray(raw, dtype=np.float64), np.ones(len(raw))] @ rel)[:, :3])
        pose = links[name].astype(np.float64)
        rot[name] = matrix_from_quat_wxyz([pose[6], pose[3], pose[4], pose[5]])   # physics 是 xyzw
        local[name] = np.vstack(pts)
        world_pts[name] = local[name] @ rot[name].T + pose[:3]
    specs = {}
    for name, other in zip(FINGER_LINKS, reversed(FINGER_LINKS)):
        inward = world_pts[other].mean(axis=0) - world_pts[name].mean(axis=0)
        inward -= down * (inward @ down)
        u = rot[name].T @ (inward / np.linalg.norm(inward))   # 往另一根手指（閉合方向）
        d = rot[name].T @ down                                 # 往指尖
        p = np.cross(u, d)                                     # 手指寬度方向
        su, sd, sp = local[name] @ u, local[name] @ d, local[name] @ p
        tip = sd > sd.max() - FINGERTIP_WINDOW_M
        across = (sp[tip].min() + sp[tip].max()) / 2
        specs[name] = {
            "center": u * (su[tip].max() - thickness_m / 2) + d * (sd.max() + sd.min()) / 2 + p * across,
            "axes": np.column_stack([u, d, p]),
            "size": (thickness_m, float(sd.max() - sd.min()), width_m),
        }
    return specs


def replace_fingers(specs):
    """停用資產手指的 visuals / collisions，在手指 link 底下放 finger_box_specs 算好的直方塊（Cube，看得到也有碰撞）。
    跟資產手指一樣不綁物理材質；質量由 PhysX 依碰撞體積重算。要在模擬停止時改，下一次 world.reset() 生效。"""
    stage = omni.usd.get_context().get_stage()
    for name, spec in specs.items():
        root = f"{ROBOT_MOUNT_PATH}/{name}"
        for sub in ("visuals", "collisions"):
            holder = stage.GetPrimAtPath(f"{root}/{sub}")
            if holder:
                holder.SetActive(False)
        cube = UsdGeom.Cube.Define(stage, f"{root}/{FINGER_BOX_NAME}")
        cube.CreateSizeAttr(1.0)
        cube.CreateDisplayColorAttr([Gf.Vec3f(0.25, 0.25, 0.27)])
        xf = UsdGeom.Xformable(cube.GetPrim())
        xf.AddTranslateOp().Set(Gf.Vec3d(*[float(v) for v in spec["center"]]))
        q = quat_wxyz_from_matrix(spec["axes"])
        xf.AddOrientOp().Set(Gf.Quatf(float(q[0]), float(q[1]), float(q[2]), float(q[3])))
        xf.AddScaleOp().Set(Gf.Vec3f(*[float(v) for v in spec["size"]]))
        UsdPhysics.CollisionAPI.Apply(cube.GetPrim())


# ============================================================
# 場景 / 機器人建置
# ============================================================

def add_lights(stage):
    dome = UsdLux.DomeLight.Define(stage, "/World/Lights/dome")
    dome.CreateIntensityAttr(500.0)
    sun = UsdLux.DistantLight.Define(stage, "/World/Lights/sun")
    sun.CreateIntensityAttr(1000.0)
    sun.CreateAngleAttr(1.0)
    UsdGeom.Xformable(sun.GetPrim()).AddRotateXYZOp().Set((-35.0, 20.0, 0.0))


def disable_robot_gravity(stage):
    """真實 UR 控制器會補償重力；模擬手臂不受重力，關節才不會下垂造成幾 mm 誤差。"""
    for prim in Usd.PrimRange(stage.GetPrimAtPath(ROBOT_MOUNT_PATH)):
        if prim.HasAPI(UsdPhysics.RigidBodyAPI):
            PhysxSchema.PhysxRigidBodyAPI.Apply(prim).CreateDisableGravityAttr(True)


def build_world():
    global world, robot, camera, arm_indices, gripper_indices, block_material, TCP_OFFSET_M
    world = World(stage_units_in_meters=1.0)
    stage = omni.usd.get_context().get_stage()
    add_lights(stage)

    add_reference_to_stage(usd_path=args.robot_usd, prim_path=ROBOT_MOUNT_PATH)
    disable_robot_gravity(stage)
    robot = Articulation(prim_paths_expr=ROBOT_ARTICULATION_PATH, name="ur3e")
    world.scene.add(robot)

    camera = Camera(prim_path=CAMERA_PATH, frequency=20, resolution=(1280, 720))
    world.reset()
    camera.initialize()

    dof_names = list(robot.dof_names)
    log(f"[isaac_sim_server] robot dof_names = {dof_names}")
    arm_indices = [dof_names.index(n) for n in ARM_JOINT_NAMES]
    gripper_indices = [dof_names.index(n) for n in GRIPPER_JOINT_NAMES]

    calibrate_robot_frame()
    finger_boxes = None
    if args.finger_thickness_m > 0 and args.finger_width_m > 0:
        finger_boxes = finger_box_specs(args.finger_thickness_m, args.finger_width_m)

    # 桌面頂面 = QR 平面（UR 基座座標 z = --qr1 的 Z），校正後才知道在 Isaac 世界的高度；地板在桌面下方接住掉落的積木
    table_z = float(qr_to_world([0.0, 0.0, 0.0])[2])
    block_material = PhysicsMaterial(prim_path=BLOCK_MATERIAL_PATH,
                                     static_friction=0.9, dynamic_friction=0.8, restitution=0.0)
    world.scene.add(GroundPlane(prim_path="/World/floor", name="floor", z_position=table_z - FLOOR_BELOW_TABLE_M,
                                size=6.0, color=np.array([0.35, 0.35, 0.37]), physics_material=block_material))
    world.stop()
    asset_tip = TCP_OFFSET_M
    body_bottom = None
    if args.fingertip_m and abs(args.fingertip_m - asset_tip) > 0.001:
        body_bottom = shift_gripper(args.fingertip_m - asset_tip)
    if finger_boxes:
        replace_fingers(finger_boxes)
    build_workspace()
    world.reset()
    apply_gripper_gains()
    if finger_boxes:
        length = finger_boxes[FINGER_LINKS[0]]["size"][1]
        calibration["finger_box_m"] = {"thickness": args.finger_thickness_m, "width": args.finger_width_m,
                                       "length": round(length, 4)}
        log(f"[isaac_sim_server] 手指換成實測直方塊：單指厚 {args.finger_thickness_m * 1000:.1f} mm、"
            f"寬 {args.finger_width_m * 1000:.1f} mm、長 {length * 1000:.1f} mm；張開時內側間距 "
            f"{args.gripper_open_m * 1000:.1f} mm、外側 {(args.gripper_open_m + 2 * args.finger_thickness_m) * 1000:.1f} mm")
    if args.fingertip_m and abs(args.fingertip_m - asset_tip) > 0.001:
        TCP_OFFSET_M = measure_fingertip() or TCP_OFFSET_M
        calibration["flange_to_fingertip_m"] = round(TCP_OFFSET_M, 4)
        calibration["gripper_body_bottom_m"] = round(body_bottom, 4) if body_bottom is not None else None
        log(f"[isaac_sim_server] 夾爪依實測 --fingertip_m {args.fingertip_m:.3f} 整支往法蘭移 "
            f"{(asset_tip - args.fingertip_m) * 1000:.0f} mm：flange → 指尖 {TCP_OFFSET_M:.4f} m，"
            f"本體下緣 {body_bottom if body_bottom is not None else float('nan'):.4f} m")
    teleport(UR_HOME)
    try:
        build_self_collision_model()
    except Exception as ex:
        self_collision["error"] = f"{type(ex).__name__}: {ex}"
        log(f"[isaac_sim_server] WARN: 手臂自撞檢查建不起來，3D 驗證會判不通過：{self_collision['error']}")
    apply_default_camera()


# ============================================================
# 桌面與 QR1-4 範圍
# ============================================================

def add_box(path, center_qr, size_xyz, yaw_qr, color, collision=False):
    """在 QR 座標系放一個長方體（純 USD，沒有 Isaac 物件包裝，重建桌面時直接刪 prim）。"""
    stage = omni.usd.get_context().get_stage()
    cube = UsdGeom.Cube.Define(stage, path)
    cube.CreateSizeAttr(1.0)
    cube.CreateDisplayColorAttr([Gf.Vec3f(*[float(c) for c in color])])
    xf = UsdGeom.Xformable(cube.GetPrim())
    xf.AddTranslateOp().Set(Gf.Vec3d(*[float(v) for v in qr_to_world(center_qr)]))
    q = quat_wxyz_from_matrix(R_WORLD_FROM_BASE @ rot_z(yaw_qr))
    xf.AddOrientOp().Set(Gf.Quatf(float(q[0]), float(q[1]), float(q[2]), float(q[3])))
    xf.AddScaleOp().Set(Gf.Vec3f(*[float(v) for v in size_xyz]))
    if collision:
        UsdPhysics.CollisionAPI.Apply(cube.GetPrim())
        UsdShade.MaterialBindingAPI.Apply(cube.GetPrim()).Bind(
            UsdShade.Material(stage.GetPrimAtPath(BLOCK_MATERIAL_PATH)), UsdShade.Tokens.weakerThanDescendants, "physics")


def build_workspace():
    """桌面（比 QR1-4 標記外緣再大 --table_margin）、四個 QR 標記、QR1→QR2→QR4→QR3 框線。
    改動碰撞體要在模擬停止時做，呼叫端負責 world.stop() / world.reset()。"""
    global workspace_signature
    stage = omni.usd.get_context().get_stage()
    if stage.GetPrimAtPath(WORKSPACE_PATH):
        stage.RemovePrim(WORKSPACE_PATH)
    UsdGeom.Xform.Define(stage, WORKSPACE_PATH)

    points = np.array(list(qr_markers.values()), dtype=np.float64)
    paper = qr_size_m + 0.03                    # 標記外圍的白紙
    lo = points.min(axis=0) - paper / 2 - args.table_margin
    hi = points.max(axis=0) + paper / 2 + args.table_margin
    base_xy = base_to_qr(np.zeros(3))[:2]       # 手臂底座也要在桌上
    lo, hi = np.minimum(lo, base_xy - 0.10), np.maximum(hi, base_xy + 0.10)
    center = (lo + hi) / 2
    add_box(f"{WORKSPACE_PATH}/table", [center[0], center[1], -TABLE_THICKNESS_M / 2],
            [hi[0] - lo[0], hi[1] - lo[1], TABLE_THICKNESS_M], 0.0, (0.88, 0.88, 0.86), collision=True)

    # 只有外觀、沒有碰撞：紙面 1 mm、黑色標記 1.4 mm、框線 1.8 mm 高，避免跟桌面閃爍重疊
    for qr_id, (x, y) in qr_markers.items():
        add_box(f"{WORKSPACE_PATH}/{qr_id}_paper", [x, y, 0.0005], [paper, paper, 0.001], 0.0, (0.97, 0.97, 0.97))
        add_box(f"{WORKSPACE_PATH}/{qr_id}", [x, y, 0.0012], [qr_size_m, qr_size_m, 0.0004], 0.0, (0.03, 0.03, 0.03))
    corners = [qr_markers.get(k) for k in ("QR1", "QR2", "QR4", "QR3")]
    if all(c is not None for c in corners):
        for i in range(4):
            a, b = np.array(corners[i]), np.array(corners[(i + 1) % 4])
            mid, d = (a + b) / 2, b - a
            add_box(f"{WORKSPACE_PATH}/edge_{i}", [mid[0], mid[1], 0.0016], [float(np.linalg.norm(d)), 0.004, 0.0004],
                    float(np.arctan2(d[1], d[0])), (0.10, 0.75, 1.0))
    workspace_signature = workspace_key()
    log(f"[isaac_sim_server] 桌面 {hi[0] - lo[0]:.3f} × {hi[1] - lo[1]:.3f} m，QR 標記 "
        + "、".join(f"{k}({v[0]:.3f},{v[1]:.3f})" for k, v in qr_markers.items()))


def workspace_key():
    return tuple(sorted((k, round(v[0], 3), round(v[1], 3)) for k, v in qr_markers.items())) + (round(qr_size_m, 3),)


def update_qr_markers(cam):
    """perception /camera 帶的 QR 位置蓋掉舊值（這一幀被遮住的 QR 沿用上次的）；回傳是否需要重建桌面。"""
    global qr_size_m
    if not cam:
        return False
    for qr_id, xy in (cam.get("qr_markers") or {}).items():
        if qr_id in ("QR1", "QR2", "QR3", "QR4") and xy is not None and len(xy) == 2:
            qr_markers[qr_id] = [float(xy[0]), float(xy[1])]
    if cam.get("qr_size_m"):
        qr_size_m = float(cam["qr_size_m"])
    return workspace_key() != workspace_signature


# ============================================================
# 相機
# ============================================================

def look_at_rotation_ros(eye, target):
    """ROS/OpenCV 相機慣例（x 右、y 下、z 朝前）的旋轉矩陣，欄 = 相機軸在世界的方向。"""
    z = target - eye
    z = z / np.linalg.norm(z)
    x = np.cross(z, np.array([0.0, 0.0, 1.0]))
    x = x / np.linalg.norm(x)
    y = np.cross(z, x)
    return np.column_stack([x, y, z])


def set_camera_pose_world(position, rotation_ros):
    global camera_pose_world
    camera_pose_world = (np.asarray(position, dtype=np.float64), np.asarray(rotation_ros, dtype=np.float64))
    camera.set_world_pose(position=camera_pose_world[0],
                          orientation=quat_wxyz_from_matrix(camera_pose_world[1]), camera_axes="ros")


def set_view_camera():
    """GUI 模式下讓主視窗直接從同步相機看出去，視窗畫面就是投影後的場景。"""
    if not args.gui:
        return
    try:
        from omni.kit.viewport.utility import get_active_viewport
        get_active_viewport().camera_path = CAMERA_PATH
    except Exception as ex:
        log(f"[isaac_sim_server] WARN: 無法切換視窗相機：{ex}")


def apply_default_camera():
    """還沒收到真實相機位姿前，先從 QR1-QR2 那側斜上方看工作區（跟實際 RealSense 大致同側）。"""
    global camera_state
    eye = qr_to_world([0.40, -0.30, 0.55])
    target = qr_to_world([0.40, 0.20, 0.0])
    set_camera_pose_world(eye, look_at_rotation_ros(eye, target))
    camera_state = {"source": "default", "position_qr": [0.40, -0.30, 0.55]}
    set_view_camera()


def apply_camera(cam):
    """cam = perception /camera：intrinsics（fx/fy/ppx/ppy/width/height）+ pose_in_qr（position/rotation）。"""
    global camera_state
    intr = cam["intrinsics"]
    pose = cam["pose_in_qr"]
    width, height = int(intr["width"]), int(intr["height"])
    fx, fy = float(intr["fx"]), float(intr["fy"])
    ppx, ppy = float(intr["ppx"]), float(intr["ppy"])

    if tuple(camera.get_resolution()) != (width, height):
        camera.set_resolution((width, height))
    # 針孔模型：fx = width · focal / horizontal_aperture（正方像素，垂直 aperture 自動跟著）
    camera.set_focal_length(fx * camera.get_horizontal_aperture() / width)
    camera.set_clipping_range(0.01, 10.0)
    # 主點不在影像中心時用 USD aperture offset 平移成像窗（直接用 USD 原始單位，比例與單位無關）：
    # cx = w/2 − h_offset·w/h_aperture；USD y 朝上、影像 y 朝下，所以 cy = h/2 + v_offset·h/v_aperture
    ha = camera.prim.GetAttribute("horizontalAperture").Get()
    va = camera.prim.GetAttribute("verticalAperture").Get()
    camera.prim.GetAttribute("horizontalApertureOffset").Set((width / 2 - ppx) * ha / width)
    camera.prim.GetAttribute("verticalApertureOffset").Set((ppy - height / 2) * va / height)

    rotation_qr = np.asarray(pose["rotation"], dtype=np.float64)   # 欄 = 相機軸在 QR 座標系的方向
    position_qr = np.asarray(pose["position"], dtype=np.float64)
    # QR 軸跟 UR 基座軸平行（跟 Unity 的換算假設相同），所以只需再乘基座 → 世界的旋轉
    set_camera_pose_world(qr_to_world(position_qr), R_WORLD_FROM_BASE @ rotation_qr)
    camera_state = {
        "source": "perception",
        "resolution": [width, height],
        "fx": fx, "fy": fy, "ppx": ppx, "ppy": ppy,
        "principal_point_offset_px": [round(ppx - width / 2, 2), round(ppy - height / 2, 2)],
        "position_qr": position_qr.round(4).tolist(),
    }
    set_view_camera()


def render_frame(min_renders=2, max_renders=10):
    """回傳模擬相機 BGR 影像。world.render() 的 annotator 資料會落後好幾幀（實測拿到上一次請求的畫面），
    改用 replicator 同步 render（delta_time=0 不推進物理）；改過解析度後前幾次可能還是舊尺寸。
    GUI 模式下主視窗綁著這台相機，在視窗裡轉視角會連相機一起移動；出圖前一律放回設定的位姿，
    LLM 看到的畫面才不會因為有人操作視窗而改變。"""
    if camera_pose_world is not None:
        camera.set_world_pose(position=camera_pose_world[0],
                              orientation=quat_wxyz_from_matrix(camera_pose_world[1]), camera_axes="ros")
    want = tuple(camera.get_resolution())
    for i in range(max_renders):
        rep.orchestrator.step(delta_time=0.0, pause_timeline=False, wait_for_render=True)
        if i + 1 < min_renders:
            continue
        rgba = camera.get_rgba()
        if rgba is not None and rgba.size > 0 and rgba.ndim == 3 and (rgba.shape[1], rgba.shape[0]) == want:
            return cv2.cvtColor(np.ascontiguousarray(rgba[:, :, :3]), cv2.COLOR_RGB2BGR)
    return None


# ============================================================
# 積木
# ============================================================

def is_simulated_block(obj):
    """只有 HSV 偵測的積木（yellow_cube、black_domino …）才建成剛體；
    cup 等 YOLO 物件尺寸未知，只原樣回報、不做物理。"""
    return block_layers.is_block(obj)


def block_dims(obj):
    s = CUBE_SIZE_M
    if obj.get("shape") == "domino":
        if obj.get("orientation") == "vertical":
            return np.array([s, 2 * s, s])
        return np.array([2 * s, s, s])
    return np.array([s, s, s])


def block_color(name):
    return COLORS.get((name or "").split("_")[0], DEFAULT_COLOR)


def do_reset(scene, cam=None, joints=None):
    global blocks
    warnings = []
    # 模擬進行中刪除 / 新增剛體會讓 physics tensor view 失效（第二次 reset 會當掉），先停下來再改場景
    world.stop()
    if update_qr_markers(cam):
        build_workspace()
    for b in blocks:
        if b["cuboid"] is not None:
            try:
                world.scene.remove_object(b["cuboid"].name)
            except Exception:
                pass
    blocks = []
    # 層高跟 csharp_server LayeredHeights（3D 夾取深度的依據）同一套規則，模擬積木才會在手指預期的高度
    tops = block_layers.scene_tops(scene, args.layer_snap_offset)
    for i, obj in enumerate(scene):
        entry = {"index": i, "source": dict(obj), "cuboid": None, "dims": None, "top": None, "z_offset": 0.0}
        if is_simulated_block(obj):
            dims = block_dims(obj)
            top = tops[i]
            perceived = float(obj.get("z", 0.0))
            snapped = block_layers.snap_top(perceived, args.layer_snap_offset)
            if top > snapped + 1e-9:
                warnings.append(f"{obj.get('name')}#{i} 跟其他積木佔地重疊，依感知高度排在第 {int(round(top / CUBE_SIZE_M))} 層"
                                f"（上下順序是依 z 猜的，黑色積木的 z 不可靠，順序可能相反）")
            elif snapped > CUBE_SIZE_M and abs(perceived + args.layer_snap_offset - snapped) > LAYER_EDGE_WARN_M:
                warnings.append(f"{obj.get('name')}#{i} 頂面 z={perceived:.4f} m 接近兩層的分界，對齊到第 "
                                f"{int(round(snapped / CUBE_SIZE_M))} 層（{snapped:.3f} m），層數可能判錯")
            center_qr = np.array([obj["x"], obj["y"], top - CUBE_SIZE_M / 2])
            yaw_qr = np.radians(args.skew_sign * float(obj.get("skew_deg") or 0.0))
            orientation = quat_wxyz_from_matrix(R_WORLD_FROM_BASE @ rot_z(yaw_qr))
            name = f"block_{i}"
            entry["cuboid"] = world.scene.add(DynamicCuboid(
                prim_path=f"/World/blocks/{name}",
                name=name,
                position=qr_to_world(center_qr),
                orientation=orientation,
                size=1.0,
                scale=dims,
                color=block_color(obj.get("name")),
                mass=BLOCK_MASS_KG,
                physics_material=block_material,
            ))
            entry["dims"] = dims
            entry["top"] = top
            entry["z_offset"] = perceived - top   # 回報時加回，對外維持 perception 慣例
        blocks.append(entry)
    def footprint(entry):
        half_x, half_y = block_layers.half_extents(entry["source"].get("shape"), entry["source"].get("orientation"))
        return {"x": float(entry["source"]["x"]), "y": float(entry["source"]["y"]), "half_x": half_x, "half_y": half_y}

    for b in blocks:
        if b["cuboid"] is None or b["top"] < 1.5 * CUBE_SIZE_M:
            continue
        fb = footprint(b)
        below = [o for o in blocks if o is not b and o["cuboid"] is not None and o["top"] < b["top"] - CUBE_SIZE_M / 2
                 and block_layers.overlaps(footprint(o), fb["x"], fb["y"], fb["half_x"], fb["half_y"])]
        if not below:
            warnings.append(f"{b['source'].get('name')}#{b['index']} 在第 2 層以上，但下方沒有偵測到積木"
                            f"（可能被遮住），模擬中會往下掉")
    world.reset()
    apply_gripper_gains()

    joints_source = "home"
    arm_q = UR_HOME
    ursim_q, ursim_closed = follower.latest() if follower else (None, False)
    if joints is not None:
        arm_q, joints_source = np.asarray(joints, dtype=np.float64), "request"
    elif ursim_q is not None:
        arm_q, joints_source = ursim_q, "ursim"
    elif follower:
        warnings.append(f"讀不到 URSim（{follower.status()['error']}），手臂先停在 Home")
    teleport(arm_q, GRIPPER_CLOSE_POS if (joints_source == "ursim" and ursim_closed) else GRIPPER_OPEN_POS)
    for _ in range(30):   # 讓積木穩定落定
        world.step(render=False)

    if cam:
        try:
            apply_camera(cam)
        except Exception as ex:
            warnings.append(f"套用相機失敗，沿用上次相機：{ex}")
    for w in warnings:
        log(f"[isaac_sim_server] WARN: {w}")
    return {
        "ok": True,
        "count": len(blocks),
        "simulated": sum(1 for b in blocks if b["cuboid"] is not None),
        "robot_joints_source": joints_source,
        "robot_joints": current_arm_q().round(4).tolist(),
        "camera": camera_state.get("source"),
        "qr_markers": qr_markers,
        "warnings": warnings,
    }


def block_pose_qr(entry):
    """(中心 QR 座標, 真實頂面高度, 在 QR 座標系的旋轉矩陣)"""
    pos, quat = entry["cuboid"].get_world_pose()
    R_qr = R_WORLD_FROM_BASE.T @ matrix_from_quat_wxyz(np.asarray(quat, dtype=np.float64))
    center_qr = world_to_qr(np.asarray(pos, dtype=np.float64))
    top_z = center_qr[2] + float(np.sum(np.abs(R_qr[2, :]) * entry["dims"] / 2))
    return center_qr, top_z, R_qr


def block_state(entry):
    """模擬中積木的 QR 座標、頂面高度（perception 慣例：模擬頂面加回投影時這塊的量測偏差）、方向。"""
    center_qr, top_z, R_qr = block_pose_qr(entry)
    dims = entry["dims"]
    source = entry["source"]
    orientation = None
    long_axis = R_qr[:, int(np.argmax(dims))]
    if source.get("shape") == "domino":
        orientation = "horizontal" if abs(long_axis[0]) >= abs(long_axis[1]) else "vertical"
    # 相對最接近的 0°/90° 的偏角，換回 perception 的影像座標正負號
    yaw_deg = float(np.degrees(np.arctan2(long_axis[1], long_axis[0])))
    skew_image = args.skew_sign * ((yaw_deg + 45.0) % 90.0 - 45.0)
    return {
        "name": source.get("name"),
        "x": float(center_qr[0]), "y": float(center_qr[1]), "z": float(top_z + entry["z_offset"]),
        "shape": source.get("shape"),
        "orientation": orientation,
        "skew_deg": round(skew_image, 2) if abs(skew_image) >= 6.0 else 0.0,
        # 離「平放」多少度：任何一個面朝上都算平放（cube 側躺還是平的；domino 立起來由高度檢查抓）
        "tilt_deg": round(float(np.degrees(np.arccos(np.clip(np.max(np.abs(R_qr[2, :])), 0.0, 1.0)))), 1),
    }


def scene_objects():
    objects = []
    for b in blocks:
        if b["cuboid"] is not None:
            objects.append(block_state(b))
        else:
            src = b["source"]
            objects.append({k: src.get(k) for k in ("name", "x", "y", "z", "shape", "orientation", "skew_deg")})
    return objects


# ============================================================
# 驗證（手臂跟隨 URSim 執行 Unity 的關節軌跡，積木用物理模擬）
# ============================================================

def snapshot_blocks():
    """{index: (中心 QR xy, 真實頂面高度)}"""
    snap = {}
    for b in blocks:
        if b["cuboid"] is not None:
            center, top, _ = block_pose_qr(b)
            snap[b["index"]] = (center[:2].copy(), top)
    return snap


def do_verify_begin(scene, cam, steps, use_current_world=False, expected_cells=None, cell_x=None, cell_y=None):
    if follower is None:
        raise RuntimeError("isaac_sim_server 沒有用 --ursim_ip 啟動，無法跟隨 URSim 驗證")
    if follower.latest()[0] is None:
        raise RuntimeError(f"讀不到 URSim 關節角（{follower.status()['error']}）")
    if use_current_world:
        # 純模擬：這裡就是世界，從目前的物理狀態開始（重新投影會把歪掉的積木擺正，等於改了世界）。
        # steps 的 source_index 對應 /perception/scene 的順序，也就是 blocks 的順序。
        reply = {"ok": True, "count": len(blocks), "simulated": sum(1 for b in blocks if b["cuboid"] is not None),
                 "use_current_world": True, "warnings": []}
    else:
        reply = do_reset(scene, cam)
    verify_state.clear()
    verify_state.update({
        "active": True,
        "steps": steps,
        "initial": snapshot_blocks(),
        "min_tip_m": np.inf,        # URSim 到 Ready 之後
        "min_tip_all_m": np.inf,    # 整段（沒偵測到 Ready 時的備援）
        "ready_reached": False,
        "self_hit": None,           # 手臂自撞（URSim 到 Ready 之後第一次）
        "self_hit_all": None,       # 整段第一次（沒偵測到 Ready 時的備援）
        "max_top": {},
        "follow_steps": 0,
        "stale_steps": 0,
        "started": time.time(),
        # 畫面重疊率用：server 依計畫算的預期格（每一格每一層一個，含壓在底下沒被搬的支撐）與格距；沒給就不算
        "expected_cells": list(expected_cells or []),
        "cell_x": float(cell_x) if cell_x else CUBE_SIZE_M,
        "cell_y": float(cell_y) if cell_y else CUBE_SIZE_M,
    })
    log(f"[isaac_sim_server] 驗證開始：{len(steps)} 步，{reply['simulated']} 塊積木，手臂跟隨 URSim")
    return reply


def convex_hull_2d(points):
    """平面點的凸包（單調鏈，逆時針）。"""
    pts = sorted({(round(float(x), 9), round(float(y), 9)) for x, y in points})
    if len(pts) <= 2:
        return np.array(pts)

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for pt in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], pt) <= 0:
            lower.pop()
        lower.append(pt)
    for pt in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], pt) <= 0:
            upper.pop()
        upper.append(pt)
    return np.array(lower[:-1] + upper[:-1])


def inside_convex(hull, px, py):
    """像素中心（px、py 陣列）是否落在逆時針凸包內（含邊界）。"""
    inside = np.ones(px.shape, dtype=bool)
    for i in range(len(hull)):
        ax, ay = hull[i]
        bx, by = hull[(i + 1) % len(hull)]
        inside &= (bx - ax) * (py - ay) - (by - ay) * (px - ax) >= -1e-12
    return inside


def figure_overlap(expected, cell_x, cell_y):
    """3D 的畫面重疊率，算法同 main Unity 的 FigureImageOverlap：範圍是預期佔地的外框四邊各加半格，1 mm 一像素，
    每一點的預期層數（cube 2.5×2.5 cm，domino 5×2.5 cm 照方向）跟模擬結束時實際疊到的層數比，重疊率 = Σmin ÷ Σmax
    （全部只有一層時就是像素的交集 ÷ 聯集，疊到別塊上也會扣分）。實際佔地用每塊積木目前的位姿（含偏轉、傾斜）投影到桌面，
    層數 = 真實頂面 ÷ 2.5 cm 四捨五入。
    跟 Unity 不同的一點：Isaac 有物理，整個圖形常一致偏幾 mm（2026-10-08 實測五塊都 Y −2.0 mm），所以先把實際的圖形
    在 ±OVERLAP_ALIGN_M 內整體平移、找重疊率最高的位置再算；個別積木放歪、少層、多放照樣扣分，位置偏差另由位置檢查把關。
    回傳 (重疊率, 預期層數圖, 對齊後的實際層數圖, 平移 (dx_m, dy_m), 不對齊時的重疊率)。"""
    half = CUBE_SIZE_M / 2
    pad = int(round(OVERLAP_ALIGN_M / OVERLAP_PIXEL_M))

    def half_xy(e):
        if e.get("shape") != "domino":
            return half, half
        return (half, 2 * half) if e.get("orientation") == "vertical" else (2 * half, half)

    def layer_of(top):
        return max(1, int(round(top / CUBE_SIZE_M)))

    x0 = min(float(e["x"]) - half_xy(e)[0] for e in expected) - cell_x / 2
    x1 = max(float(e["x"]) + half_xy(e)[0] for e in expected) + cell_x / 2
    y0 = min(float(e["y"]) - half_xy(e)[1] for e in expected) - cell_y / 2
    y1 = max(float(e["y"]) + half_xy(e)[1] for e in expected) + cell_y / 2
    w = int(np.ceil((x1 - x0) / OVERLAP_PIXEL_M))
    h = int(np.ceil((y1 - y0) / OVERLAP_PIXEL_M))
    # 實際的層數圖四邊多畫 pad 像素，平移時才有資料。圖的方向同 bitmap：上 = +Y、右 = +X
    gx0, gy1 = x0 - pad * OVERLAP_PIXEL_M, y1 + pad * OVERLAP_PIXEL_M
    px, py = np.meshgrid(gx0 + (np.arange(w + 2 * pad) + 0.5) * OVERLAP_PIXEL_M,
                         gy1 - (np.arange(h + 2 * pad) + 0.5) * OVERLAP_PIXEL_M)
    want_full = np.zeros(px.shape, dtype=np.int32)
    for e in expected:
        hx, hy = half_xy(e)
        mask = (np.abs(px - float(e["x"])) <= hx) & (np.abs(py - float(e["y"])) <= hy)
        want_full[mask] = np.maximum(want_full[mask], layer_of(float(e["z"])))
    want = want_full[pad:pad + h, pad:pad + w]
    got_full = np.zeros(px.shape, dtype=np.int32)
    corners = np.array([[sx, sy, sz] for sx in (-0.5, 0.5) for sy in (-0.5, 0.5) for sz in (-0.5, 0.5)])
    for b in blocks:
        if b["cuboid"] is None:
            continue
        center, top, R_qr = block_pose_qr(b)
        pts = center + (corners * b["dims"]) @ R_qr.T
        if (pts[:, 0].max() < gx0 or pts[:, 0].min() > x1 + pad * OVERLAP_PIXEL_M or
                pts[:, 1].max() < y0 - pad * OVERLAP_PIXEL_M or pts[:, 1].min() > gy1):
            continue
        hull = convex_hull_2d(pts[:, :2])
        if len(hull) < 3:
            continue
        mask = inside_convex(hull, px, py)
        got_full[mask] = np.maximum(got_full[mask], layer_of(top))

    def ratio_at(sx, sy):
        # 實際圖形往 +X 偏 sx 像素、往 -Y 偏 sy 像素時，取對應的視窗跟預期比
        got = got_full[pad + sy:pad + sy + h, pad + sx:pad + sx + w]
        total = int(np.maximum(want, got).sum())
        return (float(np.minimum(want, got).sum()) / total if total else 0.0), got

    unaligned, _ = ratio_at(0, 0)
    best = (unaligned, 0, 0)
    for sy in range(-pad, pad + 1):
        for sx in range(-pad, pad + 1):
            r, _ = ratio_at(sx, sy)
            # 同分時取平移最小的
            if r > best[0] + 1e-12 or (abs(r - best[0]) <= 1e-12 and sx * sx + sy * sy < best[1] ** 2 + best[2] ** 2):
                best = (r, sx, sy)
    ratio, sx, sy = best
    _, got = ratio_at(sx, sy)
    return ratio, want, got, (sx * OVERLAP_PIXEL_M, -sy * OVERLAP_PIXEL_M), unaligned


def overlap_image(want, got, scale=2, gap=8):
    """比對圖（PNG），三格同 main 的 sim_check.png：模擬結果 / 預期（灰階越深層數越多）/ 疊合
    （綠 = 吻合、紅 = 該有沒有、藍 = 多出來、橘 = 有但層數不對）。"""
    h, w = want.shape
    shade = [(255, 255, 255), (215, 215, 215), (175, 175, 175), (140, 140, 140), (110, 110, 110)]

    def layers(img):
        out = np.zeros((h, w, 3), dtype=np.uint8)
        for k, colour in enumerate(shade):
            out[np.minimum(img, len(shade) - 1) == k] = colour
        return out

    diff = np.full((h, w, 3), 255, dtype=np.uint8)
    diff[(want > 0) & (got == want)] = (80, 170, 60)       # BGR：綠
    diff[(want > 0) & (got == 0)] = (60, 60, 220)          # 紅
    diff[(want == 0) & (got > 0)] = (220, 120, 40)         # 藍
    diff[(want > 0) & (got > 0) & (got != want)] = (40, 150, 240)   # 橘
    spacer = np.full((h, gap, 3), 255, dtype=np.uint8)
    panel = np.hstack([layers(got), spacer, layers(want), spacer, diff])
    panel = cv2.resize(panel, (panel.shape[1] * scale, panel.shape[0] * scale), interpolation=cv2.INTER_NEAREST)
    ok, png = cv2.imencode(".png", panel)
    return png.tobytes() if ok else None


def released_at_end(actions):
    """這一步結束時是否已放開（最後一次 release 在最後一次 grasp 之後）。"""
    names = [a.get("function") for a in actions or []]
    last_grasp = max((i for i, n in enumerate(names) if n == "grasp"), default=-1)
    last_release = max((i for i, n in enumerate(names) if n == "release"), default=-1)
    return last_release > last_grasp


def do_verify_end():
    if not verify_state.get("active"):
        raise RuntimeError("沒有進行中的驗證（先呼叫 /verify/begin）")
    # 1) 等積木靜止：線速度都 < 5 mm/s，最多 VERIFY_SETTLE_MAX_S 秒
    settle_start = time.time()
    while time.time() - settle_start < VERIFY_SETTLE_MAX_S:
        sim_seconds(0.25)
        speeds = [float(np.linalg.norm(b["cuboid"].get_linear_velocity())) for b in blocks if b["cuboid"] is not None]
        if not speeds or max(speeds) < 0.005:
            break
    # 2) 靜止觀察窗：這段時間內的最大位移
    before = snapshot_blocks()
    sim_seconds(VERIFY_STABLE_WINDOW_S)
    final = snapshot_blocks()
    verify_state["active"] = False

    reasons, checks = [], []

    def check(name, ok, detail):
        checks.append({"name": name, "ok": bool(ok), "detail": detail})
        if not ok:
            reasons.append(f"{name}：{detail}")

    by_index = {b["index"]: b for b in blocks}
    initial = verify_state["initial"]
    steps = verify_state["steps"]
    ready_reached = verify_state["ready_reached"]
    min_tip = verify_state["min_tip_m"] if ready_reached else verify_state["min_tip_all_m"]
    tip_scope = ("URSim 到 Ready 之後的軌跡中" if ready_reached else
                 "整段 URSim 軌跡中（沒有偵測到 Ready 姿勢，連 URSim 原本停留的姿勢也算進去；請確認 --ready_q 跟 Unity 一致）")

    # 每個來源只看最後一次放開的位置
    last_place = {}
    moved = set()
    for k, step in enumerate(steps):
        src = int(step.get("source_index", -1))
        name = by_index[src]["source"].get("name") if src in by_index else f"#{src}"
        if src not in final:
            check(f"第 {k + 1} 步來源", False, f"{name} 不是可模擬的積木，無法驗證")
            continue
        moved.add(src)
        if released_at_end(step.get("actions")):
            last_place[src] = (k, step.get("target") or {})
        else:
            checks.append({"name": f"第 {k + 1} 步", "ok": True, "detail": f"{name} 結束時仍夾著，不檢查擺放"})

    for src in sorted(moved):
        name = by_index[src]["source"].get("name")
        lifted = verify_state["max_top"].get(src, initial[src][1]) - initial[src][1]
        check(f"{name}#{src} 有被夾起", lifted >= 0.005,
              f"最高抬起 {lifted * 1000:.0f} mm" if lifted >= 0.005 else
              f"積木沒有離開原位（最高抬起 {lifted * 1000:.0f} mm）；指尖最低只到桌面上 {min_tip * 1000:.0f} mm，"
              f"積木頂面 {initial[src][1] * 1000:.0f} mm，手指可能沒包住積木")

    for src, (k, tgt) in last_place.items():
        entry = by_index[src]
        name = entry["source"].get("name")
        center, top, _ = block_pose_qr(entry)
        height = float(entry["dims"][2])
        tilt = block_state(entry)["tilt_deg"]
        xy_err = float(np.hypot(center[0] - float(tgt.get("x", 0.0)), center[1] - float(tgt.get("y", 0.0))))
        check(f"第 {k + 1} 步 {name} 位置", xy_err <= VERIFY_XY_TOL_M,
              f"離目標 XY {xy_err * 1000:.1f} mm（允許 {VERIFY_XY_TOL_M * 1000:.0f} mm）")
        check(f"第 {k + 1} 步 {name} 傾斜", tilt <= VERIFY_TILT_TOL_DEG, f"傾斜 {tilt:.1f}°（允許 {VERIFY_TILT_TOL_DEG:.0f}°）")
        # 目標壓在另一塊積木上（依投影時的位置，或更早一步的放置目標）就是要疊放
        intended_stack = any(i != src and np.hypot(*(xy - np.array([tgt.get("x", 0.0), tgt.get("y", 0.0)]))) < VERIFY_SUPPORT_RADIUS_M
                             for i, (xy, _) in initial.items()) or any(
            kk < k and np.hypot(float(s.get("target", {}).get("x", 0.0)) - float(tgt.get("x", 0.0)),
                                float(s.get("target", {}).get("y", 0.0)) - float(tgt.get("y", 0.0))) < VERIFY_SUPPORT_RADIUS_M
            for kk, s in enumerate(steps))
        supports = [(i, t) for i, (xy, t) in final.items()
                    if i != src and np.hypot(*(xy - center[:2])) < VERIFY_SUPPORT_RADIUS_M and t < top - height / 2]
        support_top = max((t for _, t in supports), default=0.0)
        expected_top = support_top + height
        if intended_stack:
            check(f"第 {k + 1} 步 {name} 疊放", bool(supports),
                  "下方有積木支撐" if supports else "目標要疊在積木上，但放完下方沒有積木（掉到桌面或旁邊）")
        else:
            check(f"第 {k + 1} 步 {name} 放在桌面", not supports,
                  "直接放在桌面" if not supports else "目標是桌面，但放完疊到了其他積木上")
        check(f"第 {k + 1} 步 {name} 高度", abs(top - expected_top) <= VERIFY_Z_TOL_M,
              f"頂面 {top * 1000:.1f} mm，預期 {expected_top * 1000:.1f} mm（允許 ±{VERIFY_Z_TOL_M * 1000:.0f} mm）")
        target_orientation = tgt.get("orientation")
        if entry["source"].get("shape") == "domino" and target_orientation in ("horizontal", "vertical"):
            actual = block_state(entry)["orientation"]
            check(f"第 {k + 1} 步 {name} 方向", actual == target_orientation, f"目前 {actual}，目標 {target_orientation}")

    for i, (xy0, top0) in initial.items():
        if i in moved or i not in final:
            continue
        shift = float(np.hypot(*(final[i][0] - xy0)))
        name = by_index[i]["source"].get("name")
        check(f"{name}#{i} 沒被撞動", shift <= VERIFY_MOVED_TOL_M and abs(final[i][1] - top0) <= VERIFY_Z_TOL_M,
              f"位移 {shift * 1000:.1f} mm、高度變化 {(final[i][1] - top0) * 1000:.1f} mm")
    for i, (xy, top) in final.items():
        if top < -0.01:
            check(f"{by_index[i]['source'].get('name')}#{i} 還在桌上", False, "掉到桌面以下")
    drift = max((float(np.hypot(*(final[i][0] - before[i][0]))) + abs(final[i][1] - before[i][1])
                 for i in final if i in before), default=0.0)
    check("靜止後穩定", drift <= VERIFY_STABLE_TOL_M,
          f"最後 {VERIFY_STABLE_WINDOW_S:.0f} 秒內最大位移 {drift * 1000:.1f} mm（允許 {VERIFY_STABLE_TOL_M * 1000:.0f} mm）")
    if np.isfinite(min_tip):
        check("指尖沒有撞到桌面", min_tip >= -VERIFY_TIP_TABLE_TOL_M,
              f"{tip_scope}指尖最低點在桌面{'上' if min_tip >= 0 else '下'} {abs(min_tip) * 1000:.1f} mm")
    # 檢查本身建不起來時算不通過：看不到自撞就不能讓實機照這條軌跡動
    if self_collision["error"] is not None:
        check("手臂沒有自撞", False, f"自撞檢查無法執行（{self_collision['error']}）")
    else:
        self_hit = verify_state["self_hit"] if ready_reached else verify_state["self_hit_all"]
        check("手臂沒有自撞", self_hit is None,
              f"{tip_scope}手臂各段沒有互相碰到" if self_hit is None else f"{tip_scope}{self_hit}")
    total = verify_state["follow_steps"] + verify_state["stale_steps"]
    # 中斷期間 Isaac 的手臂停住、資料回來時一口氣追上，夾著的積木可能被甩掉：列出次數與最長的一次方便對照
    gaps = follower.gaps_since(verify_state["started"]) if follower is not None else []
    gap_text = f"；資料中斷 {len(gaps)} 次，最長 {max(gaps):.1f} s" if gaps else "；沒有超過 0.3 s 的中斷"
    check("URSim 資料完整", verify_state["follow_steps"] > 0 and verify_state["stale_steps"] <= 0.05 * max(total, 1),
          f"跟隨 {verify_state['follow_steps']} 步，其中 {verify_state['stale_steps']} 步讀不到 URSim{gap_text}")
    # 畫面重疊率（三個版本的 3D 驗證都看這一項，算法同 main 的 2D Unity 比對）
    overlap = None
    expected = verify_state.get("expected_cells") or []
    if expected:
        ratio, want, got, (dx, dy), unaligned = figure_overlap(expected, verify_state["cell_x"], verify_state["cell_y"])
        check("畫面重疊率", ratio > VERIFY_OVERLAP_THRESHOLD,
              f"{ratio * 100:.0f}%（要大於 {VERIFY_OVERLAP_THRESHOLD * 100:.0f}%；1 mm 一像素、依層數加權；"
              f"整個圖形對齊平移 X {dx * 1000:+.0f} mm、Y {dy * 1000:+.0f} mm，不對齊是 {unaligned * 100:.0f}%；"
              f"預期 {len(expected)} 格，最高 {int(want.max())} 層，模擬結果最高 {int(got.max())} 層）")
        png = overlap_image(want, got)
        overlap = {"ratio": round(ratio, 4), "unaligned_ratio": round(unaligned, 4), "threshold": VERIFY_OVERLAP_THRESHOLD,
                   "pixel_m": OVERLAP_PIXEL_M, "align_shift_m": [round(dx, 4), round(dy, 4)],
                   "image_png_base64": base64.b64encode(png).decode("ascii") if png else None}

    passed = not reasons
    log(f"[isaac_sim_server] 驗證結果：{'PASS' if passed else 'FAIL'}" + ("" if passed else "；" + "；".join(reasons)))
    return {
        "pass": passed,
        "reasons": reasons,
        "checks": checks,
        "objects": scene_objects(),
        "min_fingertip_height_mm": round(float(min_tip) * 1000, 1) if np.isfinite(min_tip) else None,
        "fingertip_tracked_from": "ready" if ready_reached else "verify_begin",
        "duration_s": round(time.time() - verify_state["started"], 1),
        "overlap": overlap,
    }


def overlay(real_jpeg, alpha):
    real = cv2.imdecode(np.frombuffer(real_jpeg, dtype=np.uint8), cv2.IMREAD_COLOR)
    if real is None:
        raise ValueError("無法解碼真實影像 JPEG")
    sim = render_frame()
    if sim is None:
        raise RuntimeError("模擬相機沒有畫面")
    if sim.shape[:2] != real.shape[:2]:
        sim = cv2.resize(sim, (real.shape[1], real.shape[0]))
    blended = cv2.addWeighted(real, 1.0 - alpha, sim, alpha, 0.0)
    cv2.putText(blended, f"real {1 - alpha:.1f} / isaac {alpha:.1f}", (20, 40),
                cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 0, 255), 2)
    return blended


# ============================================================
# 純模擬：這個服務就是世界（虛擬場景檔建立積木，之後只靠物理改變）
# ============================================================

def do_sim_load(scene, cam, if_idle=False):
    """用虛擬場景檔重建世界：中止還沒結束的驗證、丟掉舊快照，再照一般投影建立積木。
    if_idle（Unity 切換純模擬 / 換場景時用）：3D 驗證進行中就不重建，免得打斷任務。Unity 自己正在執行一批時
    按鈕不給切換，所以這裡只擋 Unity 看不到的情況：csharp_server 已開始驗證、批次還沒送到 Unity。"""
    global world_snapshot
    if if_idle and verify_state["active"]:
        raise RuntimeError("Isaac Sim 正在驗證 3D 批次（任務進行中），沒有重建世界")
    verify_state["active"] = False
    world_snapshot = None
    return do_reset(scene, cam)


def do_sim_snapshot():
    """記下每塊積木的世界位姿（位置 + 四元數），給驗證後還原。"""
    global world_snapshot
    world_snapshot = {}
    for b in blocks:
        if b["cuboid"] is not None:
            pos, quat = b["cuboid"].get_world_pose()
            world_snapshot[b["index"]] = (np.array(pos, dtype=np.float64), np.array(quat, dtype=np.float64))
    return {"ok": True, "blocks": len(world_snapshot)}


def do_sim_restore():
    """把積木放回快照的位姿並停住。驗證跑過的 URSim 動作在模擬世界裡真的移動了積木，
    正式執行要從驗證前的狀態開始（實機模式下驗證不會動到真實積木）。還沒結束的驗證一併中止。"""
    verify_state["active"] = False
    if world_snapshot is None:
        raise RuntimeError("沒有快照（先呼叫 /sim/snapshot）")
    restored = 0
    for b in blocks:
        pose = world_snapshot.get(b["index"]) if b["cuboid"] is not None else None
        if pose is None:
            continue
        b["cuboid"].set_world_pose(position=pose[0], orientation=pose[1])
        b["cuboid"].set_linear_velocity(np.zeros(3))
        b["cuboid"].set_angular_velocity(np.zeros(3))
        restored += 1
    for _ in range(10):
        follow_step(render=False)
    return {"ok": True, "restored": restored}


def perception_scene():
    """跟 perception_server /scene 同格式（物件 position 是 QR 座標、z 是頂面），給純模擬的 csharp_server / Unity。"""
    width, height = (int(v) for v in camera.get_resolution())
    objects = [{
        "name": o.get("name"),
        "confidence": 1.0,
        "source": "isaac_sim",
        "shape": o.get("shape") or "cube",
        "orientation": o.get("orientation"),
        "skew_deg": float(o.get("skew_deg") or 0.0),
        "position": {"x": float(o["x"]), "y": float(o["y"]), "z": float(o["z"]), "source": "isaac_sim"},
    } for o in scene_objects()]
    return {"timestamp": time.time(), "image_width": width, "image_height": height, "objects": objects}


def status():
    return {
        "following": follower.status() if follower else None,
        "verifying": bool(verify_state.get("active")),
        "blocks": sum(1 for b in blocks if b["cuboid"] is not None),
        "camera": camera_state.get("source"),
        "perception_mode": perception_mode,
        "snapshot_blocks": None if world_snapshot is None else len(world_snapshot),
    }


# ============================================================
# 主執行緒工作佇列：Isaac Sim API 只能在主執行緒呼叫；
# Flask 在背景執行緒收請求，交給主執行緒做完再回傳。沒有工作時主迴圈持續跟隨 URSim。
# ============================================================

jobs = queue.Queue()


def run_on_main(fn, *fn_args):
    future = Future()
    jobs.put((fn, fn_args, future))
    return future.result()


def encode_jpeg(bgr):
    ok, buf = cv2.imencode(".jpg", bgr)
    if not ok:
        raise RuntimeError("JPEG encode failed")
    return buf.tobytes()


app = Flask(__name__)
import logging
logging.getLogger("werkzeug").setLevel(logging.WARNING)


def json_error(ex, status_code=500):
    return jsonify({"error": str(ex)}), status_code


@app.route("/verify/begin", methods=["POST"])
def endpoint_verify_begin():
    data = request.get_json(force=True)
    try:
        return jsonify(run_on_main(do_verify_begin, data.get("scene", []), data.get("camera"), data.get("steps") or [],
                                   bool(data.get("use_current_world")), data.get("expected_cells"),
                                   data.get("cell_size_x_m"), data.get("cell_size_y_m")))
    except Exception as ex:
        return json_error(ex, 409)


@app.route("/verify/end", methods=["POST"])
def endpoint_verify_end():
    try:
        return jsonify(run_on_main(do_verify_end))
    except Exception as ex:
        return json_error(ex, 409)


@app.route("/reset", methods=["POST"])
def endpoint_reset():
    data = request.get_json(force=True)

    def reset_when_idle():
        # 驗證進行中重設場景會洗掉正在跟隨 URSim 的積木狀態
        if verify_state.get("active"):
            raise RuntimeError("3D 驗證進行中，不接受重設場景")
        return do_reset(data.get("scene", []), data.get("camera"), data.get("joints"))
    try:
        return jsonify(run_on_main(reset_when_idle))
    except Exception as ex:
        return json_error(ex, 409)


@app.route("/scene", methods=["GET"])
def endpoint_scene():
    try:
        return jsonify({"objects": run_on_main(scene_objects)})
    except Exception as ex:
        return json_error(ex)


@app.route("/frame", methods=["GET"])
def endpoint_frame():
    try:
        bgr = run_on_main(render_frame)
    except Exception as ex:
        return f"frame not available: {ex}", 503
    if bgr is None:
        return "frame not available", 503
    return Response(encode_jpeg(bgr), mimetype="image/jpeg")


@app.route("/overlay", methods=["POST"])
def endpoint_overlay():
    alpha = min(1.0, max(0.0, float(request.args.get("alpha", 0.5))))
    try:
        bgr = run_on_main(overlay, request.get_data(), alpha)
    except Exception as ex:
        return json_error(ex)
    return Response(encode_jpeg(bgr), mimetype="image/jpeg")


@app.route("/calibration", methods=["GET"])
def endpoint_calibration():
    return jsonify(calibration)


@app.route("/camera", methods=["GET"])
def endpoint_camera():
    return jsonify(camera_state)


@app.route("/status", methods=["GET"])
def endpoint_status():
    return jsonify(status())


# ---- 純模擬 ----

@app.route("/sim/load", methods=["POST"])
def endpoint_sim_load():
    data = request.get_json(force=True)
    try:
        return jsonify(run_on_main(do_sim_load, data.get("scene", []), data.get("camera"), bool(data.get("if_idle", False))))
    except Exception as ex:
        return json_error(ex, 409)


@app.route("/sim/snapshot", methods=["POST"])
def endpoint_sim_snapshot():
    try:
        return jsonify(run_on_main(do_sim_snapshot))
    except Exception as ex:
        return json_error(ex, 409)


@app.route("/sim/restore", methods=["POST"])
def endpoint_sim_restore():
    try:
        return jsonify(run_on_main(do_sim_restore))
    except Exception as ex:
        return json_error(ex, 409)


@app.route("/perception/scene", methods=["GET"])
def endpoint_perception_scene():
    try:
        return jsonify(run_on_main(perception_scene))
    except Exception as ex:
        return json_error(ex)


@app.route("/perception/scene/mode", methods=["GET", "POST"])
def endpoint_perception_mode():
    # 不經過主執行緒：Unity 每 0.3 秒輪詢一次
    global perception_mode
    if request.method == "POST":
        mode = (request.get_json(force=True, silent=True) or {}).get("mode")
        if mode in ("idle", "executing"):
            perception_mode = mode
    return jsonify({"mode": perception_mode})


@app.route("/perception/debug/frame", methods=["GET"])
def endpoint_perception_frame():
    return endpoint_frame()


@app.route("/perception/camera", methods=["GET"])
def endpoint_perception_camera():
    return jsonify(camera_state)


def main():
    global follower, follow_tick_count
    build_world()
    if args.ursim_ip:
        follower = UrsimFollower(args.ursim_ip, args.ursim_port)
        log(f"[isaac_sim_server] 手臂跟隨 URSim {args.ursim_ip}:{args.ursim_port}（唯讀）")
    else:
        log("[isaac_sim_server] WARN: 沒有 --ursim_ip，手臂停在 Home，無法做 3D 驗證")
    threading.Thread(target=lambda: app.run(host=args.host, port=args.port, threaded=True, use_reloader=False),
                     daemon=True, name="flask").start()
    log(f"[isaac_sim_server] listening on http://{args.host}:{args.port}")
    print("  POST /verify/begin  {'scene': [...], 'camera': {...}?, 'steps': [...], 'expected_cells': [...]?}")
    print("  POST /verify/end")
    print("  POST /reset         {'scene': [...], 'camera': {...}?, 'joints': [...]?}")
    print("  POST /overlay       body = 真實相機 JPEG")
    print("  GET  /scene  /frame  /calibration  /camera  /status")
    print("  純模擬：POST /sim/load /sim/snapshot /sim/restore；GET /perception/scene /perception/debug/frame")
    physics_dt = world.get_physics_dt()
    while simulation_app.is_running():
        try:
            fn, fn_args, future = jobs.get_nowait()
        except queue.Empty:
            started = time.perf_counter()
            if follower is not None:
                # 以真實時間推進物理，手臂持續跟隨 URSim；GUI 畫面每兩步畫一次（30 fps）
                follow_tick_count += 1
                follow_step(render=args.gui and follow_tick_count % 2 == 0)
                time.sleep(max(0.0, physics_dt - (time.perf_counter() - started)))
            elif args.gui:
                # 閒置時畫面限制在 30 fps；不限制的話 GUI 會把 GPU 跑滿，其他 Isaac / Unity 會搶不到
                world.step(render=True)
                time.sleep(max(0.0, 1.0 / 30.0 - (time.perf_counter() - started)))
            else:
                time.sleep(0.02)
            continue
        try:
            future.set_result(fn(*fn_args))
        except Exception as ex:
            future.set_exception(ex)
    simulation_app.close()


if __name__ == "__main__":
    main()
