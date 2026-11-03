using System.Globalization;
using System.Text;
using System.Text.Json;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>从实际单位的全部组归属编译原生候选闭包，不依赖复制页签或组名的前缀。</summary>
public static partial class DuplicateUpgradeCompiler
{
    internal const string UiPath = "ui/campaign ui/warband_upgrades.twui.xml";
    internal const string Sync = "studio_wb_duplicate_sync", Pay = "studio_wb_duplicate_pay";
    internal const int Scale = 10000;
    private const string Cost = "resource_costs_tables", CostPool = "resource_cost_pooled_resource_junctions_tables";
    private const string Binding = "pooled_resource_factor_junctions_tables", Resources = "pooled_resources_tables";
    private const string Tech = "unit_upgrade_to_tech_requirements_tables", Building = "unit_upgrade_to_building_level_requirements_tables";
    private const string Effects = "effect_bonus_value_unit_upgrade_to_unit_group_junctions_tables";
    internal static readonly Dictionary<string, string[]> Keys = new()
    {
        [TabFileNaming.Groups] = ["unit_group"], [TabFileNaming.Junc] = ["unit", "unit_group"],
        [TabFileNaming.Infos] = ["unit_upgrade_group"], [TabFileNaming.Routes] = ["upgrade_key"],
        [Cost] = ["id"], [CostPool] = ["pooled_resource_factor", "resource_cost"], [Binding] = ["unique_id"],
        [Resources] = ["key"], ["pooled_resource_factors_tables"] = ["key"],
        [Tech] = ["unit_upgrade", "technology"], [Building] = ["unit_upgrade", "building_level"],
        ["factions_tables"] = ["key"], ["campaign_groups_tables"] = ["id"],
        ["campaign_group_members_tables"] = ["id"], ["campaign_group_member_criteria_factions_tables"] = ["member", "context", "faction"],
        ["campaign_group_pooled_resources_tables"] = ["campaign_group", "resource"],
        [Effects] = ["effect", "bonus_value_id", "unit_upgrade_to_unit_group"]
    };
    internal sealed record Row(DbTable Table, DbValue[] Display, DbValue[] Raw, string Path)
    {
        public string this[string field]
        {
            get
            {
                var index=Table.Columns.FindIndex(c=>c.Name==field);
                if(index<0)throw new InvalidDataException($"重复兵兼容：{Table.TableName} v{Table.Version} 缺少 {field} 列，未改写该包。");
                return Display[index].ToTsv();
            }
        }
    }
    private sealed class Tables(PackArchive authored, PackArchive vanilla, IEnumerable<PackArchive> dependencies, Schema schema)
    {
        private readonly Dictionary<string, List<Row>> _local = new(), _effective = new();
        public List<Row> Local(string table)
        {
            if (_local.TryGetValue(table, out var value)) return value;
            return _local[table] = Read(authored, table);
        }
        private List<Row> Read(PackArchive pack, string table)
        {
            var rows = new List<Row>();
            foreach (var e in TableFiles.EntriesFor(pack, table))
            {
                var t = DbTable.Decode(pack.ReadDecoded(e), table, schema);
                rows.AddRange(t.Rows.Select((r, i) => new Row(t, r, t.RawRows[i], e.Path)));
            }
            return rows;
        }
        public List<Row> All(string table)
        {
            if (_effective.TryGetValue(table, out var result)) return result;
            // 有效值严格走组合键 API；另外保留来源路径，修改/删除时才能回到原文件。
            var provenance = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
            DbTable? merged=null;
            foreach(var source in new[]{vanilla}.Concat(dependencies).Append(authored))
            {
                var rows=ReferenceEquals(source,authored)?Local(table):Read(source,table);
                var current=TableFiles.ReadComposite(source,table,Keys[table],schema);
                if(current is null)continue;
                if(rows.Any(r=>r.Table.Version!=current.Version) || merged is not null &&
                    !merged.Definition.Fields.Select(f=>(f.Name,f.Type)).SequenceEqual(current.Definition.Fields.Select(f=>(f.Name,f.Type))))
                    throw new InvalidDataException("重复兵兼容发现混合版本的表，未改写："+table);
                merged=TableFiles.MergeComposite(merged,current,Keys[table]);
                foreach(var row in rows)provenance[string.Join('\u0001',Keys[table].Select(k=>row[k]))]=row;
            }
            return _effective[table]=merged is null?[]:merged.Rows.Select(row=>provenance[string.Join('\u0001',Keys[table].Select(k=>row[merged.Columns.FindIndex(c=>c.Name==k)].ToTsv()))]).ToList();
        }
        public Row Find(string table, Func<Row, bool> match) => All(table).FirstOrDefault(match)
            ?? throw new InvalidDataException("重复兵兼容缺少表行：" + table);
    }
    private sealed record Route(Row Source, string Base, string Target, string Key, string Tab, UpgradeScope Scope, UpgradeBill Bill, string Requirements, bool Direct);
    private sealed record Plan(string Id, Route[] Routes, (string Base,string Target)[] Pairs, UpgradePolicy Policy, Row RequirementSource);
    private static string Signature(object value) => JsonSerializer.Serialize(value);

