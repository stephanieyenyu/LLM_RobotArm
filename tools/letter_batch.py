"""
批次純模擬：依序把 A～Z 的平面字母指令交給 csharp_server，等每個任務的 result.json，統計成功率。

做法跟在 Unity 輸入框按送出一樣：把指令寫進 StreamingAssets/user_input.txt，csharp_server 讀走後跑完整個任務
（設計 bitmap → 規劃 → Unity 模擬驗證 → 反思，最多 10 輪），紀錄在 outputs/experiments_sim/<run>/。
這個腳本不改 csharp_server 的流程，只負責送指令、等結果、彙整。

需要先開好：
  1. Unity（Play 模式，模擬模式按鈕切到「模擬」：run_mode.json 的 mode = sim、reset_each_task = true）
  2. csharp_server（dotnet run）
  Isaac Sim 不必開：2D 沒連到 Isaac 時 csharp_server 會用內建的虛擬世界。

安全：每個任務送出前都重讀 run_mode.json，不是 sim 就停止，不會把指令送到實體手臂。

用法（在 repo 根目錄）：
  python tools/letter_batch.py                     # A～Z 各跑一次
  python tools/letter_batch.py --reps 3            # A～Z 跑三輪（A..Z、A..Z、A..Z）
  python tools/letter_batch.py --letters ACEJ      # 只跑指定字母
  python tools/letter_batch.py --resume <批次資料夾>  # 中斷後接著跑，已有結果的不重跑
  python tools/letter_batch.py --summarize <批次資料夾>  # 只重新統計（加 --export-dir 會重新匯出）
  python tools/letter_batch.py --export-dir "D:\\results\\50% sim"   # 另外匯出 terminal 紀錄
完整的啟動說明見 tools/letter_batch_README.txt。
結果在 csharp_server/outputs/letter_batches/<時間>/：trials.jsonl（每個任務一行）、summary.json、summary.md。

--export-dir：每個任務 csharp_server terminal 的內容（run 資料夾的 console.txt）複製到這個資料夾，
檔名 <label>_<字母>_<第幾輪成功／判定失敗的輪數>_<success|failed>.txt（基礎設施錯誤是 error；
--reps 大於 1 時第 2 輪起加 " (2)"），另寫 <label>_統計.txt：每個字母試了幾次、幾次成功幾次失敗。
--label 預設取匯出資料夾名稱的第一段（"50% sim" → "50%"）。每跑完一題就更新一次。
"""
import argparse
import datetime as dt
import json
import math
import os
import shutil
import string
import sys
import time
import unicodedata
from collections import Counter, defaultdict

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
ASSETS = os.path.join(REPO, "unity_project", "Assets", "StreamingAssets")
INPUT = os.path.join(ASSETS, "user_input.txt")
RUN_MODE = os.path.join(ASSETS, "run_mode.json")
SIM_RUNS = os.path.join(REPO, "csharp_server", "outputs", "experiments_sim")
BATCHES = os.path.join(REPO, "csharp_server", "outputs", "letter_batches")
DEFAULT_TEMPLATE = "用黃色方塊排成{letter}"
# 只有 success / failed 算進成功率（同 ExperimentMetrics）；其他是基礎設施問題，可以重跑
VALID = ("success", "failed")
RETRYABLE = ("infrastructure_error", "adapter_error")
# 每一輪失敗的階段（result.json 的 attempt_outcomes）
STAGE_NAMES = {"planning": "拆解或規劃", "translation_format": "轉譯讀不懂", "translation_rejected": "轉譯拒絕",
               "precheck": "執行前檢查", "simulation": "模擬驗證", "execution": "執行", "global_validation": "整體驗證"}


def log(message):
    print(f"[{dt.datetime.now():%H:%M:%S}] {message}", flush=True)


def read_json(path, retries=10):
    # csharp_server 用 File.WriteAllText 寫檔，剛好讀到一半時稍等再讀
    for _ in range(retries):
        try:
            with open(path, encoding="utf-8-sig") as f:
                return json.load(f)
        except (OSError, json.JSONDecodeError):
            time.sleep(0.3)
    return None


