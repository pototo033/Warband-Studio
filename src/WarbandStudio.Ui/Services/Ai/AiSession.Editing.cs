using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services.Ai;

public sealed partial class AiSession
{
    private object EditCells(AiArguments args)
    {
        var path = args.Required("path");
        var cells = args.Json.GetProperty("cells").EnumerateArray().Select(cell => new AiArguments(cell)).ToArray();
        var before = _backend.ReadTable(path);
        var targets = new HashSet<(int Row, string Column)>();
        // 所有旧快照先对照批次开始时的表，避免同一行的前一个字段使后一个字段误报过期。
        foreach (var cell in cells)
        {
            var row = cell.Number("row");
            var column = cell.Required("column");
            if (!targets.Add((row, column))) throw new ArgumentException("Duplicate cell in batch: " + row + "/" + column);
            if (row < 0 || row >= before.Rows.Count || !before.Rows[row].Select(value => value.ToTsv()).SequenceEqual(cell.Strings("expected")))
                throw new AiFailure("stale_table", "Table changed. Read studio_table before retrying.", new { path, row });
            if (!before.Columns.Any(c => c.Name == column)) throw new ArgumentException("Unknown column: " + column);
        }
        foreach (var cell in cells)
        {
            var row = cell.Number("row");
            var current = _backend.ReadTable(path).Rows[row].Select(value => value.ToTsv()).ToArray();
            _backend.EditTableCell(_backend.Pack!.PackPath!, path, row, cell.Required("column"), current, cell.Text("value"));
        }
        return new { editedCells = cells.Length };
    }

    private void RequireGroup(string key, bool exists = true)
    {
        if (!Regex.IsMatch(key, "^[A-Za-z0-9_]+$")) throw new ArgumentException("Invalid group key: " + key);
        var groups = _backend.AutomationTable("unit_upgrade_groups_tables", ["unit_group"]);
        var found = groups is not null && groups.Rows.Any(r => r[groups.Columns.FindIndex(c => c.Name == "unit_group")].ToTsv().Equals(key, StringComparison.OrdinalIgnoreCase));
        if (found != exists) throw new ArgumentException(exists ? "Unknown group: " + key : "Group key is already used: " + key);
    }

    private static int Port(AiArguments args, string field, int fallback)
    {
        var value = args.Number(field, fallback);
        return value is >= 1 and <= 4 ? value : throw new ArgumentException("Ports must be 1..4.");
    }

