# -*- coding: utf-8 -*-
"""列出某文件里的长注释，帮人工决定删哪几条。

为什么要它：注释预算只降不升（AGENTS.md 第 7 节），而"哪几条最该删"没有机器判据 ——
机器只报总数。这个脚本把每条注释的字数与行号摊开，人工按"是否在防住一个编译器不会拦的改动"
逐条裁。口径与 Tools/comment-lint.ps1 完全一致（逐行取注释正文、去标记、不计行尾空白）。

用法：
    python Tools/comment-probe.py Assets/Scripts/Logic/Wave/WaveLogic.cs [最短字数]
"""

import io
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def scan_comments(text):
    """逐物理行返回注释正文（已 strip），与 comment-lint 的 Scan-Cs 同口径。"""
    text = text.replace("\ufeff", "")
    L = len(text)
    i = 0
    out = []

    while i < L:
        c = text[i]

        if c == "\n":
            out.append("")
            i += 1
            continue

        if c == "\r":
            i += 1
            continue

        if c == "/" and i + 1 < L and text[i + 1] == "/":
            i += 2
            buf = []
            while i < L and text[i] != "\n":
                if text[i] != "\r":
                    buf.append(text[i])
                i += 1
            out.append("".join(buf).strip())
            continue

        if c == "/" and i + 1 < L and text[i + 1] == "*":
            i += 2
            buf = []
            while i < L:
                if text[i] == "*" and i + 1 < L and text[i + 1] == "/":
                    i += 2
                    break
                if text[i] == "\n":
                    out.append("".join(buf).strip())
                    buf = []
                    i += 1
                    continue
                if text[i] != "\r":
                    buf.append(text[i])
                i += 1
            out.append("".join(buf).strip())
            continue

        if c == '"':
            i += 1
            while i < L:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                i += 1
            continue

        if c == "'":
            i += 1
            while i < L:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            continue

        out.append("")
        i += 1

    return out


def main():
    rel = sys.argv[1]
    threshold = int(sys.argv[2]) if len(sys.argv) > 2 else 40

    path = os.path.join(ROOT, rel)
    text = io.open(path, encoding="utf-8").read()
    com = scan_comments(text)
    src = text.split("\n")

    total = sum(len(x) for x in com)
    code_lines = sum(1 for line in src if line.strip() and not line.strip().startswith("//"))

    print(f"{rel}: 注释 {total} 字符 / 代码 {code_lines} 行")
    print(f"（列出的都是 >= {threshold} 字符的注释行）")
    print()

    for idx, body in enumerate(com, start=1):
        if len(body) < threshold:
            continue
        print(f"{idx:5d} [{len(body):4d}] {body}")


if __name__ == "__main__":
    main()