def check_sim_mode():
    """run_mode.json 不是純模擬就回傳原因；是的話回傳 None。"""
    mode = read_json(RUN_MODE) if os.path.exists(RUN_MODE) else None
    if mode is None:
        return f"找不到或讀不懂 {RUN_MODE}（沒有這個檔 = 實機）"
    if str(mode.get("mode", "real")).lower() != "sim":
        return f"run_mode.json 的 mode 是 {mode.get('mode')!r}，不是 sim：請先在 Unity 按「模擬模式」"
    if mode.get("reset_each_task", True) is not True:
        return "run_mode.json 的 reset_each_task 不是 true：每個字母要從同一個場景開始，統計才能比較"
    return None


def pending_input():
    if not os.path.exists(INPUT):
        return ""
    with open(INPUT, encoding="utf-8-sig") as f:
        return f.read().strip()


def write_input(goal):
    tmp = INPUT + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(goal)
    os.replace(tmp, INPUT)


def run_dirs():
    if not os.path.isdir(SIM_RUNS):
        return set()
    return {d for d in os.listdir(SIM_RUNS) if os.path.isdir(os.path.join(SIM_RUNS, d))}


def run_task(goal, timeout_min, pickup_timeout_s=60):
    """送一個指令，等 csharp_server 寫出 result.json。回傳 (run 資料夾名, result)；result 為 None 表示逾時。"""
    before = run_dirs()
    write_input(goal)
    # csharp_server 每 0.5 秒看一次 user_input.txt，讀走後清空
    deadline = time.time() + pickup_timeout_s
    while pending_input():
        if time.time() > deadline:
            write_input("")  # 收回指令，免得之後服務開起來才突然執行
            raise RuntimeError(f"{pickup_timeout_s} 秒內 csharp_server 沒有讀走指令：確認 csharp_server 有在跑、沒有卡在上一個任務")
        time.sleep(0.5)
    run = None
    deadline = time.time() + timeout_min * 60
    last_report = time.time()
    while time.time() < deadline:
        if run is None:
            for d in sorted(run_dirs() - before):
                task = os.path.join(SIM_RUNS, d, "task.txt")
                if os.path.exists(task) and open(task, encoding="utf-8-sig").read().strip() == goal:
                    run = d
                    break
        if run is not None:
            result_path = os.path.join(SIM_RUNS, run, "result.json")
            if os.path.exists(result_path):
                result = read_json(result_path)
                if result is not None:
                    return run, result
        if time.time() - last_report >= 300:
            last_report = time.time()
            attempts = len([a for a in os.listdir(os.path.join(SIM_RUNS, run)) if a.startswith("attempt_")]) if run else 0
            log(f"  還在跑（第 {attempts} 輪）…")
        time.sleep(2)
    return run, None


def wilson(successes, n, z=1.96):
    if n == 0:
        return None
    p = successes / n
    center = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    return [round(center - half, 4), round(center + half, 4)]


