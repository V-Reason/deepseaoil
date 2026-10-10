# -*- coding: utf-8 -*-
"""就地改写 xlsx 工作表的单元格值（保持原 XML 结构，只用 zipfile + ElementTree）。

🔴 为什么要这么底层：
  openpyxl 的「读入 → 存回」在**业务表**上是安全的，但在 `__enums__.xlsx` 上会让
  Luban 报「缺失列:'alias'」或「类型重复」—— 那是 Luban 对非连续/空行的分组规则
  与 openpyxl 写出的行结构相互作用的结果。本模块直接改单元格 XML，其余字节原样保留，
  把这个变量彻底排除掉。

用法：
    from xlsx_surgery import open_xlsx
    with open_xlsx(path) as book:
        book.set_cell("Sheet1", 4, 2, "dso.Quality")
        book.set_cell("Sheet1", 4, 8, "COMMON", as_string=True)
        book.save()

单元格值约定：
    set_cell(..., value=None)                 → 清空该格（值对齐空字符串，与 Excel 清空一致）
    set_cell(..., value=数字)                  → 数值型
    set_cell(..., value="文本", as_string=True) → 共享字符串（写入 xl/sharedStrings.xml）
"""

import os
import re
import shutil
import zipfile
import xml.etree.ElementTree as ET

NS_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
NS_REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

ET.register_namespace("", NS_MAIN)
ET.register_namespace("r", NS_REL)


def col_letters(index):
    """1-based 列号 → 字母（1=A, 27=AA）。"""
    out = ""
    while index > 0:
        index, rem = divmod(index - 1, 26)
        out = chr(ord("A") + rem) + out
    return out


