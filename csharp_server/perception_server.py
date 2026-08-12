"""
perception_server.py - 即時場景感知服務

啟動時：
  1. 開啟外接 webcam 常駐串流
  2. 載入 YOLO11n (COCO 常見物件) + 自訓 pliers 模型 + Aruco 偵測器
  3. 背景執行緒每 ~200ms 拍一幀、跑三種偵測、更新記憶體中的 sceneObjects
  4. 開一個 Flask HTTP server 讓 csharp_server 或瀏覽器隨時抓當下場景

用法：
    cd csharp_server
    yolo11_env\\Scripts\\python.exe perception_server.py

Endpoint：
    GET /scene         - 當下 detection JSON（含物件、QR、時間戳）
    GET /health        - 服務健康度、FPS、上次更新時間
    GET /debug/frame   - 最新一幀原圖（JPEG，方便瀏覽器直接開起來對照）
"""

import threading
import time
from collections import Counter, deque
from pathlib import Path

import cv2
import numpy as np
import pyrealsense2 as rs
from flask import Flask, Response, jsonify, request
from ultralytics import YOLO


# ============================================================
# 設定（可依實際硬體調整）
# ============================================================

BASE_DIR = Path(__file__).resolve().parent

# --- Intel RealSense D435i ---
# USB 2.1 上限：depth 480x270 + color 640x480 @ 6fps（其他組合啟動失敗）
# 換到 USB 3 之後可以升到 1280x720
CAMERA_WIDTH = 1920              # color 解析度
CAMERA_HEIGHT = 1080
DEPTH_WIDTH = 640               # depth 解析度（會 align 到 color 座標）
DEPTH_HEIGHT = 480
CAMERA_FPS = 15
CAMERA_BRIGHTNESS_MIN = 15.0
#CAMERA_WIDTH = 640
#CAMERA_HEIGHT = 480
#DEPTH_WIDTH = 480
#DEPTH_HEIGHT = 270
#CAMERA_FPS = 6

DETECT_INTERVAL_SEC = 0.2       # 目標偵測間隔（0.2s = 5 FPS）
SERVER_HOST = "127.0.0.1"        # 只綁 loopback（同機 csharp_server 用），避免暴露到 LAN
SERVER_PORT = 5000

# --- YOLO 相關 ---
YOLO_CONF = 0.20                # YOLO 預設門檻（低，讓邊緣物件如 iPhone 過關）
PLIERS_CONF = 0.20

# COCO 白名單 + 個別 class 的最低信心門檻。
# 有些 class（例如 cup）很容易被手臂本體、圓柱形物件誤判，門檻拉高避免假陽性；
# iPhone/cell phone、pliers 常常信心較低，門檻保持低。
COCO_TARGETS = {
    "cup":        0.55,        # 手臂關節容易被誤認 → 拉高
    "bottle":     0.40,
    "cell phone": 0.20,
    "book":       0.30,
    "mouse":      0.35,
    "keyboard":   0.35,
    "laptop":     0.35,
}

# --- ArUco 相關 ---
ARUCO_DICT_TYPE = cv2.aruco.DICT_4X4_50
ARUCO_ID_TO_NAME = {1: "QR1", 2: "QR2", 3: "QR3", 4: "QR4"}
QR_MASK_DILATION_PX = 30        # QR bbox 向外擴幾個像素，避免邊緣殘影誤判成立方體（1280x720 用 30；640x480 用 15）

# --- HSV 立方體遮罩參數 ---
YELLOW_HSV_LOW = np.array([10, 100, 100])
YELLOW_HSV_HIGH = np.array([38, 255, 255])
BLACK_HSV_LOW = np.array([0, 0, 0])
# Black cardboard is not pixel-black under the current RealSense exposure;
# highlights on its faces often exceed V=60. Keep hue unrestricted and allow
# moderate saturation/brightness, while contour area/shape and workspace ROI
# continue filtering table holes, QR markers, and large shadows.
BLACK_HSV_HIGH = np.array([180, 120, 110])
# HSV 形狀偵測參數
# 目前支援兩種積木：
#   cube   = 2.5 × 2.5 × 2.5 cm（正方形俯視）
#   domino = 5 × 2.5 × 2.5 cm（長方形俯視，1:2 比例）
# 用 minAreaRect 的長短邊比 + 面積雙門檻分類
CUBE_MIN_AREA_PX = 600          # 2.5 cm cube 在 1280x720 下約 40x40 px ≈ 1600（640x480 是 150）
CUBE_MAX_AREA_PX = 6000
CUBE_RATIO_MAX = 1.35           # 長短邊比 <= 這個 → 視為 cube

DOMINO_MIN_AREA_PX = 1400       # domino 面積約 cube 的 2 倍
DOMINO_MAX_AREA_PX = 12000
DOMINO_RATIO_MIN = 1.65  # 長短邊比在這區間 → 視為 domino
DOMINO_RATIO_MAX = 2.4
# Perspective can shorten a real 2:1 domino to roughly 1.45 in the image.
# Area, solidity, and watershed checks still apply after this threshold.
DOMINO_RATIO_MIN = 1.45
DOMINO_ORIENTATION_TOL_DEG = 22.5   # 長軸偏離 X/Y 軸多少度內視為對齊；超過則當「斜擺」拒絕

SOLIDITY_MIN = 0.80             # 小物件輪廓比較毛，稍微放寬

# --- 多幀穩定濾波（減少 orientation / 面積門檻邊緣物件的閃爍）---
STABILITY_WINDOW = 5            # 檢查過去這麼多幀
STABILITY_MIN_HITS = 1          # 至少在 N 幀中出現這麼多次才回報
STABILITY_MATCH_PX = 25         # 中心點差 < 這個像素就當作「同一顆物件」
_recent_frames = deque(maxlen=STABILITY_WINDOW)

# --- 跨源去重 ---
IOU_DEDUPE_THRESHOLD = 0.5

# --- 工作區 ROI：只保留在 QR1-4 圍成的多邊形內的偵測 ---
# 若 QR 少於 3 個就不做 ROI 過濾（避免整組被誤刪）
ROI_MIN_QRS = 3
# 把工作區多邊形往外擴（正值）或往內縮（負值）幾個像素
ROI_MARGIN_PX = 10

