"""
統一物件偵測器：把三種偵測結果合併成統一輸出。

- YOLO11n（COCO 80 類 → 白名單過濾）：cup、cell phone、bottle、book...
- 自訓 pliers.pt：尖嘴鉗
- OpenCV HSV：黃色立方體、黑色立方體

輸出格式跟現有 open_vocab 版一致，方便未來取代 detect_open_vocab.py。

用法（從 csharp_server/ 底下）：
    yolo11_env\\Scripts\\python.exe unified_detector.py [image_path]

若省略 image_path 預設用 images/test_scene.jpg。
"""

import json
import sys
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO


BASE_DIR = Path(__file__).resolve().parent
DEFAULT_IMAGE = BASE_DIR / "images" / "test_scene.jpg"
OUTPUT_JSON = BASE_DIR / "outputs" / "unified_result.json"
OUTPUT_VISUAL = BASE_DIR / "outputs" / "unified_visual.jpg"

# --- YOLO 模型設定 ---
YOLO11N_WEIGHTS = BASE_DIR / "yolo11n.pt"          # 通用 COCO 模型（第一次跑會自動下載）
PLIERS_WEIGHTS = BASE_DIR / "models" / "pliers.pt"  # 自訓的尖嘴鉗模型

# COCO 白名單：只留這幾類 YOLO 偵測結果，避免場景中不相關物件干擾
COCO_TARGETS = {"cup", "cell phone", "bottle", "book", "mouse", "keyboard", "laptop"}

YOLO_CONF = 0.25
PLIERS_CONF = 0.25

# --- HSV 顏色遮罩參數（跟 cube_detector.py 一致）---
YELLOW_HSV_LOW = np.array([18, 100, 100])
YELLOW_HSV_HIGH = np.array([38, 255, 255])
BLACK_HSV_LOW = np.array([0, 0, 0])
BLACK_HSV_HIGH = np.array([180, 80, 60])
MIN_AREA_PX = 500
MAX_AREA_PX = 30000
ASPECT_RATIO_TOL = 0.35
SOLIDITY_MIN = 0.85

# 跨偵測器去重的 IoU 門檻：若兩個 bbox 重疊 > 這個值，只留 confidence 較高的
IOU_DEDUPE_THRESHOLD = 0.5


# ============================================================
# Windows 路徑含中文時 cv2 讀寫的替代方法
# ============================================================

def imread_unicode(path):
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_unicode(path, image):
    ext = Path(path).suffix
    ok, buf = cv2.imencode(ext, image)
    if not ok:
        return False
    buf.tofile(str(path))
    return True


# ============================================================
# 三個獨立偵測器
# ============================================================

def yolo_detect(model, image_path, allowed_classes, source_tag, conf):
    """通用 YOLO 偵測函式：跑模型、依白名單過濾、統一輸出格式。"""
    results = model(str(image_path), conf=conf, verbose=False)
    result = results[0]

    detections = []
    for box in result.boxes:
        cls_id = int(box.cls[0])
        name = result.names[cls_id]
        if allowed_classes is not None and name not in allowed_classes:
            continue
        confidence = float(box.conf[0])
        x1, y1, x2, y2 = [float(v) for v in box.xyxy[0]]
        detections.append({
            "name": name,
            "confidence": round(confidence, 3),
            "bbox": [round(x1, 2), round(y1, 2), round(x2, 2), round(y2, 2)],
            "center_pixel": [round((x1 + x2) / 2, 2), round((y1 + y2) / 2, 2)],
            "source": source_tag,
        })
    return detections


def hsv_cube_detect(image_bgr, hsv_low, hsv_high, class_name):
    """對單一顏色範圍找立方體，回傳 detection dict list。"""
    hsv = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2HSV)
    mask = cv2.inRange(hsv, hsv_low, hsv_high)

    kernel = np.ones((5, 5), np.uint8)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)

    detections = []
    for cnt in contours:
        area = cv2.contourArea(cnt)
        if area < MIN_AREA_PX or area > MAX_AREA_PX:
            continue

        x, y, w, h = cv2.boundingRect(cnt)
        aspect = w / h if h > 0 else 0
        if abs(aspect - 1.0) > ASPECT_RATIO_TOL:
            continue

        hull = cv2.convexHull(cnt)
        hull_area = cv2.contourArea(hull)
        solidity = area / hull_area if hull_area > 0 else 0
        if solidity < SOLIDITY_MIN:
            continue

        cx = x + w / 2
        cy = y + h / 2
        detections.append({
            "name": class_name,
            "confidence": round(float(solidity), 3),
            "bbox": [round(float(x), 2), round(float(y), 2),
                     round(float(x + w), 2), round(float(y + h), 2)],
            "center_pixel": [round(float(cx), 2), round(float(cy), 2)],
            "source": "cube_hsv",
        })
    return detections


