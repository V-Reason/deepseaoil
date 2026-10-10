# -*- coding: utf-8 -*-
"""配表 Schema 生成器（战斗/元素反应重构批次）。

为什么要脚本而不是手改 Excel：
  * 本批改动同时触及 9 张表的列结构与数据，手改无法复核；
  * 枚举值是数字（Luban 按 ##enums__ 的数值列解析），手改极易串号；
  * 脚本进 git，schema 变更本身成为可审阅的产物。

跑法（仓库根）：
  python Tools/regen-config-tables.py

🔴 本脚本只写 ConfigWorkspace/Data/*.xlsx 源表；生成物（Generated/Config、
   StreamingAssets/Luban）由 Luban 导出 + Tools/luban-mirror.ps1 镜像，不在此处。
"""

import os
import shutil
import sys
from openpyxl import load_workbook

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from xlsx_surgery import clear_range, open_xlsx   # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(ROOT, "ConfigWorkspace", "Data")

# 本仓所有表的 worksheet 名都是默认的 Sheet1（Luban 不按 sheet 名取表，但 xlsx_surgery 需要）
SHEET = "Sheet1"

# ---------------------------------------------------------------------------
# 枚举定义：(全名, 唯一, [(键, 值, 别名), ...])
# 值与 ConfigWorkspace/Data/__enums__.xlsx 现行值保持一致，避免改动历史地块的枚举号。
# ---------------------------------------------------------------------------

ENUMS = [
    ("dso.BallType", True, [
        ("Water", 0, "纯水"),
        ("Earth", 1, "纯土"),
    ]),
    ("dso.TileStateType", True, [
        ("None", 0, "无状态"),
        ("Normal", 1, "空地"),
        ("Mud", 2, "普通泥浆"),
        ("MudSkid", 3, "稀泥"),
        ("Freeze", 5, "结冰"),
        ("TerracottaBrick", 6, "陶砖"),
        ("Steam", 8, "蒸汽"),
        ("BasicWater", 14, "基础水"),
        ("BasicEarth", 15, "基础土"),
        ("BasicFire", 16, "基础火池"),
        ("BasicIce", 17, "基础冰面"),
        ("BasicElectricity", 18, "基础电源"),
        ("BasicPlant", 19, "基础植物区"),
        ("ConductZone", 20, "导电区"),
        ("FlameField", 21, "燎原火海"),
        ("ChargedThorn", 22, "带电荆棘"),
        ("FrostSpike", 23, "霜冻冰刺"),
    ]),
    # 🔴 别名在同一枚举内必须唯一，且不能等于键名：`None` 的键与别名都写 "None"
    #    会报「枚举名:'SeedType' alias:'None' 重复」。填表侧一律用数字 0 引用 None。
    ("dso.SeedType", False, [
        ("None", 0, "空种子"),
        ("Fire", 1, "火种子"),
        ("Elec", 2, "电种子"),
        ("Ice", 3, "冰种子"),
        ("Plant", 4, "植物种子"),
    ]),
]

# 要保留下来的枚举全名（其余枚举块从 __enums__ 里物理删除）
KEEP_ENUMS = {name for name, _, _ in ENUMS}

# ---------------------------------------------------------------------------
# 表定义：列头行（##var / ##type / ##group / ##comment）＋ 数据行
# ---------------------------------------------------------------------------

