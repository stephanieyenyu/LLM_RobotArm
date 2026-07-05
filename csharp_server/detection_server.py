"""
detection_server.py
============================================================
Python 偵測伺服器（依 Notion comment 建立）：
  - 使用 ultralytics 套件的預訓練 YOLO 模型做即時物件偵測
    （不需重新訓練目標物件，首次執行會自動下載 yolo11n.pt）
  - 鏡頭由本伺服器「常駐持有」：只開啟一次，解決 C# 端
    每次重開 DSHOW 造成的接相機問題，也符合「鏡頭固定」
  - 以 HTTP 提供偵測結果給 csharp_server（Program.cs 會來拉），
    並提供 MJPEG /stream 端點，之後可直接串流到 Unity

啟動方式（務必在 csharp_server 資料夾內執行）：
    cd csharp_server
    pip install ultralytics
    python detection_server.py

端點：
    GET /health   -> {"status": "ok"}
    GET /detect   -> 與 detected_objects.json 相同格式的偵測結果
    GET /visual   -> 最近一次標註後的 JPEG 影像
    GET /stream   -> MJPEG 即時串流（可給 Unity 或瀏覽器看）
============================================================
"""

import json
import os
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import cv2
import numpy as np
from ultralytics import YOLO


# ============================================================
# 設定
# ============================================================

HOST = "127.0.0.1"
PORT = 8765

# 是否使用攝影機。設為 False 會「完全不碰相機」，直接用 FALLBACK_IMAGE，
# 等同原本 PartAExporter 的 useWebcam = false 行為。
# 若相機有問題、或只想用現成照片測試，把這裡改成 False。
USE_WEBCAM = True

# 與原本 C# PartAExporter 相同的攝影機編號與解析度
CAMERA_INDEX = 2
FRAME_WIDTH = 1280
FRAME_HEIGHT = 720

# 攝影機讀取失敗時的備援影像（與原本 C# 行為一致）
FALLBACK_IMAGE = "images/test_scene.jpg"

# 相機畫面平均亮度低於此值視為「沒讀到有效畫面」（0-255，全黑=0），
# 此時自動退回使用 FALLBACK_IMAGE，避免對全黑畫面硬跑偵測。
MIN_VALID_BRIGHTNESS = 15.0

# ultralytics 預訓練模型；首次執行會自動下載權重，不需自己 train
# （可用環境變數 YOLO_MODEL 覆蓋，方便測試）
MODEL_NAME = os.environ.get("YOLO_MODEL", "yolo11n.pt")

CONFIDENCE_THRESHOLD = 0.25

# 物件過濾策略：不再用白名單限制類別，改回報 YOLO 認得的所有物件，
# 以符合「即時 ReAct：把場景物件盡量報給 LLM 推理」的目標。
# 但仍排除一定會出現在背景、手臂不會去操作的大型物件，
# 避免它們洗進 sceneObjects 干擾 LLM。
# 需要時可自行增減此清單（名稱須為 COCO 80 類的英文類別名）。
EXCLUDED_OBJECTS = {
    "person",
    "chair",
    "couch",
    "bed",
    "dining table",
    "toilet",
    "tv",
    "refrigerator",
    "oven",
    "sink",
    "potted plant",
}

# ArUco marker ID → 工作平面定位點名稱（與原本 C# QrCodeDetectorService 一致）
ARUCO_ID_TO_NAME = {
    1: "QR1",
    2: "QR2",
    3: "QR3",
    4: "QR4",
}

# 標註影像輸出位置（與原本 PartAExporter 相同）
VISUAL_OUTPUT_PATHS = [
    "outputs/visual_result.jpg",
    "../sample_json/visual_result.jpg",
]


# ============================================================
# 攝影機管理：常駐持有 handle，只開啟一次
# ============================================================

