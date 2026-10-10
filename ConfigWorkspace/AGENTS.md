# Agent 指南（本工程配表工作区）

本目录是 `deepseaoil` 的 Luban 配表工作区。AI / Agent 改配置或改表前请遵守：

## 硬性约定

1. **Schema 是契约**：不要为了让生成通过而擅自改类型 / 主键语义；应修数据，或与维护者确认后再改 schema。
2. 新表必须写进 `Data/__tables__.xlsx`（或对应 schema），否则不会被收集。
3. Excel sheet 的 A1 必须以 `##` 开头。
4. 分组：`c` 客户端、`s` 服务器、`e` 编辑器。
5. 校验可加 `--strict`；给人排错优先看中文原文，给机器排错可加 `--errorFormat json`。

## 结构

- `luban.conf`：groups / schemaFiles / dataDir / targets。**不含 `pathValidator.rootDir`**——它由命令行 `-x` 注入。
- `Defines/`：XML 定义（`builtin.xml` 提供 `vector2/3/4`）
- `Data/`：Excel 数据与 `__tables__` / `__beans__` / `__enums__`
  - 当前 9 张表**全部登记在 `__tables__.xlsx`**；加新表必须在这里加一行，否则不会被收集
  - `__tables__.xlsx` 的 `output` 列决定生成 JSON 的文件名（`dso.TbEnemy` → `dso_tbenemy`）；留空默认 `<模块>_<表名>`
  - 🔴 **改表优先用 `python Tools/regen-config-tables.py`**（在仓库根跑）：它按一处定义重写 9 张表的列结构与数据，自带 `(source_tile, ball_type)` 查重与外键校验。手改 xlsx 的两条实测坑（重建工作表破坏嵌套子表头、`full_name` 只在块首行有值）见 `Docs/表格数据配置/技术文档_配表管线.md` 第 2 节
  - `Data/` 下**只有 xlsx**，没有 `.txt` / `.md` 手记快照（旧快照已删，它们与 schema 脱节会误导改表的人）
- `Tools/Luban/`：Luban 工具本体（走 Git LFS）
- `output/`：中间产物（gitignore）。**Luban 先写这里，成功后才镜像拷贝进 Assets**
- `logs/`：每次运行的完整输出（gitignore）

## 导入命令（由 Unity 菜单调用，不要做成 .bat）

```text
dotnet Tools/Luban/Luban.dll --conf luban.conf -t client --strict \
  -c cs-simple-json -d json \
  -x outputCodeDir=output/code -x outputDataDir=output/data \
  -x pathValidator.rootDir=<工程>/Assets
```

工作目录必须是本目录（`ConfigWorkspace`）——conf 里全是相对路径。

## 🔴 两条容易踩的坑（实测结论）

1. **`--strict` 只决定退出码，不阻止写盘**。Luban 在校验失败后**照样写出全部文件**。
   所以 `-x outputCodeDir/outputDataDir` 绝不能直接指向 `Assets/`——那会让失败的导入
   当场删掉并重写整个生成目录。必须走 `output/` 暂存 + 成功后镜像拷贝。
2. **仅校验模式必须同时加 `-f` 和 `-x outputSaver=null`**（实测零写入）。
   `-f`（不产出）与 `--strict`（失败算失败）是两件独立的事。

## 生成物去向（🔴 手写文件不得放入）

- `Assets/Scripts/Generated/Config/` ← 生成的 C#（进 git）
- `Assets/StreamingAssets/Luban/` ← 生成的 JSON（进 git）

两个目录导入时会被镜像覆盖（多余文件删除）。
（第三处生成物是 `Assets/Scripts/Generated/Input/InputSys.cs`，由 `.inputactions` 生成，
不在导表链路里，但同样手改必丢。）

## 相关文档

- `Docs/表格数据配置/技术文档_配表管线.md`：契约、参数、纪律、排障（面向程序）
- `Docs/表格数据配置/策划手册_数据表填写.md`：单元格层面的动作与报错对照（面向策划）

## 🔴 不要去装 Luban 的 UPM 包

Luban 运行库已有一份**进 git 的本地拷贝** `Assets/Luban.Runtime/`（7 个 `.cs`）。
**不要**往 `Packages/manifest.json` 里加
`"com.code-philosophy.luban": "https://github.com/focus-creative-games/luban_unity.git"`：
两个来源的 asmdef 都叫 `Luban.Runtime`，撞名后报
`Assembly with name 'Luban.Runtime' already exists`，
并导致**整个工程所有程序集都编译不出来**。这是有意的本地拷贝决策（避开 Jam 期间联网拉包），
详见 `Docs/表格数据配置/技术文档_配表管线.md` 第 1 节。
