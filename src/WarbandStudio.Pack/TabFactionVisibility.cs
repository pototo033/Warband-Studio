using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WarbandStudio.Pack;

/// <summary>页签显示名单只改 TWUI；不得借此修改共用兵种的派系许可。</summary>
public static class TabFactionVisibility
{
    private const string Marker = "WarbandStudio.TabFactionVisibility.v1:";
    private const string Faction = "SelectedCharacter.MilitaryForceContext.FactionContext.FactionRecordContext.Key";

    public sealed class Settings
    {
        public Dictionary<string, string[]> Tabs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Visibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string AutoSelect { get; set; } = "";
        public string Tree { get; set; } = "";
    }

    // 原表达式与名单随 XML 注释一起保存，游戏忽略注释，工坊重开包仍能恢复与取消限制。
    private static (Settings Settings, XComment? Comment) Metadata(XDocument doc)
    {
        var comments = doc.DescendantNodes().OfType<XComment>().Where(c => c.Value.StartsWith(Marker, StringComparison.Ordinal)).ToList();
        if (comments.Count > 1) throw new InvalidDataException("页签派系显示配置重复，请先检查 TWUI。");
        if (comments.Count == 0) return (new Settings(), null);
        try
        {
            var data = JsonSerializer.Deserialize<Settings>(Convert.FromBase64String(comments[0].Value[Marker.Length..]))
                       ?? throw new InvalidDataException("配置为空");
            data.Tabs = new(data.Tabs, StringComparer.OrdinalIgnoreCase);
            data.Visibility = new(data.Visibility, StringComparer.OrdinalIgnoreCase);
            foreach (var key in data.Tabs.Keys.ToArray())
            {
                if (!Regex.IsMatch(key, "^[A-Za-z0-9_]+$")) throw new InvalidDataException("页签 key 无效。");
                data.Tabs[key] = Normalize(data.Tabs[key]);
                if (data.Tabs[key].Length == 0) data.Tabs.Remove(key);
            }
            return (data, comments[0]);
        }
        catch (Exception ex) { throw new InvalidDataException("无法读取页签派系显示配置，已停止写入以保留原内容。", ex); }
    }

    public static Dictionary<string, string[]> Read(string? xml) => string.IsNullOrWhiteSpace(xml)
        ? new(StringComparer.OrdinalIgnoreCase)
        : Metadata(XDocument.Parse(xml, LoadOptions.PreserveWhitespace)).Settings.Tabs;

