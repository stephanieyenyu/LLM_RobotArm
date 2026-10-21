字母批次模擬（A～Z 平面字母）啟動說明
======================================

這是什麼
--------
tools/letter_batch.py 依序把「用黃色方塊排成A」…「用黃色方塊排成Z」交給 csharp_server，
每題跑完（最多 10 輪）才送下一題，最後統計成功率。

- 跟在 Unity 輸入框按送出一樣（寫 StreamingAssets/user_input.txt），csharp_server 的流程不變。
- 只在純模擬模式下送指令：每題送出前都重讀 run_mode.json，不是 sim 就停止，不會動到實體手臂。
- 只用 Python 標準函式庫，不用另外裝套件。


啟動前要準備
------------
- Python 3、.NET 8、Unity 專案（跟平常跑 csharp_server 一樣）
- OpenAI / Gemini 的 API key 環境變數（跟平常一樣）
- Isaac Sim 不必開：2D 沒連到 Isaac 時，csharp_server 會用內建的虛擬世界


啟動步驟
--------
1. Unity
   打開 unity_project，按 Play，把「模擬模式」按鈕切到「模擬」。
   StreamingAssets/run_mode.json 要是：
     "mode": "sim"
     "reset_each_task": true     （每題都從同一個 16 顆方塊的場景開始，統計才能比較）

2. 第一個 terminal：啟動 csharp_server
   一定要在 csharp_server 資料夾裡跑（輸出路徑是相對路徑）：
     cd csharp_server
     dotnet run
   看到「純模擬 / 實機用 Unity 的「模擬模式」按鈕切換…」這行就準備好了。
   ※ 2026-10-21 起 csharp_server 會把每個任務的 terminal 內容存成 console.txt；
     更新程式後要重開 csharp_server 才會存。

3. 第二個 terminal：開始批次（在 repo 根目錄）
   先只檢查、不送指令（確認模式是 sim、user_input.txt 是空的）：
     python tools/letter_batch.py --dry-run
   A～Z 各跑一次，並把每個字母的 terminal 紀錄存到指定資料夾：
     python tools/letter_batch.py --export-dir "D:\results\50% sim"

   跑的時候：
   - 不要在 Unity 輸入框下指令，也不要停止 Play。
   - 每題送出、跑完都會印一行（結果、幾輪、幾分鐘、目前成功率）。
   - 一輪大約 6 分鐘，A～Z 跑一次約 3～5 小時；每題都會多次呼叫 GPT-5 與 Gemini（有 API 費用）。


中斷與接續
----------
- 在批次的 terminal 按 Ctrl+C 停止。正在跑的那題 csharp_server 會繼續跑完，等它跑完再接續。
- 接續（已經有結果的字母不會重跑，匯出資料夾沿用原本的設定）：
    python tools/letter_batch.py --resume csharp_server/outputs/letter_batches/<批次資料夾>
  批次資料夾的名稱在開始時印的第一行。
- 只重新統計／重新匯出（不跑任務）：
    python tools/letter_batch.py --summarize csharp_server/outputs/letter_batches/<批次資料夾>
    python tools/letter_batch.py --summarize <批次資料夾> --export-dir "D:\results\另一個資料夾"


輸出
----
csharp_server/outputs/letter_batches/<時間>/
  trials.jsonl   每個任務一行（字母、結果、輪數、每一輪的結果、對應的 run 資料夾）
  summary.md     成功率總表與每個字母的結果
  summary.json   同上（給程式讀）

每個任務的完整紀錄仍在 csharp_server/outputs/experiments_sim/<run>/（含 console.txt）。

有加 --export-dir 時，匯出資料夾裡會有（每跑完一題就更新）：
  <label>_<字母>_<第幾輪>_<success|failed>.txt
      該字母那次任務的 csharp terminal 內容。
      success：第幾輪成功；failed：判定失敗時跑到第幾輪。
      例：50%_A_1_success.txt、50%_C_10_failed.txt
      API 503 等基礎設施錯誤重跑後仍失敗的是 error（不算進成功率）。
      --reps 大於 1 時，第 2 輪起檔名後面加 " (2)"、" (3)"。
  <label>_統計.txt
      每個字母：試了幾次（任務數）、成功、失敗、錯誤不計，
      以及規劃輪數、成功輪、失敗輪（一個任務最多 10 輪）；
      最後是 10 輪內成功率、第 1 輪就成功的比例、成功的平均輪數、失敗的輪停在哪個階段。

<label> 預設取匯出資料夾名稱的第一段（"50% sim" → "50%"），也可以用 --label 指定。
label 只是檔名開頭，不會改變任何判定門檻。
匯出資料夾裡原本就有、同名的檔案（例如之前手動存的）不會被覆寫，會改名成「…(舊 月日_時分).txt」保留。


常用參數
--------
  --letters ACEJ          只跑指定字母（預設 A～Z）
  --reps 3                A～Z 跑三輪（A..Z、A..Z、A..Z）
  --template "用黃色方塊排成{letter}"   指令樣板（預設就是這個）
  --task-timeout-min 120  單題最多等幾分鐘；超過就停止（通常是 Unity 沒在 Play 或 csharp_server 當掉）
  --infra-retries 2       基礎設施錯誤（API 503 等）同一題最多重跑幾次，不算進成功率
  --retry-wait-s 60       基礎設施錯誤後等幾秒再重跑


會自動停止的情況
----------------
- run_mode.json 不是 sim（例如中途被切回實機）
- reset_each_task 不是 true
- 60 秒內 csharp_server 沒有讀走指令（服務沒開或正忙；指令會收回，不會之後突然執行）
- 單題超過 --task-timeout-min 沒有結果
- 結果是 execution_unknown（csharp_server 自己也會停止服務）
處理好之後用 --resume 接著跑。


注意
----
- Unity 畫面重疊率的門檻是 JsonExecutor 的 bitmapOverlapThreshold（MainScene 的 Inspector 設定，目前 0.9 = 90%）。
  要用別的門檻做實驗，先在 Inspector 改好再開始；匯出的 label 不會改門檻。
- 成功率只算 success 與 failed（跟 csharp_server 的 metrics.json 相同），基礎設施錯誤不計入。
