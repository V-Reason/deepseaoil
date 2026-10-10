# -*- coding: utf-8 -*-
"""按字数裁掉最长的注释行，把文件压回注释预算内。

为什么需要它：AGENTS.md 第 7 节的预算只降不升，而"哪几条该删"没有机器判据 ——
所以判据只能是"字数"。本脚本只删**整行注释行**（可含多行 /// 与 /* */ 的物理行），
绝不动含代码的行：那种改法有可能动到 `[Tooltip("…")]` 这类"注释长得像、其实是代码"的东西。

被删的行替换成空行（保行号，便于对照 diff），随后把连续空行压成一个。

用法：
    python Tools/comment-trim.py Assets/Scripts/Logic/Wave/WaveLogic.cs            # 预演
    python Tools/comment-trim.py Assets/Scripts/Logic/Wave/WaveLogic.cs --apply
    python Tools/comment-trim.py Assets/Scripts/Logic/Wave/WaveLogic.cs 30 --apply  # 只删 >=30 字的
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 整行注释：可选缩进 + // 或 /* 或 *（块注释中段）或 */
COMMENT_LINE = re.compile(r"^(\s*)(//+|/\*+|\*+/?)\s?(.*)$")


def comment_chars(body):
    """该行注释的有效字符数（口径同 comment-lint：去标记、两端空白不计）。"""
    return len(body.strip())


def trim(text, threshold):
    lines = text.replace("\r\n", "\n").split("\n")
    out = []
    removed = 0
    removed_chars = 0

    for line in lines:
        m = COMMENT_LINE.match(line)

        if m and comment_chars(m.group(3)) >= threshold:
            removed += 1
            removed_chars += comment_chars(m.group(3))
            out.append("")
            continue

        out.append(line)

    # 连续空行压成一个
    squashed = []
    blank = False

    for line in out:
        if line.strip() == "":
            if blank:
                continue
            blank = True
        else:
            blank = False

        squashed.append(line)

    return "\n".join(squashed), removed, removed_chars


def main():
    rel = sys.argv[1]
    args = [a for a in sys.argv[2:] if not a.startswith("--")]
    apply_it = "--apply" in sys.argv
    threshold = int(args[0]) if args else 25

    path = os.path.join(ROOT, rel)
    text = io.open(path, encoding="utf-8").read()

    new_text, removed, chars = trim(text, threshold)

    print(f"{rel}: 将删除 {removed} 行注释（{chars} 字符），阈值 >= {threshold}")

    if not apply_it:
        print("（预演；加 --apply 落盘）")
        return

    io.open(path, "w", encoding="utf-8", newline="\n").write(new_text)
    print("已落盘")


if __name__ == "__main__":
    main()