    /// <summary>只返回派生文件；调用方完成 RPFM 校验后才能原子提交。</summary>
    public static Dictionary<string, byte[]> Compile(PackArchive authored, PackArchive vanilla, Schema schema,
        WarbandEdits edits, IReadOnlyDictionary<string, string> choices, List<string> notes, IEnumerable<PackArchive>? dependencies = null)
    {
        using var rawSource=PackArchive.Open(authored.FilePath);
        var previous=DuplicateUpgradeArchive.Read(rawSource);
        if (authored.Find(DuplicateUpgradeArchive.ManifestPath) is not null || previous is null &&
            authored.VisibleEntries.Any(e => e.Path.StartsWith("script/campaign/mod/studio_duplicate_upgrade_", StringComparison.OrdinalIgnoreCase)))
        {
            notes.Add("重复兵兼容：按当前 Pack 保存，保留其现有升级执行方式和本次编辑，不套用旧来源数据。");
            return new(StringComparer.OrdinalIgnoreCase);
        }
        if(string.IsNullOrWhiteSpace(edits.ProjectKey) && previous?.ProjectKey is {Length:>0} projectKey)
        { edits=edits.Clone();edits.ProjectKey=projectKey; }
        var ns=previous?.Namespace is {Length:>0} identity?identity:DuplicateUpgradeArchive.Id((edits.ProjectKey??"studio")+"|"+Path.GetFileNameWithoutExtension(authored.FilePath));
        var tables = new Tables(authored, vanilla, dependencies ?? [], schema);
        var localRoutes = tables.Local(TabFileNaming.Routes);
        if (localRoutes.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        if (authored.Find("script/campaign/mod/studio_nagash_gold_cost.lua") is not null)
            throw new InvalidDataException("此包含旧实验版重复兵兼容脚本。请用未加入实验脚本的工程底本导出，避免同时启用两套扣费逻辑。");
        var members = tables.All(TabFileNaming.Junc).GroupBy(r => r["unit_group"])
            .ToDictionary(g => g.Key, g => g.Select(r => r["unit"]).Distinct().Order(StringComparer.Ordinal).ToArray());
        var ownedGroups = tables.Local(TabFileNaming.Infos).Select(r=>r["unit_upgrade_group"]).ToHashSet();
        var aliases = members.Where(g=>ownedGroups.Contains(g.Key)).SelectMany(g => g.Value.Select(u => (Unit: u, Group: g.Key)))
            .GroupBy(x => x.Unit).ToDictionary(g => g.Key, g => g.Select(x => x.Group).ToArray());
        // “多个地方”指当前编辑包中的多组；不能把原版参考数据中的另一份组当成用户新建的重复位置。
        var duplicated = aliases.Where(p=>p.Value.Length>1).Select(p=>p.Key).ToHashSet();
        if (duplicated.Count == 0) return new(StringComparer.OrdinalIgnoreCase);
        var affected = localRoutes.Where(r => new[] { r["base_unit_group"], r["target_unit_group"] }
            .Any(g => members.GetValueOrDefault(g, []).Any(duplicated.Contains))).ToArray();
        if (affected.Length == 0) return new(StringComparer.OrdinalIgnoreCase);

        var xml = authored.Find(UiPath) is { } ui ? Encoding.UTF8.GetString(authored.ReadDecoded(ui)) : null;
        var factions = xml is null ? new Dictionary<string, string[]>() : TabFactionVisibility.Read(xml);
        var races = xml is null ? new Dictionary<string, string[]>() : TabFactionVisibility.ReadRaces(xml);
        var excluded = xml is null ? new Dictionary<string, bool>() : TabFactionVisibility.ReadExcluded(xml);
        var categories = tables.All(TabFileNaming.Infos).ToDictionary(r => r["unit_upgrade_group"], r => r["category"]);
        var conflicts = new Dictionary<string, UpgradeRuleConflict>();
        var plans = new List<Plan>();
        string[] RequirementValues(string table,string key) => tables.All(table).Where(r=>r[table==Effects?"unit_upgrade_to_unit_group":"unit_upgrade"]==key)
            .Select(r=>string.Join("|",r.Table.Columns.Where(c=>c.Name!=(table==Effects?"unit_upgrade_to_unit_group":"unit_upgrade")).Select(c=>r[c.Name]))).Order(StringComparer.Ordinal).ToArray();
        string Requirements(Row route) => Signature(new
        {
            Rank = route["required_rank"], Subtracted = route["subtracted_rank"],
            Tech = RequirementValues(Tech,route["upgrade_key"]), Buildings = RequirementValues(Building,route["upgrade_key"]),
            Effects = RequirementValues(Effects,route["upgrade_key"])
        });
        string Choose(string id, string description, Route[] candidates, string? currentDefault = null)
        {
            var options = candidates.GroupBy(r => currentDefault == "requirements" ? r.Requirements : Signature(r.Bill))
                .Select(g => g.OrderBy(r => r.Key, StringComparer.Ordinal).First()).ToArray();
            var fullId = id + ":" + DuplicateUpgradeArchive.Id(string.Join("|", options.Select(r => r.Key + r.Requirements + Signature(r.Bill) + Signature(r.Scope))));
            if (choices.TryGetValue(fullId, out var chosen) && options.Any(r => r.Key == chosen)) return chosen;
            conflicts[fullId] = new(fullId, description, options.Select(r => new UpgradeRuleOption(r.Key,
                $"页签 {r.Tab} · {r.Key}\n{r.Source["resource_cost"]}：" + (r.Direct?"沿用原生成本":$"金币 {r.Bill.Gold}") +
                string.Concat(r.Bill.Pools.Select(p => $"，{p.Key} {p.Amount}")) + $"；等级 {r.Source["required_rank"]} / 扣等级 {r.Source["subtracted_rank"]}\n条件：{r.Requirements}")).ToArray());
            return options[0].Key; // 仅用于收集剩余冲突；存在冲突时绝不产出文件。
        }
        // 按实际单位对连接路线。双兵组与单兵组部分重叠也可编译，只补已有实际升级对的闭包。
        // 原版/依赖包提供引用与模板；不能把它们的独立原生升级树也自动改写成当前 MOD 的价格。
        var localKeys = localRoutes.Select(r=>r["upgrade_key"]).ToHashSet();
        var allRoutes = tables.All(TabFileNaming.Routes).Where(r=>localKeys.Contains(r["upgrade_key"]) && ownedGroups.Contains(r["base_unit_group"]) && ownedGroups.Contains(r["target_unit_group"])).ToList();
        HashSet<(string Base,string Target)> UnitPairs(Row row) => members.GetValueOrDefault(row["base_unit_group"],[])
            .SelectMany(b => members.GetValueOrDefault(row["target_unit_group"],[]).Select(target => (Base:b,Target:target))).ToHashSet();
        var parent = Enumerable.Range(0,allRoutes.Count).ToArray();
        int Root(int n) { while(parent[n]!=n) { parent[n]=parent[parent[n]];n=parent[n]; } return n; }
        var pairOwner = new Dictionary<(string,string),int>();
        for(var i=0;i<allRoutes.Count;i++) foreach(var unitPair in UnitPairs(allRoutes[i]))
        { if(pairOwner.TryGetValue(unitPair,out var priorIndex)) parent[Root(i)]=Root(priorIndex); else pairOwner[unitPair]=i; }
        var affectedKeys = affected.Select(r=>r["upgrade_key"]).ToHashSet();
        var components = allRoutes.Select((r,i)=>(Row:r,Root:Root(i))).GroupBy(r=>r.Root)
            .Where(g=>g.Any(r=>affectedKeys.Contains(r.Row["upgrade_key"]))).Select(g=>g.Select(r=>r.Row).ToArray()).ToArray();
        foreach (var rows in components.OrderBy(r=>r.Min(x=>x["upgrade_key"]),StringComparer.Ordinal))
        {
            var actual = rows.SelectMany(UnitPairs).ToHashSet();
            if (actual.Count == 0) continue; // 空占位组还没有实际升级对，后续加兵再编译；不能阻止保存未完成的树。
            var pair = (Item1:string.Join(",",actual.Select(p=>p.Base).Distinct().Order()),Item2:string.Join(",",actual.Select(p=>p.Target).Distinct().Order()));
            var directCost=rows.Select(r=>r["resource_cost"]).Distinct().Count()==1;
            var rs = rows.Select(r =>
            {
                var tab = categories.GetValueOrDefault(r["target_unit_group"], "");
                return new Route(r, r["base_unit_group"], r["target_unit_group"], r["upgrade_key"], tab,
                    new(factions.GetValueOrDefault(tab, []), races.GetValueOrDefault(tab, []), excluded.GetValueOrDefault(tab)),
                    directCost ? new UpgradeBill(0,[]) : ReadBill(tables, r["resource_cost"]), Requirements(r),directCost);
            }).OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
            var pairRules=actual.Select(p=>string.Join("\n",rs.Where(r=>members[r.Base].Contains(p.Base)&&members[r.Target].Contains(p.Target))
                .Select(r=>Signature(new{r.Scope,r.Bill,r.Requirements})).Distinct().Order(StringComparer.Ordinal))).Distinct().ToArray();
            if(pairRules.Length>1)
                throw new InvalidDataException($"部分重叠的多兵组包含不同的派系、成本或条件规则：{pair.Item1} → {pair.Item2}。请先拆组，避免把只属于其中一名单位的规则扩展给其他单位；原包与编辑保留。");
            var groupPairs = new HashSet<(string Base,string Target)>();
            foreach(var unitPair in actual) foreach(var b in aliases[unitPair.Base]) foreach(var target in aliases[unitPair.Target])
            {
                if(!members[b].SelectMany(u=>members[target].Select(v=>(Base:u,Target:v))).All(actual.Contains))
                    throw new InvalidDataException($"重复兵兼容不能安全连接 {b} → {target}：组中另有未配置升级的单位。请先拆分该重叠组，避免自动增加不属于原设计的升级。");
                groupPairs.Add((b,target));
            }
            var id = DuplicateUpgradeArchive.Id(ns + "\n" + string.Join("|",actual.OrderBy(p=>p.Base,StringComparer.Ordinal).ThenBy(p=>p.Target,StringComparer.Ordinal).Select(p=>p.Base+"→"+p.Target)));
            var source = rs[0];
            if (rs.Select(r => r.Requirements).Distinct().Count() > 1)
            {
                var chosen = Choose(id + ":requirements", $"{pair.Item1.Replace('\u0001', ',')} → {pair.Item2.Replace('\u0001', ',')}：原生执行的等级、科技、建筑条件必须共用一套。请选择共同条件。", rs, "requirements");
                source = rs.First(r => r.Key == chosen);
                // 原版/依赖包的组合键要求无法靠覆盖删除，不能假装移除了这些条件。
                if (conflicts.Count == 0) foreach (var table in new[] { Tech, Building, Effects })
                {
                    var keyField=table==Effects?"unit_upgrade_to_unit_group":"unit_upgrade";
                    string Value(Row row) => string.Join("|", row.Table.Columns.Where(c => c.Name != keyField).Select(c => row[c.Name]));
                    var allowed = tables.All(table).Where(q => q[keyField] == source.Key).Select(Value).ToHashSet();
                    if (rs.Any(r => tables.All(table).Any(q => q[keyField] == r.Key && !allowed.Contains(Value(q)) && !tables.Local(table).Any(l => l[keyField] == r.Key && Value(l) == Value(q)))))
                        throw new InvalidDataException("选择的共同条件无法移除原版或依赖包已有的科技/建筑要求：" + source.Key);
                }
            }
            var profiles = rs.GroupBy(r => Signature(r.Bill)).Select(g => new UpgradeProfile(
                DuplicateUpgradeArchive.Id(g.Key), g.Select(r => r.Scope).Distinct().ToArray(), g.First().Bill)).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
            var decisions = new Dictionary<string, string>();
            var actorRows = tables.All("factions_tables").ToDictionary(r => r["key"],r => r["subculture"]);
            var actors = actorRows.Select(r => (Faction: r.Key, Race: r.Value))
                .Concat(factions.Values.SelectMany(v => v).Where(f => !actorRows.ContainsKey(f)).SelectMany(f => races.Values.SelectMany(v => v).Append("__other").Distinct().Select(r => (Faction: f, Race: r))))
                .Concat(races.Values.SelectMany(v => v).Distinct().Select(r => (Faction: "__other", Race: r)))
                .Append((Faction: "__other", Race: "__other")).Distinct().ToArray();
            foreach (var set in actors.GroupBy(a => string.Join("|", profiles.Where(p => p.Scopes.Any(s => s.Matches(a.Faction, a.Race))).Select(p => p.Id))))
            {
                var active = set.Key.Split('|', StringSplitOptions.RemoveEmptyEntries); if (active.Length <= 1) continue;
                var options = rs.Where(r => active.Contains(DuplicateUpgradeArchive.Id(Signature(r.Bill)))).ToArray();
                var chosen = Choose(id + ":cost:" + set.Key, $"{pair.Item1.Replace('\u0001', ',')} → {pair.Item2.Replace('\u0001', ',')}：以下派系可同时看到不同成本：{string.Join(", ", set.Take(5).Select(a => a.Faction))}。选择该重叠范围使用的成本。", options);
                decisions[set.Key] = DuplicateUpgradeArchive.Id(Signature(rs.Single(r => r.Key == chosen).Bill));
            }
            var nativeGold = profiles.Min(p => p.Bill.Gold);
            var slots = profiles.SelectMany(p => p.Bill.Pools.Select(f => f.Key)).Distinct().Order(StringComparer.Ordinal).ToList();
            if (profiles.Any(p => p.Bill.Gold != nativeGold)) slots.Insert(0, "__gold");
            if (!directCost && !profiles.Any(p => p.Scopes.Any(s => s.Factions.Length == 0 && s.Races.Length == 0))) slots.Add("__gate");
            var financialId = DuplicateUpgradeArchive.Id(ns + Signature(new { nativeGold, profiles, decisions, slots }));
            var policy = new UpgradePolicy(financialId, directCost?rs[0].Source["resource_cost"]:"studio_du_cost_" + financialId, nativeGold, profiles, decisions, slots.ToArray(),directCost);
            plans.Add(new(id, rs, groupPairs.OrderBy(p=>p.Base,StringComparer.Ordinal).ThenBy(p=>p.Target,StringComparer.Ordinal).ToArray(), policy, source.Source));
        }
        if (conflicts.Count > 0) throw new UpgradeRuleConflictException(conflicts.Values.ToArray());
        var output = Build(authored, tables, schema, edits, plans, xml, notes,ns);
        DuplicateUpgradeArchive.Capture(authored, output, choices,ns,edits.ProjectKey);
        return output;
    }

    private static UpgradeBill ReadBill(Tables t, string key)
    {
        var row = t.Find(Cost, r => r["id"] == key);
        var gold = -long.Parse(row["treasury_cost"], CultureInfo.InvariantCulture);
        if (gold < 0 || gold > int.MaxValue) throw new InvalidDataException("重复兵兼容不支持收益价签：" + key);
        var fees = new List<UpgradeFee>();
        foreach (var link in t.All(CostPool).Where(r => r["resource_cost"] == key && r["amount"] != "0"))
        {
            var amount = -long.Parse(link["amount"], CultureInfo.InvariantCulture);
            if (amount <= 0 || amount > int.MaxValue || link["context"] != "absolute") throw new InvalidDataException("重复兵兼容需要绝对值消耗，不能静默改写收益或百分比：" + key);
            var binding = t.Find(Binding, r => r["unique_id"] == link["pooled_resource_factor"]);
            if (binding["specific_faction_set"].Length > 0) throw new InvalidDataException("重复兵成本带派系集合限定，请先拆成页签可见范围的成本：" + key);
            var resource = t.Find(Resources, r => r["key"] == binding["resource"]);
            if (resource["scope"] is not ("FACTION" or "FACTION_PROVINCE_PERSISTENT")) throw new InvalidDataException("暂不支持的重复兵资源作用域：" + resource["scope"]);
            fees.Add(new(resource["key"], resource["scope"], amount, binding["factor"], resource["optional_icon_path"], resource["key"], resource["ai_ignored"] == "true"));
        }
        // 一种资源可能连多个因子；不能按 resource_cost 的第一列吞掉，也不能重复展示两次。
        var pools = fees.GroupBy(f => (f.Key, f.Scope, f.Factor, f.Icon, f.AiIgnored)).Select(g => g.First() with { Amount = g.Sum(f => f.Amount) }).OrderBy(f => f.Key, StringComparer.Ordinal).ThenBy(f => f.Factor, StringComparer.Ordinal).ToArray();
        if (pools.GroupBy(f => f.Key).Any(g => g.Count() > 1)) throw new InvalidDataException("同一资源使用多个扣费因子，需先统一账本因子：" + key);
        return new(gold, pools);
    }

    private static Dictionary<string, byte[]> Build(PackArchive authored, Tables t, Schema schema, WarbandEdits edits, List<Plan> plans, string? xml, List<string> notes,string ns)
    {
        var staged = new Dictionary<string, DbTable>(StringComparer.OrdinalIgnoreCase);
        var resolver = new TabResolver(authored, edits, schema);
        var internalFile = "zzzz_studio_duplicate_"+ns+"_edits";
        // 成本四表仍遵守 zzzz_studio_edits；预算行使用独立 key，追加后可按清单逆向恢复。
        string PrivatePath(string table) => $"db/{table}/"+(table is Cost or CostPool or Binding or Resources or "pooled_resource_factors_tables"?"zzzz_studio_edits":internalFile);
        DbTable Destination(string path, Row template)
        {
            if (staged.TryGetValue(path, out var existing)) return existing;
            if (authored.Find(path) is { } entry) return staged[path] = DbTable.Decode(authored.ReadDecoded(entry), template.Table.TableName, schema);
            return staged[path] = new DbTable { TableName = template.Table.TableName, Version = template.Table.Version, Guid = Guid.NewGuid().ToString(), MysteriousByte = template.Table.MysteriousByte, Columns = template.Table.Columns, Definition = template.Table.Definition, Rows = [], RawRows = [] };
        }
        void Add(string table, Row template, Dictionary<string, string> changes, string? path = null)
        {
            path??=PrivatePath(table);path=authored.Find(path)?.Path??path;
            var dest = Destination(path, template);
            if (dest.Version != template.Table.Version) throw new InvalidDataException("重复兵兼容遇到不同版本的表：" + table);
            var raw = template.Raw.ToArray();
            var temp = new DbTable { TableName = table, Version = dest.Version, Guid = dest.Guid, MysteriousByte = dest.MysteriousByte, Columns = dest.Columns, Definition = dest.Definition, Rows = [template.Display.ToArray()], RawRows = [raw] };
            foreach (var (field, value) in changes) DbCellEditor.Set(temp, 0, dest.Columns.FindIndex(c => c.Name == field), value);
            var encoded = DbTable.Decode(DbEncoder.Encode(temp), table, schema);
            staged[path]=TableFiles.MergeComposite(dest,encoded,Keys[table])!;
        }
        void DropRequirements(string table, string key)
        {
            var keyField=table==Effects?"unit_upgrade_to_unit_group":"unit_upgrade";
            foreach (var row in t.Local(table).Where(r => r[keyField] == key))
            {
                var dest = Destination(row.Path, row);
                for (var i = dest.Rows.Count - 1; i >= 0; i--) if (dest.Rows[i][dest.Columns.FindIndex(c => c.Name == keyField)].ToTsv() == key) { dest.Rows.RemoveAt(i); dest.RawRows.RemoveAt(i); }
            }
        }
        var bridges = 0;
        foreach (var plan in plans)
        {
            foreach (var (b,target) in plan.Pairs)
            {
                var originals = plan.Routes.Where(r => r.Base == b && r.Target == target).ToArray();
                var keys = originals.Length > 0 ? originals.Select(r => (r.Key, r.Source)).ToArray()
                    : [("studio_du_bridge_" + DuplicateUpgradeArchive.Id(plan.Id + "|" + b + "|" + target), plan.RequirementSource)];
                foreach (var (key, template) in keys)
                {
                    if (originals.Length == 0) bridges++;
                    var path = t.Local(TabFileNaming.Routes).FirstOrDefault(r => r["upgrade_key"] == key)?.Path ?? resolver.PathFor(TabFileNaming.Routes, b);
                    var all = t.Local(TabFileNaming.Routes).Where(r => r["upgrade_key"] == key).ToArray();
                    // 当前包同 key 的多个来源都就地改，防止作者文件的覆盖顺序把成本改回去。
                    foreach (var destination in all.Length > 0 ? all.Select(r => r.Path).Distinct() : [path])
                        Add(TabFileNaming.Routes, template, new() { ["upgrade_key"] = key, ["base_unit_group"] = b, ["target_unit_group"] = target, ["resource_cost"] = plan.Policy.Cost, ["required_rank"] = plan.RequirementSource["required_rank"], ["subtracted_rank"] = plan.RequirementSource["subtracted_rank"] }, destination);
                    foreach (var table in new[] { Tech, Building, Effects })
                    {
                        DropRequirements(table, key);
                        var keyField=table==Effects?"unit_upgrade_to_unit_group":"unit_upgrade";
                        foreach (var requirement in t.All(table).Where(r => r[keyField] == plan.RequirementSource["upgrade_key"])) Add(table, requirement, new() { [keyField] = key });
                    }
                }
            }
            if(!plan.Policy.Direct)
            {
                if(t.All(Cost).Any(r=>r["id"]==plan.Policy.Cost))throw new InvalidDataException("内部兼容成本 key 已被作者数据占用："+plan.Policy.Cost);
                var costSource = t.Find(Cost, r => r["id"] == plan.Routes[0].Source["resource_cost"]);
                Add(Cost, costSource, new() { ["id"] = plan.Policy.Cost, ["treasury_cost"] = (-plan.Policy.NativeGold).ToString(CultureInfo.InvariantCulture) });
            }
        }
        var privatePlans = plans.Where(p => p.Policy.Slots.Length > 0 || p.Policy.Profiles.Length > 1).DistinctBy(p=>p.Policy.Id).ToArray();
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (privatePlans.Length > 0)
        {
            if (xml is null) throw new InvalidDataException("重复兵成本分流需要当前包的 warband_upgrades.twui.xml，未修改原包。");
            AddRuntimeTables(t, privatePlans, Add, ns);
            result[UiPath] = Encoding.UTF8.GetBytes(PatchUi(xml, privatePlans.Select(p => p.Policy).ToArray()));
            var module = "script/campaign/studio_duplicate_upgrade_" + ns + "_data";
            var config = CreateConfig(privatePlans.Select(p => p.Policy).ToArray(), ns);
            result[module + ".lua"] = Encoding.UTF8.GetBytes(config);
            using var stream = typeof(DuplicateUpgradeCompiler).Assembly.GetManifestResourceStream("WarbandStudio.Pack.duplicate_upgrades.lua") ?? throw new InvalidDataException("安装缺少重复兵兼容运行脚本，请重新安装完整更新包。");
            using var ms = new MemoryStream(); stream.CopyTo(ms);
            result["script/campaign/mod/studio_duplicate_upgrade_" + ns + ".lua"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ms.ToArray()).Replace("__DATA_MODULE__", module, StringComparison.Ordinal));
            result["text/db/zzzz_studio_duplicate_"+ns+"_resources.loc"] = ResourceLoc(privatePlans.Select(p=>p.Policy).ToArray());
        }
        foreach (var (path, table) in staged) result[path] = DbEncoder.Encode(table);
        // 保留作者原价签/因子/许可；只有路线指向私有价格，运行时按派系选择实际账单。
        notes.Add($"重复兵兼容：{plans.Count} 组实际单位升级，{bridges} 条原生桥接路线，{privatePlans.Length} 套派系/资源分流；授权与画布位置不变。");
        return result;
    }
}
