# LLM_RobotArm

以自然語言（中文）指令控制 UR3e 機械手臂完成物件操作的框架。

系統將完整流程切成四段（Sense → Recognize+Plan → Execute），對應 kickoff 中的 Framework 項目：

- **Part A（Sense）**：C# / OpenCvSharp — Webcam 拍照，偵測 ArUco 定位碼與場景物件
- **Part B（Coordinate mapping）**：Python — 用 solvePnP 建工作平面座標系，把物件像素轉成 3D 座標
- **Part C（Recognize + Plan）**：C# + OpenAI — LLM 把使用者指令解析成機械手臂任務計畫
- **Part D（Execute）**：Unity — 讀取任務計畫，展開成動作序列，透過 TCP 送 URScript 給 URSim/UR3e

---

## 系統架構

```
┌──────────────────────────────────────────────────────────────────────┐
│  csharp_server（.NET 8 Console App，主控端）                          │
│                                                                       │
│   Part A                     Part B                    Part C         │
│   ────────                   ────────                  ────────       │
│   Webcam ─┐                                                          │
│           ├─► ArUco 偵測 ──►                                          │
│   test_   │                  solvePnP（Python）───►  LlmPlanner       │
│   scene   ├─► OwlViT 偵測 ►                          （OpenAI）       │
│           │   (YOLO fallback)  workspace frame ──►                   │
│                                                       ▼               │
│                                             robot_plan.json          │
└──────────────────────────────────────────────────────────────────────┘
                    ▲                                    │
                    │ user_input.txt                    │ robot_plan.json
                    │                                    ▼
┌──────────────────────────────────────────────────────────────────────┐
│  unity_project（Unity 2022.3 LTS，遙控端）                            │
│                                                                       │
│   UIManager ──► JsonExecutor ──► URPackageListener ──► TCP 30002     │
│   (輸入指令)     (展開動作序列)    (URScript 送出)                     │
└──────────────────────────────────────────────────────────────────────┘
                                                        │
                                                        ▼
                                              URSim / 真實 UR3e
```

---

## 目錄結構

```
LLM_RobotArm/
├── csharp_server/                      Part A + B + C
│   ├── Program.cs                      主流程排程 A → B → C
│   ├── PartAExporter.cs                Part A 排程
│   ├── WebcamCapture.cs                webcam 拍照
│   ├── QrCodeDetector.cs               ArUco 偵測（Dict4X4_50）
│   ├── OpenVocabDetector.cs            spawn Python OwlViT
│   ├── yolo_detector.cs                YOLO fallback（COCO 80 類）
│   ├── DetectionModels.cs              偵測結果 POCO
│   ├── coordinate_mapper.cs            debug 用 2D homography
│   ├── coordinate_mapper_3d.py         Part B 主體（solvePnP）
│   ├── llm_planner.cs                  Part C，呼叫 OpenAI gpt-5
│   ├── RobotPlan.cs                    任務計畫 POCO
│   ├── open_vocab/
│   │   ├── detect_open_vocab.py        OwlViT 推論腳本
│   │   └── prompts.txt                 OwlViT 候選類別
│   ├── open_vocab_env/                 Python venv（本機建立，git 不追）
│   ├── models/yolo11n.onnx             YOLO 權重
│   ├── images/
│   │   ├── test_scene.jpg              執行時的場景圖（webcam 覆寫）
│   │   └── test_scene_nocam.jpg        無 webcam 環境下的測試備援
│   └── outputs/                        本地備份輸出（gitignored）
│
├── unity_project/                      Part D
│   └── Assets/
│       ├── Scripts/
│       │   ├── UIManager.cs            指令輸入 UI、監看計畫檔更新
│       │   ├── JsonExecutor.cs         讀計畫、展開動作、送 URScript
│       │   ├── URPackageListener.cs    UR TCP client（30002 主要）
│       │   ├── URUtil.cs               UR 封包 struct 轉換
│       │   └── Util.cs                 big-endian 網路型別
│       ├── Scenes/MainScene.unity
│       └── StreamingAssets/
│           ├── user_input.txt          Unity 寫入、csharp_server 讀取
│           └── robot_plan.json         csharp_server 寫入、Unity 讀取
│
├── sample_json/                        Part A/B 中繼 JSON（gitignored）
├── docs/                               系統架構、週報、Part C 說明
├── requirements.txt                    Python 相依（給 Part B / OwlViT）
└── README.md
```

