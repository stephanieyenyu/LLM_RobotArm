5/23 markdown
# Part A：YOLO 物件偵測與 QRCode 定位點輸出

Part A 的目標是讀取一張場景圖片，偵測其中的物件與 QRCode 定位點，並輸出 JSON 檔案給下一階段的座標轉換模組使用。

目前系統會讀取：

```text
csharp_server/images/test_scene.jpg
````

並輸出：

```text
csharp_server/outputs/detection_result.json
csharp_server/outputs/visual_result.jpg
```

---

## 目前功能

目前版本已完成以下功能：

1. 讀取 `images/test_scene.jpg`
2. 偵測 QRCode 定位點 `QR1`、`QR2`、`QR3`
3. 使用 YOLO ONNX 模型偵測常見物件
4. 輸出偵測結果到 `outputs/detection_result.json`
5. 輸出視覺化檢查圖到 `outputs/visual_result.jpg`

`detection_result.json` 會給 Part B 使用，Part B 可以從中取得 QRCode 和物件的影像座標。

`visual_result.jpg` 是除錯用圖片，用來確認 QRCode 和物件框是否正確畫出來。

---

## 測試圖片要求

測試圖片必須放在：

```text
csharp_server/images/test_scene.jpg
```

圖片中需要包含：

* `QR1`
* `QR2`
* `QR3`
* 至少一個 YOLO 可辨識的常見物件，例如 cup、bottle、book、cell phone、laptop、mouse、keyboard

QRCode 需要形成三角形，不能排成一直線。建議擺放方式如下：

```text
QR3

QR1                 QR2
```

目前設定中，建議：

* `QR1` 放左下
* `QR2` 放右下
* `QR3` 放左上

這樣 Part B 可以用三個 QRCode 建立工作平面與座標方向。

---

## 輸出格式

程式會輸出以下 JSON 格式：

```json
{
  "image_width": 1280,
  "image_height": 720,
  "objects": [
    {
      "name": "cup",
      "confidence": 0.823,
      "bbox": [779.42, 34.17, 1081.2, 328.82],
      "center_pixel": [930.31, 181.5],
      "source": "yolo_coco"
    }
  ],
  "qrcodes": [
    {
      "id": "QR1",
      "center_pixel": [310.5, 503.33],
      "corners": [[264, 596], [264, 457], [403.5, 457]]
    },
    {
      "id": "QR2",
      "center_pixel": [908.83, 503.67],
      "corners": [[862.5, 596.5], [862.5, 457.5], [1001.5, 457]]
    },
    {
      "id": "QR3",
      "center_pixel": [308.67, 162.83],
      "corners": [[260, 260.5], [260, 114], [406, 114]]
    }
  ]
}
```

欄位說明：

```text
image_width      圖片寬度
image_height     圖片高度

objects          YOLO 偵測到的物件清單
name             物件名稱
confidence       模型信心分數
bbox             物件框座標，格式為 [x1, y1, x2, y2]
center_pixel     物件中心點影像座標
source           偵測來源，目前為 yolo_coco

qrcodes          偵測到的 QRCode 清單
id               QRCode 內容，例如 QR1、QR2、QR3
center_pixel     QRCode 中心點影像座標
corners          QRCode 角點座標
```

Part B 目前主要可以使用：

```text
qrcodes[].id
qrcodes[].center_pixel
objects[].name
objects[].center_pixel
objects[].bbox
```

---

## YOLO 模型限制

目前使用的模型是：

```text
models/yolo11n.onnx
```

這是以 COCO 類別為基礎的 YOLO 預訓練模型。

COCO 是常見物件資料集，所以目前模型可以辨識一些日常物件，例如：

* person
* bottle
* cup
* book
* cell phone
* laptop
* mouse
* keyboard
* chair

目前模型不能真正辨識任意自訂物件，例如：

* red cube
* blue cube
* custom metal part
* robot component
* unknown tool

注意：不能只修改 `yolo_detector.cs` 裡面的 `classNames` 來新增物件類別。

`classNames` 只是把模型輸出的 class ID 轉換成可讀名稱。模型本身沒有訓練過的物件，單純改名稱不會讓模型真的學會辨識。

如果後續需要辨識自訂物件，需要新增以下其中一種方法：

1. 訓練 custom YOLO model
2. 加入 open-vocabulary detection，例如 OWL-ViT 或 Grounding DINO

目前 Part A 第一版先完成穩定的 QRCode 定位點輸出與 COCO 常見物件偵測。

---

## 如何執行

從 repo 根目錄進入 `csharp_server`：

```powershell
cd csharp_server
```

還原套件：

```powershell
dotnet restore
```

執行程式：

```powershell
dotnet run
```

執行後會產生：

```text
outputs/detection_result.json
outputs/visual_result.jpg
```

如果 `outputs` 資料夾不存在，程式會自動建立。

---

## 測試方式

執行後請檢查：

```text
outputs/detection_result.json
```

確認 JSON 中有：

* 至少一個 object
* `QR1`
* `QR2`
* `QR3`

也要打開：

```text
outputs/visual_result.jpg
```

確認圖片上有：

* QRCode 標記
* 物件綠色框
* 物件名稱，例如 cup

---

## 目前完成狀態

Part A 基本版已完成。

目前版本可以穩定輸出 QRCode 定位點與 YOLO 常見物件偵測結果，並已可交給 Part B 做座標轉換。

目前尚未支援任意自訂物件辨識。這部分會作為後續擴充。

````

更新後照這樣 commit：

```powershell
cd C:\Users\steph\source\repos\stephanieyenyu\LLM_RobotArm

git add csharp_server/README.md
git commit -m "Add Chinese README for Part A detection pipeline"
git push
````

如果你還沒有加 README 檔，就在 Visual Studio 右鍵 `csharp_server`，新增 `README.md`，再貼上這份。
