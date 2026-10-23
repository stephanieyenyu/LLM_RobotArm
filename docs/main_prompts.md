# main 版本 LLM prompt 整理

依 2026-10-22 的程式整理：commit `522b5e7`，加上本機尚未 commit 的兩項修改（安全檢查 X 上限 0.805、計時）。這兩項都不影響 prompt。
每個 LLM 呼叫實際送出的 system / user 文字，都會存在 `csharp_server/outputs/experiments/<任務>/` 底下：
- 設計階段在 `design/`；
- 每一輪在 `attempt_XX/`，檔名是 `<階段>.system.txt`、`<階段>.user.txt`，回覆是 `<階段>.txt`。

---

## 0. 總覽

| 順序 | 階段 | 何時呼叫 | 模型 | 程式位置 | 紀錄檔名 |
|---|---|---|---|---|---|
| A1 | 平面／立體判斷 | 每個任務一次 | gpt-5 | `FigureDimensionJudge.cs` | `design/01_dimension.*` |
| A2 | 平面圖形設計（雙 LLM） | A1 判斷為 2d | gpt-5 ＋ Gemini | `PatternDesigner.cs` | `design/*` |
| A3 | 立體圖形設計（雙 LLM） | A1 判斷為 3d | gpt-5 ＋ Gemini | `SpatialPatternDesigner.cs` | `design/*` |
| B1 | 拆解 | 每一輪 | gpt-5 | `ExperimentLlm.Decompose` | `attempt_XX/decomposition.*` |
| B2 | 規劃 | 每一輪 | gpt-5 | `ExperimentLlm.Plan` | `attempt_XX/operation_plan.*` |
| B3 | 轉譯＋執行前檢查 | 每一輪 | **不是 LLM** | `NaturalLanguagePlanAdapter.cs`、`MotionPlanValidator.cs` | `attempt_XX/translation.*` |
| B4 | 模擬畫面判定 | 模擬重疊率／物理檢查通過後 | gpt-5 | `ExperimentLlm.Validate` | 2D：`attempt_XX/unity_validation.*`；3D：`attempt_XX/isaac_sim/isaac_validation.*` |
| B5 | 反思 | 這一輪失敗、而且不是手臂執行中失敗 | gpt-5 | `ExperimentLlm.Reflect` | `attempt_XX/reflection.*` |

- 主控模型由環境變數 `ROBOT_MODEL` 決定，預設 `gpt-5`。
- Gemini 由 `GEMINI_MODEL` 決定，預設 `gemini-3.1-flash-lite`。
- 一個任務最多 10 輪；第 1 輪之後的每一輪，都會帶著之前反思累積的「暫定規則」。
- 相機只在任務開始時拍一次；之後每一輪都用同一張照片和同一份場景。

---

## A. 設計階段（每個任務一次）

開始前，程式先決定兩件事，都不經過 LLM：
- **顏色：** 指令有「黃／yellow」或「黑／black」就用那個顏色，否則用桌上比較多的顏色。
- **庫存：** 桌上那個顏色的所有 cube（和 domino）。main 沒有補貨區。

### A1. 平面／立體判斷

**system**
> 判斷使用者指令要在桌面上排出的東西是平面的還是立體的。2d：積木都直接放在桌面上、只有一層，從正上方看排出要求的樣子；3d：積木要往上疊、不只一層，排成立起來的結構。只依指令本身的意思判斷。只回傳符合 schema 的 JSON。

**user**
> 使用者指令：{指令}

**輸出：** JSON `{ "dimension": "2d" | "3d", "reason": "…" }`

### A2. 平面圖形設計（2d）

畫布最多 5 × 5。流程如下，最多 2 回合：
1. **生成：** OpenAI、Gemini 各自畫一張。
2. **互審：** 各審對方那一張，可以附上修改版。
3. **投票：** 通過互審的候選（含修改版）由兩邊匿名打分，加權 OpenAI 80%、Gemini 20%。
4. **只剩一張候選時：** 加權分數要達 0.50 才採用，否則進下一回合重畫。

這三段 prompt 都是 release 260917 的原文。

