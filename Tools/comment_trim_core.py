# -*- coding: utf-8 -*-
"""注释统计内核：口径与 Tools/comment-lint.ps1 的 Scan-Cs 一致。

抽出来是因为 comment-trim.py / comment-trim-budget.py / comment-probe.py 三个脚本
都要用同一份口径 —— 各写一份迟早会漂，而"口径不一致"在这里的表现是
"脚本说达标了、守卫说超了"这种没法用数字验收的僵局。
"""

import io
import json


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


def comment_chars(body):
    return len(body.strip())


def code_lines(text):
    """非空、非纯注释行的行数（预算表里 CodeLn 的口径）。"""
    count = 0

    for line in text.replace("\r\n", "\n").split("\n"):
        s = line.strip()
        if not s:
            continue
        if s.startswith("//") or s.startswith("/*") or s.startswith("*"):
            continue
        count += 1

    return count


def load_budget(path):
    with io.open(path, encoding="utf-8") as f:
        data = json.load(f)

    mapping = {k: int(v) for k, v in data.get("files", {}).items()}
    cap = float(data.get("densityCap", 5.37))

    return mapping, cap


def _compress(body, keep):
    """把一条长注释压到 keep 字以内，且保持 XML 标签配平。

    🔴 两条硬约束：
      ① 标签必须成对：守卫报「跨行」的判据是"本行内 <summary>/<remarks> 开闭配平"，
         截断时补一个 </summary> 才能保住形态；
      ② 尽量在标点处断开：断在句中会得到半句看不懂的中文，等于把"有信息"变成"有噪音"。
    """
    import re

    text = body.strip()

    # 优先在 ] > ； 、 等处断开，其次 。 ！ ？
    cut = -1
    for i in range(min(keep, len(text)) - 1, max(0, keep // 2), -1):
        if text[i] in "；。！？":
            cut = i + 1
            break

    if cut < 0:
        for i in range(min(keep, len(text)) - 1, max(0, keep // 2), -1):
            if text[i] in "：、，,)":
                cut = i + 1
                break

    if cut < 0:
        cut = min(keep, len(text))

    text = text[:cut]

    if text.count("<summary>") > text.count("</summary>"):
        text += "</summary>"

    if text.count("<remarks>") > text.count("</remarks>"):
        text += "</remarks>"

    # 截断点可能正好落在标签中间：清掉未闭合的残片
    text = re.sub(r"<[^<>]*$", "", text).strip()

    if text.count("<summary>") > text.count("</summary>"):
        text += "</summary>"

    if text.count("<remarks>") > text.count("</remarks>"):
        text += "</remarks>"

    return text


def trim(text, threshold, keep_prefix=0):
    """按阈值裁整行注释行（保行号：替换成空行）。

    threshold 以上的注释行要么被删（keep_prefix=0），要么被**压到 keep_prefix 字**
    —— 压缩比删除保留更多"这条注释为什么存在"的信息，而预算只认字数。

    返回 (新文本, 删除行数, 删除字符数)。
    """
    import re

    pattern = re.compile(r"^(\s*)(//+|/\*+|\*+/?)\s?(.*)$")

    lines = text.replace("\r\n", "\n").split("\n")
    out = []
    removed = 0
    removed_chars = 0

    for line in lines:
        m = pattern.match(line)

        if m and comment_chars(m.group(3)) >= threshold:
            removed += 1
            removed_chars += comment_chars(m.group(3))

            if keep_prefix > 0:
                body = _compress(m.group(3), keep_prefix)
                out.append(f"{m.group(1)}// {body}")
                removed_chars -= len(body) + 3
                continue

            out.append("")
            continue

        out.append(line)

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
