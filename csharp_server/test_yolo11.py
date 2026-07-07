"""
用 Ultralytics YOLO11 對現有 test_scene.jpg 做一次靜態偵測。
目的是評估「不 fine-tune 用預訓練 weights」能認出哪些物件，
方便跟 OwlViT 舊結果比對。

用法（從 csharp_server/ 底下）：
    yolo11_env\\Scripts\\python.exe test_yolo11.py
"""

import json
from pathlib import Path

from ultralytics import YOLO


BASE_DIR = Path(__file__).resolve().parent
IMAGE_PATH = BASE_DIR / "images" / "test_scene.jpg"
OUTPUT_DIR = BASE_DIR / "outputs"
OUTPUT_JSON = OUTPUT_DIR / "yolo11_result.json"
ANNOTATED_IMAGE = OUTPUT_DIR / "yolo11_visual.jpg"

# 換成 yolo11s.pt / yolo11m.pt 準度更高但更慢；n=nano 是最小最快版本
MODEL_NAME = "yolo11n.pt"
CONF_THRESHOLD = 0.20


def main():
    if not IMAGE_PATH.exists():
        print(f"找不到影像：{IMAGE_PATH}")
        return

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    print(f"載入 {MODEL_NAME}（第一次跑會下載 ~5-10 MB 權重）...")
    model = YOLO(MODEL_NAME)

    print(f"對 {IMAGE_PATH.name} 執行偵測，conf ≥ {CONF_THRESHOLD}")
    results = model(str(IMAGE_PATH), conf=CONF_THRESHOLD, verbose=False)
    result = results[0]

    detected = []
    for box in result.boxes:
        cls_id = int(box.cls[0])
        name = result.names[cls_id]
        conf = float(box.conf[0])
        x1, y1, x2, y2 = [float(v) for v in box.xyxy[0]]
        cx = (x1 + x2) / 2
        cy = (y1 + y2) / 2
        detected.append({
            "name": name,
            "confidence": round(conf, 3),
            "bbox": [round(x1, 2), round(y1, 2), round(x2, 2), round(y2, 2)],
            "center_pixel": [round(cx, 2), round(cy, 2)],
            "source": "yolo11n",
        })

    print()
    print(f"共偵測到 {len(detected)} 個物件（conf ≥ {CONF_THRESHOLD}）：")
    for obj in detected:
        print(f"  {obj['name']:20s}  conf={obj['confidence']:.3f}  "
              f"bbox={obj['bbox']}  center={obj['center_pixel']}")

    output = {
        "image": str(IMAGE_PATH.name),
        "image_width": int(result.orig_shape[1]),
        "image_height": int(result.orig_shape[0]),
        "model": MODEL_NAME,
        "conf_threshold": CONF_THRESHOLD,
        "objects": detected,
    }
    OUTPUT_JSON.write_text(json.dumps(output, indent=2, ensure_ascii=False), encoding="utf-8")
    print()
    print(f"JSON 輸出：{OUTPUT_JSON}")

    annotated = result.plot()
    import cv2
    cv2.imwrite(str(ANNOTATED_IMAGE), annotated)
    print(f"視覺化圖：{ANNOTATED_IMAGE}")


if __name__ == "__main__":
    main()
