// ---------------------------------------------------------------------------
// 配表管线 · 行为测试
//
// 判据：只留「错了会静默出事」的断言。
//
//   · 参数串缺 --strict        → 校验失败仍返回退出码 0，整套校验静默失效
//   · 仅校验模式缺 -f / outputSaver=null → 校验模式也会写盘
//   · 工作目录不是 workspace   → conf 里全是相对路径，基准一错全错
//   · 暂存区落进 Assets        → Luban 校验失败也写盘，一次失败导入当场毁掉生成目录
//   · 引号转义错               → 路径带空格 / 结尾反斜杠时命令行破损（只测 Windows 分支：
//                                Quote.cs 的 Posix 分支在 Windows 上不是生产路径，那两条已删）
//   · 日志解析错               → 不知道哪些文件被写
//   · 预检失效                 → 坏工具链不被拦下，报的是原生英文错
//                               （"有锁文件则 pre.Ok 必须为 false"那条分支不可达，一并删）
//
// 【刻意删掉的一类断言】读 .cs 源码 grep 中文提示 / 注释文本
// （旧 B1/B2/B3/D1/D2/D3/E1/E2/E3/F1/H1/M1，共 11 项）。
// 那类断言不测行为、只测文本：改个措辞就红，真正的逻辑回归却不报。
// 其中旧 D2「预检在启动进程之前」本身是**空断言**——取了两个下标却从不比较。
// 删掉它们顺带消除了一个隐患：旧 FindRepoFile 找不到源码时返回空串，
// 于是 `Assert.IsFalse(src.Contains(...))` 型断言会**静默假通过**。
//
// 跑法：Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All
// ---------------------------------------------------------------------------

