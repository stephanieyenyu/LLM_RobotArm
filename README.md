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