**A2-1 生成：system**
```
Convert the requested text, symbol, or shape into a binary matrix.
- Use at most 5 rows and 5 columns; every row has equal length.
- Each character is only 0 (empty) or 1 (occupied).
- Prioritize human visual recognizability over a standard font, fixed proportions, or conventional stroke placement.
- You may adjust stroke position, thickness, turns, and local filling to improve recognizability.
- Work independently. Do not assume or imitate another candidate.
- No glyph template, target-specific stroke rule, font, example bitmap, or confusion list is supplied by this application.
- Available pieces: {cube 數} cubes and {domino 數} dominoes. A cube covers one cell and a domino covers two orthogonally adjacent cells.
- If no sufficiently recognizable and realizable matrix fits, set feasible=false instead of forcing an answer.
- With feasible=false bitmap is empty; with feasible=true failure_reason is empty.
Return only JSON matching the schema.
```
**user**
```
Original user command: {指令}
Block color: {顏色}
（第 2 回合起多一行）Previous cross-review feedback: {對方的審查意見}
Generate a fresh candidate for the original command. Feedback is advisory and must not change the target.
```

**A2-2 互審：system**
```
Independently cross-review a binary bitmap made by another model.
Judge whether the actual matrix clearly resembles the original request to a human and obeys the matrix constraints.
Inspect the cells; do not trust candidate ID, author, or claimed label.
Do not use any application-provided target template, target-specific stroke rule, font, example, or confusion list; none is provided.
Reject uncertain, ambiguous, broken, or wrong-target candidates. Give concise structural problems and actionable corrections based only on this matrix.
If you can materially improve the submitted matrix, set has_revision=true and return the complete revised bitmap in revised_bitmap.
A revision must target the original request and must not merely describe edits in suggested_changes.
If no revision is needed or possible, set has_revision=false and revised_bitmap=[];
Return only JSON matching the schema.
```
**user**
```
Original user command: {指令}
Candidate bitmap:
{對方的 0/1 矩陣}
Deterministic format/inventory check: {valid 或程式檢查出的錯誤}
Review this candidate without seeing the other candidate.
```

**A2-3 投票：system**（2D、3D 共用）
```
Score anonymized binary matrices against the original request.
Judge each actual matrix independently by human visual recognizability and fidelity to the requested target.
Candidate order and identity carry no meaning. Do not infer authorship.
Do not use application-provided templates, target-specific rules, fonts, examples, or confusion lists; none is supplied.
Return exactly one score entry for every candidate index. Scores range from 0 to 1.
Return only JSON matching the schema.
```
**user**
```
Original user command: {指令}
Candidate 0:
{矩陣}
Candidate 1:
{矩陣}
```

### A3. 立體圖形設計（3d）

畫布固定 **1 排 × 3 欄 × 3 層**，只用 cube。回合數、投票加權、只剩一張要達 0.50 的規則都跟 A2 相同。

**A3-1 生成：system**
260917 原文共 16 行；最後一行是 2026-10-07 加的，說明矩陣的列和欄各對應哪個方向。
```
Design an upright, physically self-supporting 3D voxel rendering of the requested text or symbol.
Use a fixed, canonical viewing convention: the requested glyph is read from the front,
+Z is visually upward, the table is the bottom support edge, +X runs left-to-right,
and depth/Y runs front-to-back. Never rotate, mirror, turn sideways, or invert the
requested glyph merely to make it physically feasible.
Represent it as column_heights: an exactly 1 by 3 integer matrix.
Each value N means a contiguous vertical column of N cubes starting on the table; gaps and overhangs are impossible.
The available voxel volume is exactly 1 x 3 x 3; every height must be 0..3, and the pattern may use at most {cube 數} cubes total.
Decide feasibility first. A front-view pixel at height z can exist only when every voxel below it in the same column also exists. If that monotone, ground-supported silhouette cannot preserve the requested glyph, immediately return feasible=false; do not keep searching for a forced approximation.
Prioritize human recognizability when viewed from the front. Do not use a supplied font, template, target-specific rule, or hardcoded glyph; none is provided.
If the requested form cannot remain recognizable under continuous vertical support, size, height, and inventory constraints, return feasible=false rather than changing it into another form.
For feasible=true, view_direction must be front, up_axis must be +z,
support_edge must be bottom, rotation_deg must be 0, and mirrored must be false.
feasible=false requires an empty column_heights array and a clear failure_reason.
feasible=true requires an empty failure_reason.
Return only schema-valid JSON.
column_heights has exactly 1 row(s) along depth/Y; within each row, the 3 numbers are the column heights from left to right along +X.   ← 2026-10-07 加
```
**user**
```
Original command: {指令}
Block color: {顏色}
（第 2 回合起）Previous cross-review feedback: {對方的審查意見}
Generate a fresh candidate for the original command. Feedback is advisory and must not change the target.
```