TILE_STATE = dict(
    name="tile_state.xlsx",
    var=["id", "name", "duration", "slow_rate", "dot_damage", "is_obstacle", "is_conductor"],
    type=["TileStateType", "string!", "float", "float", "int", "bool", "bool"],
    comment=["地块id", "显示名", "存活秒数(-1=永久)", "移速倍率(1=不减速)",
             "每秒伤害(0=无)", "物理阻挡墙体", "网格关联导通"],
    rows=[
        [1, "空地", -1, 1.0, 0, "FALSE", "FALSE"],
        [14, "基础水", -1, 1.0, 0, "FALSE", "TRUE"],
        [15, "基础土", -1, 1.0, 0, "FALSE", "TRUE"],
        [2, "普通泥浆", 10, 0.5, 0, "FALSE", "TRUE"],
        [3, "稀泥", 8, 1.2, 0, "FALSE", "TRUE"],
        [16, "基础火池", -1, 1.0, 0, "FALSE", "FALSE"],
        [18, "基础电源", -1, 1.0, 0, "FALSE", "FALSE"],
        [17, "基础冰面", -1, 1.0, 0, "FALSE", "FALSE"],
        [19, "基础植物区", -1, 0.7, 0, "FALSE", "FALSE"],
        [6, "陶砖", 15, 1.0, 0, "TRUE", "FALSE"],
        [5, "结冰", 8, 1.0, 0, "FALSE", "FALSE"],
        [8, "蒸汽", 3, 1.0, 1, "FALSE", "FALSE"],
        # 导通必须是 TRUE：TileChainReactor 的泛洪起点也走 is_conductor 门禁，
        # 导电区自己不导通就没有任何一格能把电传下去（水砸电源的整条链会当场死掉）。
        [20, "导电区", 3, 1.0, 1, "FALSE", "TRUE"],
        [21, "燎原火海", 3, 1.0, 2, "FALSE", "FALSE"],
        [22, "带电荆棘", -1, 0.4, 1, "FALSE", "FALSE"],
        [23, "霜冻冰刺", -1, 0.5, 1, "FALSE", "FALSE"],
    ],
)

PROJECTILE = dict(
    name="projectile.xlsx",
    var=["id", "name"],
    type=["BallType", "string!"],
    comment=["球种ID", "显示名"],
    rows=[
        ["Water", "纯水"],
        ["Earth", "纯土"],
    ],
)

ELEMENT_RULE = dict(
    name="element_rule.xlsx",
    var=["id", "source_tile", "ball_type", "result_tile",
         "impact_damage", "impact_knockback", "impact_stun", "trigger_chain"],
    type=["int", "TileStateType", "BallType", "TileStateType",
          "int", "float", "float", "bool"],
    comment=["编号", "原格状态", "投入小球", "生成地貌",
             "瞬时伤害", "击退距离(格)", "麻痹时间(秒)", "触发网格连锁"],
    # 🔴 (source_tile, ball_type) 必须唯一：ReactionResolver 用它做 O(1) 字典键，
    #    重复键会静默后写覆盖，故本脚本结尾有查重断言。
    rows=[
        [1, "BasicEarth", "Water", "Mud", 0, 0, 0, "FALSE"],
        [2, "BasicWater", "Earth", "Mud", 0, 0, 0, "FALSE"],
        [3, "Mud", "Water", "MudSkid", 0, 2, 0, "FALSE"],
        # trigger_chain 只在结果地貌有连锁方向时才填 TRUE：可燃/火海走燎原，导通走导电。
        # 蒸汽两样都不是 —— 填 TRUE 只会在受击格上凭空补一次 1.5s 麻痹（详见 TileChainReactor.Propagate）。
        [4, "BasicFire", "Water", "Steam", 2, 3, 0, "FALSE"],
        [5, "BasicFire", "Earth", "TerracottaBrick", 0, 1, 0, "FALSE"],
        [6, "BasicElectricity", "Water", "ConductZone", 1, 0, 1.5, "TRUE"],
        [7, "BasicIce", "Water", "Freeze", 0, 3, 0, "FALSE"],
        [8, "BasicPlant", "Water", "Mud", 0, 0, 0, "FALSE"],
        [9, "Mud", "Earth", "TerracottaBrick", 0, 1, 0, "FALSE"],
        # 空地兜底：落到表里而不是只靠 C# 兜底，配置才是唯一可信数据源。
        [10, "Normal", "Water", "BasicWater", 0, 0, 0, "FALSE"],
        [11, "Normal", "Earth", "BasicEarth", 0, 0, 0, "FALSE"],
    ],
)

