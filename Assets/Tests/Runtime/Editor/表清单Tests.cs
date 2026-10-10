// 表清单判据：TablesMeta.Names（手写）必须逐项命中生成物 cfg.Tables 的公开属性。
// 名字写错既不编译失败、也不让别的用例变红，只在游戏启动时由 StartupValidator 抛
// ConfigLoadException —— 加表 / 删表最容易漏同步，故单独给它一条判据。
// 跑法：Window ▸ General ▸ Test Runner ▸ EditMode ▸ Run All

using System.Collections.Generic;
using DeepseaOil.Data;
using NUnit.Framework;

namespace DeepseaOil.Tests
{
    public class 表清单Tests
    {
        [Test]
        public void 表清单与生成物逐项对齐()
        {
            var generated = new List<string>();

            foreach (var prop in typeof(cfg.Tables).GetProperties())
                generated.Add(prop.Name);

            Assert.Greater(generated.Count, 0, "生成物 cfg.Tables 没有任何属性：导表没跑或镜像没发布");

            // 多重集比对：漏登记 / 多登记 / 拼错 / 重复，四种都会红。
            CollectionAssert.AreEquivalent(generated, TablesMeta.Names,
                "TablesMeta.Names 与 cfg.Tables 属性不一致：加 / 删表必须同步这两处");
        }
    }
}