class XlsxBook:
    def __init__(self, path):
        self.path = path
        with zipfile.ZipFile(path) as z:
            self.parts = {n: z.read(n) for n in z.namelist()}
            self.namelist = z.namelist()

        self.shared = []
        if "xl/sharedStrings.xml" in self.parts:
            root = ET.fromstring(self.parts["xl/sharedStrings.xml"])
            for si in root.findall(f"{{{NS_MAIN}}}si"):
                self.shared.append("".join(t.text or "" for t in si.iter(f"{{{NS_MAIN}}}t")))

        # sheet 名 → sheet xml 路径（按 workbook.xml 的 r:id 经 rels 解析）
        self.sheet_path = {}
        wb = ET.fromstring(self.parts["xl/workbook.xml"])
        rels = ET.fromstring(self.parts["xl/_rels/workbook.xml.rels"])
        rid2target = {r.get("Id"): r.get("Target") for r in rels}
        for sh in wb.find(f"{{{NS_MAIN}}}sheets"):
            rid = sh.get(f"{{{NS_REL}}}id")
            target = rid2target.get(rid, "")
            if target.startswith("/xl/"):
                target = target[1:]
            elif not target.startswith("xl/"):
                target = "xl/" + target.lstrip("/")
            self.sheet_path[sh.get("name")] = target

        self.trees = {}
        for name, p in self.sheet_path.items():
            self.trees[name] = ET.fromstring(self.parts[p])

    # ---------------------------------------------------------------- 读

    def get_cell(self, sheet, row, col):
        ws = self.trees[sheet]
        ref = f"{col_letters(col)}{row}"
        sd = ws.find(f"{{{NS_MAIN}}}sheetData")
        for r in sd.findall(f"{{{NS_MAIN}}}row"):
            if r.get("r") != str(row):
                continue
            for c in r.findall(f"{{{NS_MAIN}}}c"):
                if c.get("r") != ref:
                    continue
                v = c.find(f"{{{NS_MAIN}}}v")
                if v is None:
                    return None
                if c.get("t") == "s":
                    return self.shared[int(v.text)]
                return v.text
        return None

    # ---------------------------------------------------------------- 写

    def set_cell(self, sheet, row, col, value, as_string=None):
        ws = self.trees[sheet]
        sd = ws.find(f"{{{NS_MAIN}}}sheetData")
        ref = f"{col_letters(col)}{row}"

        r = None
        for cand in sd.findall(f"{{{NS_MAIN}}}row"):
            if cand.get("r") == str(row):
                r = cand
                break
        if r is None:
            r = ET.SubElement(sd, f"{{{NS_MAIN}}}row", {"r": str(row)})
            self._resort_rows(sd)

        c = None
        for cand in r.findall(f"{{{NS_MAIN}}}c"):
            if cand.get("r") == ref:
                c = cand
                break
        if c is None:
            c = ET.SubElement(r, f"{{{NS_MAIN}}}c", {"r": ref})
            self._resort_cells(r)

        for child in list(c):
            c.remove(child)

        if value is None:
            if c.get("t") is not None:
                del c.attrib["t"]
            return

        if as_string is None:
            as_string = isinstance(value, str)

        if as_string:
            try:
                idx = self.shared.index(str(value))
            except ValueError:
                idx = len(self.shared)
                self.shared.append(str(value))
            c.set("t", "s")
            v = ET.SubElement(c, f"{{{NS_MAIN}}}v")
            v.text = str(idx)
        else:
            if value is True or value is False:
                c.set("t", "b")
                v = ET.SubElement(c, f"{{{NS_MAIN}}}v")
                v.text = "1" if value else "0"
            else:
                if c.get("t") is not None:
                    del c.attrib["t"]
                v = ET.SubElement(c, f"{{{NS_MAIN}}}v")
                v.text = str(value)

    @staticmethod
    def _resort_rows(sd):
        rows = sorted(sd.findall(f"{{{NS_MAIN}}}row"), key=lambda e: int(e.get("r")))
        for r in rows:
            sd.remove(r)
        for r in rows:
            sd.append(r)

    @staticmethod
    def _resort_cells(r):
        def key(e):
            m = re.match(r"([A-Z]+)\d+", e.get("r"))
            letters = m.group(1)
            n = 0
            for ch in letters:
                n = n * 26 + (ord(ch) - ord("A") + 1)
            return n

        cells = sorted(r.findall(f"{{{NS_MAIN}}}c"), key=key)
        for c in cells:
            r.remove(c)
        for c in cells:
            r.append(c)

    # ---------------------------------------------------------------- 存

    def save(self, path=None):
        path = path or self.path
        tmp = path + ".tmp"

        shared_xml = ET.tostring(self._shared_root(), encoding="UTF-8", xml_declaration=True)

        with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as z:
            for name in self.namelist:
                if name == "xl/sharedStrings.xml":
                    z.writestr(name, shared_xml)
                    continue
                if name in self.sheet_path.values():
                    sheet = next(s for s, p in self.sheet_path.items() if p == name)
                    z.writestr(name, ET.tostring(self.trees[sheet], encoding="UTF-8",
                                                 xml_declaration=True))
                    continue
                z.writestr(name, self.parts[name])

        shutil.move(tmp, path)

    def _shared_root(self):
        root = ET.Element(f"{{{NS_MAIN}}}sst",
                          {"count": str(len(self.shared)), "uniqueCount": str(len(self.shared))})
        for text in self.shared:
            si = ET.SubElement(root, f"{{{NS_MAIN}}}si")
            t = ET.SubElement(si, f"{{{NS_MAIN}}}t")
            t.text = text
            if text != text.strip():
                t.set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
        return root


def open_xlsx(path):
    return XlsxBook(path)


def clear_range(book, sheet, row_from, row_to, col_from, col_to):
    for r in range(row_from, row_to + 1):
        for c in range(col_from, col_to + 1):
            book.set_cell(sheet, r, c, None)


def sheet_names(path):
    with zipfile.ZipFile(path) as z:
        wb = ET.fromstring(z.read("xl/workbook.xml"))
    return [sh.get("name") for sh in wb.find(f"{{{NS_MAIN}}}sheets")]


if __name__ == "__main__":
    import sys
    for p in sys.argv[1:]:
        print(os.path.basename(p), "→", sheet_names(p))