    public static string[] Normalize(IEnumerable<string> factions) => factions.Select(f => f.Trim())
        .Where(f => f.Length > 0).Select(f => Regex.IsMatch(f, "^[A-Za-z0-9_]+$") ? f
            : throw new ArgumentException("无效的派系 key：" + f))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<XElement> Callbacks(XContainer root) => root.Descendants()
        .Where(e => e.Name.LocalName == "callback_with_context");
    private static string Expression(XElement e) => (string?)e.Attribute("context_function_id") ?? "";
    private static string Id(XElement e) => (string?)e.Attribute("this") ?? (string?)e.Attribute("id") ?? "";
    private static Dictionary<string, XElement> Holders(XDocument doc) => doc.Descendants()
        .Where(e => ((string?)e.Attribute("id"))?.StartsWith("holder_tab_", StringComparison.OrdinalIgnoreCase) == true)
        .ToDictionary(e => ((string)e.Attribute("id")!)[11..], StringComparer.OrdinalIgnoreCase);
    private static XElement Visibility(XElement holder) => Callbacks(holder)
        .SingleOrDefault(e => (string?)e.Attribute("callback_id") == "ContextVisibilitySetter")
        ?? throw new InvalidDataException("页签缺少唯一的可见性回调：" + (string?)holder.Attribute("id"));
    private static string Allowed(Settings settings, string category) => string.Join(" && ", settings.Tabs.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .Select(kv => $"({category} != \"{kv.Key}\" || ({string.Join(" || ", kv.Value.Select(f => $"{Faction} == \"{f}\""))}))"));

    public static string Apply(string xml, IReadOnlyDictionary<string, string[]> edits,
        IEnumerable<(string Old, string New)>? renames = null,
        IEnumerable<(string Key, string Donor)>? newTabs = null)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var (settings, comment) = Metadata(doc);
        var holders = Holders(doc);
        foreach (var (oldKey, newKey) in renames ?? [])
            if (settings.Tabs.Remove(oldKey, out var selected)) settings.Tabs[newKey] = selected;

        // 克隆页签默认不限派系，不能悄悄继承母版的限制表达式。
        foreach (var (key, donor) in newTabs ?? [])
            if (holders.TryGetValue(key, out var clone) && holders.TryGetValue(donor, out var original)
                && settings.Visibility.TryGetValue(Id(original), out var baseExpression))
                Visibility(clone).SetAttributeValue("context_function_id", baseExpression);

        foreach (var (key, factions) in edits)
        {
            if (!Regex.IsMatch(key, "^[A-Za-z0-9_]+$")) throw new ArgumentException("无效的页签 key：" + key);
            if (!holders.ContainsKey(key)) throw new InvalidDataException("页签缺少 TWUI holder，无法设置派系显示：" + key);
            var selected = Normalize(factions);
            if (selected.Length == 0) settings.Tabs.Remove(key);
            else
            {
                var canonical = (string?)holders[key].Descendants().FirstOrDefault(p => p.Name.LocalName == "property"
                    && (string?)p.Attribute("name") == "God")?.Attribute("value") ?? key;
                if (!Regex.IsMatch(canonical, "^[A-Za-z0-9_]+$")) throw new InvalidDataException("页签分类 key 无效。");
                settings.Tabs.Remove(key);
                settings.Tabs[canonical] = selected;
            }
        }
        foreach (var (key, holder) in holders)
        {
            var cb = Visibility(holder);
            if (settings.Visibility.TryGetValue(Id(holder), out var original)) cb.SetAttributeValue("context_function_id", original);
            if (!settings.Tabs.TryGetValue(key, out var factions)) continue;
            var baseline = Expression(cb);
            settings.Visibility.TryAdd(Id(holder), baseline);
            cb.SetAttributeValue("context_function_id", $"({baseline}) && ({string.Join(" || ", factions.Select(f => $"{Faction} == \"{f}\""))})");
        }
        var auto = Callbacks(doc).SingleOrDefault(e => (string?)e.Attribute("context_object_id") == "CcoCampaignUnit"
            && (Expression(e).Contains("FirstValidUpgradeCategoryForUnit") || Expression(e).Contains("studioVisibleTabs")))
            ?? throw new InvalidDataException("未识别战帮自动选页回调，已停止导出派系显示配置。");
        var tree = Callbacks(doc).SingleOrDefault(e => Expression(e).Contains("UnitGroupUpgradesTree("))
            ?? throw new InvalidDataException("未识别战帮兵牌树回调，已停止导出派系显示配置。");
        if (settings.AutoSelect.Length == 0) settings.AutoSelect = Expression(auto);
        if (settings.Tree.Length == 0) settings.Tree = Expression(tree);
        auto.SetAttributeValue("context_function_id", settings.AutoSelect);
        tree.SetAttributeValue("context_function_id", settings.Tree);
        if (settings.Tabs.Count > 0)
        {
            // 与实机验证的两页测试相同：可选列表先排除不属于军队派系的页，再保留原有优先页/回退到首个可用页。
            var preferred = "UnitUpgradeManagerContext.FirstValidUpgradeCategoryForUnit(this, self.CurrentSelectedComponents.FirstContext.ParentContext.GetProperty(\"God\"))";
            var tabs = "self.ChildList.Filter((tab) => " + Allowed(settings, "tab.GetProperty(\"God\")") +
                " && UnitUpgradeManagerContext.ForceUnitGroupUpgradesList(SelectedCharacter.MilitaryForceContext).Filter(Category == tab.GetProperty(\"God\")).IsEmpty == false)";
            auto.SetAttributeValue("context_function_id", "(studioVisibleTabs = " + tabs + ", preferred = " + preferred + ") => { " +
                "studioVisibleTabs.FirstContext((x) => x.GetProperty(\"God\") == GetIfElse(studioVisibleTabs.Any((y) => y.GetProperty(\"God\") == preferred), " +
                "preferred, studioVisibleTabs.FirstContext.GetProperty(\"God\"))).ChildList.FirstContext(HasCallback(\"Button\")).SimulateLClick }");
            const string treeFilter = ".Filter(Category == ";
            if (settings.Tree.Split(treeFilter).Length != 2)
                throw new InvalidDataException("未识别战帮兵牌树分类筛选表达式，已停止导出以保留其他 MOD 的界面逻辑。");
            tree.SetAttributeValue("context_function_id", settings.Tree.Replace(treeFilter, ".Filter(" + Allowed(settings, "Category") + " && Category == ", StringComparison.Ordinal));
            var value = Marker + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(settings));
            if (comment is null) doc.Root!.AddFirst(new XComment(value));
            else comment.Value = value;
        }
        else comment?.Remove();
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
