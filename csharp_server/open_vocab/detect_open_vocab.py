import json
import sys
from pathlib import Path

import torch
from PIL import Image
from transformers import OwlViTProcessor, OwlViTForObjectDetection


BASE_DIR = Path(__file__).resolve().parent
PROJECT_DIR = BASE_DIR.parent

IMAGE_PATH = PROJECT_DIR / "images" / "test_scene.jpg"
PROMPTS_PATH = BASE_DIR / "prompts.txt"
OUTPUT_PATH = BASE_DIR / "outputs" / "objects_open.json"

SCORE_THRESHOLD = 0.08


def load_prompts():
    if not PROMPTS_PATH.exists():
        return ["object"]

    prompts = []

    for line in PROMPTS_PATH.read_text(encoding="utf-8").splitlines():
        item = line.strip()
        if item:
            prompts.append(item)

    return prompts if prompts else ["object"]


def main():
    if not IMAGE_PATH.exists():
        print(f"Image not found: {IMAGE_PATH}")
        sys.exit(1)

    prompts = load_prompts()

    image = Image.open(IMAGE_PATH).convert("RGB")
    width, height = image.size

    processor = OwlViTProcessor.from_pretrained("google/owlvit-base-patch32")
    model = OwlViTForObjectDetection.from_pretrained("google/owlvit-base-patch32")

    device = "cuda" if torch.cuda.is_available() else "cpu"
    model.to(device)

    inputs = processor(
        text=[prompts],
        images=image,
        return_tensors="pt"
    )

    inputs = {key: value.to(device) for key, value in inputs.items()}

    with torch.no_grad():
        outputs = model(**inputs)

    target_sizes = torch.tensor([[height, width]], device=device)

    # post_process_object_detection renamed in newer transformers versions
    try:
        results = processor.post_process_grounded_object_detection(
            outputs=outputs,
            target_sizes=target_sizes,
            threshold=SCORE_THRESHOLD
        )[0]
    except AttributeError:
        results = processor.post_process_object_detection(
            outputs=outputs,
            target_sizes=target_sizes,
            threshold=SCORE_THRESHOLD
        )[0]

    objects = []

    for score, label, box in zip(
        results["scores"],
        results["labels"],
        results["boxes"]
    ):
        name = prompts[label.item()]
        confidence = float(score.item())

        x1, y1, x2, y2 = [float(v) for v in box.tolist()]

        x1 = max(0, min(x1, width - 1))
        y1 = max(0, min(y1, height - 1))
        x2 = max(0, min(x2, width - 1))
        y2 = max(0, min(y2, height - 1))

        objects.append({
            "name": name,
            "confidence": round(confidence, 3),
            "bbox": [
                round(x1, 2),
                round(y1, 2),
                round(x2, 2),
                round(y2, 2)
            ],
            "center_pixel": [
                round((x1 + x2) / 2, 2),
                round((y1 + y2) / 2, 2)
            ],
            "source": "open_vocab"
        })

    output = {
        "image_width": width,
        "image_height": height,
        "objects": objects,
        "prompts": prompts
    }

    OUTPUT_PATH.parent.mkdir(parents=True, exist_ok=True)

    OUTPUT_PATH.write_text(
        json.dumps(output, indent=2, ensure_ascii=False),
        encoding="utf-8"
    )

    print(json.dumps(output, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()