using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Rpfm;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.Services.Ai;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    private static async Task<JsonElement> AiCall(AiSession session, string name, object args)
    {
        var result = await session.CallAsync(name, JsonSerializer.SerializeToElement(args));
        return JsonSerializer.SerializeToElement(result, AiSession.Json).GetProperty("structuredContent");
    }

    private (MainViewModel Vm, Backend Backend, AiSession Session) AiFixture(Func<string, RpfmDiagnosticReport>? validate = null)
    {
        var vm = new MainViewModel(_settings, validate ?? ValidateFixturePack);
        var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        return (vm, backend, new AiSession(vm, ownsBackend: true));
    }

    [Fact]
    public async Task AiProtocolNegotiatesListsSchemasAndReturnsStructuredToolErrors()
    {
        var fixture = AiFixture();
        await using var session = fixture.Session;
        var input = string.Join("\n", new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"studio_status","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"studio_status","arguments":{"unexpected":1}}}"""
        });
        var output = new StringWriter();
        await new McpConnection(session).RunAsync(new StringReader(input), output);
        var responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToDictionary(r => r.GetProperty("id").GetInt32());
        Assert.Equal(4, responses.Count);
        Assert.Equal("2025-11-25", responses[1].GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(AiCatalog.Tools.Count, responses[2].GetProperty("result").GetProperty("tools").GetArrayLength());
        Assert.True(responses[3].GetProperty("result").GetProperty("structuredContent").GetProperty("ok").GetBoolean());
        Assert.True(responses[4].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("invalid_arguments", responses[4].GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AiProtocolRejectsMalformedAndUninitializedRequests()
    {
        var fixture = AiFixture();
        await using var session = fixture.Session;
        var output = new StringWriter();
        await new McpConnection(session).RunAsync(new StringReader("invalid\n[]\n" +
            """{"jsonrpc":"2.0","id":"bad","method":2}""" + "\n" +
            """{"jsonrpc":"2.0","id":"before","method":"tools/list"}"""), output);
        var errors = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonDocument.Parse(s).RootElement.GetProperty("error").GetProperty("code").GetInt32()).ToArray();
        Assert.Contains(-32700, errors);
        Assert.Contains(-32600, errors);
        Assert.Contains(-32002, errors);
    }

    [Fact]
    public async Task AiSearchMatchesChineseLogText()
    {
        var fixture = AiFixture();
        await using var session = fixture.Session;
        await AiCall(session, "studio_open_pack", new { path = _pack });
        var logs = await AiCall(session, "studio_logs", new { query = "已打开" });
        Assert.True(logs.GetProperty("result").GetProperty("total").GetInt32() > 0, logs.ToString());
    }

    [Fact]
    public async Task AiBatchIsAtomicHasOneUndoAndUsesCurrentRevision()
    {
        var path = await ClipboardFixture();
        var fixture = AiFixture();
        await using var session = fixture.Session;
        Assert.True((await AiCall(session, "studio_open_pack", new { path })).GetProperty("ok").GetBoolean());
        var depth = fixture.Backend.UndoDepth;
        var moved = await AiCall(session, "studio_edit", new { pack = path, operations = new object[] {
            new { action = "move_group", group = "A", x = 350, y = 450 },
            new { action = "rename_group", group = "B", newKey = "Renamed" } } });
        Assert.True(moved.GetProperty("ok").GetBoolean(), moved.ToString());
        Assert.Equal(depth + 1, fixture.Backend.UndoDepth);
        var revision = moved.GetProperty("state").GetProperty("revision").GetInt32();
        Assert.True((await AiCall(session, "studio_undo", new { pack = path })).GetProperty("ok").GetBoolean());
        Assert.Equal(0, fixture.Backend.EditCount);
        Assert.True((await AiCall(session, "studio_redo", new { pack = path })).GetProperty("ok").GetBoolean());
        var snapshot = JsonSerializer.Serialize(fixture.Backend.Edits, AiSession.Json);
        var failed = await AiCall(session, "studio_edit", new { pack = path, operations = new object[] {
            new { action = "move_group", group = "A", x = 999, y = 888 },
            new { action = "move_group", group = "nonexistent", x = 1, y = 2 } } });
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Equal(snapshot, JsonSerializer.Serialize(fixture.Backend.Edits, AiSession.Json));
        var stale = await AiCall(session, "studio_edit", new { pack = path, expectedRevision = revision, operations = new[] {
            new { action = "move_group", group = "A", x = 1, y = 2 } } });
        Assert.Equal("stale_revision", stale.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(snapshot, JsonSerializer.Serialize(fixture.Backend.Edits, AiSession.Json));
    }

    [Fact]
    public async Task AiReadsPendingTablesAndRejectsStaleCellBatch()
    {
        var fixture = AiFixture();
        await using var session = fixture.Session;
        await AiCall(session, "studio_open_pack", new { path = _pack });
        const string path = "db/resource_costs_tables/author";
        var table = await AiCall(session, "studio_table", new { path });
        var row = table.GetProperty("result").GetProperty("rows").GetProperty("items")[0].GetProperty("values")
            .EnumerateArray().Select(v => v.GetString()!).ToArray();
        var failed = await AiCall(session, "studio_table_edit", new { pack = _pack, path, cells = new[] {
            new { row = 0, column = "treasury_cost", expected = row, value = "-333" },
            new { row = 0, column = "treasury_cost", expected = row, value = "-444" } } });
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Equal(0, fixture.Backend.EditCount);
        var success = await AiCall(session, "studio_table_edit", new { pack = _pack, path, cells = new[] {
            new { row = 0, column = "treasury_cost", expected = row, value = "-333" } } });
        Assert.True(success.GetProperty("ok").GetBoolean(), success.ToString());
        Assert.Equal(-333, fixture.Backend.CostList().Single().Gold);
        var reread = await AiCall(session, "studio_table", new { path, query = "-333" });
        Assert.Equal(1, reread.GetProperty("result").GetProperty("rows").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AiTableBatchEditsDifferentCellsInOneRowAndRollsBackInvalidValues()
    {
        var pack = await ClipboardFixture();
        var fixture = AiFixture();
        await using var session = fixture.Session;
        await AiCall(session, "studio_open_pack", new { path = pack });
        var path = $"db/{TI}/SourceData";
        var table = await AiCall(session, "studio_table", new { pack, path });
        var row = table.GetProperty("result").GetProperty("rows").GetProperty("items")[0].GetProperty("values")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        var failed = await AiCall(session, "studio_table_edit", new { pack, path, cells = new[] {
            new { row = 0, column = "x", expected = row, value = "333" },
            new { row = 0, column = "y", expected = row, value = "invalid" } } });
        Assert.False(failed.GetProperty("ok").GetBoolean());
        Assert.Equal(0, fixture.Backend.EditCount);
        Assert.Equal(0, fixture.Backend.UndoDepth);
        var saved = await AiCall(session, "studio_table_edit", new { pack, path, cells = new[] {
            new { row = 0, column = "x", expected = row, value = "333" },
            new { row = 0, column = "y", expected = row, value = "444" } } });
        Assert.True(saved.GetProperty("ok").GetBoolean(), saved.ToString());
        Assert.Equal(1, fixture.Backend.UndoDepth);
        Assert.Equal("333", ClipCell(fixture.Backend.ReadTable(path), 0, "x"));
        Assert.Equal("444", ClipCell(fixture.Backend.ReadTable(path), 0, "y"));
        var stale = await AiCall(session, "studio_table_edit", new { pack, path, cells = new[] {
            new { row = 0, column = "x", expected = row, value = "555" } } });
        Assert.Equal("stale_table", stale.GetProperty("error").GetProperty("code").GetString());
        await AiCall(session, "studio_undo", new { pack });
        Assert.Equal("10", ClipCell(fixture.Backend.ReadTable(path), 0, "x"));
        Assert.Equal("20", ClipCell(fixture.Backend.ReadTable(path), 0, "y"));
        await AiCall(session, "studio_redo", new { pack });
        Assert.Equal("333", ClipCell(fixture.Backend.ReadTable(path), 0, "x"));
        Assert.Equal("444", ClipCell(fixture.Backend.ReadTable(path), 0, "y"));
    }

    [Fact]
    public async Task AiSupportsMultipleResourceLinksAndReturnsRefusals()
    {
        var fixture = AiFixture();
        await using var session = fixture.Session;
        await AiCall(session, "studio_open_pack", new { path = _pack });
        var update = await AiCall(session, "studio_costs", new { pack = _pack, action = "update", id = "C", resource = "food", amount = -50L, replacePool = "food_pool", gold = -500 });
        Assert.True(update.GetProperty("ok").GetBoolean(), update.ToString());
        var cost = fixture.Backend.CostList().Single();
        Assert.Equal(-500, cost.Gold);
        Assert.Equal(2, cost.Resources.Count);
        Assert.Contains(cost.Resources, r => r.Resource == "food" && r.Amount == -50);
        Assert.Contains(cost.Resources, r => r.Resource == "chivalry");
        var before = JsonSerializer.Serialize(fixture.Backend.Edits, AiSession.Json);
        var refused = await AiCall(session, "studio_costs", new { pack = _pack, action = "update", id = "C", resource = "invalid_resource" });
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Backend.Edits, AiSession.Json));
    }

    [Fact]
    public async Task AiReferencePacksAreReadOnlyAndExternalUnitsCanBeAddedWithoutGrants()
    {
        var path = await ClipboardFixture();
        var external = MakePack("external_ai.pack", ("db/main_units_tables/external",
            Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "external_unit" })));
        var fixture = AiFixture();
        await using var session = fixture.Session;
        await AiCall(session, "studio_open_pack", new { path });
        await AiCall(session, "studio_open_pack", new { path = external, reference = true });
        var added = await AiCall(session, "studio_edit", new { pack = path, operations = new[] {
            new { action = "add_unit", unit = "external_unit", group = "A" } } });
        Assert.True(added.GetProperty("ok").GetBoolean(), added.ToString());
        Assert.Empty(fixture.Backend.Edits.AddUnitGroup);
        Assert.Empty(fixture.Backend.Edits.AddExclusive);
        var refused = await AiCall(session, "studio_edit", new { pack = external, operations = new[] {
            new { action = "new_group", group = "Bad", category = "EMP", x = 0, y = 0 } } });
        Assert.Equal("read_only", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, fixture.Backend.EditCountOf(external));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AiSaveUsesExistingErrorWarningGate(bool error)
    {
        var original = File.ReadAllBytes(_pack);
        await _backend.CloseSessionAsync(_backend.Pack!);
        var fixture = AiFixture(_ => new RpfmDiagnosticReport([
            new(error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, "db/resource_costs_tables/author", "Test", "test diagnostic") ]));
        await using var session = fixture.Session;
        await AiCall(session, "studio_open_pack", new { path = _pack });
        await AiCall(session, "studio_costs", new { pack = _pack, action = "update", id = "C", gold = -777 });
        var saved = await AiCall(session, "studio_save", new { pack = _pack });
        Assert.True(saved.GetProperty("ok").GetBoolean() == !error, saved.ToString());
        if (error)
        {
            Assert.Equal(original, File.ReadAllBytes(_pack));
            Assert.True(fixture.Backend.EditCount > 0);
            Assert.Equal(1, saved.GetProperty("error").GetProperty("details").GetProperty("errors").GetInt32());
        }
        else
        {
            Assert.Equal(0, fixture.Backend.EditCount);
            Assert.Equal(1, saved.GetProperty("result").GetProperty("diagnostics").GetProperty("warnings").GetInt32());
            Assert.Equal(-777, fixture.Backend.CostList().Single().Gold);
        }
    }

    [Fact]
    public async Task AiProjectSwitchRequiresAnExplicitUnsavedPolicy()
    {
        var fixture = AiFixture();
        await using var session = fixture.Session;
        var directory = Path.Combine(_root, "ai_project");
        Assert.True((await AiCall(session, "studio_project", new { action = "create", directory, key = "AI" })).GetProperty("ok").GetBoolean());
        Assert.True((await AiCall(session, "studio_project", new { action = "import", path = _pack })).GetProperty("ok").GetBoolean());
        var pack = fixture.Backend.Pack!.PackPath!;
        await AiCall(session, "studio_costs", new { pack, action = "update", id = "C", gold = -333 });
        var refused = await AiCall(session, "studio_project", new { action = "close" });
        Assert.Equal("unsaved_edits", refused.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(directory, fixture.Vm.ProjectDir);
        Assert.True((await AiCall(session, "studio_project", new { action = "close", unsaved = "discard" })).GetProperty("ok").GetBoolean());
        Assert.True((await AiCall(session, "studio_project", new { action = "open", directory })).GetProperty("ok").GetBoolean());
        Assert.Equal(-100, fixture.Backend.CostList().Single().Gold);
    }

    [Fact]
    public async Task AiNamedPipeSharesTheExistingEditorSession()
    {
        var path = await ClipboardFixture();
        var fixture = AiFixture();
        await AiCall(fixture.Session, "studio_open_pack", new { path });
        await using var host = new AiPipeHost(fixture.Session);
        host.Start();
        await using (var client = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(5000);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25"}}""");
            Assert.Contains("serverInfo", await reader.ReadLineAsync());
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            var call = new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = "studio_edit",
                arguments = new { pack = path, operations = new[] { new { action = "move_group", group = "A", x = 600, y = 700 } } } } };
            await writer.WriteLineAsync(JsonSerializer.Serialize(call));
            using var response = JsonDocument.Parse((await reader.ReadLineAsync())!);
            Assert.False(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            Assert.Equal(600, fixture.Backend.Edits.InfoEdits["A"].X);
        }
    }

    [Fact]
    public async Task AiConcurrentClientsCannotApplyTheSameStaleRevisionTwice()
    {
        var path = await ClipboardFixture();
        var fixture = AiFixture();
        await using var session = fixture.Session;
        var opened = await AiCall(session, "studio_open_pack", new { path });
        var revision = opened.GetProperty("state").GetProperty("revision").GetInt32();
        var requests = await Task.WhenAll(
            AiCall(session, "studio_edit", new { pack = path, expectedRevision = revision, operations = new[] {
                new { action = "move_group", group = "A", x = 100, y = 200 } } }),
            AiCall(session, "studio_edit", new { pack = path, expectedRevision = revision, operations = new[] {
                new { action = "move_group", group = "A", x = 300, y = 400 } } }));
        Assert.Single(requests, r => r.GetProperty("ok").GetBoolean());
        Assert.Single(requests, r => !r.GetProperty("ok").GetBoolean() && r.GetProperty("error").GetProperty("code").GetString() == "stale_revision");
        Assert.Equal(1, fixture.Backend.UndoDepth);
    }

    [Fact]
    public async Task AiExplicitPackQueryKeepsTheVisibleEditorInSync()
    {
        var other = await ClipboardFixture();
        var fixture = AiFixture();
        await using var lifetime = fixture.Session;
        await fixture.Backend.OpenPackAsync(_pack);
        await fixture.Backend.OpenPackAsync(other);
        await using var attached = new AiSession(fixture.Vm, action => action());
        var query = await AiCall(attached, "studio_files", new { pack = _pack });
        Assert.True(query.GetProperty("ok").GetBoolean(), query.ToString());
        Assert.Equal(_pack, Assert.IsType<CanvasTab>(fixture.Vm.ActiveCenterTab).PackPath);
        Assert.Equal(_pack, fixture.Backend.Pack!.PackPath);
        Assert.Equal(0, fixture.Backend.EditCount);
    }

    [Fact]
    public async Task AiAttachedSessionRefreshesTheEditorWithoutReplacingItsTable()
    {
        var fixture = AiFixture();
        await using var lifetime = fixture.Session;
        await fixture.Backend.OpenPackAsync(_pack);
        await fixture.Vm.OpenTableAsync("db/resource_costs_tables/author", _pack);
        var table = fixture.Vm.ActiveCenterTab;
        await using var attached = new AiSession(fixture.Vm, action => action());
        var refreshes = 0;
        fixture.Vm.PackOpened += () => refreshes++;
        var edited = await AiCall(attached, "studio_costs", new { pack = _pack, action = "update", id = "C", gold = -222 });
        Assert.True(edited.GetProperty("ok").GetBoolean(), edited.ToString());
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("displayWarning").ValueKind);
        Assert.Equal(1, refreshes);
        Assert.Same(table, fixture.Vm.ActiveCenterTab);
        await AiCall(attached, "studio_costs", new { pack = _pack, action = "list" });
        Assert.Equal(1, refreshes);
    }
}
