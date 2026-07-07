"""
用 Ultralytics YOLO11 對從 Roboflow Universe 下載的 pliers dataset 做 fine-tune。

用法（先下載並解壓 dataset 到 csharp_server/pliers_dataset/）：
    yolo11_env\\Scripts\\python.exe train_pliers.py

訓練完成後 best weights 會存在 runs/detect/pliers_train/weights/best.pt。
把它複製到 models/pliers.pt 供 unified detector 使用。
"""

from pathlib import Path

from ultralytics import YOLO


BASE_DIR = Path(__file__).resolve().parent
DATASET_DIR = BASE_DIR / "pliers_dataset"
DATA_YAML = DATASET_DIR / "data.yaml"

# 訓練參數：epochs 100 對這種小資料集通常夠，太多會 overfit。
# imgsz 640 是 YOLO 標準。你的 4060 (8GB) 撐得起 batch 16-32。
EPOCHS = 100
IMAGE_SIZE = 640
BATCH_SIZE = 16
DEVICE = 0    # 0 = 第一張 GPU；'cpu' 若沒 CUDA


def main():
    if not DATASET_DIR.exists():
        print(f"找不到資料集資料夾：{DATASET_DIR}")
        print("請先從 Roboflow Universe 下載並解壓到這個位置")
        return

    if not DATA_YAML.exists():
        print(f"找不到 data.yaml：{DATA_YAML}")
        print("dataset 目錄結構可能不對，看看實際檔名並調整下面路徑")
        return

    print("=== 訓練參數 ===")
    print(f"  資料集：{DATASET_DIR}")
    print(f"  data.yaml：{DATA_YAML}")
    print(f"  epochs：{EPOCHS}")
    print(f"  image size：{IMAGE_SIZE}")
    print(f"  batch size：{BATCH_SIZE}")
    print(f"  device：{DEVICE}")
    print()

    # 起始 weights 用官方 yolo11n（nano，最小最快）。
    # 若準度不夠可改成 yolo11s / yolo11m 更大版本
    model = YOLO("yolo11n.pt")

    results = model.train(
        data=str(DATA_YAML),
        epochs=EPOCHS,
        imgsz=IMAGE_SIZE,
        batch=BATCH_SIZE,
        device=DEVICE,
        project=str(BASE_DIR / "runs" / "detect"),
        name="pliers_train",
        exist_ok=True,
    )

    print()
    print("=== 訓練完成 ===")
    print(f"最好的 weights：{BASE_DIR / 'runs/detect/pliers_train/weights/best.pt'}")
    print(f"驗證指標請看 runs/detect/pliers_train/results.png")
    print()
    print("下一步：把 best.pt 複製到 models/pliers.pt")


if __name__ == "__main__":
    main()