CUBE_SKEW_TOL_DEG = 6.0

# --- Touching-cube watershed split ---
# Only contours that already look like a domino are considered. The split is
# accepted only when it yields exactly two similarly sized, near-square parts.
WATERSHED_PEAK_RATIO = 0.52
WATERSHED_CUBE_RATIO_MAX = 1.50
WATERSHED_AREA_BALANCE_MIN = 0.65
WATERSHED_MIN_PART_AREA_FACTOR = 0.45

# --- Part B: QR + solvePnP 3D 座標對應 ---
QR_SIZE_M = 0.073                    # ArUco 實際邊長，公尺
OBJECT_HEIGHT_OFFSET_M = 0.025       # 2.5 cm 立方體頂面；只在深度讀失敗時當 fallback

# RealSense 提供真實的相機內參（fx, fy, cx, cy），這裡的值只是預設，
# 啟動時會被真實 intrinsics 覆蓋（見 setup_realsense）
CAMERA_INTRINSICS = {
    "fx": 600.0, "fy": 600.0,
    "ppx": CAMERA_WIDTH / 2.0, "ppy": CAMERA_HEIGHT / 2.0,
}
RS_INTRINSICS = None                 # pyrealsense2 intrinsics 物件（給 deproject 用）

# 工作平面快取：QR 貼紙+相機都固定的話，workspace_frame 永遠一樣。
# 一旦算出來就存下來，之後 QR 被手臂遮住還是能繼續算物件位置。
_cached_workspace_frame = None
_cached_workspace_info = None


# ============================================================
# 共享狀態（perception thread 寫、Flask thread 讀，靠 lock 保護）
# ============================================================

state_lock = threading.Lock()
latest_state = {
    "timestamp": None,
    "image_width": CAMERA_WIDTH,
    "image_height": CAMERA_HEIGHT,
    "objects": [],
    "qrcodes": [],
    "workspace_polygon": None,
    "workspace_info": None,
    "frames_processed": 0,
    "detect_ms": 0,
}
latest_frame = None
stop_event = threading.Event()

# 執行狀態：idle = 感知快照可供 Unity 顯示；executing = 手臂執行中，Unity 端凍結場景
# 由 Unity JsonExecutor 在動作前後 POST 切換
_execution_mode = "idle"
_execution_mode_lock = threading.Lock()


# ============================================================
# 模型與偵測器（啟動時載入一次）
# ============================================================

print("[perception] Loading YOLO11n (COCO baseline)...")
YOLO11N = YOLO(str(BASE_DIR / "yolo11n.pt"))

# pliers 偵測暫時抽掉：Roboflow 那個 dataset 的 domain gap 太大，
# 在我們俯視工作台場景 conf < 0.06。之後拍實照 fine-tune 再加回來。
PLIERS_MODEL = None

print("[perception] Initializing ArUco detector...")
aruco_dict = cv2.aruco.getPredefinedDictionary(ARUCO_DICT_TYPE)
aruco_params = cv2.aruco.DetectorParameters()
aruco_detector = cv2.aruco.ArucoDetector(aruco_dict, aruco_params)


# ============================================================
# 偵測函式
# ============================================================

def yolo_detect_np(model, image_bgr, allowed_classes, source_tag, conf):
    """
    對記憶體中的 numpy image 跑 YOLO；不走檔案路徑，速度較快。
    allowed_classes 可以是 None（不過濾）、set（僅過濾類別）、或 dict（類別 → 個別最低 conf）
    """
    if model is None:
        return []
    results = model(image_bgr, conf=conf, verbose=False)
    result = results[0]

    detections = []
    for box in result.boxes:
        cls_id = int(box.cls[0])
        name = result.names[cls_id]
        confidence = float(box.conf[0])

        # 白名單過濾 + per-class 信心門檻
        if allowed_classes is not None:
            if isinstance(allowed_classes, dict):
                if name not in allowed_classes:
                    continue
                if confidence < allowed_classes[name]:
                    continue
            else:
                if name not in allowed_classes:
                    continue

        x1, y1, x2, y2 = [float(v) for v in box.xyxy[0]]
        detections.append({
            "name": name,
            "confidence": round(confidence, 3),
            "bbox": [round(x1, 2), round(y1, 2), round(x2, 2), round(y2, 2)],
            "center_pixel": [round((x1 + x2) / 2, 2), round((y1 + y2) / 2, 2)],
            "source": source_tag,
        })
    return detections


def detect_qrcodes(image_bgr):
    """偵測 QR1-4；同時回傳 QR 區域的二值 mask，供 cube 偵測排除。"""
    gray = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2GRAY)
    corners, ids, _ = aruco_detector.detectMarkers(gray)

    qrcodes = []
    qr_mask = np.zeros(image_bgr.shape[:2], dtype=np.uint8)

    if ids is None:
        return qrcodes, qr_mask

    for i, aruco_id in enumerate(ids.flatten()):
        name = ARUCO_ID_TO_NAME.get(int(aruco_id))
        if name is None:
            continue
        pts = corners[i][0]  # (4, 2)
        cx = float(pts[:, 0].mean())
        cy = float(pts[:, 1].mean())
        qrcodes.append({
            "id": name,
            "center_pixel": [round(cx, 2), round(cy, 2)],
            "corners": [[round(float(p[0]), 2), round(float(p[1]), 2)] for p in pts],
        })
        # 加入 mask（凸多邊形填實）
        cv2.fillPoly(qr_mask, [pts.astype(np.int32)], 255)

    # 稍微膨脹，蓋掉 QR 邊框附近的雜訊
    if qr_mask.any():
        kernel = np.ones((QR_MASK_DILATION_PX, QR_MASK_DILATION_PX), np.uint8)
        qr_mask = cv2.dilate(qr_mask, kernel)

    return qrcodes, qr_mask


