using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarbandStudio.Ui.Services.Ai;

public sealed record AiTool(string Name, string Description, JsonObject InputSchema, bool ReadOnly = false)
{
    public object ToProtocol() => new
    {
        name = Name, description = Description, inputSchema = InputSchema,
        annotations = new { readOnlyHint = ReadOnly, destructiveHint = !ReadOnly, openWorldHint = false }
    };
}

public static class AiCatalog
{
    // 发现接口与执行校验共用同一份字段定义，避免客户端看到的参数和实际要求分离。
    private static JsonObject Object(string fields, string required = "")
    {
        var properties = new JsonObject();
        foreach (var field in fields.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = field.Split(':', 2);
            JsonNode schema = parts[1] switch
            {
                "s" => new JsonObject { ["type"] = "string" },
                "i" => new JsonObject { ["type"] = "integer" },
                "n" => new JsonObject { ["type"] = "number" },
                "b" => new JsonObject { ["type"] = "boolean" },
                "strings" => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                "choices" => new JsonObject { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
                "members" => new JsonObject { ["type"] = "array", ["items"] = Object("group:s unit:s", "group unit") },
                _ => new JsonObject { ["type"] = "string",
                    ["enum"] = new JsonArray(parts[1].Split(',').Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) }
            };
            properties[parts[0]] = schema;
        }
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false,
            ["required"] = new JsonArray(required.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
        };
    }

    private static JsonObject Operation(string name, string fields, string required, string description)
    {
        var schema = Object("action:" + name + " " + fields, "action " + required);
        schema["description"] = description;
        return schema;
    }

    public static readonly JsonObject[] Operations =
    [
        Operation("new_group", "group:s category:s x:i y:i", "group category x y", "Create an empty group using an unused key."),
        Operation("add_unit", "unit:s group:s category:s x:i y:i", "unit", "Add a unit to an existing group, or create a group when group is omitted. Does not grant military permissions."),
        Operation("remove_unit", "group:s unit:s", "group unit", "Remove a membership."),
        Operation("remove_group", "group:s", "group", "Delete group and associated routes and links."),
        Operation("rename_group", "group:s newKey:s", "group newKey", "Rename a group and synchronize references."),
        Operation("move_group", "group:s x:i y:i", "group x y", "Set the group origin in game pixels."),
        Operation("set_axis", "groups:strings axis:x,y value:i", "groups axis value", "Set one coordinate on multiple groups."),
        Operation("set_category", "group:s category:s x:i y:i", "group category x y", "Move a group to another tab."),
        Operation("merge_groups", "keep:s groups:strings x:i y:i category:s", "keep groups", "Merge into an existing group. Existing routes follow the normal editor merge behavior."),
        Operation("split_group", "group:s x:i y:i category:s", "group x y", "Split all member units into independent groups."),
        Operation("move_units", "group:s members:members category:s x:i y:i", "group members category x y", "Move selected memberships into a new group."),
        Operation("new_tab", "key:s donor:s background:s button:s fileName:s", "key donor fileName", "Create a tab from an existing donor layout, with an explicit DB filename."),
        Operation("rename_tab", "category:s newKey:s fileName:s", "category newKey fileName", "Rename tab and its exclusive files."),
        Operation("delete_tab", "category:s", "category", "Delete tab with its groups and associated routes."),
        Operation("copy_tab", "category:s", "category", "Copy internal groups and routes to the page clipboard."),
        Operation("paste_tab", "category:s", "category", "Paste clipboard into an existing tab, generating independent keys and preserving positions."),
        Operation("open_tabs", "categories:strings", "categories", "Open existing tabs in the editor."),
        Operation("close_tab", "category:s", "category", "Close an editor-opened tab without deleting its pack data."),
        Operation("import_tab", "source:s category:s race:s faction:s", "source category", "Import tab layout/art from a loaded source pack, using the normal editor import behavior."),
        Operation("set_editor_races", "category:s races:strings", "category races", "Set editor tab race classification; runtime display permissions use set_visibility."),
        Operation("set_visibility", "category:s factions:strings races:strings excluded:b", "category factions races excluded", "Set faction and subculture allow/exclusion rules. Does not change military permissions."),
        Operation("set_art", "category:s background:s button:s", "category", "Change tab art from local files or existing pack paths."),
        Operation("add_route", "base:s target:s cost:s rank:i subtract:i childPort:i parentPort:i midOffset:n linked:b mutual:b", "base target cost rank subtract", "Create a route. Ports are 1=top,2=right,3=bottom,4=left; linked defaults true."),
        Operation("update_route", "key:s base:s target:s cost:s rank:i subtract:i", "key base target cost rank subtract", "Update a route using its existing key."),
        Operation("swap_route", "key:s base:s target:s cost:s rank:i subtract:i childPort:i parentPort:i linked:b", "key base target cost rank subtract", "Reverse a route and optionally its drawing link."),
        Operation("remove_route", "key:s base:s target:s linked:b", "key base target", "Remove a route and optionally its link."),
        Operation("set_link", "child:s parent:s childPort:i parentPort:i midOffset:n", "child parent childPort parentPort", "Create or update a drawing link."),
        Operation("remove_link", "child:s parent:s", "child parent", "Remove a drawing link while retaining the upgrade route."),
        Operation("grant_permissions", "units:strings race:s faction:s", "units", "Explicitly grant military or exclusive faction permissions. External unit packs are supported."),
        Operation("remove_permissions", "units:strings race:s faction:s kind:all,military,exclusive", "units kind", "Remove only the requested permission channel."),
        Operation("rename_file", "path:s newName:s", "path newName", "Rename one internal pack file."),
        Operation("remove_file", "path:s", "path", "Remove one internal pack file; save still requires RPFM validation.")
    ];

    private static AiTool Tool(string name, string description, string fields = "", string required = "", bool readOnly = false)
        => new(name, description, Object(fields, required), readOnly);

    public static IReadOnlyList<AiTool> Tools { get; } = CreateTools();

    private static IReadOnlyList<AiTool> CreateTools()
    {
        const string target = "pack:s expectedRevision:i ";
        const string page = "query:s offset:i limit:i ";
        var edit = Object(target, "pack");
        edit["properties"]!["operations"] = new JsonObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 200,
            ["items"] = new JsonObject { ["oneOf"] = new JsonArray(Operations.Select(o => (JsonNode?)o.DeepClone()).ToArray()) }
        };
        ((JsonArray)edit["required"]!).Add("operations");
        var cells = Object(target + "path:s", "pack path");
        cells["properties"]!["cells"] = new JsonObject
        {
            ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 500,
            ["items"] = Object("row:i column:s expected:strings value:s", "row column expected value")
        };
        ((JsonArray)cells["required"]!).Add("cells");
        return
        [
            Tool("studio_status", "Get session state, open packs, current revision, dirty counts, undo depth and project.", readOnly: true),
            Tool("studio_configure", "Set the game root (containing data). Returns engine/schema availability.", "gameDir:s", "gameDir"),
            Tool("studio_project", "Create/open/close a project; list or set recognized packs; import a pack or the WUU template. Unsaved policy defaults fail.", "action:create,open,close,packs,recognize,import,template directory:s packDir:s key:s path:s packs:strings unsaved:fail,save,discard", "action"),
            Tool("studio_open_pack", "Open a writable pack or load an external unit reference. Existing edits are retained.", "path:s reference:b", "path"),
            Tool("studio_select_pack", "Select an already open pack.", "pack:s", "pack"),
            Tool("studio_close_pack", "Close a pack; unsaved policy defaults fail.", "pack:s unsaved:fail,save,discard", "pack"),
            Tool("studio_files", "List all effective internal files, including pending changes. Pagination defaults limit=100, maximum=500.", target + page, readOnly: true),
            Tool("studio_table", "Read effective DB rows and column types/references. Row indices are zero-based original indices, suitable for studio_table_edit.", target + page + "path:s", "path", true),
            Tool("studio_canvas", "Read tabs, group positions/members, routes, drawing links and visibility. Category filters groups/routes. Icon data is omitted.", target + "category:s", readOnly: true),
            Tool("studio_factions", "Read available factions and subcultures.", target + page, readOnly: true),
            Tool("studio_units", "Query the unit library including external packs and unassigned units. scope accepts race:<subculture> or faction key.", target + page + "scope:s reference:s unassigned:b", readOnly: true),
            Tool("studio_costs", "List costs/resources/references, create/update/rename/delete costs or remove a resource link. Signed gold/amount: negative spends. update supports append or replacePool for multiple resources.", target + page + "action:list,resources,references,create,update,rename,delete,remove_resource id:s newId:s gold:n resource:s amount:i replacePool:s pool:s append:b", "action"),
            new AiTool("studio_edit", "Apply a validated batch as one atomic undo step. All edits remain pending until studio_save. Include pack to identify the exact target. Existing permissions are preserved unless an explicit permission action is requested.", edit),
            new AiTool("studio_table_edit", "Edit cells atomically with original row snapshots for stale-data detection. Rename group keys synchronizes references; tab keys use rename_tab. Use studio_table to obtain expected rows.", cells),
            Tool("studio_undo", "Undo one edit in the selected pack.", target, "pack"),
            Tool("studio_redo", "Redo one edit in the selected pack.", target, "pack"),
            Tool("studio_save", "Save in place with backup or export to path. Error diagnostics block writes; warnings are returned. Different destination retains pending edits. Existing destination requires overwrite=true.", target + "path:s overwrite:b", "pack"),
            Tool("studio_validate", "Compile current edits to a temporary export and run the same RPFM save gate, returning errors/warnings or duplicate-rule conflicts.", target, "pack"),
            Tool("studio_resolve_rules", "Choose only option IDs returned by a preceding conflict result, then retry save. Choices are undoable.", target + "choices:choices", "pack choices"),
            Tool("studio_history", "List backups or restore a backup after handling current edits.", target + "action:list,restore path:s unsaved:fail,save,discard", "pack action"),
            Tool("studio_bundle", "Export current base pack, unsaved edits, references, undo/redo and logs into a diagnostic ZIP.", target + "path:s overwrite:b", "pack path"),
            Tool("studio_logs", "Read recent log entries from this automation session.", page, readOnly: true),
            Tool("studio_assets", "List UI reference art, import local art, add art to current pack or change tab art using studio_edit.", target + page + "action:list,import,add kind:bg,btn files:strings file:s", "action"),
            Tool("studio_file", "Read effective raw files (text or base64, up to 1 MiB), or stage a local source file at an internal path. DB edits normally use studio_table_edit.", target + "action:read,write path:s source:s encoding:text,base64", "action path")
        ];
    }

