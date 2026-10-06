# 修复 WarbandNewTab.cs：头部被误插坏 + 新页签位置排到最下面
import io

P = "src/WarbandStudio.Pack/WarbandNewTab.cs"
raw = io.open(P, encoding="utf-8", newline="").read()
NL = "\r\n" if "\r\n" in raw else "\n"

marker = "/// <summary>新建页签的结果。</summary>"
i = raw.find(marker)
assert i > 0, "找不到正文起点"

head = ("""using System.Text;
using System.Text.RegularExpressions;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

""").replace("\n", NL)

raw = head + raw[i:]

old = "            var y = double.Parse(offset.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 20;"
new = ("""            // 新页签的位置：**排在所有已有页签的最下面**（原来"母版 +20"，连建两个会叠在同一处 → 游戏里只看得到一个）
            var maxY = Regex.Matches(xml, "offset=\\"[0-9.]+,([0-9.]+)\\"")
                .Cast<Match>()
                .Select(m => double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0)
                .DefaultIfEmpty(0).Max();
            var y = maxY + 20;""").replace("\n", NL)
assert old in raw, "找不到 y 计算那句"
raw = raw.replace(old, new, 1)

io.open(P, "w", encoding="utf-8", newline="").write(raw)
print("WarbandNewTab.cs 已修复")
