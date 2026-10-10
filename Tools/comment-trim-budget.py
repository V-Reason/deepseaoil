# -*- coding: utf-8 -*-
"""把超出注释预算的文件裁回预算内。

判据来自 Tools/comment-budget.json（已登记文件用登记上限；新文件用密度上限 5.37×代码行）。
对每个超标文件按"从长到短"的阈值依次裁掉整行注释，直到进入预算。裁完打印前后对照。

🔴 只删整行注释行：含代码的行一律不动（`[Tooltip("…")]` 里的中文是代码，
   而"看起来像注释"的行一旦被按注释处理就会改坏代码）。

用法：
    python Tools/comment-trim-budget.py            # 预演
    python Tools/comment-trim-budget.py --apply
"""

import io
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from comment_trim_core import code_lines, comment_chars, load_budget, scan_comments, trim  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def measure(path):
    text = io.open(path, encoding="utf-8").read()
    return sum(len(x) for x in scan_comments(text)), code_lines(text)


def target_for(rel, budget_map, density_cap, code_ln):
    if rel in budget_map:
        return budget_map[rel]
    return int(density_cap * code_ln)


def main():
    apply_it = "--apply" in sys.argv

    budget_map, density_cap = load_budget(os.path.join(ROOT, "Tools", "comment-budget.json"))

    scripts = os.path.join(ROOT, "Assets", "Scripts")
    files = []

    for base, _dirs, names in os.walk(scripts):
        if "Generated" in base:
            continue
        for n in names:
            if n.endswith(".cs"):
                files.append(os.path.join(base, n))

    files.sort()

    print(f"{'file':<62} {'now':>6} {'budget':>7} {'after':>6}  removed")

    changed = 0

    for path in files:
        rel = os.path.relpath(path, ROOT).replace("\\", "/")
        cur, code_ln = measure(path)
        cap = target_for(rel, budget_map, density_cap, code_ln)

        if cur <= cap:
            continue

        text = io.open(path, encoding="utf-8").read()

        # 先压不删：阈值降到 keep 长度，长注释保留前 keep 字
        best = None
        for threshold, keep in ((120, 0), (90, 0), (70, 40), (55, 40), (45, 32),
                                (38, 32), (32, 26), (27, 26), (22, 20), (18, 20),
                                (14, 16), (10, 10), (6, 0)):
            candidate, removed, _chars = trim(text, threshold, keep)
            now = sum(len(x) for x in scan_comments(candidate))
            if now <= cap:
                best = (candidate, now, removed)
                break
            if best is None:
                best = (candidate, now, removed)

        if best is None:
            continue

        new_text, now, removed = best

        print(f"{rel:<62} {cur:>6} {cap:>7} {now:>6}  -{removed} 行")

        if apply_it:
            io.open(path, "w", encoding="utf-8", newline="\n").write(new_text)
            changed += 1
        else:
            changed += 1

    if apply_it:
        print(f"\n已落盘 {changed} 个文件")
    else:
        print(f"\n预演：{changed} 个文件需要裁剪（加 --apply 落盘）")


if __name__ == "__main__":
    main()