**A3-2 正面方向互審：system**（260917 原文；沒有修改版這一步）
```
Independently review a voxel glyph in its fixed physical orientation.
Read the bitmap exactly as displayed: top row is highest +Z, bottom row
touches the table, columns run left-to-right, and the viewer looks from
the front. Do not rotate, mirror, transpose, invert, or reinterpret the
bitmap. Decide whether it clearly represents the exact text or symbol
requested by the user in that orientation. If it resembles the requested
glyph only after any rotation or mirroring, set accept=false and identify
that transformation. Do not substitute a different glyph merely because
it is physically supportable. Return only schema-valid JSON.
```
**user**
```
Original request: {指令}

Fixed front-view bitmap (1=voxel, 0=empty):
{正面圖，例如 100 / 100 / 111}
```

**A3-3 投票**
- **system：** 同 A2-3。
- **user：** 比 2D 多一行說明（2026-10-14 加，跟 rulebased 相同）：
```
Original user command: {指令}
Each candidate is the fixed front view of the stacked cube structure (1=voxel, 0=empty): the top row is the highest +Z layer, the bottom row touches the table, and columns run left-to-right.   ← 2026-10-14 加
Candidate 0:
{正面圖}
```

**設計結果怎麼往後傳：** 選出的圖存成目標 bitmap，交給 B1、B2、B5（見下方「目標圖形」）。
- 2D 是 0/1 矩陣；
- 3D 是俯視高度圖，例如 `311`，數字表示那格疊幾層。

設計失敗時，這個任務直接記為失敗，不進入規劃。

---

## B. 每一輪（最多 10 輪）

B1、B2、B5 用到的固定說明文字都整理在 **C 節**。下面用【名稱】代替那些文字。

### B1. 拆解（decomposition）

**system**
> 你是 UR3 機械手臂的任務控制者。根據使用者目標與目前觀測，自行規劃並完成任務。

第 2 輪起，後面再接一段：
> 本任務依失敗經驗累積的暫定規則如下，按時間由舊到新排列。不衝突的舊規則持續有效；若新規則根據較新證據明確修正或取代舊規則，以新規則優先：
> --- 暫定規則版本 1 ---
> {第 1 輪反思寫的內容}
> --- 暫定規則版本 2 ---
> …

**user**
```
目標：{指令}
目前場景：{場景 JSON，每個物件有 index、名稱、x/y/z、形狀、方向、歪斜角}
上次結果：{上一輪的系統紀錄；第 1 輪是「無前次結果。」}
環境：【座標慣例】【方向詞】【物件尺寸】【桌面與基座】【圖形驗收】【字母規範（2D）或正面判讀（3D）】【附圖說明（只在沒開 Isaac 的純模擬）】【目標圖形（有設計結果時）】
注意：index 只代表這一張目前場景清單的位置，每次重新觀測都可能重排，不能當作跨輪次的物件身分。請自行拆解本輪的子任務，以自然語言描述。
```
另附任務開始時的相機照片。

**輸出：** 自然語言的子任務說明，會整段交給 B2。

**要注意：** 拆解**看不到**執行介面、夾爪尺寸和計畫格式。它可能自己寫出介面不接受的寫法，例如把 `source`／`target` 寫成座標，B2 又會照抄。

### B2. 規劃（operation_plan）

**system：** 同 B1，含暫定規則。