---

## 環境需求

| 項目 | 版本 |
|---|---|
| .NET SDK | 8.0+ |
| Python | 3.10+（測試過 3.13） |
| Unity | 2022.3 LTS（測試過 2022.3.62f3）|
| OpenAI API Key | 需可用 `gpt-5` |
| VirtualBox + URSim | VIRTUAL-5.9.4.1031232，Bridged Adapter 網路 |
| Webcam（可選） | 若無可改讀 `images/test_scene_nocam.jpg` |
| GPU（可選） | NVIDIA + CUDA，用於加速 OwlViT |

---

## 首次安裝

### 1. Clone repo

```powershell
git clone https://github.com/stephanieyenyu/LLM_RobotArm.git
cd LLM_RobotArm
```

### 2. 設定 OpenAI API Key

```powershell
setx OPENAI_API_KEY "sk-你的-key"
```
設定後**關掉 PowerShell 再重開一次**才會生效。

### 3. 建立 Part B / OwlViT 用的 Python venv

```powershell
cd csharp_server
python -m venv open_vocab_env
```

**NVIDIA GPU 版**（推薦，OwlViT 推論快很多）：
```powershell
open_vocab_env\Scripts\python.exe -m pip install --upgrade pip
open_vocab_env\Scripts\pip.exe install torch --index-url https://download.pytorch.org/whl/cu124
open_vocab_env\Scripts\pip.exe install transformers pillow numpy opencv-python
```

**純 CPU 版**：
```powershell
open_vocab_env\Scripts\pip.exe install torch transformers pillow numpy opencv-python
```

第一次執行 OwlViT 會下載模型權重（~600 MB），要等一下。

### 4. 準備 URSim / UR3e

1. VirtualBox 啟動 URSim 虛擬機（網路設 Bridged Adapter）
2. URSim 中 Initialize Robot → START，左下角要是 **Normal**
3. 右上角 About 記下 IP，例如 `192.168.50.204`
4. 開 Unity 專案 → 選中 Hierarchy 的 `Executor` → Inspector 中 `Ur IP` 填入該 IP

### 5. 準備場景

- 桌面貼四個 ArUco 定位碼（可用 `csharp_server/aruco_1.png ~ aruco_4.png` 列印）
- 依 `QR1`（左下）、`QR2`（右下）、`QR3`（左上）、`QR4`（右上）擺，形成一個工作平面矩形
- 中間放一個物件（OwlViT prompts.txt 認得的類別）

---

## 執行流程

**每次跑要開兩個東西：csharp_server 一個、Unity 一個。**

### Step 1. 啟動 csharp_server（開一個 PowerShell）

```powershell
cd csharp_server
dotnet run
```

啟動後會依序：
1. 拍照或讀 `images/test_scene.jpg`
2. 偵測 QRCode 與物件（優先 OwlViT，失敗才 YOLO）
3. 寫 `sample_json/detected_objects.json`
4. 呼叫 Python 算 3D 座標，寫 `sample_json/objects_world.json`
5. 進入監聽狀態：
   ```
   === LLM Planner 已啟動 ===
   監聽：...\unity_project\Assets\StreamingAssets\user_input.txt
   輸出：...\unity_project\Assets\StreamingAssets\robot_plan.json
   等待 Unity 輸入指令...
   ```

### Step 2. 啟動 Unity（Unity Hub 開 `unity_project/` → Play）

Unity 起來後，`Executor` 會自動 TCP 連 URSim。畫面下方會有輸入框與「執行」按鈕。

### Step 3. 下指令

在輸入框打自然語言指令，例如：

- 「把杯子往前移動 5 公分」→ `move_relative`
- 「把杯子放到書本上面」→ `pick_and_place`
- 「把工具向左移動 10 公分」→ 中文 → OwlViT 偵測到的 `tool`