    private object Edit(AiArguments args)
    {
        var action = args.Required("action");
        string Group(string field = "group") { var key = args.Required(field); RequireGroup(key); return key; }
        switch (action)
        {
            case "new_group":
                var newGroup = args.Required("group");
                RequireGroup(newGroup, false);
                _backend.NewGroup(newGroup, args.Number("x"), args.Number("y"), args.Required("category"));
                return new { group = newGroup };
            case "add_unit":
                var unit = args.Required("unit");
                _backend.AutomationRequireUnit(unit);
                var create = !args.Has("group");
                var group = create ? _backend.MakeGroupKey(args.Required("category"), [unit], "studio_ai_" + Guid.NewGuid().ToString("N")[..12]) : Group();
                _backend.AddUnitToGroup(group, unit, args.Number("x"), args.Number("y"), create, create ? args.Required("category") : null);
                return new { group, unit };
            case "remove_unit": _backend.RemoveUnitFromGroup(Group(), args.Required("unit")); break;
            case "remove_group": _backend.RemoveGroup(Group()); break;
            case "rename_group": _backend.RenameGroup(Group(), args.Required("newKey")); break;
            case "move_group": _backend.SetGroupPos(Group(), args.Number("x"), args.Number("y")); break;
            case "set_axis":
                foreach (var key in args.Strings("groups")) RequireGroup(key);
                return new { moved = _backend.SetGroupsAxis(args.Strings("groups"), args.Required("axis"), args.Number("value")) };
            case "set_category": _backend.SetGroupCategory(Group(), args.Required("category"), args.Number("x"), args.Number("y")); break;
            case "merge_groups":
                foreach (var key in args.Strings("groups")) RequireGroup(key);
                _backend.MergeGroups(Group("keep"), args.Strings("groups"), args.Has("x") ? args.Number("x") : null,
                    args.Has("y") ? args.Number("y") : null, args.Has("category") ? args.Text("category") : null);
                break;
            case "split_group": return new { groups = _backend.SplitGroup(Group(), args.Number("x"), args.Number("y"), args.Text("category"), null, null) };
            case "move_units":
                RequireGroup(args.Required("group"), false);
                var members = args.Json.GetProperty("members").EnumerateArray()
                    .Select(m => (Group: m.GetProperty("group").GetString()!, Unit: m.GetProperty("unit").GetString()!)).ToList();
                foreach (var member in members) { RequireGroup(member.Group); _backend.AutomationRequireUnit(member.Unit); }
                _backend.MoveUnitsToNewGroup(args.Required("group"), members, args.Number("x"), args.Number("y"), args.Required("category"), null, null);
                break;
            case "new_tab": _backend.NewTab(args.Required("key"), args.Required("donor"), args.Text("background"), args.Text("button"), args.Required("fileName")); break;
            case "rename_tab": _backend.RenameTab(args.Required("category"), args.Required("newKey"), args.Required("fileName")); break;
            case "delete_tab": return _backend.DeleteTab(args.Required("category"));
            case "copy_tab": return _backend.CopyTab(args.Required("category"));
            case "paste_tab": return _backend.PasteTab(args.Required("category"));
            case "open_tabs": _backend.OpenTabs(args.Strings("categories")); break;
            case "close_tab": _backend.CloseOpenedTab(args.Required("category")); break;
            case "set_editor_races": _backend.ApplyTabToRaces(args.Required("category"), args.Strings("races")); break;
            case "import_tab":
                var imported = _backend.ImportTab(args.Required("source"), args.Required("category"), args.Text("race"), args.Text("faction"));
                if (!imported.StartsWith("从 ")) throw new AiFailure("tab_import_failed", imported);
                return imported;
            case "set_visibility": _backend.SetTabVisibility(args.Required("category"), args.Strings("factions"), args.Strings("races"), args.Flag("excluded")); break;
            case "set_art": _backend.SetTabArt(args.Required("category"), args.Has("background") ? args.Text("background") : null, args.Has("button") ? args.Text("button") : null); break;
            case "add_route":
            case "update_route":
            case "swap_route":
                var source = Group("base");
                var target = Group("target");
                var cost = args.Text("cost");
                if (cost.Length > 0 && !_backend.CostList().Any(c => c.Id == cost)) throw new ArgumentException("Unknown cost: " + cost);
                var rank = args.Number("rank");
                var subtract = args.Number("subtract");
                if (rank < 0 || subtract < 0) throw new ArgumentException("Rank requirements and subtracted ranks must be nonnegative.");
                if (action == "add_route")
                    return new { route = _backend.AddRoute(source, target, cost, rank, subtract, args.Flag("mutual"),
                        Port(args, "childPort", 1), Port(args, "parentPort", 3), alsoLink: args.Flag("linked", true), midOffset: args.Decimal("midOffset")) };
                RequireRoute(args.Required("key"), source, target);
                if (action == "update_route") _backend.UpdateRoute(args.Required("key"), source, target, cost, rank, subtract);
                else _backend.SwapRoute(args.Required("key"), source, target, cost, rank, subtract,
                    Port(args, "childPort", 1), Port(args, "parentPort", 3), args.Flag("linked", true));
                break;
            case "remove_route":
                RequireRoute(args.Required("key"), Group("base"), Group("target"));
                _backend.RemoveRoute(args.Required("key"), args.Required("base"), args.Required("target"), args.Flag("linked", true));
                break;
            case "set_link": _backend.AddLinkOnly(Group("child"), Group("parent"), Port(args, "childPort", 1), Port(args, "parentPort", 3), "AI edit", args.Decimal("midOffset")); break;
            case "remove_link": _backend.RemoveLinkOnly(Group("child"), Group("parent")); break;
            case "grant_permissions":
                if (!args.Has("race") && !args.Has("faction")) throw new ArgumentException("Provide race or faction for explicit permissions.");
                return new { grants = args.Strings("units").Sum(u => _backend.GrantUnit(u, args.Text("race"), args.Text("faction"))) };
            case "remove_permissions": return _backend.RemoveUnitFromMilGroup(args.Strings("units"), args.Text("race"), args.Text("faction"), args.Required("kind"));
            case "rename_file": return _backend.RenamePackFile(args.Required("path"), args.Required("newName"));
            case "remove_file": return _backend.RemovePackFile(args.Required("path"));
            default: throw new ArgumentException("Unknown edit operation: " + action);
        }
        return new { action, applied = true };
    }