class CameraManager:
    def __init__(self, camera_index, width, height):
        self.camera_index = camera_index
        self.width = width
        self.height = height
        self.lock = threading.Lock()
        self.capture = None
        self.available = False

    def open(self):
        """
        開啟攝影機並暖機。
        - USE_WEBCAM = False：完全不碰相機，直接用 FALLBACK_IMAGE。
        - 開啟失敗或畫面持續全黑：available = False，改用 FALLBACK_IMAGE。
        """
        with self.lock:
            if not USE_WEBCAM:
                print(f"[camera] USE_WEBCAM = False. Using {FALLBACK_IMAGE} directly.")
                self.capture = None
                self.available = False
                return

            try:
                self.capture = cv2.VideoCapture(self.camera_index)

                if not self.capture.isOpened():
                    print(f"[camera] Cannot open webcam at index {self.camera_index}. "
                          f"Falling back to {FALLBACK_IMAGE}.")
                    self.capture = None
                    self.available = False
                    return

                self.capture.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
                self.capture.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)

                # 暖機：讓 exposure / AWB 有時間穩定（同原 C# 行為）
                for _ in range(15):
                    self.capture.read()
                    time.sleep(0.03)

                # 開機後先驗證能不能讀到「非全黑」的有效畫面；
                # 讀不到就當作相機不可用，直接退回備援影像
                test_frame = self._read_bright_frame()

                if test_frame is None:
                    print(f"[camera] Webcam {self.camera_index} opened but only produced "
                          f"dark/invalid frames. Falling back to {FALLBACK_IMAGE}.")
                    self.capture.release()
                    self.capture = None
                    self.available = False
                    return

                self.available = True
                print(f"[camera] Webcam {self.camera_index} opened and warmed up.")
            except Exception as e:
                print(f"[camera] Open failed: {e}. Falling back to {FALLBACK_IMAGE}.")
                if self.capture is not None:
                    self.capture.release()
                self.capture = None
                self.available = False

    def read_frame(self):
        """
        取得一張影像。
        優先讀攝影機（含亮度檢查與重試）；讀不到有效畫面就用備援影像；
        兩者都失敗回傳 None。
        """
        with self.lock:
            if self.available and self.capture is not None:
                frame = self._read_bright_frame()

                if frame is not None:
                    return frame

                # 相機讀不到有效畫面（例如中途被拔線或全黑）→ 用備援影像
                print("[camera] No valid webcam frame; falling back to image file.")

        return self._read_fallback_image()

    def _read_fallback_image(self):
        """讀取備援影像 FALLBACK_IMAGE。"""
        if os.path.exists(FALLBACK_IMAGE):
            frame = cv2.imread(FALLBACK_IMAGE)

            if frame is not None and frame.size > 0:
                return frame

            print(f"[camera] Fallback image {FALLBACK_IMAGE} exists but could not be read.")
        else:
            print(f"[camera] Fallback image {FALLBACK_IMAGE} not found.")

        return None

    def _read_bright_frame(self):
        """
        讀相機並做亮度檢查 + 重試。
        全部 retry 都太暗（讀不到有意義畫面）就回傳 None，
        讓上層改用備援影像。
        """
        if self.capture is None:
            return None

        max_retries = 10

        for retry in range(max_retries + 1):
            ok, frame = self.capture.read()

            if not ok or frame is None or frame.size == 0:
                return None

            gray = frame if frame.ndim == 2 else cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            mean_brightness = float(gray.mean())

            if mean_brightness >= MIN_VALID_BRIGHTNESS:
                return frame

            print(f"[camera] Frame too dark (mean {mean_brightness:.1f}), "
                  f"retry {retry + 1}/{max_retries}...")
            time.sleep(0.1)

        return None

    def release(self):
        with self.lock:
            if self.capture is not None:
                self.capture.release()
                self.capture = None
            self.available = False


# ============================================================
# 偵測服務：ultralytics YOLO + OpenCV QRCode
# ============================================================