按執行按鈕，接下來的流程是：

```
UIManager 寫 user_input.txt
      ↓
csharp_server 讀到，呼叫 LLM，寫 robot_plan.json
      ↓
UIManager 偵測到 mtime 更新，呼叫 JsonExecutor.LoadAndExecute()
      ↓
JsonExecutor 依動作類型展開成 8 步：
  上方 → 位置 → grasp → 抬起 → 目標上方 → 目標位置 → release → 抬起
      ↓
URPackageListener 每步送 URScript 到 TCP 30002：
  move_to → movej(get_inverse_kin(p[x,y,z,0,3.14,0], ...))
  grasp   → set_standard_digital_out(4, True)
  release → set_standard_digital_out(4, False)
      ↓
URSim / UR3e 執行
```

Unity Console 會顯示每步的 SEND 內容；URSim I/O 頁面可看到 digital_out 4 隨 grasp/release 亮滅。

---

## 檔案流

跨程式溝通全部走檔案：

| 檔案 | 誰寫 | 誰讀 | 用途 |
|---|---|---|---|
| `csharp_server/images/test_scene.jpg` | WebcamCapture | Part A | 每次執行的場景圖 |
| `sample_json/detected_objects.json` | Part A | Part B | 偵測結果 |
| `sample_json/objects_world.json` | Part B | Part C | 物件 3D 座標 |
| `StreamingAssets/user_input.txt` | Unity UIManager | csharp_server 輪詢 | 使用者中文指令 |
| `StreamingAssets/robot_plan.json` | csharp_server | Unity JsonExecutor | LLM 產出的任務計畫 |

Unity ↔ UR3e 之間走 TCP：port 30002（Primary，送 URScript）、30003（Realtime,收狀態封包）。

---

## 座標系換算

Part B 輸出的 x/y/z 是「QR 工作平面局部座標，公尺」：
- `x` = QR1 → QR2 方向（水平）
- `y` = QR1 → QR3 方向(縱深)
- `z` = 工作平面法向（高度）

`JsonExecutor.cs` 中的 `QR1_X / Y / Z` 常數是 QR1 在 UR3 基座座標系的量測值（用 UR3 Teach Pendant 手動移到 QR1 正上方 5 cm 讀出來的 TCP 座標）。運算方式：

```
UR3_base = QR1_offset + QR_local
```

換場地或重貼 QRCode 一定要重新量測 `QR1_X / Y / Z`，否則機械手臂會定位錯誤。

---

## 支援的動作

Part C 目前定義兩種 action：

| action | LLM 輸出欄位 | 由 Unity 展開的動作序列 |
|---|---|---|
| `pick_and_place` | object, target | 上方 → 物件位置 → grasp → 抬 → 目標上方 → 目標位置 → release → 抬 |
| `move_relative` | object, direction, distance_cm | 同上，但 target 座標由 C# 從 direction + distance_cm 計算 |

`direction` 只能是 `left / right / forward / backward / up / down`。單位換算以公分為主，會處理中文數字（例如「五」「兩公分半」）。

---

## 常見問題

**csharp_server 顯示「監聽」的路徑跟 Unity 印的 StreamingAssetsPath 不一樣**
你有兩份 repo 副本，`dotnet run` 跑到不是 Unity 開的那份。確認 `cd` 到 Unity 專案同一層的 `csharp_server` 再跑。

**Part A 沒偵測到物件（`objects: []`）**
- 場景裡的物件 OwlViT prompts.txt 沒列到 → 加進 `csharp_server/open_vocab/prompts.txt`
- OwlViT 環境沒建 → 檢查 `open_vocab_env/Scripts/python.exe` 是否存在
- confidence 太低（< 0.08）→ 減少 prompts.txt 裡不相關的類別

**Unity Console 出現「找不到 JSON」**
csharp_server 沒把 `robot_plan.json` 寫到 Unity 讀的位置。確認 `unity_project/Assets/Scripts/JsonExecutor.cs` 的 `LoadAndExecute()` 用的是 `Path.Combine(Application.streamingAssetsPath, jsonFileName)`。