**user**
```
目標：{指令}
子任務：{B1 的整段輸出}
目前場景：{場景 JSON}
上次結果：{上一輪的系統紀錄}
環境：【座標慣例】【方向詞】【物件尺寸】【桌面與基座】【圖形驗收】【字母規範（2D）或正面判讀（3D）】【附圖說明】場景 index 只適用本輪目前清單，重新觀測後可能重排。【目標圖形】
每一輪 operation plan 都必須在文字中完整列出每個 target 的實際 (x,y,z) 數值；不得只寫「沿用 P0~P4」、「維持既定目標點」或其他需要查舊輪上下文的代號。
執行介面提供 move_above(location,height_m)、descend(location)、grasp()、release()、lift(location,height_m)、wait(seconds)。location 可為 source 或 target；source 是本次操作所選的來源物件，target 是把來源物件放到的位置，target 的 Z 指來源物件放好後的頂面高度（與場景物件 Z 同一慣例）；height_m 是端點上方的距離。參數範圍：height_m 0.05～0.15 m，seconds 0.1～3。同一個操作內的函式依序執行，中途不會重新感知；每個操作開始前系統會重新觀測來源物件位置。【夾爪事實】介面不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條確定且可執行的動作路徑，執行失敗後由下一輪根據新觀測重規劃。整批動作完成後，執行器會在收尾路徑安全時回到 Ready/Home；若收尾路徑不安全，會留在最後的安全抬升位置，不會因此否定已完成的任務動作。規劃內不得自行加入 go_home。這只是設備能力說明，不規定任務拆解、動作順序或完成方式。
低階操作範例（僅示範介面語意，不是固定解法）：
若要將目前場景 index 0 的物件移到桌面位置 x=0.10、y=0.15、z=0.02：
source index 0 → target (0.10, 0.15, 0.02)
move_above(source, 0.12)
descend(source)
grasp()
lift(source, 0.12)
move_above(target, 0.12)
descend(target)
release()
lift(target, 0.12)
請根據實際目標與目前觀測，自行規劃操作。迭代執行期間無法向使用者追問或等待補充資料；本輪計畫只能使用提示中已有的目標、場景、結果與暫定規則。請以自然語言自行決定本輪操作與參數。為讓本地執行介面忠實辨識你的決定，在「執行路徑開始」與「執行路徑結束」之間，每組抓放先寫一行「source index N → target (x, y, z)」，下面緊接著逐行列出這一組要執行的函式呼叫，然後才寫下一組。
```
另附任務開始時的相機照片。

**輸出：** 自然語言計畫。其中「執行路徑開始」到「執行路徑結束」之間的內容會交給 B3 轉譯。

### B3. 轉譯與執行前檢查（不是 LLM）

- **轉譯（`NaturalLanguagePlanAdapter`）：** 照字面找出 `source index N → target (x, y, z)` 和函式呼叫，不替 LLM 做任何決定。
  - 遇到「放下後的下一個 `move_above(source, …)`」就切成新的一組抓放；
  - 每組最多 20 個函式。
- **轉譯失敗時的訊息：** 不寫上限數字（2026-10-07 起）。注意：如果位置寫成座標而不是 `source`／`target`，轉譯器切不出組，回報的會是「第 1 個步驟的函式數超過介面上限」。
- **執行前檢查（`MotionPlanValidator`）：**
  - 座標要在工作區內：X −0.025～0.805（本機未 commit 的修改；之前是 0.745）、Y −0.025～0.475；
  - 移動高度、和其他方塊的間距、搬運距離是否安全；
  - 計畫排出的圖形要跟目標 bitmap **完全相同**；
  - 失敗訊息只給座標，不給允許範圍。
- **之後：** 交給 Unity（2D 預覽）或 URSim＋Isaac（3D）模擬。Unity 另有手臂可達與碰撞檢查：取料離底座 0.20～0.47 m、放置 0.16～0.47 m。

### B4. 模擬畫面判定（驗證者）

**這一關在什麼時候跑：**
- **2D：** Unity 預覽的畫面重疊率 > 90% 之後。
- **3D：** Isaac 物理檢查和重疊率都通過之後。

驗證者是一個獨立的 LLM，**不帶暫定規則**。

**system**
> 你是獨立的結果驗證者。只根據原始目標、初始場景及目前實際觀測判斷，不提供操作解法。檢查整體目標與局部幾何完整度（包含直線、連接與堆疊）。若目標要求平移、對齊、放置到座標或距離，實際結果與目標值在 0.02 m（2 公分）以內的量測誤差可接受，不得只因 2 公分內的座標偏差判定失敗；超過 2 公分或方向明顯錯誤才視為幾何未達標。沒有足夠觀測證據或目標含糊時不可通過。第一行僅寫 PASS 或 FAIL，後續自然語言描述觀測問題與不確定性。

**user**
```
原始目標：{指令}
初始場景：{任務開始的場景 JSON}
目前場景：{模擬結束後的場景 JSON}
環境：【座標慣例】【方向詞】【物件尺寸】【圖形驗收】【點陣判讀】【正面判讀（只有 3D）】
{附圖說明：2D 用【Unity 畫面說明】，3D 用【Isaac 畫面說明】}
```
附圖：2D 是 Unity 預覽結束時主相機的畫面，3D 是 Isaac Sim 的畫面。

**輸出：** 第一行 `PASS` 才把同一批動作送到實體手臂（純模擬直接算完成）。`FAIL` 時，判定理由會寫進回饋，交給 B5。
目標 bitmap **不會**給驗證者，它只看原始指令和畫面。