    private void RequireRoute(string key, string source, string target)
    {
        var table = _backend.AutomationTable(TabFileNaming.Routes, ["upgrade_key"]);
        if (table is null) throw new ArgumentException("No upgrade routes.");
        var k = table.Columns.FindIndex(c => c.Name == "upgrade_key");
        var b = table.Columns.FindIndex(c => c.Name == "base_unit_group");
        var t = table.Columns.FindIndex(c => c.Name == "target_unit_group");
        if (!table.Rows.Any(r => r[k].ToTsv() == key && r[b].ToTsv() == source && r[t].ToTsv() == target))
            throw new AiFailure("stale_route", "Route key/endpoints do not match. Read the canvas again.");
    }

    private object Costs(AiArguments args)
    {
        var action = args.Required("action");
        if (action == "list") return Page(_backend.CostList(), args);
        if (action == "resources") return Page(_backend.PooledResources(), args);
        if (action == "references") return _backend.CostReferences(args.Required("id"));
        RequireWritable();
        return _backend.Edit(() =>
        {
            var id = args.Required("id");
            var old = _backend.CostList().SingleOrDefault(c => c.Id == id);
            if (action != "create" && old is null) throw new ArgumentException("Unknown cost: " + id);
            if (args.Has("resource") && !_backend.PooledResources().Any(r => r.Key == args.Text("resource")))
                throw new ArgumentException("Unknown resource key: " + args.Text("resource"));
            var gold = args.Decimal("gold", old?.Gold ?? 0);
            var message = action switch
            {
                "create" => _backend.CreateWarbandCost(id, gold, args.Text("resource"), args.Long("amount")),
                "update" => _backend.UpdateWarbandCost(id, gold, args.Text("resource"), args.Long("amount"),
                    args.Has("resource"), replacePoolId: args.Has("replacePool") ? args.Text("replacePool") : null, addResource: args.Flag("append")),
                "rename" => _backend.RenameWarbandCost(id, args.Required("newId")),
                "delete" => _backend.DeleteWarbandCost(id),
                "remove_resource" => _backend.RemoveWarbandCostResource(id, args.Required("pool")),
                _ => throw new ArgumentException("Unsupported cost action.")
            };
            if (!message.StartsWith("成本工坊")) throw new AiFailure("cost_edit_failed", message);
            return new { message };
        });
    }

    private object Assets(AiArguments args)
    {
        return args.Required("action") switch
        {
            "list" => Page(_backend.UiLibrary(), args),
            "import" => new { imported = _backend.ImportUiAssets(args.Strings("files"), args.Required("kind")) },
            "add" => AddAsset(args),
            _ => throw new ArgumentException("Unsupported asset action.")
        };
    }

    private object AddAsset(AiArguments args)
    {
        RequireWritable();
        var asset = _backend.UiLibrary().SingleOrDefault(a => a.File == args.Required("file"))
            ?? throw new ArgumentException("Unknown asset file.");
        return _backend.Edit(() => _backend.AddUiAssetToPack(asset));
    }

    private object PackFile(AiArguments args)
    {
        var path = args.Required("path").Replace('\\', '/');
        if (path.StartsWith('/') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("Invalid internal pack path.");
        if (args.Required("action") == "read")
        {
            var bytes = _backend.AutomationReadFile(path);
            if (bytes.Length > 1024 * 1024) throw new AiFailure("file_too_large", "Use DB table pagination or a local file tool for files above 1 MiB.");
            return new { path, bytes = bytes.Length, content = args.Text("encoding", "text") == "base64" ? Convert.ToBase64String(bytes) : new UTF8Encoding(false, true).GetString(bytes) };
        }
        RequireWritable();
        var source = Path.GetFullPath(args.Required("source"));
        if (!File.Exists(source)) throw new FileNotFoundException("Local source file does not exist.", source);
        return _backend.Edit(() =>
        {
            _backend.Edits.RemoveFiles.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            _backend.Edits.FileReplacements.RemoveAll(p => p.Target.Equals(path, StringComparison.OrdinalIgnoreCase));
            _backend.Edits.FileReplacements.Add((path, source));
            return new { path, source, pending = true };
        });
    }
}
