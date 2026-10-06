using System.Text.RegularExpressions;

namespace WarbandStudio.Pack;

/// <summary>
/// twui 里**页签背景状态**的手术（配合 <see cref="TwuiTabs"/>）：
/// 页签在游戏里的面板背景 = `warband_upgrades` 组件 `&lt;states&gt;` 下**以页签 key 命名**的那个状态
/// （`&lt;skvg name="skvg"&gt;` → 里面的 image → `background_images_skvg.png`）。
///
/// 两个必须动的场景：
///   · **页签改名** → 状态名（标签 + name 属性）要跟着改成新 key，否则游戏按新 key 找不到状态 →
///     面板背景退回上一次的状态（用户实测：SKVG 改名 SKV2 后，界面显示的是 SKV 的图 ✗）；
///   · **老版本改名留下的烂摊子**（只改了 holder_tab，状态/按钮组件还是旧 key）→ 按页签 key 补一个状态出来，
///     让"换图"换的那张图真的能显示（否则换图写到旧状态用的文件上，游戏里看不到变化）。
/// </summary>
public static class WarbandTabArt
{
    /// <summary>定位"自命名状态"块（`&lt;skvg … name="skvg"&gt;…&lt;/skvg&gt;`），返回 [start, end) 下标。</summary>
    public static (int Start, int End)? FindState(string xml, string name)
    {
        if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(name)) return null;
        foreach (Match m in Regex.Matches(xml, @"<([A-Za-z0-9_]+)\s+[^>]*?\bname=""([A-Za-z0-9_\.]+)"""))
        {
            var tag = m.Groups[1].Value;
            if (!tag.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (!m.Groups[2].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            var close = xml.IndexOf("</" + tag + ">", m.Index, StringComparison.OrdinalIgnoreCase);
            if (close < 0) continue;
            return (m.Index, close + tag.Length + 3);
        }
        return null;
    }

    /// <summary>把状态改名为 <paramref name="newName"/>（标签名 + name 属性；GUID 不动 —— 别处按 GUID 引用它）。</summary>
    public static string RenameState(string xml, string oldName, string newName)
    {
        var hit = FindState(xml, oldName);
        if (hit is null || oldName.Equals(newName, StringComparison.OrdinalIgnoreCase)) return xml;
        var (s, e) = hit.Value;
        var block = xml[s..e];
        var nb = Regex.Replace(block, "<(/?)" + Regex.Escape(oldName) + @"(?=[\s>/])", "<$1" + newName, RegexOptions.IgnoreCase);
        nb = Regex.Replace(nb, @"\bname=""" + Regex.Escape(oldName) + @"""", "name=\"" + newName + "\"", RegexOptions.IgnoreCase);
        return xml[..s] + nb + xml[e..];
    }

    /// <summary>
    /// 克隆一个状态给新名字（插在母版状态后面）。**自己的 GUID 换新**（this/uniqueguid），
    /// `componentimage` 这类"指向别处的引用"保持原值 —— 换了就指不到那张图了。
    /// 母版状态找不到时原样返回。
    /// </summary>
    public static string CloneState(string xml, string donorName, string newName)
    {
        var hit = FindState(xml, donorName);
        if (hit is null) return xml;
        var (s, e) = hit.Value;
        var block = RenameState(xml[s..e], donorName, newName);
        block = Regex.Replace(block, @"\b(this|uniqueguid)=""(" + GuidPattern + @")""",
            m => $"{m.Groups[1].Value}=\"{NewGuidLike(m.Groups[2].Value)}\"");
        return xml[..e] + "\r\n\t\t" + block + xml[e..];
    }

    private const string GuidPattern = @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-?[0-9a-fA-F]{12,20}";

    /// <summary>按原 GUID 的写法生成一个新 GUID（4 段带横线 / 8-4-4-16 紧写法）。</summary>
    private static string NewGuidLike(string like)
    {
        var raw = Guid.NewGuid().ToString();
        return like.Count(c => c == '-') >= 4 ? raw : raw.Remove(23, 1);
    }
}