# ============================================================
# 跨偵測器去重
# ============================================================

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


def cross_source_dedupe(detections, iou_threshold=IOU_DEDUPE_THRESHOLD):
    """
    若不同來源的 bbox 重疊嚴重，只保留 confidence 較高者。
    同來源內的重複已由各自 detector 的 NMS 處理過。
    """
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


# ============================================================
# 主流程
# ============================================================

# 模組載入時就把兩個 YOLO 權重都讀進來（未來即時偵測就不用每次載入）
print("載入 YOLO11n（COCO 通用）...")
YOLO11N = YOLO(str(YOLO11N_WEIGHTS))
print(f"載入自訓 pliers 模型：{PLIERS_WEIGHTS.name}")
PLIERS_MODEL = YOLO(str(PLIERS_WEIGHTS))
print()


def detect_all(image_path):
    image = imread_unicode(image_path)
    if image is None:
        return None

    h, w = image.shape[:2]

    # 1. YOLO11n → COCO 常見物件（白名單過濾）
    yolo_dets = yolo_detect(YOLO11N, image_path, COCO_TARGETS, "yolo_coco", YOLO_CONF)

    # 2. 自訓 pliers 模型
    pliers_dets = yolo_detect(PLIERS_MODEL, image_path, None, "yolo_pliers", PLIERS_CONF)

    # 3. HSV 顏色立方體
    cube_dets = (
        hsv_cube_detect(image, YELLOW_HSV_LOW, YELLOW_HSV_HIGH, "yellow_cube") +
        hsv_cube_detect(image, BLACK_HSV_LOW, BLACK_HSV_HIGH, "black_cube")
    )

    all_dets = cross_source_dedupe(yolo_dets + pliers_dets + cube_dets)

    return {
        "image_width": w,
        "image_height": h,
        "objects": all_dets,
    }


def main():
    image_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_IMAGE

    if not image_path.exists():
        print(f"找不到影像：{image_path}")
        sys.exit(1)

    OUTPUT_JSON.parent.mkdir(parents=True, exist_ok=True)

    print(f"對 {image_path.name} 執行統一偵測...")
    output = detect_all(image_path)

    if output is None:
        print(f"影像讀取失敗：{image_path}")
        sys.exit(1)

    OUTPUT_JSON.write_text(json.dumps(output, indent=2, ensure_ascii=False), encoding="utf-8")

    print()
    print(f"共 {len(output['objects'])} 個物件（跨源去重後）：")
    for obj in output["objects"]:
        print(f"  {obj['name']:15s}  conf={obj['confidence']:.3f}  "
              f"bbox={obj['bbox']}  src={obj['source']}")

    print()
    print(f"JSON 輸出：{OUTPUT_JSON}")

    # 視覺化
    image = imread_unicode(image_path)
    color_map = {
        "yolo_coco": (0, 255, 0),        # 綠
        "yolo_pliers": (0, 0, 255),      # 紅
        "cube_hsv": (0, 255, 255),       # 黃
    }
    for obj in output["objects"]:
        x1, y1, x2, y2 = [int(v) for v in obj["bbox"]]
        color = color_map.get(obj["source"], (255, 255, 255))
        cv2.rectangle(image, (x1, y1), (x2, y2), color, 3)
        label = f"{obj['name']} {obj['confidence']:.2f}"
        cv2.putText(image, label, (x1, max(y1 - 8, 15)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
    imwrite_unicode(OUTPUT_VISUAL, image)
    print(f"視覺化圖：{OUTPUT_VISUAL}")


if __name__ == "__main__":
    main()