def split_touching_cubes(image_bgr, binary_mask, contour):
    """Try to split one domino-like contour into two touching cube contours."""
    x, y, w, h = cv2.boundingRect(contour)
    if w < 3 or h < 3:
        return []

    roi_image = image_bgr[y:y + h, x:x + w].copy()
    roi_mask = binary_mask[y:y + h, x:x + w].copy()

    contour_mask = np.zeros((h, w), dtype=np.uint8)
    shifted = contour.copy()
    shifted[:, 0, 0] -= x
    shifted[:, 0, 1] -= y
    cv2.drawContours(contour_mask, [shifted], -1, 255, -1)
    roi_mask = cv2.bitwise_and(roi_mask, contour_mask)

    distance = cv2.distanceTransform(roi_mask, cv2.DIST_L2, 5)
    max_distance = float(distance.max())
    if max_distance <= 0:
        return []

    sure_foreground = np.uint8(
        distance >= WATERSHED_PEAK_RATIO * max_distance
    ) * 255
    component_count, markers = cv2.connectedComponents(sure_foreground)
    # One background plus exactly two object seeds.
    if component_count != 3:
        return []

    sure_background = cv2.dilate(
        roi_mask, np.ones((3, 3), np.uint8), iterations=1
    )
    unknown = cv2.subtract(sure_background, sure_foreground)
    markers = markers + 1
    markers[unknown == 255] = 0
    markers = cv2.watershed(roi_image, markers)

    parts = []
    part_areas = []
    for marker_id in np.unique(markers):
        if marker_id <= 1:
            continue
        part_mask = np.uint8(markers == marker_id) * 255
        contours, _ = cv2.findContours(
            part_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE
        )
        if not contours:
            continue
        part = max(contours, key=cv2.contourArea)
        part_area = float(cv2.contourArea(part))
        if part_area < CUBE_MIN_AREA_PX * WATERSHED_MIN_PART_AREA_FACTOR:
            continue

        (_, _), (part_w, part_h), _ = cv2.minAreaRect(part)
        if part_w < 1 or part_h < 1:
            continue
        part_ratio = max(part_w, part_h) / min(part_w, part_h)
        if part_ratio > WATERSHED_CUBE_RATIO_MAX:
            continue

        part[:, 0, 0] += x
        part[:, 0, 1] += y
        parts.append(part)
        part_areas.append(part_area)

    if len(parts) != 2:
        return []
    area_balance = min(part_areas) / max(part_areas)
    if area_balance < WATERSHED_AREA_BALANCE_MIN:
        return []
    return parts


def append_cube_detection(detections, contour, color_name, confidence, source):
    """Append one cube detection generated from an HSV or watershed contour."""
    (cx, cy), (_, _), angle = cv2.minAreaRect(contour)
    raw_skew = angle % 90
    if raw_skew > 45:
        raw_skew -= 90
    skew_deg = 0.0 if abs(raw_skew) < CUBE_SKEW_TOL_DEG else round(raw_skew, 2)
    x, y, w, h = cv2.boundingRect(contour)
    detections.append({
        "name": f"{color_name}_cube",
        "confidence": round(confidence, 3),
        "bbox": [round(float(x), 2), round(float(y), 2),
                 round(float(x + w), 2), round(float(y + h), 2)],
        "center_pixel": [round(float(cx), 2), round(float(cy), 2)],
        "source": source,
        "shape": "cube",
        "orientation": None,
        "skew_deg": skew_deg,
    })


def hsv_shape_detect(image_bgr, hsv_low, hsv_high, color_name, exclude_mask=None):
    """
    對單一顏色 HSV 範圍找 cube（2.5×2.5）或 domino（5×2.5）兩種形狀。
    用旋轉最小外接矩形（minAreaRect）取長短邊，比軸對齊 boundingRect 準——
    domino 若斜放，軸對齊 bbox 會接近正方形被誤判成 cube。

    每個偵測物件多兩個欄位：
      shape       = "cube" 或 "domino"
      orientation = None（cube）/ "horizontal"（長軸沿 QR X）/ "vertical"（長軸沿 QR Y）
    name 也對應改成 {color}_cube 或 {color}_domino。
    """
    hsv = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2HSV)
    mask = cv2.inRange(hsv, hsv_low, hsv_high)

    if exclude_mask is not None:
        # 把 QR 範圍從偵測 mask 中扣掉
        mask = cv2.bitwise_and(mask, cv2.bitwise_not(exclude_mask))

    # 3x3 kernel：2.5 cm cube 在畫面上只有 ~20x20 px，5x5 open 會侵蝕太多
    kernel = np.ones((3, 3), np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)
    # Do not apply MORPH_CLOSE here. Closing bridges narrow background gaps and
    # makes two adjacent cubes look like one 2:1 domino contour. Keeping the gap
    # intact lets findContours return two independent cube detections.

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    detections = []
    for cnt in contours:
        area = float(cv2.contourArea(cnt))
        # 大範圍先過濾：從 cube 最小到 domino 最大都可能
        if area < CUBE_MIN_AREA_PX or area > DOMINO_MAX_AREA_PX:
            continue

        hull_area = float(cv2.contourArea(cv2.convexHull(cnt)))
        solidity = area / hull_area if hull_area > 0 else 0.0
        if solidity < SOLIDITY_MIN:
            continue

        # 旋轉最小外接矩形
        (rcx, rcy), (rw, rh), angle = cv2.minAreaRect(cnt)
        if rw < 1 or rh < 1:
            continue
        long_side = max(rw, rh)
        short_side = min(rw, rh)
        ratio = long_side / short_side

        skew_deg = 0.0

        if ratio <= CUBE_RATIO_MAX and CUBE_MIN_AREA_PX <= area <= CUBE_MAX_AREA_PX:
            shape = "cube"
            raw_skew = angle % 90
            if raw_skew > 45:
                raw_skew -= 90
            skew_deg = 0.0 if abs(raw_skew) < CUBE_SKEW_TOL_DEG else round(raw_skew, 2)
            orientation = None
        elif (DOMINO_RATIO_MIN <= ratio <= DOMINO_RATIO_MAX
              and DOMINO_MIN_AREA_PX <= area <= DOMINO_MAX_AREA_PX):
            split_parts = split_touching_cubes(image_bgr, mask, cnt)
            if len(split_parts) == 2:
                for part in split_parts:
                    append_cube_detection(
                        detections, part, color_name, solidity,
                        "cube_hsv_watershed"
                    )
                continue

            shape = "domino"
            # 長軸角度 normalize 到 [0, 180)：0=沿 X（水平）、90=沿 Y（垂直）
            long_angle = angle if rw >= rh else angle + 90
            long_angle = long_angle % 180

            # 不再拒絕斜擺 domino——像 cube 一樣算出跟最近象限的偏差角，
            # orientation 表達「哪個象限最近」，skew_deg 補償實際傾斜。
            #   long_angle in [0, 45) 或 [135, 180) → 較接近水平（0°）
            #   long_angle in [45, 135)              → 較接近垂直（90°）
            if long_angle < 45 or long_angle >= 135:
                orientation = "horizontal"
                raw_skew = long_angle if long_angle < 45 else long_angle - 180
            else:
                orientation = "vertical"
                raw_skew = long_angle - 90

            skew_deg = 0.0 if abs(raw_skew) < CUBE_SKEW_TOL_DEG else round(raw_skew, 2)
        else:
            continue

        # 軸對齊 bbox 保留給 depth 讀取跟 workspace polygon 判定用，跟原本一致
        x, y, w, h = cv2.boundingRect(cnt)

        detections.append({
            "name": f"{color_name}_{shape}",
            "confidence": round(solidity, 3),
            "bbox": [round(float(x), 2), round(float(y), 2),
                     round(float(x + w), 2), round(float(y + h), 2)],
            "center_pixel": [round(float(rcx), 2), round(float(rcy), 2)],   # 旋轉 bbox 中心較準
            "source": "cube_hsv",
            "shape": shape,
            "orientation": orientation,
            "skew_deg": skew_deg,
        })
    return detections


