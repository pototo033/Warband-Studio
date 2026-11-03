using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WarbandStudio.Pack;

public static partial class DuplicateUpgradeCompiler
{
    private static string Resource(UpgradePolicy policy, string slot) => "studio_du_" + DuplicateUpgradeArchive.Id(policy.Id + "|" + slot);
    private static string Q(string value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static string Lua(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().Select(p => "[" + Q(p.Name) + "]=" + Lua(p.Value))) + "}",
        JsonValueKind.Array => "{" + string.Join(",", value.EnumerateArray().Select(Lua)) + "}",
        JsonValueKind.String => Q(value.GetString()!), JsonValueKind.True => "true", JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(), _ => "nil"
    };
    private static string CreateConfig(UpgradePolicy[] policies, string ns)
    {
        var data = new
        {
            version = 1, @namespace = ns, scale = Scale, budget_cap = 10000000, sync_factor = Sync, pay_factor = Pay,
            policies = policies.Select(p => new
            {
                id = p.Id, native_gold = p.NativeGold, decisions = p.Decisions,
                profiles = p.Profiles.Select(r => new
                {
                    id = r.Id, scopes = r.Scopes.Select(s => new { factions = s.Factions, races = s.Races, excluded = s.Excluded }),
                    gold = r.Bill.Gold, pools = r.Bill.Pools.Select(f => new { key = f.Key, scope = f.Scope, amount = f.Amount, factor = f.Factor, ai_ignored = f.AiIgnored })
                }),
                slots = p.Slots.Select(s => new { key = s, resource = Resource(p, s), id = DuplicateUpgradeArchive.Id(p.Id + "|" + s) })
            })
        };
        var json = JsonSerializer.Serialize(data);
        using var doc = JsonDocument.Parse(json);
        return "-- 战帮工坊从实际表单自动生成；不要逐条手工配置路线或金额。\nlocal config = " + Lua(doc.RootElement) + "\nconfig.fingerprint = " + Q(DuplicateUpgradeArchive.Id(json)) + "\nreturn config\n";
    }

    private static void AddRuntimeTables(Tables t, Plan[] plans,
        Action<string, Row, Dictionary<string, string>, string?> add, string ns)
    {
        void Add(string table, Row row, Dictionary<string, string> fields) => add(table, row, fields, null);
        var factor = t.Find("pooled_resource_factors_tables", _ => true);
        foreach (var key in new[] { Sync, Pay })
        {
            if (t.All("pooled_resource_factors_tables").Any(r => r["key"] == key)) throw new InvalidDataException("重复兵内部因子名称已被占用：" + key);
            Add("pooled_resource_factors_tables", factor, new() { ["key"] = key, ["is_hidden"] = "true" });
        }
        var group = "studio_du_factions_" + ns;
        if (t.All("campaign_groups_tables").Any(r => r["id"] == group)) throw new InvalidDataException("重复兵战役组名称已被占用。");
        Add("campaign_groups_tables", t.Find("campaign_groups_tables", _ => true), new() { ["id"] = group });
        var actors = t.All("factions_tables").Select(r => r["key"])
            .Concat(plans.SelectMany(p => p.Policy.Profiles).SelectMany(p => p.Scopes).SelectMany(s => s.Factions)).Distinct().Order(StringComparer.Ordinal);
        foreach (var faction in actors)
        {
            var member = group + "_" + DuplicateUpgradeArchive.Id(faction);
            Add("campaign_group_members_tables", t.Find("campaign_group_members_tables", _ => true), new() { ["id"] = member, ["group"] = group, ["priority"] = "0" });
            Add("campaign_group_member_criteria_factions_tables", t.Find("campaign_group_member_criteria_factions_tables", _ => true), new() { ["member"] = member, ["context"] = "ACTOR", ["faction"] = faction });
        }
        var prototype = t.Find(Resources, r => r["scope"] == "FACTION");
        var binding = t.Find(Binding, r => r["specific_faction_set"].Length == 0);
        foreach (var plan in plans) foreach (var slot in plan.Policy.Slots)
        {
            var resource = Resource(plan.Policy, slot);
            if (t.All(Resources).Any(r => r["key"] == resource)) throw new InvalidDataException("重复兵内部资源名称已被占用：" + resource);
            var pool = plan.Policy.Profiles.SelectMany(p => p.Bill.Pools).FirstOrDefault(f => f.Key == slot);
            Add(Resources, prototype, new()
            {
                ["key"] = resource, ["maximum"] = "2147483647", ["minimum"] = "0", ["ai_ignored"] = "false",
                ["default_factor"] = Sync, ["optional_icon_path"] = pool?.Icon ?? "icon_treasury.png", ["scope"] = "FACTION",
                ["reset_before_income"] = "false", ["has_persistent_factors"] = "false", ["appy_income_on_creation"] = "false",
                ["battle_conversion"] = "", ["always_display_zero_factors"] = "false", ["can_scale_effect_thresholds_per_unit"] = "false"
            });
            foreach (var (suffix, f, maximum) in new[] { ("_sync", Sync, "2147483647"), ("_pay", Pay, "0") })
                Add(Binding, binding, new() { ["unique_id"] = resource + suffix, ["factor"] = f, ["resource"] = resource, ["minimum"] = "-2147483647", ["maximum"] = maximum, ["specific_faction_set"] = "" });
            Add("campaign_group_pooled_resources_tables", t.Find("campaign_group_pooled_resources_tables", _ => true), new() { ["campaign_group"] = group, ["resource"] = resource, ["initial_amount"] = "0" });
            Add(CostPool, t.Find(CostPool, r => r["context"] == "absolute" && r["ui_resource_transaction_pooled_resource"] == "default"), new()
            { ["pooled_resource_factor"] = resource + "_pay", ["resource_cost"] = plan.Policy.Cost, ["amount"] = (-Scale).ToString(), ["context"] = "absolute", ["ui_resource_transaction_pooled_resource"] = "default" });
        }
    }

