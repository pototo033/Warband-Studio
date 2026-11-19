using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using WarbandStudio.Pack;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Services.Ai;

public sealed class AiFailure(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

internal readonly record struct AiArguments(JsonElement Json)
{
    public bool Has(string name) => Json.TryGetProperty(name, out _);
    public string Text(string name, string fallback = "") => Has(name) ? Json.GetProperty(name).GetString()! : fallback;
    public string Required(string name) => !string.IsNullOrWhiteSpace(Text(name)) ? Text(name) : throw new ArgumentException(name + " is required.");
    public int Number(string name, int fallback = 0) => Has(name) ? Json.GetProperty(name).GetInt32() : fallback;
    public long Long(string name, long fallback = 0) => Has(name) ? Json.GetProperty(name).GetInt64() : fallback;
    public double Decimal(string name, double fallback = 0) => Has(name) ? Json.GetProperty(name).GetDouble() : fallback;
    public bool Flag(string name, bool fallback = false) => Has(name) ? Json.GetProperty(name).GetBoolean() : fallback;
    public string[] Strings(string name) => Has(name) ? Json.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
}

public sealed partial class AiSession : IAsyncDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly MainViewModel _vm;
    private readonly Backend _backend;
    private readonly Func<Func<Task<object?>>, Task<object?>> _dispatch;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _logs = new();
    private readonly Dictionary<string, IReadOnlyList<UpgradeRuleConflict>> _conflicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ownsBackend;
    private readonly bool _hasUi;
    public string Id { get; } = Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..12];

    public AiSession(MainViewModel vm, Func<Func<Task<object?>>, Task<object?>>? dispatch = null, bool ownsBackend = false)
    {
        _vm = vm;
        _backend = vm.AutomationBackend;
        _dispatch = dispatch ?? (action => action());
        _ownsBackend = ownsBackend;
        _hasUi = dispatch is not null;
        _backend.Log += CaptureLog;
    }

    private void CaptureLog(string message)
    {
        lock (_logs)
        {
            _logs.Enqueue(DateTimeOffset.Now.ToString("O") + " " + message);
            while (_logs.Count > 500) _logs.Dequeue();
        }
    }

    public async Task<object> CallAsync(string name, JsonElement arguments, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            return await _dispatch(async () =>
            {
                try
                {
                    var tool = AiCatalog.Tools.SingleOrDefault(t => t.Name == name)
                        ?? throw new AiFailure("unknown_tool", "Unknown tool: " + name);
                    AiCatalog.Validate(arguments, tool.InputSchema);
                    if (_vm.IsBusy) throw new AiFailure("busy", "The editor is busy. Retry after it finishes.");
                    if (_vm.FlushTableEdits is not null) await _vm.FlushTableEdits();
                    _backend.ResetExportDiagnostics();
                    cancellation.ThrowIfCancellationRequested();
                    var args = new AiArguments(arguments);
                    if (SelectTarget(args) && _hasUi) _vm.RefreshAutomation();
                    if (args.Has("expectedRevision") && args.Number("expectedRevision") != _backend.CaptureReadContext().Revision)
                        throw new AiFailure("stale_revision", "Pack changed. Read studio_status before retrying.");
                    CaptureLog("AI " + name);
                    if (!tool.ReadOnly) _vm.BeginAutomationEdit();
                    _vm.SetStatus("AI 正在执行：" + name);
                    var data = await ExecuteAsync(name, args);
                    string? displayWarning = null;
                    var queryOnly = name == "studio_costs" && args.Text("action") is "list" or "resources" or "references"
                        || name == "studio_assets" && args.Text("action") == "list"
                        || name == "studio_project" && args.Text("action") == "packs"
                        || name == "studio_history" && args.Text("action") == "list";
                    if (!tool.ReadOnly && !queryOnly && _hasUi)
                    {
                        try { _vm.RefreshAutomation(); }
                        catch (Exception ex) { displayWarning = ex.Message; CaptureLog("UI refresh: " + ex.Message); }
                    }
                    _vm.SetStatus("AI 已完成：" + name);
                    return Result(false, new { ok = true, result = data, displayWarning, state = State() });
                }
                catch (UpgradeRuleConflictException ex)
                {
                    if (_backend.Pack?.PackPath is { } path) _conflicts[path] = ex.Conflicts;
                    return Error("rule_conflict", ex.Message, ex.Conflicts);
                }
                catch (AiFailure ex) { return Error(ex.Code, ex.Message, ex.Details); }
                catch (OperationCanceledException ex) { return Error("cancelled", ex.Message); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    return Error(ex is ArgumentException ? "invalid_arguments" : "operation_failed", ex.Message,
                        _backend.LastExportDiagnostics);
                }
                catch (Exception ex)
                {
                    FileLog.Write("AI tool failed: " + name, ex);
                    return Error("internal_error", ex.Message);
                }
            }) ?? throw new InvalidOperationException("Automation dispatcher returned no result.");
        }
        finally { _gate.Release(); }
    }

    private object Error(string code, string message, object? details = null)
    {
        CaptureLog("AI error " + code + " " + message);
        _vm.SetStatus("AI 操作未完成：" + message);
        return Result(true, new { ok = false, error = new { code, message, details }, state = State() });
    }

    private static object Result(bool error, object data) => new
    {
        isError = error, structuredContent = data,
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(data, Json) } }
    };

    private object State() => new
    {
        session = Id, version = Updater.CurrentVersion, gameDir = _vm.GameDir,
        project = _vm.Project, projectDirectory = _vm.HasProject ? _vm.ProjectDir : null,
        activePack = _backend.Pack?.PackPath, revision = _backend.CaptureReadContext().Revision,
        undoDepth = _backend.UndoDepth,
        packs = _backend.Packs.Select(p => new { path = p.PackPath, p.DisplayName, reference = p.IsUnitPack,
            pendingEdits = _backend.EditCountOf(p.PackPath), summary = _backend.EditSummaryOf(p.PackPath) }).ToArray()
    };

    private bool SelectTarget(AiArguments args)
    {
        if (!args.Has("pack")) return false;
        var path = Path.GetFullPath(args.Required("pack"));
        var session = _backend.Packs.SingleOrDefault(p => string.Equals(p.PackPath, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new AiFailure("pack_not_open", "Open the pack first: " + path);
        var changed = !ReferenceEquals(_backend.Pack, session);
        _backend.SwitchTo(session);
        return changed;
    }

    private void RequireWritable()
    {
        var pack = _backend.Pack ?? throw new InvalidOperationException("Open a pack first.");
        if (pack.IsUnitPack || string.Equals(pack.PackPath, _backend.VanillaDbPackPath, StringComparison.OrdinalIgnoreCase))
            throw new AiFailure("read_only", "Reference packs and vanilla packs are read-only.");
    }

    private static object Page<T>(IEnumerable<T> items, AiArguments args)
    {
        var query = args.Text("query");
        var filtered = items.Where(item => query.Length == 0 ||
            JsonSerializer.Serialize(item, Json).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var offset = args.Number("offset");
        var limit = args.Number("limit", 100);
        if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("offset must be >=0 and limit must be 1..500.");
        return new { total = filtered.Length, offset, limit, items = filtered.Skip(offset).Take(limit).ToArray(),
            nextOffset = offset + limit < filtered.Length ? (int?)(offset + limit) : null };
    }

    private async Task<object?> ExecuteAsync(string name, AiArguments args)
    {
        switch (name)
        {
            case "studio_status": return State();
            case "studio_configure":
                await _vm.ConfigureAutomationGameAsync(Path.GetFullPath(args.Required("gameDir")));
                return new { _backend.EngineText, _backend.SchemaText };
            case "studio_project": return await ProjectAsync(args);
            case "studio_open_pack":
                var opened = await _backend.OpenPackAsync(args.Required("path"), asUnitPack: args.Flag("reference"));
                if (!opened.IsUnitPack && _vm.HasProject) _backend.BindProject(opened, _vm.ProjectDir, _vm.ProjectKey);
                return new { opened.PackPath, opened.IsUnitPack, opened.CompatibilityRecoveryNotes };
            case "studio_select_pack": return State();
            case "studio_close_pack":
                await SettleUnsavedAsync(args, [_backend.Pack!]);
                await _backend.CloseSessionAsync(_backend.Pack!);
                return new { closed = args.Text("pack") };
            case "studio_files": return await Task.Run(() => Page(_backend.AutomationFiles(), args));
            case "studio_table": return await Task.Run(() => Table(args));
            case "studio_canvas": return await Task.Run(() => Canvas(args));
            case "studio_factions":
                return await Task.Run(() => Page(JsonNode.Parse(_backend.BuildFactionJson())!["races"]!.AsArray().Select(r => r!.DeepClone()), args));
            case "studio_units": return await Task.Run(() => Units(args));
            case "studio_costs": return await Task.Run(() => Costs(args));
            case "studio_edit":
                RequireWritable();
                return await Task.Run(() => _backend.Edit(() =>
                    args.Json.GetProperty("operations").EnumerateArray().Select(op => Edit(new AiArguments(op))).ToArray()));
            case "studio_table_edit":
                RequireWritable();
                return await Task.Run(() => _backend.Edit(() => EditCells(args)));
            case "studio_undo": RequireWritable(); return new { applied = _backend.Undo() };
            case "studio_redo": RequireWritable(); return new { applied = _backend.Redo() };
            case "studio_save": return await SaveAsync(args);
            case "studio_validate": return await ValidateAsync();
            case "studio_resolve_rules": return ResolveRules(args);
            case "studio_history":
                RequireWritable();
                var project = _backend.Pack!.ProjectDirectory ?? ProjectStore.ProjectDirOfPack(_backend.Pack.PackPath!);
                if (project is null) throw new AiFailure("no_project", "History requires a project pack.");
                if (args.Required("action") == "list")
                    return ProjectStore.HistoryOf(project, _backend.Pack.PackPath!).Select(f => new { path = f.FullName, f.Length, f.LastWriteTimeUtc }).ToArray();
                await SettleUnsavedAsync(args, [_backend.Pack]);
                return await _backend.RestoreFromBackupAsync(_backend.Pack.PackPath!, args.Required("path"));
            case "studio_bundle":
                var destination = Destination(args);
                await Task.Run(() => _backend.ExportDiagnosticBundle(destination, "Exported through WarbandStudio MCP."));
                return new { path = destination };
            case "studio_logs":
                lock (_logs) return Page(_logs.Reverse().ToArray(), args);
            case "studio_assets": return Assets(args);
            case "studio_file": return PackFile(args);
            default: throw new AiFailure("unknown_tool", name);
        }
    }

    private object Table(AiArguments args)
    {
        var table = _backend.ReadTable(args.Required("path"));
        var indexed = table.Rows.Select((row, index) => new { index, values = row.Select(c => c.ToTsv()).ToArray() });
        return new
        {
            path = args.Text("path"), table.TableName, table.Version,
            columns = table.Columns.Select(c => new { c.Name, type = c.Source?.Type.ToString(), key = c.Source?.IsKey, reference = c.Source?.IsReference }).ToArray(),
            rows = Page(indexed, args)
        };
    }

    private object Canvas(AiArguments args)
    {
        var canvas = JsonNode.Parse(_backend.BuildWarbandJson())!.AsObject();
        canvas.Remove("icons");
        canvas.Remove("skin");
        var category = args.Text("category");
        if (category.Length > 0)
        {
            var groups = canvas["groups"]!.AsArray().Where(g => string.Equals(g!["category"]?.GetValue<string>(), category, StringComparison.OrdinalIgnoreCase)).Select(g => g!.DeepClone()).ToArray();
            var keys = groups.Select(g => g["unit_group"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            canvas["groups"] = new JsonArray(groups);
            foreach (var field in new[] { "routeList", "links" })
            {
                var source = canvas[field]?.AsArray() ?? [];
                var first = field == "links" ? "p" : "b";
                var second = field == "links" ? "c" : "t";
                canvas[field] = new JsonArray(source.Where(r => keys.Contains(r![first]!.GetValue<string>()) || keys.Contains(r![second]!.GetValue<string>())).Select(r => r!.DeepClone()).ToArray());
            }
        }
        return canvas;
    }

    private object Units(AiArguments args)
    {
        var scope = args.Text("scope");
        var text = args.Has("reference")
            ? _backend.BuildModUnitLibraryJson(Path.GetFullPath(args.Required("reference")), race: scope.Length == 0 ? null : scope.Replace("race:", ""))
            : _backend.BuildUnitLibraryJson(args.Text("scope"), args.Flag("unassigned"));
        var library = JsonNode.Parse(text)!;
        var names = library["names"]!.AsObject();
        var inTree = library["inTree"]?.AsArray().Select(u => u!.GetValue<string>()).ToHashSet() ?? [];
        return Page(library["units"]!.AsArray().Select(u => new { key = u!.GetValue<string>(),
            name = names[u.GetValue<string>()]?.GetValue<string>(), inTree = inTree.Contains(u.GetValue<string>()) }), args);
    }

    private async Task<object> ProjectAsync(AiArguments args)
    {
        var action = args.Required("action");
        if (action is "create" or "open" or "close") await SettleUnsavedAsync(args, _backend.Packs.ToArray());
        var ok = action switch
        {
            "create" => await _vm.NewProjectAsync(args.Required("directory"), args.Text("packDir"), args.Text("key")),
            "open" => await _vm.OpenProjectAsync(args.Required("directory")),
            "close" => await CloseProjectAsync(),
            "import" => await _vm.ImportPackToProjectAsync(args.Required("path")),
            "template" => await _vm.ImportWuuTemplateAsync(),
            "recognize" => await RecognizeAsync(args),
            "packs" => true,
            _ => throw new ArgumentException("Unsupported project action.")
        };
        if (!ok) throw new AiFailure("project_failed", _vm.Status);
        return new { project = _vm.Project, packs = _vm.ProjectPacksList(), message = _vm.Status };
    }

    private async Task<bool> CloseProjectAsync() { await _vm.CloseProjectAsync(); return !_vm.HasProject; }
    private async Task<bool> RecognizeAsync(AiArguments args) { await _vm.ApplyProjectPacksAsync(args.Strings("packs").ToList()); return !_vm.Status.Contains("失败"); }

    private async Task SettleUnsavedAsync(AiArguments args, IEnumerable<PackSession> packs)
    {
        var dirty = packs.Where(p => _backend.EditCountOf(p.PackPath) > 0).ToArray();
        if (dirty.Length == 0) return;
        switch (args.Text("unsaved", "fail"))
        {
            case "discard":
                foreach (var pack in dirty) _backend.DiscardEditsOf(pack.PackPath!);
                break;
            case "save":
                foreach (var pack in dirty) { _backend.ResetExportDiagnostics(); await Task.Run(() => _backend.SaveInPlaceAsync(pack)); }
                break;
            default: throw new AiFailure("unsaved_edits", "Choose unsaved=save or discard to switch/close.", dirty.Select(p => p.PackPath).ToArray());
        }
    }

    private static string Destination(AiArguments args)
    {
        var path = Path.GetFullPath(args.Required("path"));
        if (File.Exists(path) && !args.Flag("overwrite")) throw new AiFailure("destination_exists", "Use overwrite=true to replace: " + path);
        return path;
    }

    private async Task<object> SaveAsync(AiArguments args)
    {
        RequireWritable();
        _backend.ResetExportDiagnostics();
        var current = _backend.Pack!.PackPath!;
        if (!args.Has("path") || string.Equals(Path.GetFullPath(args.Required("path")), current, StringComparison.OrdinalIgnoreCase))
            return new { path = current, message = await Task.Run(() => _backend.SaveInPlaceAsync()), diagnostics = _backend.LastExportDiagnostics };
        var path = Destination(args);
        var report = await Task.Run(() => _backend.Export(path));
        return new { path, report, diagnostics = _backend.LastExportDiagnostics };
    }

    private async Task<object> ValidateAsync()
    {
        RequireWritable();
        _backend.ResetExportDiagnostics();
        var temp = Path.Combine(DerivedCache.Root("diagnostics"), "ai-" + Guid.NewGuid().ToString("N") + ".pack");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            var report = await Task.Run(() => _backend.Export(temp));
            return new { report, diagnostics = _backend.LastExportDiagnostics };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private object ResolveRules(AiArguments args)
    {
        RequireWritable();
        var path = _backend.Pack!.PackPath!;
        if (!_conflicts.TryGetValue(path, out var conflicts)) throw new AiFailure("no_conflicts", "Run save/validate to obtain current conflict options.");
        var choices = args.Json.GetProperty("choices").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        foreach (var choice in choices)
            if (!conflicts.Any(c => c.Id == choice.Key && c.Options.Any(o => o.Id == choice.Value)))
                throw new ArgumentException("Unknown conflict or option: " + choice.Key);
        _backend.SetDuplicateUpgradeChoices(path, choices);
        return new { selected = choices.Count };
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _backend.Log -= CaptureLog;
            if (_ownsBackend) await _backend.DisposeAsync();
        }
        finally { _gate.Release(); }
    }
}