def bbox_iou(b1, b2):
    ax1, ay1, ax2, ay2 = b1
    bx1, by1, bx2, by2 = b2
    xx1 = max(ax1, bx1)
    yy1 = max(ay1, by1)
    xx2 = min(ax2, bx2)
    yy2 = min(ay2, by2)
    iw = max(0.0, xx2 - xx1)
    ih = max(0.0, yy2 - yy1)
    inter = iw * ih
    a1 = (ax2 - ax1) * (ay2 - ay1)
    a2 = (bx2 - bx1) * (by2 - by1)
    union = a1 + a2 - inter
    return inter / union if union > 0 else 0.0


# ============================================================
# Part B: solvePnP 建工作平面 + 物件世界座標
# ============================================================

def build_camera_matrix(image_width, image_height):
    """用 RealSense 提供的真實 intrinsics（若還沒收到就用估計值）。"""
    fx = CAMERA_INTRINSICS["fx"]
    fy = CAMERA_INTRINSICS["fy"]
    cx = CAMERA_INTRINSICS["ppx"]
    cy = CAMERA_INTRINSICS["ppy"]
    return np.array([
        [fx, 0.0, cx],
        [0.0, fy, cy],
        [0.0, 0.0, 1.0],
    ], dtype=np.float64)


def deproject_pixel_to_camera(pixel, depth_meters):
    """
    像素 + 深度 → 相機座標系的 3D 點（公尺）。
    用 RealSense SDK 內建的 deproject（含畸變修正），比手算 pinhole 準。
    """
    if RS_INTRINSICS is None or depth_meters is None or depth_meters <= 0:
        return None
    return np.array(
        rs.rs2_deproject_pixel_to_point(RS_INTRINSICS, [float(pixel[0]), float(pixel[1])], float(depth_meters)),
        dtype=np.float64,
    )


def _qr_object_points(qr_size_m):
    s = qr_size_m / 2.0
    return np.array([
        [-s,  s, 0.0],   # top-left
        [ s,  s, 0.0],   # top-right
        [ s, -s, 0.0],   # bottom-right
        [-s, -s, 0.0],   # bottom-left
    ], dtype=np.float64)


def solve_qr_center(qr, camera_matrix, dist_coeffs):
    """對單一 QR 跑 solvePnP，回傳它在相機座標系的 3D 中心。失敗回 None。"""
    image_points = np.array(qr["corners"], dtype=np.float64)
    if image_points.shape != (4, 2):
        return None
    ok, rvec, tvec = cv2.solvePnP(
        _qr_object_points(QR_SIZE_M),
        image_points,
        camera_matrix,
        dist_coeffs,
        flags=cv2.SOLVEPNP_IPPE_SQUARE,
    )
    if not ok:
        return None
    return tvec.reshape(3)


def _normalize(v):
    n = np.linalg.norm(v)
    if n < 1e-9:
        return None
    return v / n


def build_workspace_frame(p1, p2, p3):
    """
    p1 = QR1（原點）、p2 = QR2（+X 方向）、p3 = QR3（+Y 方向）
    回傳 dict：origin, x_axis, z_axis, normal, width_m, depth_m
    """
    origin = p1
    x_vec = p2 - p1
    z_raw = p3 - p1

    width_m = float(np.linalg.norm(x_vec))
    x_axis = _normalize(x_vec)
    if x_axis is None:
        return None

    normal = _normalize(np.cross(x_axis, z_raw))
    if normal is None:
        return None

    z_axis = _normalize(np.cross(normal, x_axis))
    if z_axis is None:
        return None

    depth_m = float(np.dot(z_raw, z_axis))
    if depth_m < 0:
        z_axis = -z_axis
        normal = -normal
        depth_m = -depth_m

    return {
        "origin": origin,
        "x_axis": x_axis,
        "z_axis": z_axis,
        "normal": normal,
        "width_m": width_m,
        "depth_m": depth_m,
    }


