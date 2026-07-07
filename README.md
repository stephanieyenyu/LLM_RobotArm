# LLM_RobotArm

以中文自然語言指令控制 UR3e 機械手臂的框架。RealSense D435i 即時偵測工作台物件 → OpenAI gpt-5 解析指令 → Unity 送 URScript 到手臂。

## 系統流程

```
Unity UI（輸入指令）
   ↓  StreamingAssets/user_input.txt
csharp_server (dotnet)
   ↓  HTTP GET localhost:5000/scene
perception_server (Python + Flask)
   ├─ RealSense 常駐串流
   ├─ YOLO11n（COCO 物件） + HSV 立方體 + ArUco QR
   └─ 每 200ms 更新場景，回傳 3D 世界座標
   ↓
LLM Planner（gpt-5，四種 action：pick_and_place / move_relative / place_relative / error）
   ↓  StreamingAssets/robot_plan.json
Unity JsonExecutor
   ↓  TCP 30002 URScript
UR3e
```

## 檔案總覽

**csharp_server/**
- `perception_server.py` — RealSense 常駐 + YOLO + HSV + QR 偵測 + Part B 3D 座標 + Flask HTTP
- `Program.cs` — 監聽 user_input.txt、拉 HTTP 場景、呼叫 LLM、寫 robot_plan.json
- `llm_planner.cs` — gpt-5 呼叫、JSON schema、error 處理
- `RobotPlan.cs` — plan / SceneObject 資料類別
- `models/pliers.pt`、`yolo11n.pt` — YOLO 權重
- `QRcode/aruco_1~4.png` — 可列印定位碼

**unity_project/Assets/Scripts/**
- `UIManager.cs` — 指令輸入 UI、監看計畫更新
- `JsonExecutor.cs` — 展開動作序列、送 URScript、任務後回 home
- `URPackageListener.cs` — UR TCP client（port 30002）
- `URUtil.cs`、`Util.cs` — 封包型別工具

## 前置

- .NET SDK 8+
- Python 3.10+（用 `csharp_server/yolo11_env` 這個 venv）
- Unity 2022.3 LTS
- Intel RealSense D435i（USB 3 直接接筆電）
- `setx OPENAI_API_KEY "sk-你的-key"` 後重開 PowerShell
- UR3e 或 URSim（Teach Pendant 切 Remote Control、TCP Z offset 設 0.170、速度滑桿 100%）
- 工作台貼四張 ArUco（QR1 左下、QR2 右下、QR3 左上、QR4 右上）

## 每次執行

**Terminal 1**（感知）：
```powershell
cd csharp_server
yolo11_env\Scripts\python.exe perception_server.py
```

**Terminal 2**（LLM planner）：
```powershell
cd csharp_server
dotnet run
```

**Unity**：Hub 開 `unity_project` → Play → Executor 的 `Ur IP` 填 UR3e IP。

**Debug**：瀏覽器 `http://localhost:5000/debug/live` 看即時偵測畫面。

## 指令範例

- 「把黑色方塊放到黃色方塊上面」→ `pick_and_place` 疊放
- 「把杯子往前移 5 公分」→ `move_relative`
- 「把手機放到杯子左邊 15 公分」→ `place_relative`
- 「把飛機推倒」→ `error`（場景沒有飛機）

## 支援的物件

YOLO11n COCO 白名單：cup、cell phone、bottle、book、mouse、keyboard、laptop
HSV：5cm 黃色立方體、5cm 黑色立方體
QR：QR1-4（ArUco Dict4X4_50）

## 座標校準

`unity_project/Assets/Scripts/JsonExecutor.cs` 頂部三個常數：
```csharp
QR1_X, QR1_Y, QR1_Z   // Teach Pendant 手動 jog TCP 到 QR1 上方 5cm 讀值，Z 減 0.05 填入
Z_CORRECTION = 0.02f  // 補償 depth 系統性偏低
SAFE_Z_OFFSET = 0.08f // 抓取前後在物件上方留 8cm 安全空間
```
換場地或重貼 QRCode 一定要重新量測。

## 常見問題

- **「無法連線 perception_server」** → Terminal 1 沒起或還在載入 model
- **「場景中沒有帶有效座標的物件」** → QR1-3 沒都在鏡頭裡
- **等待 robot_plan.json 逾時（120 秒）** → OpenAI API 慢
- **手臂完全不動** → Teach Pendant 沒切 Remote Control、速度滑桿在 0、或 IP 錯

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