**URSim 進入 Protective Stop**
`movel` 目標超出工作範圍或近奇異點。改讓 `robot_plan.json` 的 `move_to` 帶 `joints` 走 `movej`，或縮小 `SAFE_Z_OFFSET`。

**UR3 連線失敗**
- URSim 沒起 / 沒 START
- IP 錯（Unity Inspector 的 `Ur IP` 要對）
- 主機與虛擬機不在同網段（VirtualBox 網路要 Bridged Adapter，不是 NAT）

**OwlViT 太慢（每次 Part A 都要重載 model）**
目前架構每次 spawn 新 Python process。未來可改成 Python 常駐 server + HTTP/named pipe 呼叫。

---

## 已完成 / 尚未完成

**已完成**
- Part A：ArUco + OwlViT（YOLO fallback）偵測
- Part B：solvePnP 三點建工作平面，物件像素轉 3D
- Part C：OpenAI gpt-5 中文指令解析，支援 pick_and_place 與 move_relative
- Part D：URScript 產生與 TCP 送出、pick_and_place 8 步展開

**尚未完成**
- 與真實 UR3e 的長時間穩定測試
- URScript 執行失敗的錯誤回報回 Unity
- 多任務排程與中斷指令
- UR3e 連接夾治具
- Unity 場景中的目標物件視覺化

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

# Part D：Unity Execute 與 UR3e 指令發送

Part D 的目標是讀取 Part C 產生的 `robot_plan.json`，將動作序列轉換成 URScript 指令，並透過 TCP 連線發送到 URSim（或真實 UR3e），讓機械手臂依照規劃執行動作。

目前系統會讀取：

```text
unity_project/Assets/StreamingAssets/robot_plan.json
```

並透過 TCP 將 URScript 指令發送到：

```text
URSim 虛擬機 IP（例如 192.168.31.225）
```

---

## 目前功能

目前版本已完成以下功能：

1. 讀取 `Assets/StreamingAssets/robot_plan.json`
2. 解析 `action_sequence` 中的每個動作
3. 將 `move_to` 轉換成 URScript 的 `movel` 或 `movej`
4. 將 `grasp` 與 `release` 轉換成數位輸出指令（digital_out 4）
5. 透過 `URPackageListener` 用 TCP 將 URScript 字串發送到 URSim
6. 提供 Unity UI 對話框，使用者可輸入指令後按執行按鈕觸發
7. 即時 Console Log 顯示載入任務、執行進度、發送的 URScript 內容

`robot_plan.json` 由 Part C 產生，Part D 只負責讀取與轉送指令，不做逆運動學或路徑規劃，這些都由 URSim 內建處理。

---

## 系統環境需求

* Unity 2022.3 LTS
* Oracle VirtualBox
* URSim VIRTUAL-5.9.4.1031232（虛擬機）
* 虛擬機網路設定為 橋接介面卡（Bridged Adapter）

虛擬機與主機需在同一網域，URSim 啟動後請至 About 確認 IP（例如 `192.168.31.225`），並在 Unity 的 `JsonExecutor` 元件 Inspector 中填入該 IP。

---

## 場景物件

Unity 場景僅保留以下物件：

```text
Main Camera
Directional Light
Executor       掛載 JsonExecutor 腳本
UIDocument     掛載 UI Document 與 UIManager 腳本
```

機械手臂模型不在 Unity 場景中，手臂視覺呈現於 URSim 視窗。Unity 在此架構中僅作為遙控端。

---

## 輸入格式

Part C 提供的 `robot_plan.json` 格式如下：

```json
{
  "task": "move_object",
  "target_object": "cup",
  "action_sequence": [
    {
      "action": "move_to",
      "position": { "x": -0.12, "y": 0.20, "z": 0.18 }
    },
    {
      "action": "move_to",
      "joints": {
        "pan": 0,
        "lift": -90,
        "elbow": 0,
        "wrist1": -90,
        "wrist2": 0,
        "wrist3": 0
      }
    },
    { "action": "grasp" },
    { "action": "release" }
  ]
}
```