def project_pixel_to_workspace(pixel, camera_matrix, frame, obj_top_z_m=OBJECT_HEIGHT_OFFSET_M):
    """
    像素 → 相機射線 → 「工作平面 + obj_top_z_m 高度」的平面交點 → 局部 (x, y) 座標（公尺）。
    obj_top_z_m 預設 = cube / domino 頂面高度（2.5 cm）；若給 0 就是 QR 平面。

    這個方法**跟相機角度無關**，適合已知高度的物件（cube / domino）。
    傾斜相機下比 depth 讀值準：depth 讀值容易讀到頂面前緣或側邊，導致 Z 誤差。

    輸出 x = QR1→QR2、y = QR1→QR3、z = obj_top_z_m。
    """
    px, py = pixel
    pixel_h = np.array([px, py, 1.0], dtype=np.float64)
    ray = np.linalg.inv(camera_matrix) @ pixel_h
    ray = _normalize(ray)
    if ray is None:
        return None

    denom = float(np.dot(ray, frame["normal"]))
    if abs(denom) < 1e-9:
        return None

    # 平面方程：P · normal = (origin + obj_top_z_m * normal) · normal
    #                     = origin · normal + obj_top_z_m（normal 是單位向量）
    # 射線 P(t) = t · ray；求 t
    t = (float(np.dot(frame["origin"], frame["normal"])) + obj_top_z_m) / denom
    if t < 0:
        return None

    point_camera = t * ray
    rel = point_camera - frame["origin"]
    local_x = float(np.dot(rel, frame["x_axis"]))
    local_y = float(np.dot(rel, frame["z_axis"]))

    return {
        "x": round(local_x, 6),                        # QR1→QR2 方向
        "y": round(local_y, 6),                        # QR1→QR3 方向
        "z": round(obj_top_z_m, 6),                    # 已知高度
    }


def read_depth_meters(depth_image, pixel, patch_radius=3, percentile=None):
    """
    對深度圖 (H, W) uint16 (單位 mm) 在指定像素周圍取樣。
    - percentile=None → 用中位數（QR 這種平面用這個）
    - percentile=數字 → 用該分位數（例如 20 就是最靠近相機的 20% 那一群，適合物件頂面）
    回傳公尺；讀不到就回 None。
    """
    if depth_image is None:
        return None
    h, w = depth_image.shape[:2]
    cx = int(round(pixel[0]))
    cy = int(round(pixel[1]))
    if cx < 0 or cy < 0 or cx >= w or cy >= h:
        return None
    x1 = max(0, cx - patch_radius)
    y1 = max(0, cy - patch_radius)
    x2 = min(w, cx + patch_radius + 1)
    y2 = min(h, cy + patch_radius + 1)
    patch = depth_image[y1:y2, x1:x2]
    valid = patch[patch > 0]
    if valid.size == 0:
        return None
    if percentile is None:
        return float(np.median(valid)) / 1000.0
    return float(np.percentile(valid, percentile)) / 1000.0