def summarize(batch_dir):
    trials = []
    path = os.path.join(batch_dir, "trials.jsonl")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            trials = [json.loads(line) for line in f if line.strip()]
    meta = read_json(os.path.join(batch_dir, "batch.json")) or {}
    valid = [t for t in trials if t["status"] in VALID]
    successes = [t for t in valid if t["success"]]

    def rate(k):
        return round(sum(1 for t in successes if t["attempts"] <= k) / len(valid), 4) if valid else None

    by_letter = defaultdict(list)
    for t in trials:
        by_letter[t["letter"]].append(t)
    letters = {}
    for letter in sorted(by_letter):
        ts = by_letter[letter]
        v = [t for t in ts if t["status"] in VALID]
        s = [t for t in v if t["success"]]
        letters[letter] = {
            "valid_trials": len(v), "successes": len(s),
            "success_rate": round(len(s) / len(v), 4) if v else None,
            "first_attempt_successes": sum(1 for t in s if t["attempts"] == 1),
            "mean_attempts_among_successes": round(sum(t["attempts"] for t in s) / len(s), 2) if s else None,
            "excluded_trials": len(ts) - len(v),
            "outcomes": [t["status"] if not t["success"] else f"success@{t['attempts']}" for t in ts],
        }
    outcome_counts = Counter(o for t in trials for o in t.get("attempt_outcomes") or [])
    summary = {
        "goal_template": meta.get("goal_template"), "started": meta.get("started"),
        "updated": dt.datetime.now().isoformat(timespec="seconds"),
        "total_trials": len(trials), "valid_trials": len(valid),
        "excluded_trials": dict(Counter(t["status"] for t in trials if t["status"] not in VALID)),
        "success_rate_within_10": rate(10), "success_rate_95ci": wilson(len(successes), len(valid)),
        "first_attempt_success_rate": rate(1),
        "mean_attempts_among_successes": round(sum(t["attempts"] for t in successes) / len(successes), 2) if successes else None,
        "cumulative_success_rates": [{"attempt": k, "success_rate": rate(k)} for k in range(1, 11)],
        "design_failures": sum(1 for t in valid if t.get("design_error")),
        "attempt_outcome_counts": dict(sorted(outcome_counts.items())),
        "mean_minutes_per_trial": round(sum(t["minutes"] for t in trials) / len(trials), 1) if trials else None,
        "letters": letters,
    }
    with open(os.path.join(batch_dir, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    pct = lambda x: "—" if x is None else f"{x * 100:.1f}%"
    dash = lambda x: "—" if x is None else x
    lines = [
        f"# 字母批次模擬結果（{summary['updated']}）", "",
        f"指令：`{summary['goal_template']}`", "",
        f"- 有效任務 {len(valid)} 個（另有 {len(trials) - len(valid)} 個基礎設施錯誤不計入：{summary['excluded_trials'] or '無'}）",
        f"- 10 輪內成功率 **{pct(summary['success_rate_within_10'])}**"
        + (f"（95% CI {pct(summary['success_rate_95ci'][0])}～{pct(summary['success_rate_95ci'][1])}）" if summary["success_rate_95ci"] else ""),
        f"- 第 1 輪就成功 {pct(summary['first_attempt_success_rate'])}；成功的平均輪數 {dash(summary['mean_attempts_among_successes'])}",
        f"- bitmap 設計失敗 {summary['design_failures']} 個；每個任務平均 {dash(summary['mean_minutes_per_trial'])} 分鐘",
        f"- 每一輪的結果次數：{summary['attempt_outcome_counts'] or '—'}", "",
        "| 字母 | 有效 | 成功 | 成功率 | 第 1 輪成功 | 成功平均輪數 | 每次結果 |",
        "|---|---|---|---|---|---|---|",
    ]
    for letter, s in letters.items():
        lines.append(f"| {letter} | {s['valid_trials']} | {s['successes']} | {pct(s['success_rate'])} | "
                     f"{s['first_attempt_successes']} | {dash(s['mean_attempts_among_successes'])} | {', '.join(s['outcomes'])} |")
    with open(os.path.join(batch_dir, "summary.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    return summary


def pad(text, width):
    """中文字在等寬字型佔兩格，補空白對齊欄位。"""
    text = str(text)
    used = sum(2 if unicodedata.east_asian_width(c) in "WF" else 1 for c in text)
    return text + " " * max(1, width - used)


def export(batch_dir, export_dir, label):
    """把每個任務的 terminal 紀錄（console.txt）照檔名格式複製到 export_dir，另寫 <label>_統計.txt。"""
    path = os.path.join(batch_dir, "trials.jsonl")
    if not os.path.exists(path):
        return
    with open(path, encoding="utf-8") as f:
        trials = [json.loads(line) for line in f if line.strip()]
    meta = read_json(os.path.join(batch_dir, "batch.json")) or {}
    os.makedirs(export_dir, exist_ok=True)
    # 這個批次寫過的檔案可以覆寫（每跑完一題整批重新匯出）；資料夾裡原本就有、同名的檔案（例如手動存的）
    # 先改名成「… (舊 時間).txt」保留下來，不覆寫
    written_path = os.path.join(batch_dir, "exported_files.json")
    written = set(read_json(written_path) or []) if os.path.exists(written_path) else set()

    def claim(name):
        target = os.path.join(export_dir, name)
        key = os.path.normcase(target)
        if os.path.exists(target) and key not in written:
            stem, ext = os.path.splitext(target)
            backup = f"{stem} (舊 {dt.datetime.fromtimestamp(os.path.getmtime(target)):%m%d_%H%M}){ext}"
            os.replace(target, backup)
            log(f"  {name} 原本就在匯出資料夾裡，改名成 {os.path.basename(backup)} 保留")
        written.add(key)
        with open(written_path, "w", encoding="utf-8") as f:
            json.dump(sorted(written), f, ensure_ascii=False, indent=2)
        return target

    missing = 0
    for t in trials:
        verdict = "success" if t["success"] else "failed" if t["status"] == "failed" else "error"
        t["file"] = f"{label}_{t['letter']}_{t['attempts']}_{verdict}" + (f" ({t['rep']})" if t["rep"] > 1 else "") + ".txt"
        source = os.path.join(SIM_RUNS, t["run"], "console.txt") if t.get("run") else None
        target = claim(t["file"])
        if source and os.path.exists(source):
            shutil.copyfile(source, target)
            continue
        # 舊版 csharp_server 沒有存 console.txt（改版後要重開服務才會存）
        missing += 1
        with open(target, "w", encoding="utf-8-sig") as f:
            f.write(f"指令：{t['goal']}\n（這個任務沒有 terminal 紀錄：csharp_server 更新後還沒重開，沒有存 console.txt）\n\n"
                    f"結果：{t['status']}，{t['attempts']} 輪，每一輪：{', '.join(t.get('attempt_outcomes') or []) or '—'}\n"
                    f"紀錄資料夾：{os.path.join(SIM_RUNS, t.get('run') or '?')}\n")

    by_letter = defaultdict(list)
    for t in trials:
        by_letter[t["letter"]].append(t)
    columns = [("字母", 6), ("試了幾次", 10), ("成功", 6), ("失敗", 6), ("錯誤不計", 10),
               ("規劃輪數", 10), ("成功輪", 8), ("失敗輪", 8), ("檔案", 0)]
    header = "".join(pad(name, width) if width else name for name, width in columns)
    rows, totals = [], Counter()
    for letter in sorted(by_letter):
        ts = by_letter[letter]
        outcomes = [o for t in ts for o in t.get("attempt_outcomes") or []]
        row = {"tasks": len(ts), "success": sum(t["success"] for t in ts),
               "failed": sum(t["status"] == "failed" for t in ts),
               "error": sum(t["status"] not in VALID for t in ts),
               "rounds": len(outcomes), "round_success": outcomes.count("success"),
               "round_failed": len(outcomes) - outcomes.count("success")}
        totals.update(row)
        values = [letter] + list(row.values()) + ["、".join(t["file"] for t in ts)]
        rows.append("".join(pad(v, w) if w else str(v) for v, (_, w) in zip(values, columns)))
    rows.append("".join(pad(v, w) if w else str(v) for v, (_, w) in
                        zip(["合計"] + [totals[k] for k in ("tasks", "success", "failed", "error",
                                                            "rounds", "round_success", "round_failed")] + [""], columns)))

    valid = [t for t in trials if t["status"] in VALID]
    successes = [t for t in valid if t["success"]]
    pct = lambda n, d: f"{n / d * 100:.1f}%" if d else "—"
    stages = Counter(o for t in trials for o in t.get("attempt_outcomes") or [] if o != "success")
    lines = [
        f"{label} 批次模擬統計（更新 {dt.datetime.now():%Y-%m-%d %H:%M:%S}）",
        f"指令：{meta.get('goal_template', DEFAULT_TEMPLATE)}；每個任務最多 10 輪；批次紀錄 {batch_dir}",
        "「試了幾次」是送了幾次指令（任務數）；一個任務裡規劃、模擬驗證沒過會反思再試，最多 10 輪，"
        "「規劃輪數」是這些任務總共跑了幾輪。",
        "",
        header,
        *rows,
        "",
        f"10 輪內成功率：{pct(len(successes), len(valid))}（成功 {len(successes)} / 有效任務 {len(valid)}；"
        f"基礎設施錯誤 {len(trials) - len(valid)} 個不計入）",
        f"第 1 輪就成功：{pct(sum(t['attempts'] == 1 for t in successes), len(valid))}",
        f"成功的平均輪數：{sum(t['attempts'] for t in successes) / len(successes):.2f}" if successes else "成功的平均輪數：—",
        "失敗的輪停在哪個階段：" + ("、".join(f"{STAGE_NAMES.get(k, k)} {v}" for k, v in stages.most_common()) or "—"),
    ]
    if missing:
        lines.append(f"注意：{missing} 個任務沒有 terminal 紀錄（csharp_server 更新後還沒重開）。")
    with open(claim(f"{label}_統計.txt"), "w", encoding="utf-8-sig") as f:
        f.write("\n".join(lines) + "\n")


def report(batch_dir):
    """更新 summary；batch.json 有設定匯出資料夾時一起匯出。"""
    summary = summarize(batch_dir)
    meta = read_json(os.path.join(batch_dir, "batch.json")) or {}
    if meta.get("export_dir"):
        export(batch_dir, meta["export_dir"], meta.get("label") or default_label(meta["export_dir"]))
    return summary


def default_label(export_dir):
    name = os.path.basename(os.path.normpath(export_dir)).strip()
    return name.split()[0] if name else "batch"


def main():
    ap = argparse.ArgumentParser(description="批次純模擬 A～Z 平面字母並統計成功率")
    ap.add_argument("--letters", default=string.ascii_uppercase, help="要跑的字母（預設 A～Z）")
    ap.add_argument("--reps", type=int, default=1, help="每個字母跑幾次（依 A..Z 一輪一輪跑）")
    ap.add_argument("--template", default=DEFAULT_TEMPLATE, help="指令樣板，{letter} 會換成字母")
    ap.add_argument("--task-timeout-min", type=float, default=120, help="單一任務最多等幾分鐘（10 輪 × 約 6 分鐘）")
    ap.add_argument("--infra-retries", type=int, default=2, help="基礎設施錯誤（API 503 等）同一題最多重跑幾次")
    ap.add_argument("--retry-wait-s", type=float, default=60, help="基礎設施錯誤後等幾秒再重跑")
    ap.add_argument("--resume", help="接續這個批次資料夾，已有結果的字母不重跑")
    ap.add_argument("--summarize", help="只重新統計這個批次資料夾")
    ap.add_argument("--dry-run", action="store_true", help="只列出要送的指令與檢查，不送")
    ap.add_argument("--export-dir", help="把每個任務的 terminal 紀錄依字母存成 txt、另寫統計到這個資料夾")
    ap.add_argument("--label", help="匯出檔名開頭（預設取匯出資料夾名稱第一段，例如 \"50%% sim\" → 50%%）")
    args = ap.parse_args()

    def update_meta(batch_dir, **values):
        path = os.path.join(batch_dir, "batch.json")
        meta = read_json(path) if os.path.exists(path) else {}
        meta.update({k: v for k, v in values.items() if v is not None})
        with open(path, "w", encoding="utf-8") as f:
            json.dump(meta, f, ensure_ascii=False, indent=2)

    export_dir = os.path.abspath(args.export_dir) if args.export_dir else None
    label = args.label or (default_label(export_dir) if export_dir else None)

    if args.summarize:
        batch_dir = os.path.abspath(args.summarize)
        if export_dir:
            update_meta(batch_dir, export_dir=export_dir, label=label)
        s = report(batch_dir)
        print(open(os.path.join(batch_dir, "summary.md"), encoding="utf-8").read())
        meta = read_json(os.path.join(batch_dir, "batch.json")) or {}
        if meta.get("export_dir"):
            print(f"terminal 紀錄與統計已匯出到 {meta['export_dir']}")
        return 0 if s else 1

    letters = [c for c in args.letters.upper() if c in string.ascii_uppercase]
    if not letters or "{letter}" not in args.template:
        ap.error("--letters 要有 A～Z 的字母，--template 要有 {letter}")
    plan = [(rep, letter) for rep in range(1, args.reps + 1) for letter in letters]

    if args.resume:
        batch_dir = os.path.abspath(args.resume)
        meta = read_json(os.path.join(batch_dir, "batch.json")) or {}
        if meta.get("goal_template") and meta["goal_template"] != args.template:
            log(f"注意：這個批次原本的指令樣板是 {meta['goal_template']!r}，改用原本的。")
            args.template = meta["goal_template"]
    else:
        batch_dir = os.path.join(BATCHES, dt.datetime.now().strftime("%Y%m%d_%H%M%S"))
    trials_path = os.path.join(batch_dir, "trials.jsonl")
    done = set()
    if os.path.exists(trials_path):
        with open(trials_path, encoding="utf-8") as f:
            done = {(t["rep"], t["letter"]) for t in map(json.loads, filter(str.strip, f))}
    todo = [p for p in plan if p not in done]

    problem = check_sim_mode()
    log(f"共 {len(plan)} 個任務，已完成 {len(plan) - len(todo)}，這次要跑 {len(todo)} 個；紀錄在 {batch_dir}")
    if args.dry_run:
        for rep, letter in todo:
            print(f"  第 {rep} 輪 {letter}：{args.template.format(letter=letter)}")
        print("模式檢查：" + (problem or "OK（純模擬）"))
        print("user_input.txt：" + (repr(pending_input()) if pending_input() else "空的（OK）"))
        return 0
    if problem:
        log("不執行：" + problem)
        return 1
    if pending_input():
        log(f"不執行：user_input.txt 裡還有沒被讀走的指令 {pending_input()!r}（csharp_server 沒開或正忙）")
        return 1

    os.makedirs(batch_dir, exist_ok=True)
    if not os.path.exists(os.path.join(batch_dir, "batch.json")):
        update_meta(batch_dir, goal_template=args.template, letters="".join(letters), reps=args.reps,
                    started=dt.datetime.now().isoformat(timespec="seconds"), run_mode=read_json(RUN_MODE))
    # 接續時沒給 --export-dir 就沿用原本的設定
    update_meta(batch_dir, export_dir=export_dir, label=label)

    for index, (rep, letter) in enumerate(todo, 1):
        goal = args.template.format(letter=letter)
        for retry in range(args.infra_retries + 1):
            problem = check_sim_mode()  # 中途被切回實機就停
            if problem:
                log("停止：" + problem)
                report(batch_dir)
                return 1
            log(f"({index}/{len(todo)}) 第 {rep} 輪 {letter}：送出「{goal}」" + (f"（重跑第 {retry} 次）" if retry else ""))
            started = time.time()
            try:
                run, result = run_task(goal, args.task_timeout_min)
            except RuntimeError as ex:
                log("停止：" + str(ex))
                report(batch_dir)
                return 1
            minutes = round((time.time() - started) / 60, 1)
            if result is None:
                log(f"停止：{args.task_timeout_min:.0f} 分鐘內沒有結果（run = {run}）。"
                    "csharp_server 可能在等 Unity（Unity 有在 Play 嗎？）或已經當掉；處理好後用 --resume 接著跑。")
                report(batch_dir)
                return 1
            status = result.get("status")
            log(f"  → {status}，{result.get('attempts')} 輪，{minutes} 分鐘（{run}）")
            if status == "execution_unknown":
                log("停止：執行狀態未知，csharp_server 已自己停止服務；確認後重開服務再 --resume。")
            if status in RETRYABLE and retry < args.infra_retries:
                log(f"  基礎設施錯誤不算進成功率，{args.retry_wait_s:.0f} 秒後重跑同一題。")
                time.sleep(args.retry_wait_s)
                continue
            with open(trials_path, "a", encoding="utf-8") as f:
                f.write(json.dumps({"rep": rep, "letter": letter, "goal": goal, "run": run, "status": status,
                                    "success": bool(result.get("success")), "attempts": result.get("attempts", 0),
                                    "attempt_outcomes": result.get("attempt_outcomes") or [],
                                    "design_error": result.get("design_error"),
                                    "bitmap": (result.get("design") or {}).get("Bitmap") or (result.get("design") or {}).get("bitmap"),
                                    "infra_retries": retry, "minutes": minutes}, ensure_ascii=False) + "\n")
            break
        s = report(batch_dir)
        rate = s["success_rate_within_10"]
        log(f"  目前 10 輪內成功率 {'—' if rate is None else f'{rate * 100:.1f}%'}（有效 {s['valid_trials']} 個）")
        if status == "execution_unknown":
            return 1

    report(batch_dir)
    print(open(os.path.join(batch_dir, "summary.md"), encoding="utf-8").read())
    meta = read_json(os.path.join(batch_dir, "batch.json")) or {}
    if meta.get("export_dir"):
        log(f"terminal 紀錄與統計已匯出到 {meta['export_dir']}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        log("中斷。正在跑的任務 csharp_server 會繼續跑完；之後用 --resume 接著跑（這一題會重跑）。")
        sys.exit(130)
