# 目前版本：人為條件限制 Rules 短版

## 一句話總結

目前系統不是讓 LLM 直接控制機械手臂，而是讓 LLM 在固定規則內產生「圖案」與「高階動作」。真正執行前，會經過 C# server 與 Unity 兩層驗證；不符合規則的輸出不會送到實體 UR3e。

## 整體安全原則

- LLM 不可輸出任意 URScript、座標、joint angle、速度、加速度或 I/O 指令。
- LLM 只能使用系統開放的 high-level robot functions。
- 每一層都會做 deterministic check，不只依賴 LLM 自己遵守 prompt。
- 驗證失敗時，系統會要求重新規劃、retry、replan 或直接 abort。
- Unity 端仍保留獨立硬體安全檢查，即使關閉實驗用 bitmap 驗證也不會關掉。

## 圖案生成限制

- LLM 產生的 bitmap 必須是合法矩陣：每列等長、只能包含 `0` / `1`。
- 圖案大小不能超出目前工作區能擺放的範圍。
- 圖案需要的積木數不能超過現場可用庫存。
- 不可行的圖案應回傳不可行，不可硬湊。
- 一般模式下會用 OpenAI / Gemini 雙模型互審。
- 關閉 pattern 審查時，仍保留本地格式與庫存檢查。

## 工作區與供料限制

- 工作區分成 supply zone 與 target zone。
- 一般排圖案時，只能從 supply zone 取料。
- 已完成的 target 區積木不會被當成供料重複使用。
- source 必須符合目標需要的顏色與形狀。
- source / target 都必須落在 UR3e 安全可達範圍內。
- target 採遠端優先排序，減少後續手臂跨過已完成區域。
- recovery mode 才允許從非供料區回收掉落或失敗的積木。

## Motion Planner 限制

- LLM motion planner 只能組合固定 high-level functions：
  - `move_above`
  - `descend`
  - `grasp`
  - `release`
  - `lift`
  - `wait`
  - `go_home`
- function 的 location 只能是 `source` 或 `target`。
- 每一步 motion plan 有最大長度限制。
- 高度與等待時間都有上下限。
- `go_home` 只在明確需要時使用；batch 執行時預設不每步回 Home。

## 動作順序限制

motion plan 必須遵守固定 pick-and-place 流程：

1. 從上方接近 source。
2. 垂直下降。
3. 夾取。
4. 垂直抬升。
5. 在安全高度移動到 target。
6. 垂直下降。
7. 釋放。
8. 垂直抬升離開 target。

系統禁止：

- 工具下降後做水平移動。
- 還沒夾取就移動到 target。
- 還拿著物件就結束動作。
- 放置後沒有抬升就結束。
- target 已被佔用時直接硬放；只有合法 stack 才允許。

## Unity / UR3e 硬體安全限制

- Unity 執行前會先掃整個 batch。
- 任一步如果不可達、接近奇點、joint transition 不安全或可能碰撞，就 abort。
- 手臂底座附近有排除區。
- source / target 都有最大伸展範圍限制。
- 若路徑可能穿過底座排除區，Unity 會使用 detour path。
- 執行中如果 UR 進入 protective stop / safety stop，batch 會停止並等待人工排除。
- safety recovery 成功後，才允許做收尾或回 Home。

## 驗證開關的影響範圍

- `Unity驗證：開`：模擬結束後 bitmap 比對不通過，就不送實體手臂。
- `Unity驗證：關`：bitmap 比對只記錄，不阻擋實體執行。
- 這個開關只影響「模擬結束 bitmap 比對」。
- 它不會關閉 motion plan validation、pre-flight kinematics、碰撞 / 奇點檢查、硬體安全範圍或 safety stop handling。

## 結果驗證與錯誤處理

- 每一步做完後會檢查 source 是否消失、target 是否出現正確物件。
- target 上的物件需要符合顏色、形狀與位置誤差要求。
- 若沒夾到或中途掉落，通常 retry。
- 若顏色或形狀錯誤，通常 abort。
- 若場景變化不明，會 replan。
- stack 場景會額外用高度、位置、遮蔽狀況判斷是否成功。

## 目前版本的設計重點

- LLM 只負責產生「意圖層」與「高階動作層」。
- 座標、安全半徑、可達性、碰撞、奇點與硬體停止都由程式端把關。
- 實驗組 / 對照組只是在比較 bitmap 模擬驗證是否阻擋實體執行，不是比較有沒有硬體安全保護。
- 人為 rule 的主要功能是把 LLM 的自由度限制在可驗證、可回復、可安全執行的範圍內。