def read_depth_meters_for_object(depth_image, bbox):
    """
    對物件 bbox 內的深度圖找「最靠近相機的一群」— 就是物件頂面。
    俯視相機下，最小深度 = 離相機最近 = 物件頂面。
    用 20% 分位數而不是最小值，避免單點噪點。
    """
    if depth_image is None:
        return None
    h, w = depth_image.shape[:2]
    x1, y1, x2, y2 = [int(round(v)) for v in bbox]
    # 稍微內縮避免抓到邊緣往下傾斜的深度
    inset = max(1, (x2 - x1) // 8)
    x1i = max(0, x1 + inset)
    x2i = min(w, x2 - inset)
    y1i = max(0, y1 + inset)
    y2i = min(h, y2 - inset)
    if x2i <= x1i or y2i <= y1i:
        return None
    patch = depth_image[y1i:y2i, x1i:x2i]
    valid = patch[patch > 0]
    if valid.size < 5:
        return None
    return float(np.percentile(valid, 10)) / 1000.0


def _try_build_workspace_frame(qrcodes, depth_image, image_width, image_height):
    """試著從當下這幀的 QR 建工作平面。QR 沒偵測全或 solvePnP 失敗就回 None。"""
    qr_by_id = {qr["id"]: qr for qr in qrcodes}
    if not all(k in qr_by_id for k in ("QR1", "QR2", "QR3")):
        return None

    camera_matrix = build_camera_matrix(image_width, image_height)
    dist_coeffs = np.zeros((5, 1), dtype=np.float64)

    p1 = p2 = p3 = None
    if depth_image is not None and RS_INTRINSICS is not None:
        def qr_center_from_depth(qr):
            d = read_depth_meters(depth_image, qr["center_pixel"], patch_radius=4)
            if d is None:
                return None
            return deproject_pixel_to_camera(qr["center_pixel"], d)
        p1 = qr_center_from_depth(qr_by_id["QR1"])
        p2 = qr_center_from_depth(qr_by_id["QR2"])
        p3 = qr_center_from_depth(qr_by_id["QR3"])

    if p1 is None or p2 is None or p3 is None:
        p1 = solve_qr_center(qr_by_id["QR1"], camera_matrix, dist_coeffs)
        p2 = solve_qr_center(qr_by_id["QR2"], camera_matrix, dist_coeffs)
        p3 = solve_qr_center(qr_by_id["QR3"], camera_matrix, dist_coeffs)
    if p1 is None or p2 is None or p3 is None:
        return None

    return build_workspace_frame(p1, p2, p3)


def compute_world_positions(objects, qrcodes, image_width, image_height, depth_image=None):
    """
    對每個物件計算 QR 工作平面局部座標（公尺），寫進 obj["position"]。
    - 有 depth 且該像素有值：z 是真實高度
    - 否則 fallback 到 ray-plane 交點，z 用 OBJECT_HEIGHT_OFFSET_M
    - QR 被手臂遮住這一幀 → 用上一次成功的 workspace_frame 快取
      （前提是相機和 QR 之後都固定不動）
    """
    global _cached_workspace_frame, _cached_workspace_info

    frame = _try_build_workspace_frame(qrcodes, depth_image, image_width, image_height)
    if frame is not None:
        # 這幀 QR 有偵測到 → 更新快取
        _cached_workspace_frame = frame
        _cached_workspace_info = {
            "width_m": round(frame["width_m"], 6),
            "depth_m": round(frame["depth_m"], 6),
            "source": "current",
        }
    else:
        # QR 被遮住或還沒偵測到 → 拿快取
        frame = _cached_workspace_frame
        if frame is None:
            return None                                # 從來沒建成功過就沒辦法了
        _cached_workspace_info = {**_cached_workspace_info, "source": "cached"}

    camera_matrix = build_camera_matrix(image_width, image_height)

    for obj in objects:
        # 讀深度：中心像素周圍 3px patch、25 分位數（偏向頂面）
        depth_m = (
            read_depth_meters(depth_image, obj["center_pixel"], patch_radius=3, percentile=25)
            if depth_image is not None else None
        )

        shape = obj.get("shape")
        if shape in ("cube", "domino"):
            # 已知形狀：X/Y 用射線 + 標稱 2.5 cm 平面（避免 depth 噪點傳染到 XY 定位）；
            # Z 仍從 depth 讀（動態高度，維持「不寫死高度」的設計）
            xy_pos = project_pixel_to_workspace(
                obj["center_pixel"], camera_matrix, frame,
                obj_top_z_m=OBJECT_HEIGHT_OFFSET_M,
            )
            if xy_pos is None:
                continue

            local_z = OBJECT_HEIGHT_OFFSET_M
            if depth_m is not None:
                obj_camera = deproject_pixel_to_camera(obj["center_pixel"], depth_m)
                if obj_camera is not None:
                    rel = obj_camera - frame["origin"]
                    local_z = float(np.dot(rel, frame["normal"]))

            obj["position"] = {
                "x": xy_pos["x"],                    # 射線幾何（穩）
                "y": xy_pos["y"],
                "z": round(local_z, 6),              # depth 實測（動態）
                "source": "ray_xy+depth_z",
            }
            continue

        # 未知形狀（cup、bottle 等，沒有固定高度）→ 全部從 depth 算
        obj_camera = deproject_pixel_to_camera(obj["center_pixel"], depth_m) if depth_m else None
        if obj_camera is not None:
            rel = obj_camera - frame["origin"]
            local_x = float(np.dot(rel, frame["x_axis"]))
            local_y = float(np.dot(rel, frame["z_axis"]))
            local_z = float(np.dot(rel, frame["normal"]))
            obj["position"] = {
                "x": round(local_x, 6),
                "y": round(local_y, 6),
                "z": round(local_z, 6),
                "source": "depth",
            }
        else:
            pos = project_pixel_to_workspace(
                obj["center_pixel"], camera_matrix, frame,
                obj_top_z_m=OBJECT_HEIGHT_OFFSET_M,
            )
            if pos is not None:
                pos["source"] = "ray_plane_fallback"
                obj["position"] = pos

    return _cached_workspace_info


def build_workspace_polygon(qrcodes):
    """
    從 QR1-4 的中心點建工作區凸多邊形。
    QR 數量 < ROI_MIN_QRS 時回傳 None（跳過 ROI 過濾）。
    """
    if len(qrcodes) < ROI_MIN_QRS:
        return None

    centers = np.array(
        [[qr["center_pixel"][0], qr["center_pixel"][1]] for qr in qrcodes],
        dtype=np.float32,
    )
    hull = cv2.convexHull(centers)                    # (N, 1, 2)
    hull_pts = hull.reshape(-1, 2)                    # (N, 2)

    if ROI_MARGIN_PX != 0:
        cx = float(hull_pts[:, 0].mean())
        cy = float(hull_pts[:, 1].mean())
        expanded = []
        for x, y in hull_pts:
            dx, dy = x - cx, y - cy
            norm = (dx * dx + dy * dy) ** 0.5
            if norm > 1e-6:
                x += dx / norm * ROI_MARGIN_PX
                y += dy / norm * ROI_MARGIN_PX
            expanded.append([x, y])
        hull_pts = np.array(expanded, dtype=np.float32)

    return hull_pts


def filter_by_workspace(detections, polygon):
    """把 bbox 中心點不在 polygon 內的偵測濾掉。polygon=None 時不過濾。"""
    if polygon is None:
        return detections

    poly = polygon.astype(np.float32)
    kept = []
    for d in detections:
        cx, cy = d["center_pixel"]
        inside = cv2.pointPolygonTest(poly, (float(cx), float(cy)), False)
        if inside >= 0:                               # 0 = 在邊上，>0 = 在內部
            kept.append(d)
    return kept


def cross_source_dedupe(detections, iou_threshold=IOU_DEDUPE_THRESHOLD):
    kept = []
    for d in sorted(detections, key=lambda x: -x["confidence"]):
        duplicated = False
        for k in kept:
            if bbox_iou(d["bbox"], k["bbox"]) > iou_threshold:
                duplicated = True
                break
        if not duplicated:
            kept.append(d)
    return kept


def stabilize_detections(current):
    """
    多幀穩定濾波：只回報過去 STABILITY_WINDOW 幀內出現 ≥ STABILITY_MIN_HITS 次的物件。
    對每個穩定物件，位置 / skew 用歷史平均、orientation 用歷史多數，減少邊緣像素造成的閃爍。

    副作用：新放上桌的物件會慢 (WINDOW - MIN_HITS + 1) 幀才被回報；
    拿走的物件會多顯示 (WINDOW - MIN_HITS + 1) 幀後才消失。
    """
    _recent_frames.append(current)

    # buffer 還沒累積夠 → 直接回原始 detections（不然一啟動什麼都看不到）
    if len(_recent_frames) < STABILITY_MIN_HITS:
        return current

    stable = []
    for det in current:
        # 找過去所有幀中「同 name + 中心點接近」的匹配
        matches = []
        cx, cy = det["center_pixel"]
        for frame_dets in _recent_frames:
            for other in frame_dets:
                if other["name"] != det["name"]:
                    continue
                ocx, ocy = other["center_pixel"]
                if abs(ocx - cx) < STABILITY_MATCH_PX and abs(ocy - cy) < STABILITY_MATCH_PX:
                    matches.append(other)
                    break                    # 每幀最多算 1 次

        if len(matches) < STABILITY_MIN_HITS:
            continue

        # 平均位置 / skew、多數 orientation
        avg_cx = sum(m["center_pixel"][0] for m in matches) / len(matches)
        avg_cy = sum(m["center_pixel"][1] for m in matches) / len(matches)
        avg_skew = sum(m.get("skew_deg", 0.0) for m in matches) / len(matches)
        orientations = [m.get("orientation") for m in matches]
        mode_orient = Counter(orientations).most_common(1)[0][0]

        smoothed = dict(det)
        smoothed["center_pixel"] = [round(avg_cx, 2), round(avg_cy, 2)]
        smoothed["skew_deg"] = round(avg_skew, 2)
        smoothed["orientation"] = mode_orient
        stable.append(smoothed)

    return stable


def detect_all(image_bgr, depth_image=None):
    """對單一幀跑完整偵測管線；QR 先偵測、遮蓋後再跑 HSV 立方體。
    depth_image (uint16 mm, 已 align 到 color 座標) 可選；有就給物件真實高度。"""
    h, w = image_bgr.shape[:2]

    qrcodes, qr_mask = detect_qrcodes(image_bgr)
    workspace_polygon = build_workspace_polygon(qrcodes)

    yolo_dets = yolo_detect_np(YOLO11N, image_bgr, COCO_TARGETS, "yolo_coco", YOLO_CONF)
    # 一次找 cube + domino（每個顏色都會回傳兩種形狀混合的清單）
    cube_dets = (
        hsv_shape_detect(image_bgr, YELLOW_HSV_LOW, YELLOW_HSV_HIGH, "yellow", qr_mask)
        + hsv_shape_detect(image_bgr, BLACK_HSV_LOW, BLACK_HSV_HIGH, "black", qr_mask)
    )

    # 只保留在 QR 圍出的工作區內的偵測 → 手臂本體、桌邊雜物都會被濾掉
    yolo_dets = filter_by_workspace(yolo_dets, workspace_polygon)
    cube_dets = filter_by_workspace(cube_dets, workspace_polygon)

    all_dets = cross_source_dedupe(yolo_dets + cube_dets)

    # 多幀穩定濾波：過濾掉閃爍的偵測、平均位置/skew/orientation
    all_dets = stabilize_detections(all_dets)

    # Part B：算 3D 世界座標（QR1 為原點的工作平面局部座標，單位公尺）
    # 有深度時 z 是真實高度；沒有時 z 用 OBJECT_HEIGHT_OFFSET_M。
    workspace_info = compute_world_positions(all_dets, qrcodes, w, h, depth_image)

    return {
        "image_width": w,
        "image_height": h,
        "objects": all_dets,
        "qrcodes": qrcodes,
        "workspace_polygon": workspace_polygon.tolist() if workspace_polygon is not None else None,
        "workspace_info": workspace_info,
    }


# ============================================================
# 相機 + 偵測背景執行緒
# ============================================================

def setup_realsense():
    """啟動 RealSense pipeline，讀 color intrinsics 存到全域變數。"""
    global RS_INTRINSICS
    print("[perception] Starting RealSense pipeline...")
    pipeline = rs.pipeline()
    config = rs.config()
    config.enable_stream(rs.stream.color, CAMERA_WIDTH, CAMERA_HEIGHT, rs.format.bgr8, CAMERA_FPS)
    config.enable_stream(rs.stream.depth, DEPTH_WIDTH, DEPTH_HEIGHT, rs.format.z16, CAMERA_FPS)
    profile = pipeline.start(config)

    # 抓 color intrinsics 覆蓋預設值（solvePnP 用得到）
    color_profile = profile.get_stream(rs.stream.color).as_video_stream_profile()
    intr = color_profile.get_intrinsics()
    RS_INTRINSICS = intr
    CAMERA_INTRINSICS["fx"] = intr.fx
    CAMERA_INTRINSICS["fy"] = intr.fy
    CAMERA_INTRINSICS["ppx"] = intr.ppx
    CAMERA_INTRINSICS["ppy"] = intr.ppy
    print(f"[perception] color intrinsics: fx={intr.fx:.1f} fy={intr.fy:.1f} "
          f"ppx={intr.ppx:.1f} ppy={intr.ppy:.1f} model={intr.model}")

    # 把 depth align 到 color 座標，這樣 depth[cy, cx] 對應 color[cy, cx]
    align = rs.align(rs.stream.color)
    return pipeline, align


def perception_loop():
    global latest_frame

    try:
        pipeline, align = setup_realsense()
    except Exception as ex:
        print(f"[perception] ERROR: RealSense startup failed: {ex}")
        stop_event.set()
        return

    print("[perception] Warming up (~15 frames)...")
    for _ in range(15):
        try:
            pipeline.wait_for_frames(timeout_ms=5000)
        except Exception:
            pass

    print("[perception] Entering detection loop")
    consecutive_dark_frames = 0

    while not stop_event.is_set():
        loop_start = time.time()

        try:
            frames = pipeline.wait_for_frames(timeout_ms=3000)
        except Exception as ex:
            print(f"[perception] wait_for_frames failed: {ex}")
            time.sleep(0.1)
            continue

        aligned = align.process(frames)
        color_frame = aligned.get_color_frame()
        depth_frame = aligned.get_depth_frame()

        if not color_frame:
            time.sleep(0.05)
            continue

        color_image = np.asarray(color_frame.get_data())
        depth_image = np.asarray(depth_frame.get_data()) if depth_frame else None

        # 黑屏檢查
        gray = cv2.cvtColor(color_image, cv2.COLOR_BGR2GRAY)
        brightness = float(gray.mean())
        if brightness < CAMERA_BRIGHTNESS_MIN:
            consecutive_dark_frames += 1
            if consecutive_dark_frames % 20 == 0:
                print(f"[perception] {consecutive_dark_frames} dark frames (mean={brightness:.1f})")
            time.sleep(0.1)
            continue
        consecutive_dark_frames = 0

        detect_start = time.time()
        try:
            result = detect_all(color_image, depth_image)
        except Exception as ex:
            print(f"[perception] detect_all error: {ex}")
            time.sleep(0.1)
            continue
        detect_ms = int((time.time() - detect_start) * 1000)

        with state_lock:
            latest_state["timestamp"] = time.time()
            latest_state["image_width"] = result["image_width"]
            latest_state["image_height"] = result["image_height"]
            latest_state["objects"] = result["objects"]
            latest_state["qrcodes"] = result["qrcodes"]
            latest_state["workspace_polygon"] = result["workspace_polygon"]
            latest_state["workspace_info"] = result["workspace_info"]
            latest_state["frames_processed"] += 1
            latest_state["detect_ms"] = detect_ms
            latest_frame = color_image

        elapsed = time.time() - loop_start
        sleep_time = max(0.0, DETECT_INTERVAL_SEC - elapsed)
        if sleep_time > 0:
            time.sleep(sleep_time)

    pipeline.stop()
    print("[perception] Detection loop stopped, RealSense pipeline closed")


# ============================================================
# Flask HTTP server
# ============================================================

app = Flask(__name__)
# 關掉 werkzeug 每次 request 的存取 log，避免刷版
import logging
logging.getLogger("werkzeug").setLevel(logging.WARNING)


@app.route("/scene")
def endpoint_scene():
    with state_lock:
        snapshot = dict(latest_state)
    return jsonify(snapshot)


@app.route("/scene/mode", methods=["GET"])
def endpoint_get_mode():
    with _execution_mode_lock:
        return jsonify({"mode": _execution_mode})


@app.route("/scene/mode", methods=["POST"])
def endpoint_set_mode():
    global _execution_mode
    data = request.get_json(silent=True) or {}
    mode = data.get("mode")
    if mode not in ("idle", "executing"):
        return jsonify({"error": "mode must be 'idle' or 'executing'"}), 400
    with _execution_mode_lock:
        prev = _execution_mode
        _execution_mode = mode
    if prev != mode:
        print(f"[perception] execution mode: {prev} -> {mode}")
    return jsonify({"mode": mode})


@app.route("/health")
def endpoint_health():
    with state_lock:
        ts = latest_state["timestamp"]
        info = {
            "status": "ok" if ts else "no_frame_yet",
            "frames_processed": latest_state["frames_processed"],
            "detect_ms": latest_state["detect_ms"],
            "last_update_seconds_ago": round(time.time() - ts, 2) if ts else None,
        }
    return jsonify(info)


@app.route("/debug/live")
def endpoint_live():
    """HTML 頁面：每 200ms 自動重載 /debug/frame，看起來像即時影片。"""
    html = """<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>Perception Live</title>
<style>
  body{margin:0;background:#111;color:#eee;font-family:sans-serif;text-align:center}
  #info{padding:8px;font-size:14px}
  img{max-width:100%;height:auto;display:block;margin:0 auto}
</style></head>
<body>
<div id="info">loading...</div>
<img id="cam" src="/debug/frame?t=0">
<script>
const img = document.getElementById('cam');
const info = document.getElementById('info');
let t = 0;
setInterval(() => {
  t++;
  img.src = '/debug/frame?t=' + t;
}, 200);
setInterval(async () => {
  try {
    const r = await fetch('/health');
    const j = await r.json();
    info.textContent = `frames=${j.frames_processed}  detect=${j.detect_ms}ms  last=${j.last_update_seconds_ago}s ago`;
  } catch(e) {}
}, 500);
</script></body></html>"""
    return Response(html, mimetype="text/html")


@app.route("/debug/frame")
def endpoint_frame():
    with state_lock:
        frame = latest_frame.copy() if latest_frame is not None else None
    if frame is None:
        return "no frame yet", 503

    # 順便畫上 detection bbox 方便 debug
    with state_lock:
        objects = list(latest_state["objects"])
        qrs = list(latest_state["qrcodes"])
        polygon = latest_state["workspace_polygon"]

    # 先畫工作區多邊形，讓你看到 ROI 邊界
    if polygon is not None:
        pts = np.array(polygon, dtype=np.int32).reshape(-1, 1, 2)
        cv2.polylines(frame, [pts], isClosed=True, color=(255, 200, 0), thickness=2)

    color_map = {
        "yolo_coco": (0, 255, 0),
        "yolo_pliers": (0, 0, 255),
        "cube_hsv": (0, 255, 255),
    }
    for obj in objects:
        x1, y1, x2, y2 = [int(v) for v in obj["bbox"]]
        c = color_map.get(obj["source"], (255, 255, 255))
        cv2.rectangle(frame, (x1, y1), (x2, y2), c, 3)
        cv2.putText(frame, f"{obj['name']} {obj['confidence']:.2f}",
                    (x1, max(y1 - 8, 15)), cv2.FONT_HERSHEY_SIMPLEX, 0.7, c, 2)
    for qr in qrs:
        cx, cy = qr["center_pixel"]
        cv2.circle(frame, (int(cx), int(cy)), 8, (0, 0, 255), -1)
        cv2.putText(frame, qr["id"], (int(cx) + 10, int(cy)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)

    ok, buf = cv2.imencode(".jpg", frame)
    if not ok:
        return "encode failed", 500
    return Response(buf.tobytes(), mimetype="image/jpeg")


# ============================================================
# 主程式
# ============================================================

def main():
    thread = threading.Thread(target=perception_loop, daemon=True, name="perception_loop")
    thread.start()

    print()
    print(f"[server] Starting on http://localhost:{SERVER_PORT}")
    print(f"  GET /scene            - 當下 detection JSON")
    print(f"  GET /scene/mode       - 執行狀態（idle / executing）")
    print(f"  POST /scene/mode      - 由 Unity 切換執行狀態")
    print(f"  GET /health           - FPS / 上次更新時間")
    print(f"  GET /debug/frame      - 標好 bbox 的最新一幀 JPEG")
    print()

    try:
        app.run(host=SERVER_HOST, port=SERVER_PORT, threaded=True, use_reloader=False)
    finally:
        stop_event.set()
        thread.join(timeout=2)


if __name__ == "__main__":
    main()
