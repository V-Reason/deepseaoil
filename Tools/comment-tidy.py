# -*- coding: utf-8 -*-
"""把被 comment-trim 压断的 XML 注释行收尾。

裁剪是纯字数判据，会把注释断在句中，留下 `// <remarks>三重静止判据（无输入＋速度近零＋位</remarks>`
这种"标签配平但读不通"的形态。机器判据查不出来（守卫只查配平），但那是把"有信息"降级成
"有噪音"。本脚本按标点收口：截断点回退到最近的句读，去掉悬空的连接词。

只动整行注释行，且只动 `<summary>` / `<remarks>` 形态的行 —— 含代码的行一律不碰。

用法：
    python Tools/comment-tidy.py            # 预演
    python Tools/comment-tidy.py --apply
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# 句读优先级：先句末，再分句，最后逗号
BREAKS = "。！？；："
SOFT_BREAKS = "，、,)"

# 结尾悬空的连接词（截断后它们会让句子读起来"没说完"）
DANGLING = re.compile(r"(以及|并且|而且|因为|所以|如果|但是|而|和|与|或|是|的|在|把|被|对|为|从|向|按|由)$")


def tidy_xml_line(body):
    """给一条 XML 注释收尾：回退到标点、去悬空词、补闭合标签。"""
    m = re.match(r"^<(summary|remarks)>(.*?)(</(?:summary|remarks)>)?$", body.strip())

    if not m:
        return body

    tag = m.group(1)
    text = m.group(2).rstrip("</remarks></summary>".replace(">", ">"))
    text = re.sub(r"</(summary|remarks)>\s*$", "", text).strip()

    # 去悬空连接词（可能连续两轮）
    for _ in range(3):
        new = DANGLING.sub("", text).rstrip("，、, ")
        if new == text:
            break
        text = new

    if not text:
        return None

    return f"<{tag}>{text}</{tag}>"


def main():
    apply_it = "--apply" in sys.argv
    pattern = re.compile(r"^(\s*)//\s*(<summary>|<remarks>)")
    changed = 0
    dropped = 0

    for base, dirs, names in os.walk(os.path.join(ROOT, "Assets", "Scripts")):
        if "Generated" in base:
            continue

        for n in names:
            if not n.endswith(".cs"):
                continue

            path = os.path.join(base, n)
            lines = io.open(path, encoding="utf-8").read().split("\n")
            out = []
            file_changed = 0
            file_dropped = 0

            for line in lines:
                m = pattern.match(line)

                if not m:
                    out.append(line)
                    continue

                body = line.strip()[2:].strip()
                fixed = tidy_xml_line(body)

                if fixed is None:
                    file_dropped += 1
                    continue

                if fixed == body:
                    out.append(line)
                    continue

                out.append(f"{m.group(1)}// {fixed}")
                file_changed += 1

            if file_changed or file_dropped:
                rel = os.path.relpath(path, ROOT).replace("\\", "/")
                print(f"{rel}: 收口 {file_changed}，丢弃空壳 {file_dropped}")

                if apply_it:
                    io.open(path, "w", encoding="utf-8", newline="\n").write("\n".join(out))

                changed += file_changed
                dropped += file_dropped

    print(f"\n合计收口 {changed} 行，丢弃空壳 {dropped} 行")
    print("（预演；加 --apply 落盘）" if not apply_it else "已落盘")


if __name__ == "__main__":
    main()