ELEMENT_DUO = dict(
    name="element_duo_reaction.xlsx",
    var=["id", "elem_a", "elem_b", "result_tile",
         "impact_damage", "impact_knockback", "result_duration", "effect_radius", "trigger_chain"],
    type=["int", "TileStateType", "TileStateType", "TileStateType",
          "int", "float", "float", "float", "bool"],
    comment=["编号", "元素发生器A", "元素发生器B", "激发出的地貌",
             "瞬发伤害", "击退距离(格)", "结果存续(秒)", "波及半径(格)", "连锁模式"],
    rows=[
        [1, "BasicFire", "BasicPlant", "FlameField", 0, 0, 3, 1, "TRUE"],
        # 产物是蒸汽/结冰的行不许填 TRUE：两者既不可燃也不导通，连锁无处可去
        [2, "BasicFire", "BasicElectricity", "Steam", 4, 4, 2, 1.5, "FALSE"],
        [3, "BasicIce", "BasicElectricity", "Freeze", 0, 0, 4, 2.5, "FALSE"],
        [4, "BasicElectricity", "BasicPlant", "ChargedThorn", 1, 0, 3, 1, "TRUE"],
        [5, "BasicIce", "BasicFire", "Normal", 0, 0, 6, 2.5, "FALSE"],
        [6, "BasicIce", "BasicPlant", "FrostSpike", 1, 0, 3, 1, "FALSE"],
    ],
)

SEED = dict(
    name="seed.xlsx",
    var=["id", "name", "spawn_tile", "icon_key"],
    type=["SeedType", "string!", "TileStateType", "string"],
    comment=["种子ID", "显示名", "播种生成设施地貌", "图标资源Key"],
    rows=[
        ["Fire", "火种子", "BasicFire", "Icons/Seed_Fire"],
        ["Elec", "电种子", "BasicElectricity", "Icons/Seed_Elec"],
        ["Ice", "冰种子", "BasicIce", "Icons/Seed_Ice"],
        ["Plant", "植物种子", "BasicPlant", "Icons/Seed_Plant"],
    ],
)

WAVE = dict(
    name="wave.xlsx",
    var=["id", "name", "prep_time", "battle_time", "settle_time",
         "enemies_per_wave", "spawn_interval", "spawn_radius", "grant_seed"],
    type=["int", "string!", "float", "float", "float",
          "int!", "float!", "float!#range=[0.5,50]", "SeedType"],
    comment=["波次", "显示名", "备战时长", "战斗时长", "结算时长",
             "敌人数", "刷怪间隔", "出生环半径", "战备配给种子"],
    rows=[
        [1, "首波侵袭", 10, 30, 8, 6, 0.4, 5, "Fire"],
        [2, "疾跑蜂拥", 10, 30, 8, 10, 0.3, 5, "Elec"],
        [3, "精英试炼", 10, 30, 8, 12, 0.25, 5, "Ice"],
    ],
)

PLAYER = dict(
    name="player.xlsx",
    var=["id", "name", "max_hp", "contact_damage", "invulnerable_duration", "retry_delay",
         "attack_interval", "knockback_impulse", "knockback_speed_limit", "contact_radius",
         "water_capacity", "water_start", "earth_capacity", "earth_start", "life_heal_interval"],
    type=["int", "string!", "float!#range=[1,99999]", "float!#range=[0,9999]",
          "float!#range=[0,10]", "float!#range=[0,60]", "float!#range=[0.01,10]",
          "float!#range=[0,100]", "float!#range=[0,100]", "float!#range=[0.05,10]",
          "int!#range=[0,99]", "int!#range=[0,99]", "int!#range=[0,99]", "int!#range=[0,99]",
          "float!#range=[0.1,60]"],
    comment=["编号", "显示名", "血量上限", "敌人贴身一次造成的伤害", "受击无敌时长", "打空后重来延时",
             "攻击间隔", "受击被推开的冲量", "受击期间的速度上限", "敌人贴身判定的圆心距（世界单位）",
             "水弹药上限", "开局水弹药", "土弹药上限", "开局土弹药", "神泉回血所需静止秒数"],
    rows=[
        [1, "玩家", 3, 1, 0.8, 1.2, 0.35, 12, 12, 1, 5, 5, 5, 5, 3],
    ],
)