    private static byte[] ResourceLoc(UpgradePolicy[] policies)
    {
        var values=new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var p in policies) foreach(var slot in p.Slots)
        {
            var name=slot=="__gold"?"金币":slot=="__gate"?"升级许可":slot;
            values["pooled_resources_display_name_"+Resource(p,slot)]=name;
            values["pooled_resources_description_"+Resource(p,slot)]=name;
        }
        using var ms=new MemoryStream();using(var writer=new BinaryWriter(ms,Encoding.UTF8,true))
        {
            writer.Write(new byte[]{255,254,76,79,67,0});writer.Write(1u);writer.Write((uint)values.Count);
            foreach(var (key,value) in values)
            { writer.Write(checked((ushort)key.Length));writer.Write(Encoding.Unicode.GetBytes(key));writer.Write(checked((ushort)value.Length));writer.Write(Encoding.Unicode.GetBytes(value));writer.Write((byte)0); }
        }
        return ms.ToArray();
    }

    private static string ScopeExpression(UpgradeScope scope)
    {
        var fs = string.Join(" || ", scope.Factions.Select(f => $"PlayersFaction.FactionRecordContext.Key == {Q(f)}"));
        var rs = string.Join(" || ", scope.Races.Select(r => $"PlayersFaction.SubcultureContext.Key == {Q(r)}"));
        var parts = new[] { fs, rs }.Where(s => s.Length > 0).Select(s => "(" + s + ")");
        var expression = string.Join(scope.Excluded && fs.Length > 0 && rs.Length > 0 ? " && " : " || ", parts);
        return expression.Length == 0 ? "true" : scope.Excluded ? "!(" + expression + ")" : "(" + expression + ")";
    }
    private static Dictionary<string, string> ProfileExpressions(UpgradePolicy policy)
    {
        var active = policy.Profiles.ToDictionary(p => p.Id, p => "(" + string.Join(" || ", p.Scopes.Select(ScopeExpression)) + ")");
        return policy.Profiles.ToDictionary(p => p.Id, p =>
        {
            var choices = policy.Decisions.Select(d => (Set: d.Key.Split('|').ToHashSet(), Winner: d.Value)).ToArray();
            string Pattern(HashSet<string> set) => "(" + string.Join(" && ", policy.Profiles.Select(r => set.Contains(r.Id) ? active[r.Id] : "!" + active[r.Id])) + ")";
            var denied = choices.Where(d => d.Set.Contains(p.Id) && d.Winner != p.Id).Select(d => "!" + Pattern(d.Set)).ToArray();
            return "(" + active[p.Id] + (denied.Length == 0 ? "" : " && " + string.Join(" && ", denied)) + ")";
        });
    }

    private static string PatchUi(string xml, UpgradePolicy[] policies)
    {
        var original = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var expressions = policies.ToDictionary(p => p.Id, ProfileExpressions);
        string Amount(UpgradePolicy p, string slot)
        {
            var expression = "0";
            foreach (var profile in p.Profiles.Reverse())
            {
                var value = slot == "__gold" ? profile.Bill.Gold - p.NativeGold : profile.Bill.Pools.FirstOrDefault(f => f.Key == slot)?.Amount ?? 0;
                if (value != 0) expression = $"GetIfElse({expressions[p.Id][profile.Id]}, {value}, {expression})";
            }
            return expression;
        }
        string Lookup(string key, Func<UpgradePolicy, string, string> value, string fallback)
        {
            var result = fallback;
            foreach (var p in policies.Reverse()) foreach (var slot in p.Slots.Reverse())
            {
                var expression=value(p,slot);
                if(expression!=fallback) result = $"GetIfElse({key} == {Q(Resource(p, slot))}, {expression}, {result})";
            }
            return result;
        }
        string Gold(string tx)
        {
            var native = $"GetIfElse({tx}TreasuryTransactionList.IsEmpty, 0, Abs({tx}TreasuryTransactionList.Sum(Value)))";
            var goldSlot = Lookup("fee.ResourceKey", (_,s)=>s=="__gold"?"true":"false","false");
            var full = Lookup("fee.ResourceKey", (p,s)=>s=="__gold"?$"(({Amount(p,s)}) + {p.NativeGold})":"0","0");
            // 不在 fee 的局部 CCO 上读取父交易的字段，避免跨上下文求值造成原生 UI 错误。
            return $"GetIfElse({tx}PooledResourceTransactionList.Filter((fee) => {goldSlot}).IsEmpty, {native}, {tx}PooledResourceTransactionList.Sum((fee) => Abs(fee.Value) * ({full}) / 10000.0))";
        }
        void Patch(string guid, string callback, string context, Func<string, string> transform)
        {
            var components = original.Descendants().Where(e => (string?)e.Attribute("this") == guid && e.Attribute("id") is not null).ToArray();
            if (components.Length != 1) throw new InvalidDataException("战帮 UI 缺少唯一成本组件：" + guid);
            var c = components[0];
            var matches = c.Descendants("callback_with_context").Where(e => (string?)e.Attribute("callback_id") == callback && (string?)e.Attribute("context_object_id") == context).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("战帮 UI 成本回调不兼容：" + guid);
            var old = (string?)matches[0].Attribute("context_function_id") ?? "";
            var next = transform(old);
            var componentPattern = $"<{Regex.Escape(c.Name.LocalName)}\\b(?=[^>]*\\bthis=\"{Regex.Escape(guid)}\")(?=[^>]*\\bid=\")[^>]*>[\\s\\S]*?</{Regex.Escape(c.Name.LocalName)}>";
            var cm = Regex.Matches(xml, componentPattern);
            if (cm.Count != 1) throw new InvalidDataException("战帮 UI 组件文本定位不唯一：" + guid);
            var cbPattern = $"<callback_with_context\\b(?=[^>]*\\bcallback_id=\"{callback}\")(?=[^>]*\\bcontext_object_id=\"{context}\")[^>]*>";
            var m = Regex.Matches(cm[0].Value, cbPattern);
            if (m.Count != 1) throw new InvalidDataException("战帮 UI 回调文本定位不唯一：" + guid);
            var attr = Regex.Match(m[0].Value, "context_function_id=\"[^\"]*\"");
            var tag = m[0].Value[..attr.Index] + "context_function_id=\"" + SecurityElement.Escape(next) + "\"" + m[0].Value[(attr.Index + attr.Length)..];
            var chunk = cm[0].Value[..m[0].Index] + tag + cm[0].Value[(m[0].Index + m[0].Length)..];
            xml = xml[..cm[0].Index] + chunk + xml[(cm[0].Index + cm[0].Length)..];
        }
        const string units = "Component(\"B10C28DC-AFC1-4907-B20296FD966B63E2\").UpgradingUnitsContextList";
        var gold = Gold("");
        foreach (var guid in new[] { "1327510D-0F47-4163-A5895007FC2C62F1", "0EF9609C-AF47-4D87-9ADED8135CE9E866" }) Patch(guid, "ContextVisibilitySetter", "CcoCampaignResourceTransaction", _ => $"RoundFloat({gold}) > 0");
        Patch("AC66D87F-E4D5-4535-BE42079C8D1BE917", "ContextTextLabel", "CcoCampaignResourceTransaction", _ => $"RoundFloat({gold})");
        Patch("B9425855-B760-4503-8AD0CBC1AA1F98B8", "ContextTextLabel", "CcoCampaignResourceTransaction", _ => $"RoundFloat({gold} * {units}.Size)");
        var poolAmount = Lookup("ResourceKey", Amount, "10000");
        var filter = Lookup("fee.ResourceKey", (p, s) => s is "__gold" or "__gate" ? "false" : $"({Amount(p, s)}) > 0", "true");
        foreach (var guid in new[] { "2A6EE4EE-97F8-4B03-A68D80D2D5AA60AD", "C569EF2A-9F8C-429E-A3B80C5C44A44CAC" })
            Patch(guid, "ContextList", "CcoCampaignResourceTransaction", old => old == "PooledResourceTransactionList" ? $"PooledResourceTransactionList.Filter((fee) => {filter})" : throw new InvalidDataException("已有自定义成本列表，无法安全叠加重复兵兼容。"));
        Patch("ABB3BF43-6107-4F7E-871182361426AB7D", "ContextTextLabel", "CcoCampaignPooledResourceTransaction", _ => $"RoundFloat(Abs(Value) * ({poolAmount}) / 10000.0)");
        Patch("67DE1D18-4503-4182-A9CFD41AEEB45643", "ContextTextLabel", "CcoCampaignPooledResourceTransaction", _ => $"RoundFloat(Abs(Value) * ({poolAmount}) / 10000.0 * {units}.Size)");
        // 私有资源名复用真实资源的本地化键，不让用户看到内部预算池 key。
        var name = Lookup("ResourceKey", (p, s) => s.StartsWith("__", StringComparison.Ordinal) ? "Name" : $"Loc({Q("pooled_resources_display_name_" + s)})", "Name");
        foreach (var guid in new[] { "ABB3BF43-6107-4F7E-871182361426AB7D", "67DE1D18-4503-4182-A9CFD41AEEB45643" })
            Patch(guid,"ContextTooltipSetter","CcoCampaignPooledResourceTransaction",_ => name);
        var total = $"{units}.Sum((unit) => RoundFloat({Gold("StoredContext(\"CcoCampaignUnitGroupUpgrade\").ResourceTransactionContext(unit).") }))";
        Patch("CD87E96C-7C16-4ACA-B181479669F835DE", "ContextInactiveStateSetter", "CcoMainUnitRecord", old => $"({old}) && PlayersFaction.TreasuryAmount >= ({total})");
        Patch("CD87E96C-7C16-4ACA-B181479669F835DE", "ContextCommandLeftClick", "CcoMainUnitRecord", old => old == $"{units}.ForEach(UpgradeUnit(this))" ? $"(studioGoldTotal = {total}) => {{ {units}.Filter((unit) => PlayersFaction.TreasuryAmount >= studioGoldTotal).ForEach(UpgradeUnit(this)) }}" : throw new InvalidDataException("已有自定义换兵命令，无法安全叠加重复兵兼容。"));
        if (XDocument.Parse(xml).Descendants().Count() != original.Descendants().Count()) throw new InvalidDataException("战帮 UI 节点发生意外变化。");
        return xml;
    }
}