using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeepseaOil.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace DeepseaOil.EditorTools.Tests
{
    public class LubanWorkflowTests
    {
        // ================================================================
        // 1 · 参数串完整性
        // ================================================================

        [Test]
        public void 导表参数串_含strict与两个输出目录()
        {
            var argv = LubanProject.BuildExportCommand().Argv;
            var all = string.Join(" ", argv);

            Assert.Contains("--strict", argv,
                "缺 --strict → 校验失败仍返回退出码 0，整套校验静默失效");
            Assert.Contains(LubanProject.LubanConf, argv, "--conf 必须指向推算出的 luban.conf");
            Assert.Contains("client", argv, "target 必须是 client");
            Assert.Contains("cs-simple-json", argv, "缺代码目标 -c cs-simple-json");
            Assert.Contains("json", argv, "缺数据目标 -d json");

            // 三个 -x：代码目录、数据目录、路径校验基准
            Assert.IsTrue(all.Contains("outputCodeDir="), "缺 -x outputCodeDir");
            Assert.IsTrue(all.Contains("outputDataDir="), "缺 -x outputDataDir");
            Assert.IsTrue(all.Contains("pathValidator.rootDir=" + Application.dataPath),
                "pathValidator.rootDir 必须等于 Assets 的绝对路径（表里 #path=unity 以此为基准）");

            // -x 必须成对：每个 -x 后面都跟一个 key=value
            int keyValues = argv.Count(a => a.StartsWith("outputCodeDir=")
                || a.StartsWith("outputDataDir=") || a.StartsWith("pathValidator.rootDir="));
            Assert.AreEqual(argv.Count(a => a == "-x"), keyValues, "-x 与 key=value 数量不匹配");
        }

        [Test]
        public void 仅校验模式_零写入()
        {
            var argv = LubanProject.BuildValidateCommand().Argv;
            var all = string.Join(" ", argv);

            Assert.Contains("--strict", argv, "仅校验模式也要 --strict");
            Assert.Contains("-f", argv, "缺 -f（只校验不产出）——它与 --strict 是两件独立的事");
            Assert.IsTrue(all.Contains("outputSaver=null"),
                "只带 -f 不保证零写入，必须同时 -x outputSaver=null");

            Assert.AreEqual(0, argv.Count(a => a.StartsWith("outputCodeDir=")),
                "仅校验模式不得带 outputCodeDir");
            Assert.AreEqual(0, argv.Count(a => a.StartsWith("outputDataDir=")),
                "仅校验模式不得带 outputDataDir");
        }

        // ================================================================
        // 2 · 工作目录与暂存区隔离 —— 「失败导入不污染工程」的全部基础
        // ================================================================

        [Test]
        public void 工作目录_必须是workspace()
        {
            Assert.AreEqual(LubanProject.Workspace, LubanProject.BuildExportCommand().WorkingDirectory,
                "WorkingDirectory 必须是 workspace：conf 里全是相对路径，CWD 错则基准错");
            Assert.AreEqual(LubanProject.Workspace, LubanProject.BuildValidateCommand().WorkingDirectory,
                "仅校验模式同样要设 WorkingDirectory");
            Assert.IsTrue(Directory.Exists(LubanProject.Workspace),
                "workspace 目录不存在：" + LubanProject.Workspace);
        }

        [Test]
        public void 暂存区_不在Assets内()
        {
            // 🔴 Luban 在校验失败后照样写盘，所以它的落盘位置不能是 Assets。
            //    这条断了，「一次失败的导入当场删掉整个生成目录」就会发生。
            Assert.IsTrue(LubanProject.StageCodeDir.StartsWith(LubanProject.Workspace),
                "暂存代码目录必须在 ConfigWorkspace 内：" + LubanProject.StageCodeDir);
            Assert.IsTrue(LubanProject.StageDataDir.StartsWith(LubanProject.Workspace),
                "暂存数据目录必须在 ConfigWorkspace 内：" + LubanProject.StageDataDir);
            Assert.IsFalse(LubanProject.StageCodeDir.StartsWith(Application.dataPath),
                "🔴 暂存目录不能在 Assets 内");
            Assert.IsFalse(LubanProject.StageDataDir.StartsWith(Application.dataPath),
                "🔴 暂存目录不能在 Assets 内");

            // 发布目录必须落在工程内，且与暂存区不同（两者是镜像拷贝的源与目标）
            Assert.IsTrue(LubanProject.OutputCodeDir.StartsWith(Application.dataPath),
                "发布目录（代码）应在 Assets 内：" + LubanProject.OutputCodeDir);
            Assert.IsTrue(LubanProject.OutputDataDir.StartsWith(Application.dataPath),
                "发布目录（数据）应在 Assets 内：" + LubanProject.OutputDataDir);
        }

        // ================================================================
        // 3 · 命令行引号（纯函数，边界出过错）
        // ================================================================

        [Test]
        public void 引号_Windows含空格与结尾反斜杠()
        {
            // 含空格 → 包裹
            Assert.AreEqual("\"T:\\My Games\\Jam\\Assets\"",
                Quote.QuoteOneForWindows(@"T:\My Games\Jam\Assets"));

            // 🔴 规则：需要包裹时，结尾连续反斜杠数量翻倍（C:\x\ → "C:\x\\"），
            //    否则收尾引号会被反斜杠吃掉。
            //    用字符级结构比较，不用肉眼数反斜杠——字面量里数反斜杠是出过错的。
            AssertTrailingBackslashes(Quote.QuoteOneForWindows(@"C:\x y\"), 2, "含空格 + 结尾 1 个反斜杠");
            AssertTrailingBackslashes(Quote.QuoteOneForWindows("C:\\x y\\\\"), 4, "含空格 + 结尾 2 个反斜杠");
            AssertTrailingBackslashes(Quote.QuoteOneForWindows(@"C:\x y"), 0, "含空格 + 结尾无反斜杠");

            // 不需要包裹的情形：原样返回，结尾反斜杠无需翻倍（没有引号可吃）
            Assert.AreEqual(@"C:\x\", Quote.QuoteOneForWindows(@"C:\x\"),
                "无空格无引号 → 原样返回，不包裹");
            Assert.AreEqual("\\", Quote.QuoteOneForWindows("\\"));
            Assert.AreEqual("C:\\x\\\\", Quote.QuoteOneForWindows("C:\\x\\\\"));

            // 内部引号转义 + 空串
            Assert.AreEqual("\"a\\\"b\"", Quote.QuoteOneForWindows("a\"b"));
            Assert.AreEqual("\"\"", Quote.QuoteOneForWindows(""));
            Assert.AreEqual("\"a b\"", Quote.QuoteOneForWindows("a b"));

            // 整串组装：参数间空格分隔，只包裹需要包裹的
            Assert.AreEqual("dotnet \"/p/a b/Luban.dll\" --strict",
                Quote.QuoteForWindows(new[] { "dotnet", "/p/a b/Luban.dll", "--strict" }));
        }

        // 已删：引号_Posix单引号 —— Assets/Editor/Quote.cs 的 Posix 分支在 Windows 上
        // 根本不是生产路径（导表工具链只走 QuoteForWindows / QuoteOneForWindows），
        // 测它只会在改 Windows 分支时替 Posix 分支保持"绿"，没有任何运行时静默失效面。

        // ================================================================
        // 4 · Luban 日志解析
        // ================================================================

        [Test]
        public void 日志解析_写出文件清单()
        {
            const string sample =
                "2026/09/30 01:36:39.000|INFO|process data target:\"json\" begin\n" +
                "2026/09/30 01:36:39.001|INFO|[new] T:\\x\\output\\data/dso_tbenemy.json \n" +
                "2026/09/30 01:36:39.002|INFO|[overwrite] T:\\x\\output\\data/dso_tbplayer.json \n" +
                "2026/09/30 01:36:39.003|INFO|[new] T:\\x\\output\\code/dso/Enemy.cs \n" +
                "2026/09/30 01:36:39.004|INFO|[remove] T:\\x\\output\\code/old.cs\n" +
                "2026/09/30 01:36:39.005|INFO|bye~\n";

            var files = LubanImport.ParseWrittenFiles(sample);

            // ParseWrittenFiles 内部按 OrdinalIgnoreCase 排序
            CollectionAssert.AreEqual(
                new List<string> { "dso_tbenemy.json", "dso_tbplayer.json", "Enemy.cs" },
                files);
        }

        // ================================================================
        // 5 · 环境闸门
        // ================================================================

        [Test]
        public void 预检可用且表名映射正确()
        {
            // 表名换算：~$enemy.xlsx -> enemy.xlsx
            var names = LubanProject.TableNamesOf(new List<string> { "~$enemy.xlsx", "~$player.xlsx" });
            CollectionAssert.AreEqual(new List<string> { "enemy.xlsx", "player.xlsx" }, names,
                "锁文件名 → 表名的换算不对");

            Assert.IsNotNull(LubanProject.FindExcelLockFiles(), "FindExcelLockFiles 不应返回 null");

            // 真实环境闸门：工具链、conf、workspace 三者齐备。
            // 刻意**不**在这里再断"有锁文件时 pre.Ok 必须为 false"：那条分支永远不可达
            // （有锁文件时下面的断言先红），是断言与实现自相矛盾 —— 已删。
            var pre = LubanProject.Precheck();
            Assert.IsTrue(pre.Ok,
                "预检应当通过，实际：" + pre.Message
                + "\n（若这里失败是因为有 ~$Excel 锁文件，请先保存并关闭被占用的表格再跑测试）");
        }

        // ================================================================
        // 6 · 输入动作表契约
        //
        // 为什么用 JSON 直读而不是 InputActionAsset.FromJson：
        //   只需断言"资产里确实有这些动作与绑定"。用资产 API 会把断言绑到 Unity 的
        //   资产加载时机上（Test Runner 里带 .meta 的资源加载路径更脆），而这里要守的
        //   是**文件内容**的契约：少一个动作，InputProvider 的 FindAction 会当场报错、
        //   对应输入永久失效。
        // ================================================================

        /// <summary>战斗动作 → 必须存在的绑定路径。改键位表就要同步改这里。</summary>
        static readonly Dictionary<string, string> RequiredPlayerBindings = new Dictionary<string, string>
        {
            { "Move", "2DVector" },
            { "Dash", "<Keyboard>/leftShift" },
            { "Attack", "<Mouse>/leftButton" },
            { "AltAttack", "<Mouse>/rightButton" },
            { "Plant", "<Keyboard>/e" },
            { "Aim", "<Mouse>/position" },
        };

        [Test]
        public void 输入动作表_含全部战斗动作与绑定()
        {
            string path = Path.Combine(Application.dataPath, "Scripts", "Presentation", "Input", "InputSys.inputactions");

            Assert.IsTrue(File.Exists(path), "找不到输入动作表：" + path);

            string json = File.ReadAllText(path);

            // 逐个动作断言：用简单的字符串扫描而不是 JSON 解析，避免引第三方解析器
            foreach (var pair in RequiredPlayerBindings)
            {
                Assert.IsTrue(
                    json.Contains("\"name\": \"" + pair.Key + "\""),
                    $"InputSys.inputactions 里没有动作 {pair.Key}：InputProvider 的 FindAction 会当场报错，该输入永久失效");

                Assert.IsTrue(
                    json.Contains("\"path\": \"" + pair.Value + "\"") && json.Contains("\"action\": \"" + pair.Key + "\""),
                    $"动作 {pair.Key} 没有绑定到 {pair.Value}");
            }
        }

        // ================================================================
        // 辅助
        // ================================================================

        /// <summary>
        /// 断言「被引号包裹的字符串，结尾恰好有 n 个反斜杠」。
        /// 用字符级结构比较代替肉眼数字面量里的反斜杠——后者在落地时出过错。
        /// </summary>
        static void AssertTrailingBackslashes(string quoted, int expected, string tag)
        {
            Assert.IsTrue(quoted.Length >= 2 && quoted[0] == '"' && quoted[quoted.Length - 1] == '"',
                tag + "：参数未被双引号包裹：[" + quoted + "]");

            int n = 0;
            for (int i = quoted.Length - 2; i >= 1 && quoted[i] == '\\'; i--) n++;

            Assert.AreEqual(expected, n, tag + "：结尾反斜杠数量不对（含引号原样：[" + quoted + "]）");
        }
    }
}