ENEMY = dict(
    name="enemy.xlsx",
    var=["id", "name", "max_speed", "hp", "contact_damage"],
    type=["int", "string!", "float!#range=[0,50]", "int!#range=[1,999]", "int!#range=[0,999]"],
    comment=["编号", "显示名", "追击满速", "耐久", "贴身一次造成的伤害"],
    rows=[
        [1, "普通怪", 3.5, 3, 1],
        # 预留行：当前 CombatDirector 按 Ids.Enemy(1) 生成，第 2 行是多敌种刷怪的前置数据。
        [2, "精英怪", 4.2, 6, 2],
    ],
)

TABLES = [
    ("dso.TbProjectile", "Projectile", True, "projectile.xlsx", "id", "c", "投掷物表", ""),
    ("dso.TbEnemy", "Enemy", True, "enemy.xlsx", "id", "c", "敌人表", ""),
    ("dso.TbTileState", "TileState", True, "tile_state.xlsx", "id", "c", "格子状态表", ""),
    ("dso.TbPlayer", "Player", True, "player.xlsx", "id", "c", "玩家数值表", ""),
    ("dso.TbWave", "Wave", True, "wave.xlsx", "id", "c", "波次表", ""),
    ("dso.TbTileInitial", "TileInitial", True, "tile_initial.xlsx", "id", "c", "关卡初始格子状态", ""),
    ("dso.TbElementRule", "ElementRule", True, "element_rule.xlsx", "id", "c", "元素反应规则", ""),
    ("dso.TbSeed", "Seed", True, "seed.xlsx", "id", "c", "种子基建配置表", ""),
    ("dso.TbElementDuoReaction", "ElementDuoReaction", True, "element_duo_reaction.xlsx", "id",
     "c", "二级元素跨界反应表", ""),
]


# ---------------------------------------------------------------------------
# 工具
# ---------------------------------------------------------------------------

def sheet0(wb):
    return wb[wb.sheetnames[0]]


def write_header_4plus(ws, var, typ, comment, group="c", extra_left=4):
    ws.cell(row=1, column=1, value="##var")
    ws.cell(row=2, column=1, value="##type")
    ws.cell(row=3, column=1, value="##group")
    ws.cell(row=4, column=1, value="##comment")
    for i in range(len(var)):
        col = extra_left + 1 + i
        ws.cell(row=1, column=col, value=var[i])
        ws.cell(row=2, column=col, value=typ[i])
        ws.cell(row=3, column=col, value=group)
        ws.cell(row=4, column=col, value=comment[i])


def fresh_sheet(wb):
    """清空并重建第一张工作表，只保留 sheet 名。

    🔴 必须整表重建、且**一格旧表头都不留**：表头是 Luban 的 schema 来源
       （`__tables__` 的 read_schema_from_file 全为真），旧表头比新表头宽时
       （删列后）右侧会留下孤儿列，报「字段 type 类型 ElementType 非法」这类
       幽灵字段错误 —— 明明数据行已经写对了。
       表头四行由 write_header_4plus 按新宽度全量重写，所以无需保留任何旧行。
       sheet 名必须保留，Luban 按 sheet 名取表（本仓所有表的 sheet 名都是 Sheet1）。
    """
    name = wb[wb.sheetnames[0]].title

    wb.remove(wb[wb.sheetnames[0]])

    return wb.create_sheet(title=name)


def load_or_new(path):
    if os.path.exists(path):
        return load_workbook(path)
    from openpyxl import Workbook
    return Workbook()


def numeric_cells(spec, rows):
    """把地块列折成数字。

    🔴 为什么只有地块列转数字：tile_state.id 现行就是数字（42/47/53...），
       且 multiple-enum 之间共用 "None=0" 这类值，字符串名在部分列上会歧义；
       而 BallType 列现行数据就是中文别名，原样保留最稳。
    """
    members = next(m for n, _, m in ENUMS if n == "dso.TileStateType")
    value_of = {key: value for key, value, _ in members}
    cols = [i for i, t in enumerate(spec["type"]) if t == "TileStateType"]
    if not cols:
        return rows
    out = []
    for row in rows:
        row = list(row)
        for i in cols:
            v = row[i]
            if isinstance(v, str):
                row[i] = value_of[v]
        out.append(row)
    return out