欄位說明：

```text
task                任務描述
target_object       目標物件名稱
action_sequence     依序執行的動作清單

action              動作類型，目前支援 move_to / grasp / release
position            move_to 的 TCP 目標座標，單位是公尺
position.x          機械手臂座標系 X
position.y          機械手臂座標系 Y
position.z          機械手臂座標系 Z
joints              move_to 改用關節角度時的角度設定，單位是度
joints.pan          shoulder_pan 角度
joints.lift         shoulder_lift 角度
joints.elbow        elbow 角度
joints.wrist1       wrist_1 角度
joints.wrist2       wrist_2 角度
joints.wrist3       wrist_3 角度
```

`move_to` 同一動作中 `position` 與 `joints` 二擇一即可。`joints` 全為 0 時視為未設定，會改走 `position`。

---

## 轉換對照

Part D 將 `action_sequence` 轉成下列 URScript 指令：

```text
move_to + position
    movel(p[x, y, z, 3.14, 0, 0], a=1.2, v=0.5)

move_to + joints
    movej([pan, lift, elbow, wrist1, wrist2, wrist3], a=1.2, v=1.05)
    （所有角度會由度轉成弧度）

grasp
    set_standard_digital_out(4, True)

release
    set_standard_digital_out(4, False)
```

URScript 參考來源：Universal Robots Script Manual e-Series SW 5.11

---

## URScript 限制

* `movel` 的 TCP 座標需為機械手臂座標系，單位是公尺，姿態以軸角弧度表示
* `movel` 在工作範圍邊界或奇異點會觸發 Protective Stop
* 接近奇異點時建議改用 `movej`（關節角度）
* digital_out 4 對應夾爪訊號，需在 URSim Installation 中將 TCP Z 設為 170mm 才會對應到正確的夾爪行為

---

## 如何執行

從 Unity Hub 開啟專案：

```text
LLM_RobotArm/unity_project
```

開啟前請確認：

1. Oracle VirtualBox 已啟動 URSim 虛擬機
2. URSim 已 Initialize Robot 並按下 START，左下角狀態顯示 Normal
3. URSim 右上角 About 確認 IP

進入 Unity 後：

1. 點選 `Executor` 物件
2. Inspector 中將 `JsonExecutor` 的 `Ur IP` 改成 URSim 顯示的 IP
3. 按 Play
4. 在畫面下方對話框輸入指令，按執行按鈕（或鍵盤空白鍵）

執行後 Unity Console 會顯示：

```text
載入任務：move_object，目標：cup
=== 開始任務：move_object ===
[1/4] move_to
SEND: movel(p[-0.1200, 0.2000, 0.1800, 3.14, 0, 0], a=1.2, v=0.5)
[2/4] grasp
SEND: set_standard_digital_out(4, True)
...
=== 任務完成 ===
```

URSim 視窗會看到手臂依照指令移動，I/O 頁面可以看到 digital_out 4 燈號隨夾爪指令亮滅。

---

## 測試方式

執行後請檢查：

URSim 視窗

* 手臂依序移動到 JSON 指定的位置
* I/O 頁面中 digital_out 4 隨 grasp 與 release 改變亮滅
* 左下角狀態維持 Normal，未進入 Protective Stop

Unity Console

* 顯示每個動作的 SEND 內容
* 顯示「任務完成」字樣

如果出現 Protective Stop，通常是目標位置超出工作範圍或落在奇異點，請改用 `movej` 並調整關節角度。

---

## 目前完成狀態

Part D 基本版已完成。

目前版本可以穩定讀取 `robot_plan.json`，將動作序列轉成 URScript 並透過 TCP 發送到 URSim，手臂可依照 Part C 規劃的動作執行移動、夾取與放下。

尚未支援：

1. 與真實 UR3e 的同步連線測試（預計 7/3 整合）
2. URScript 執行失敗時的錯誤回報機制
3. 多任務排程與中斷指令
4. URSim 中加入工作物件視覺化（如目標方塊）

這些會作為後續擴充。