class DetectionService:
    def __init__(self, camera):
        self.camera = camera

        print(f"[model] Loading ultralytics model: {MODEL_NAME} ...")
        self.model = YOLO(MODEL_NAME)
        print("[model] Model loaded.")

        # ArUco marker 偵測（與原本 C# QrCodeDetectorService 完全一致）：
        #   dictionary = DICT_4X4_50
        #   ArUco ID 1~4 → QR1~QR4
        self.aruco_dictionary = cv2.aruco.getPredefinedDictionary(
            cv2.aruco.DICT_4X4_50)
        self.aruco_params = cv2.aruco.DetectorParameters()
        self.aruco_detector = cv2.aruco.ArucoDetector(
            self.aruco_dictionary, self.aruco_params)

        self.visual_lock = threading.Lock()
        self.latest_visual_jpeg = None

    # ---------- 物件偵測（ultralytics）----------

    def detect_objects(self, frame):
        results = self.model.predict(
            source=frame,
            conf=CONFIDENCE_THRESHOLD,
            verbose=False,
        )

        objects = []
        result = results[0]

        if result.boxes is None:
            return objects

        names = result.names

        for box in result.boxes:
            class_id = int(box.cls[0])
            name = names.get(class_id, str(class_id))

            # 回報所有 YOLO 認得的物件，只排除背景大型物件
            if name in EXCLUDED_OBJECTS:
                continue

            confidence = float(box.conf[0])
            x1, y1, x2, y2 = [float(v) for v in box.xyxy[0].tolist()]

            objects.append({
                "name": name,
                "confidence": round(confidence, 3),
                "bbox": [round(x1, 2), round(y1, 2), round(x2, 2), round(y2, 2)],
                "center_pixel": [round((x1 + x2) / 2.0, 2), round((y1 + y2) / 2.0, 2)],
                "source": "yolo_ultralytics",
            })

        return objects

    # ---------- QRCode 偵測（OpenCV）----------

    def detect_qrcodes(self, frame):
        """
        用 ArUco marker 偵測工作平面定位點（對應原本 C# 的 Aruco 方案）。
        回傳格式沿用 detected_objects.json 的 qrcodes 欄位（id/corners/center_pixel），
        讓 coordinate_mapper_3d.py 完全不用改。
        """
        qrcodes = []

        gray = frame if frame.ndim == 2 else cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)

        corners_list, ids, _ = self.aruco_detector.detectMarkers(gray)

        if ids is None or len(ids) == 0:
            return qrcodes

        for marker_corners, marker_id in zip(corners_list, ids.flatten()):
            name = ARUCO_ID_TO_NAME.get(int(marker_id))

            if name is None:
                # 偵測到但不是 QR1~QR4 的 marker，略過
                continue

            # marker_corners shape = (1, 4, 2)，四個角依序 左上→右上→右下→左下
            pts = marker_corners.reshape(4, 2)

            corner_list = [[round(float(x), 2), round(float(y), 2)] for x, y in pts]
            center = pts.mean(axis=0)

            qrcodes.append({
                "id": name,
                "corners": corner_list,
                "center_pixel": [round(float(center[0]), 2), round(float(center[1]), 2)],
            })

        return qrcodes

    # ---------- 一次完整偵測 ----------

    def detect(self):
        """
        拍一張 → YOLO 物件偵測 → QR 偵測 → 存標註圖，
        回傳與 detected_objects.json 相同格式的 dict。
        失敗時回傳 None。
        """
        frame = self.camera.read_frame()

        if frame is None:
            print("[detect] No frame available (camera and fallback image both failed).")
            return None

        height, width = frame.shape[:2]

        objects = self.detect_objects(frame)
        qrcodes = self.detect_qrcodes(frame)

        self._save_visual(frame, objects, qrcodes)

        return {
            "image_width": int(width),
            "image_height": int(height),
            "objects": objects,
            "qrcodes": qrcodes,
        }

    # ---------- 標註圖 ----------

    def _save_visual(self, frame, objects, qrcodes):
        visual = frame.copy()

        for qr in qrcodes:
            cx, cy = int(qr["center_pixel"][0]), int(qr["center_pixel"][1])
            cv2.circle(visual, (cx, cy), 8, (0, 0, 255), -1)
            cv2.putText(visual, qr["id"], (cx + 10, cy),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)

            for corner in qr["corners"]:
                cv2.circle(visual, (int(corner[0]), int(corner[1])), 5, (255, 0, 0), -1)

        for obj in objects:
            x1, y1, x2, y2 = [int(v) for v in obj["bbox"]]
            cv2.rectangle(visual, (x1, y1), (x2, y2), (0, 255, 0), 4)
            cv2.putText(visual, f'{obj["name"]} {obj["confidence"]}',
                        (x1, max(y1 - 10, 20)),
                        cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 255, 0), 3)

        ok, jpeg = cv2.imencode(".jpg", visual)

        if ok:
            with self.visual_lock:
                self.latest_visual_jpeg = jpeg.tobytes()

        for path in VISUAL_OUTPUT_PATHS:
            try:
                directory = os.path.dirname(path)

                if directory:
                    os.makedirs(directory, exist_ok=True)

                cv2.imwrite(path, visual)
            except Exception as e:
                print(f"[visual] Failed to save {path}: {e}")

    def get_latest_visual_jpeg(self):
        with self.visual_lock:
            return self.latest_visual_jpeg