def write_table(spec):
    path = os.path.join(DATA, spec["name"])
    wb = load_or_new(path)
    ws = fresh_sheet(wb)

    write_header_4plus(ws, spec["var"], spec["type"], spec["comment"])

    for r, data in enumerate(numeric_cells(spec, spec["rows"]), start=5):
        for i, v in enumerate(data):
            ws.cell(row=r, column=4 + 1 + i, value=v)

    wb.save(path)
    print(f"  写出 {spec['name']}: {len(spec['var'])} 列 × {len(spec['rows'])} 行")


def write_enums():
    """就地改写 __enums__.xlsx 的枚举块（第 4 行起），保留 1-3 行表头。

    🔴 四条实测结论（全踩过；改用 Tools/xlsx_surgery.py 直接改单元格 XML，
       openpyxl 的「读入 → 存回」在本文件上会破坏 Luban 对嵌套子表头的解析）：
       ① 不能重建工作表：1-3 行表头里有嵌套子表头（第 2 行 H 列起的 ##var 定义
          __EnumItem__.name/alias/value/comment/tags），重建后 Luban 报
          「bean:'__intern__.__EnumItem__' 缺失列:'alias'」。
       ② B 列（full_name）只在**块的首行**有值，成员行留空；每行都写会被当成多块，
          报「类型 'dso.Xxx' 重复」。
       ③ 改写前必须把整个数据区清空：仓库原版里 `dso.Quality` 在表头下多声明过一次
          （B4 与 B7 两处），只在原内容上覆盖会留下孤儿成员行，报「重复」或
          「[Bxx] 字段不允许为空」。
       ④ alias 是必填列（留空报「缺失列:'alias'」），没给别名时回落到枚举键名。
    """
    path = os.path.join(DATA, "__enums__.xlsx")
    book = open_xlsx(path)

    clear_from = 4
    clear_to = max(128, book_row_count(path))

    clear_range(book, SHEET, clear_from, clear_to, 1, 13)

    rows = []
    for full, unique, members in ENUMS:
        for k, (key, value, alias) in enumerate(members):
            rows.append((full if k == 0 else None, 0, 1 if unique else 0, key,
                         alias if alias is not None else key, value))
        rows.append(None)   # 块间空行

    for i, row in enumerate(rows):
        r = clear_from + i
        if row is None:
            continue
        full, flags, unique, key, alias, value = row
        if full is not None:
            book.set_cell(SHEET, r, 2, full, as_string=True)
            book.set_cell(SHEET, r, 3, flags)
            book.set_cell(SHEET, r, 4, unique)
            book.set_cell(SHEET, r, 5, "c", as_string=True)
        book.set_cell(SHEET, r, 8, key, as_string=True)
        book.set_cell(SHEET, r, 9, alias, as_string=True)
        if value is not None:
            book.set_cell(SHEET, r, 10, value)

    book.save()
    print(f"  写出 __enums__.xlsx: {len(ENUMS)} 个枚举块 / {len(rows)} 行")


def book_row_count(path):
    """已有行数估计：用于决定清理到哪一行，避免留下孤儿成员行。"""
    book = open_xlsx(path)
    lo, hi = 4, 4096
    while lo < hi:
        mid = (lo + hi + 1) // 2
        if book.get_cell(SHEET, mid, 1) is not None or book.get_cell(SHEET, mid, 2) is not None \
                or book.get_cell(SHEET, mid, 8) is not None:
            lo = mid
        else:
            hi = mid - 1
    return lo