### B5. 反思（reflection）

**什麼時候不跑：**
- 這一輪成功；
- 手臂執行到一半失敗：桌面已經變了，任務直接結束。

**system**
> 你是 UR3 機械手臂的任務控制者，負責分析這一輪的結果。輸出格式：第一行只寫 GIVE_UP 或 CONTINUE（GIVE_UP = 放棄這個任務，CONTINUE = 繼續下一輪）；第二行起寫什麼由你決定，這些內容會累積起來，交給之後每一輪的規劃與反思參考。

**user**
```
目標：{指令}
本輪操作：{B2 的整段計畫}
轉譯結果（內部執行資料）：{B3 的 JSON}
本輪結果：{系統紀錄的流程與失敗原因}
本輪最新場景：{場景 JSON}
環境事實：QR 座標為公尺，X/Y 在桌面上，Z 向上；場景物件的 Z 是物件頂面高度。【方向詞】【物件尺寸】【桌面與基座】【介面事實】【夾爪事實】【圖形驗收】【字母規範（2D）或正面判讀（3D）】【目標圖形】
既有暫定規則（由舊到新）：{之前累積的規則}
```

**輸出的用法：**
- 第一行 `GIVE_UP`：結束任務。
- 第一行 `CONTINUE`：第二行起的內容存成新一版「暫定規則」，加到 B1、B2、B5 的 system prompt 後面。

---

## C. 固定說明文字（`ExperimentLlm.cs` 裡的常數）

| 名稱 | 內容 |
|---|---|
| 【座標慣例】 | QR 座標為公尺，X/Y 在桌面上，Z 向上；物件 Z 為頂面高度。 |
| 【方向詞】 | QR 工作座標方向：+X=左、-X=右、+Y=後、-Y=前、+Z=上、-Z=下；方向詞一律依 QR 工作座標解讀，不依相機畫面方向。 |
| 【物件尺寸】 | cube 尺寸 0.025m，domino 為 0.05×0.025×0.025m。 |
| 【桌面與基座】 | 桌面工作區是 QR1～QR4 標記圍成的矩形 X=0.000..0.805 m、Y=0.000..0.371 m，相機在這個範圍內量得到物件位置。手臂基座在 QR (0.390, 0.363)。 |
| 【夾爪事實】 | 夾爪為平行雙指，每根手指厚約 0.015 m、寬約 0.015 m；張開時兩指內側間距只有約 0.035 m：只跨得住物件 0.025 m 寬的邊，跨不住 domino 的 0.05 m 長邊。夾 cube 時兩指落在 cube 的 ±X 兩側；夾 domino 時系統會轉動夾爪讓兩指跨住短邊、落在長邊兩側（horizontal domino 在 ±Y 兩側，vertical domino 在 ±X 兩側）。descend 夾取或放置時，兩指會下降到物件在閉合方向上的兩側：每根手指內側離物件表面只有約 0.005 m、外側離物件中心約 0.0325 m，垂直閉合方向只佔物件中心線兩側各約 0.0075 m；旁邊物件的佔地碰到這兩塊手指範圍就會被撞到，例如閉合方向上相鄰的兩個 cube，中心要相距約 0.045 m 以上。只能夾取上方沒有其他物件的物件。 |
| 【介面事實】（只有反思） | 執行介面：設備只提供 source/target 與 move_above、descend、grasp、release、lift、wait（height_m 0.05～0.15 m、seconds 0.1～3；同一個操作內依序執行，中途不會重新感知），不提供任意 XY 偏移、條件分支或同輪失敗後續跑；每輪只能提交一條動作路徑。執行器會在收尾路徑安全時自動回 Ready/Home，否則留在最後的安全抬升位置。 |
| 【圖形驗收】 | 目標要求排出圖形（字母、形狀等）時，驗收條件是圖形排在桌面上空著的地方：組成圖形的物件要跟沒有用到的物件明顯分開，不能夾雜在其他物件之間，圖形要能單獨被辨認出來。 |
| 【字母規範】（2D 時） | 在桌面上排字母時，每個字母設計在最多 5×5 的方格上（高最多 5 格、寬最多 5 格）：每個方向用固定的格距（相鄰格的中心距，格距要讓手指放得下，見夾爪事實），每一格放一個 cube，domino 佔同一排相鄰的兩格；筆畫要像 5×5 點陣字一樣足以辨識是哪個字母。字母形狀以相機畫面判讀：畫面上方是 +Y、畫面右方是 +X（也就是方向詞的「左」），字母在這個方向下要是正的，不能上下或左右顛倒。 |
| 【正面判讀】（3D 時，取代字母規範） | 立起來的圖形（往上疊的）以正面判讀：從 -Y 那側（相機畫面下方）水平看過去，往右是 +X、往上是 +Z，不能左右或上下顛倒。 |
| 【點陣判讀】（只有驗證者） | 用積木排的字母或圖形像點陣字：每一格放一塊積木，同一個方向用固定格距排，格子之間的空隙不算斷開。 |
| 【目標圖形】（有設計結果時） | 目標圖形：這個任務的目標圖形已經在設計階段選出，用下面的 bitmap 表示（第一列是相機畫面最上方、+Y 那側；往右是 +X，也就是方向詞的「左」）：{bitmap}　0 = 空；1 = 放一塊；2 以上 = 那格要疊幾層。請把積木排成這個 bitmap，不必自己重新設計圖形：每一格對應桌面上一個位置，同一個方向用固定格距（相鄰格的中心距，要讓手指放得下），圖形排在桌面上空著的地方。執行前會檢查計畫排出的圖形跟這個 bitmap 完全相同，不同就不執行。 |
| 【附圖說明】（只在沒開 Isaac 的純模擬） | 附圖是程式依目前場景座標畫的俯視示意圖（純模擬沒開 Isaac Sim），不是相機照片：上方 = +Y、右方 = +X，方塊上的數字是場景 index。 |
| 【Unity 畫面說明】（2D 驗證者） | 附圖是 Unity 模擬（預覽）結束時主相機的畫面，不是相機照片；方塊位置是模擬算出的落點。 |
| 【Isaac 畫面說明】（3D 驗證者） | 附圖是 Isaac Sim 模擬（URSim 執行完這一批）結束時的畫面，模擬相機的位置與內參跟實體相機相同。 |

