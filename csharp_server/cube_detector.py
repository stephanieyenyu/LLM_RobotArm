"""
用 OpenCV HSV 顏色遮罩偵測黃色與黑色 5cm 立方體。

不需要 ML model、單張推論 <5ms、100% 準確度（前提是顏色跟背景區別夠大）。
輸出跟 YOLO 一樣格式的 detection dict list，可跟 YOLO / OwlViT 結果合併。

用法（從 csharp_server/ 底下）：
    yolo11_env\\Scripts\\python.exe cube_detector.py [image_path]
"""

import json
import sys
from pathlib import Path

import cv2
import numpy as np


def imread_unicode(path):
    """OpenCV imread 在 Windows 讀不到含中文的路徑，改用 numpy 讀 bytes 再 decode。"""
    data = np.fromfile(str(path), dtype=np.uint8)
    return cv2.imdecode(data, cv2.IMREAD_COLOR)


def imwrite_unicode(path, image):
    """同上，寫入也走 encode + tofile。"""
    ext = Path(path).suffix
    ok, buf = cv2.imencode(ext, image)
    if not ok:
        return False
    buf.tofile(str(path))
    return True


BASE_DIR = Path(__file__).resolve().parent
DEFAULT_IMAGE = BASE_DIR / "images" / "test_scene.jpg"
OUTPUT_JSON = BASE_DIR / "outputs" / "cube_result.json"
OUTPUT_VISUAL = BASE_DIR / "outputs" / "cube_visual.jpg"

# HSV 顏色範圍。cv2 的 H 通道範圍是 0-180（不是 0-360），S 和 V 是 0-255。
# 之後跑實圖若被光線影響可微調上下限
YELLOW_HSV_LOW = np.array([18, 100, 100])
YELLOW_HSV_HIGH = np.array([38, 255, 255])

BLACK_HSV_LOW = np.array([0, 0, 0])
BLACK_HSV_HIGH = np.array([180, 80, 60])  # S 低（無彩色）、V 低（暗）

# 5cm 立方體從上方看應該接近正方形。這裡的面積閾值假設鏡頭離桌面約 40-60cm
# 你固定好相機後可以量一次實際像素面積再調整這兩個常數。
MIN_AREA_PX = 500       # 太小的忽略（可能是噪點）
MAX_AREA_PX = 30000     # 太大的忽略（可能是背景大片色塊）

# 立方體從上方看應該長寬比 ~1、solidity ~1（凸多邊形）
ASPECT_RATIO_TOL = 0.35         # 允許 |aspect - 1| < 這個值
SOLIDITY_MIN = 0.85


def detect_color_cubes(image_bgr, hsv_low, hsv_high, class_name):
    """對單一顏色範圍找立方體。回傳 detection dict list。"""
    hsv = cv2.cvtColor(image_bgr, cv2.COLOR_BGR2HSV)
    mask = cv2.inRange(hsv, hsv_low, hsv_high)

    # 形態學開運算去掉小雜訊、閉運算填小洞
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
        # 「confidence」對顏色遮罩沒有真實意義；用 solidity 當代理值方便合併
        detections.append({
            "name": class_name,
            "confidence": round(float(solidity), 3),
            "bbox": [round(float(x), 2), round(float(y), 2),
                     round(float(x + w), 2), round(float(y + h), 2)],
            "center_pixel": [round(float(cx), 2), round(float(cy), 2)],
            "source": "cube_hsv",
        })

    return detections


def main():
    image_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_IMAGE

    if not image_path.exists():
        print(f"找不到影像：{image_path}")
        return

    OUTPUT_JSON.parent.mkdir(parents=True, exist_ok=True)

    image = imread_unicode(image_path)
    if image is None:
        print(f"影像無法讀取：{image_path}")
        return

    print(f"對 {image_path.name} 執行顏色立方體偵測...")

    yellow_cubes = detect_color_cubes(image, YELLOW_HSV_LOW, YELLOW_HSV_HIGH, "yellow_cube")
    black_cubes = detect_color_cubes(image, BLACK_HSV_LOW, BLACK_HSV_HIGH, "black_cube")

    all_detections = yellow_cubes + black_cubes

    print()
    print(f"共偵測到 {len(all_detections)} 個立方體：")
    print(f"  yellow_cube: {len(yellow_cubes)}")
    print(f"  black_cube:  {len(black_cubes)}")
    for obj in all_detections:
        print(f"    {obj['name']:15s}  bbox={obj['bbox']}  center={obj['center_pixel']}")

    output = {
        "image": image_path.name,
        "image_width": image.shape[1],
        "image_height": image.shape[0],
        "objects": all_detections,
    }
    OUTPUT_JSON.write_text(json.dumps(output, indent=2, ensure_ascii=False), encoding="utf-8")
    print()
    print(f"JSON 輸出：{OUTPUT_JSON}")

    # 視覺化：在原圖上畫 bbox
    visual = image.copy()
    color_map = {"yellow_cube": (0, 255, 255), "black_cube": (255, 255, 255)}
    for obj in all_detections:
        x1, y1, x2, y2 = [int(v) for v in obj["bbox"]]
        color = color_map.get(obj["name"], (0, 255, 0))
        cv2.rectangle(visual, (x1, y1), (x2, y2), color, 3)
        cv2.putText(visual, obj["name"], (x1, max(y1 - 8, 15)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.7, color, 2)
    imwrite_unicode(OUTPUT_VISUAL, visual)
    print(f"視覺化圖：{OUTPUT_VISUAL}")


if __name__ == "__main__":
    main()