    public static void Validate(JsonElement value, JsonObject schema, string location = "arguments")
    {
        if (schema["oneOf"] is JsonArray alternatives)
        {
            foreach (var alternative in alternatives.OfType<JsonObject>())
            {
                try { Validate(value, alternative, location); return; }
                catch (ArgumentException) { }
            }
            throw new ArgumentException(location + " does not match a supported operation schema.");
        }
        var type = schema["type"]?.GetValue<string>();
        var valid = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => true
        };
        if (!valid) throw new ArgumentException(location + " must be " + type + ".");
        if (schema["enum"] is JsonArray allowed && !allowed.Any(v => v!.GetValue<string>() == value.GetString()))
            throw new ArgumentException(location + " has an unsupported value.");
        if (type == "object")
        {
            foreach (var name in schema["required"] as JsonArray ?? [])
                if (!value.TryGetProperty(name!.GetValue<string>(), out _)) throw new ArgumentException(location + "." + name + " is required.");
            var properties = schema["properties"] as JsonObject ?? new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                if (properties[property.Name] is JsonObject child) Validate(property.Value, child, location + "." + property.Name);
                else if (schema["additionalProperties"] is JsonObject extra) Validate(property.Value, extra, location + "." + property.Name);
                else throw new ArgumentException(location + "." + property.Name + " is unknown.");
            }
        }
        if (type == "array")
        {
            var length = value.GetArrayLength();
            if (length < (schema["minItems"]?.GetValue<int>() ?? 0) || length > (schema["maxItems"]?.GetValue<int>() ?? 10000))
                throw new ArgumentException(location + " has an invalid length.");
            foreach (var item in value.EnumerateArray()) Validate(item, (JsonObject)schema["items"]!, location + "[]");
        }
    }
}