### 各階段看得到哪些說明

| 說明 | B1 拆解 | B2 規劃 | B4 驗證者 | B5 反思 |
|---|:-:|:-:|:-:|:-:|
| 座標慣例 | ✓ | ✓ | ✓ | ✓（另寫一句同義的） |
| 方向詞、物件尺寸、圖形驗收 | ✓ | ✓ | ✓ | ✓ |
| 桌面與基座 | ✓ | ✓ | — | ✓ |
| 字母規範（2D）／正面判讀（3D） | ✓ | ✓ | 只有 3D 的正面判讀 | ✓ |
| 目標圖形 | ✓ | ✓ | — | ✓ |
| 執行介面、計畫格式、低階範例 | — | ✓ | — | 只有介面事實 |
| 夾爪事實 | — | ✓ | — | ✓ |
| 點陣判讀 | — | — | ✓ | — |
| 暫定規則（system 後段） | ✓ | ✓ | — | ✓（放在 user 裡） |
| 相機照片／模擬畫面 | 照片 | 照片 | 模擬畫面 | — |

---

## D. 已知問題（尚未修改）

1. **過時的描述（B2）：** 下面兩句跟目前流程不符。2026-10-08 起相機只在任務開始拍一次，手臂執行失敗任務就結束。
   - 「每個操作開始前系統會重新觀測來源物件位置」；
   - 「執行失敗後由下一輪根據新觀測重規劃」。
2. **驗證者的判準寫死（B4）：** 「連接」和「2 公分」曾讓格距 6 cm 的立體 L 被判 FAIL；【點陣判讀】是為了補這個問題才加的。
3. **夾爪事實裡算好的答案：** 「中心要相距約 0.045 m 以上」，反思常直接照抄這個數字當格距。
4. **低階範例有固定數字：** x=0.10、y=0.15、z=0.02、抬升 0.12，可能被沿用。
5. **拆解看不到介面規定（B1）：** 2026-10-22 排 A 時，拆解把函式位置寫成座標，規劃照抄，第 2～8 輪都轉譯失敗。
6. **轉譯失敗訊息不準（B3）：** 位置寫成座標時，回報的是「函式數超過介面上限」，反思因此一直往錯的方向修。
7. **可達範圍不給 LLM（B3、Unity）：** 超出時只回座標或 Unity 的技術訊息，例如「自身碰撞：連桿 1↔3」、「IK 無解（肘關節奇異點）」。LLM 很難從這些訊息知道該往哪個方向移。