def write_tables():
    """重写 __tables__.xlsx 的数据行。

    🔴 不要动 1-3 行的表头结构：它是 Luban 的 dict 头 + 「##」嵌套定义机制
       （A 列 ##var/## 引导，J/K/N/O 列是 field 级的 name/alias/type/comment）。
       把它拉平成一行 var 会报「存在多个无 variant 的 fallback 定义」。
    """
    path = os.path.join(DATA, "__tables__.xlsx")
    wb = load_workbook(path)
    ws = sheet0(wb)

    for row in ws.iter_rows(min_row=4, max_row=max(ws.max_row, 64), max_col=11):
        for c in row:
            c.value = None

    for k, (full, value_type, read_schema, inp, index, group, comment, output) in enumerate(TABLES):
        r = 4 + k
        ws.cell(row=r, column=2, value=full)
        ws.cell(row=r, column=3, value=value_type)
        ws.cell(row=r, column=4, value=1 if read_schema else 0)
        ws.cell(row=r, column=5, value=inp)
        ws.cell(row=r, column=6, value=index)
        ws.cell(row=r, column=7, value=None)
        ws.cell(row=r, column=8, value=group)
        ws.cell(row=r, column=9, value=comment)
        ws.cell(row=r, column=11, value=output if output else None)

    wb.save(path)
    print(f"  写出 __tables__.xlsx: {len(TABLES)} 张表登记")


def assert_unique_rule_keys():
    seen = {}
    for row in ELEMENT_RULE["rows"]:
        key = (row[1], row[2])
        if key in seen:
            sys.exit(f"🔴 element_rule 重复键 {key}（id={row[0]} 与 id={seen[key]}）")
        seen[key] = row[0]
    print(f"  element_rule 键唯一性: {len(seen)} 个 (source_tile, ball_type) 组合无冲突")


def assert_fk():
    """source/result/elem/spawn 用到的地块必须在 tile_state 里有行。

    🔴 数据单元格里地块列写的是**数字**（与现行 element_rule/tile_state 一致），
       所以这里要把枚举键折成值再比，否则断言会在正确数据上假红。
    """
    members = next(m for n, _, m in ENUMS if n == "dso.TileStateType")
    value_of = {key: value for key, value, _ in members}
    states = {r[0] for r in TILE_STATE["rows"]}

    def cell(v):
        return v if isinstance(v, int) else value_of[v]

    bad = []
    for row in ELEMENT_RULE["rows"]:
        for idx in (1, 3):
            if cell(row[idx]) not in states:
                bad.append(f"element_rule id={row[0]} 引用了无行的地块 {row[idx]}")
    for row in ELEMENT_DUO["rows"]:
        for idx in (1, 2, 3):
            if cell(row[idx]) not in states:
                bad.append(f"element_duo id={row[0]} 引用了无行的地块 {row[idx]}")
    for row in SEED["rows"]:
        if cell(row[2]) not in states:
            bad.append(f"seed {row[0]} 的 spawn_tile={row[2]} 在 tile_state 里没有行")
    if bad:
        sys.exit("🔴 外键校验失败:\n  " + "\n  ".join(bad))
    print(f"  外键校验: 全部地块引用都有 tile_state 行（共 {len(states)} 行）")


def remove_tile_effect():
    p = os.path.join(DATA, "tile_effect.xlsx")
    if os.path.exists(p):
        os.remove(p)
        print("  删除 tile_effect.xlsx（温湿电效果档位表退役）")


def main():
    if not os.path.isdir(DATA):
        sys.exit(f"找不到配表目录: {DATA}")

    print("[1/5] 枚举 __enums__.xlsx")
    write_enums()

    print("[2/5] 表登记 __tables__.xlsx")
    write_tables()

    print("[3/5] 业务表数据")
    for spec in (TILE_STATE, PROJECTILE, ELEMENT_RULE, ELEMENT_DUO, SEED, WAVE, PLAYER, ENEMY):
        write_table(spec)

    print("[4/5] 断言")
    assert_unique_rule_keys()
    assert_fk()

    print("[5/5] 清理")
    remove_tile_effect()

    print("完成。下一步：ConfigWorkspace 下跑 Luban 校验（-f -x outputSaver=null）。")


if __name__ == "__main__":
    main()