# ============================================================
# HTTP server
# ============================================================

camera_manager = CameraManager(CAMERA_INDEX, FRAME_WIDTH, FRAME_HEIGHT)
detection_service = None  # 在 main() 內初始化


class DetectionRequestHandler(BaseHTTPRequestHandler):

    def do_GET(self):
        if self.path == "/health":
            self._send_json({"status": "ok"})
        elif self.path == "/detect":
            self._handle_detect()
        elif self.path == "/visual":
            self._handle_visual()
        elif self.path == "/stream":
            self._handle_stream()
        else:
            self.send_error(404, "Unknown endpoint")

    def _send_json(self, payload, status=200):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _handle_detect(self):
        result = detection_service.detect()

        if result is None:
            self._send_json({"error": "no frame available"}, status=500)
            return

        self._send_json(result)

    def _handle_visual(self):
        jpeg = detection_service.get_latest_visual_jpeg()

        if jpeg is None:
            self.send_error(404, "No visual yet. Call /detect first.")
            return

        self.send_response(200)
        self.send_header("Content-Type", "image/jpeg")
        self.send_header("Content-Length", str(len(jpeg)))
        self.end_headers()
        self.wfile.write(jpeg)

    def _handle_stream(self):
        """MJPEG 即時串流：之後可直接讓 Unity 或瀏覽器讀取此端點。"""
        self.send_response(200)
        self.send_header(
            "Content-Type", "multipart/x-mixed-replace; boundary=frame")
        self.end_headers()

        try:
            while True:
                frame = camera_manager.read_frame()

                if frame is None:
                    time.sleep(0.5)
                    continue

                ok, jpeg = cv2.imencode(".jpg", frame)

                if not ok:
                    continue

                data = jpeg.tobytes()
                self.wfile.write(b"--frame\r\n")
                self.wfile.write(b"Content-Type: image/jpeg\r\n")
                self.wfile.write(f"Content-Length: {len(data)}\r\n\r\n".encode())
                self.wfile.write(data)
                self.wfile.write(b"\r\n")
                time.sleep(0.1)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass  # 客戶端斷線屬正常情況

    def log_message(self, format, *args):
        # 保留精簡 log，避免每次輪詢洗版
        if "/detect" in (args[0] if args else ""):
            return
        super().log_message(format, *args)


def main():
    global detection_service

    print("=== Detection Server (ultralytics) ===")

    camera_manager.open()
    detection_service = DetectionService(camera_manager)

    # 啟動時先做一次偵測，確認整條鏈可用，也讓 /visual 立即有圖
    warmup = detection_service.detect()

    if warmup is None:
        print("[startup] Warning: initial detection produced no frame. "
              "Server will still run; check camera or fallback image.")
    else:
        print(f"[startup] Initial detection OK: "
              f"{len(warmup['objects'])} objects, {len(warmup['qrcodes'])} qrcodes.")

    server = ThreadingHTTPServer((HOST, PORT), DetectionRequestHandler)
    print(f"Serving on https://{HOST}:{PORT}")
    print("Endpoints: /health  /detect  /visual  /stream")
    print("Press Ctrl+C to stop.")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down...")
    finally:
        server.server_close()
        camera_manager.release()


if __name__ == "__main__":
    main()
