using System.Data;
using System.Reflection;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanvasDragUpdatesOpenTableAndPersistsThroughUndoRedoAndSave(bool multiple)
    {
        const string infos = "unit_upgrade_group_ui_infos_tables";
        const string innerPath = "db/" + infos + "/author";
        var path = MakePack("drag_positions.pack",
            ("db/unit_upgrade_groups_tables/author", Table("unit_upgrade_groups_tables",
                new() { ["unit_group"] = "dragA" }, new() { ["unit_group"] = "dragB" })),
            ("db/unit_to_unit_group_junctions_tables/author", Table("unit_to_unit_group_junctions_tables",
                new() { ["unit_group"] = "dragA", ["unit"] = "u1" },
                new() { ["unit_group"] = "dragB", ["unit"] = "u2" })),
            (innerPath, Table(infos,
                new() { ["unit_upgrade_group"] = "dragA", ["category"] = "EMP", ["x"] = 137, ["y"] = 262 },
                new() { ["unit_upgrade_group"] = "dragB", ["category"] = "EMP", ["x"] = 287, ["y"] = 412 })));
        var vm = new MainViewModel(_settings, ValidateFixturePack);
        var backend = (Backend)typeof(MainViewModel)
            .GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await using var lifetime = backend;
        await backend.OpenPackAsync(path);
        await vm.OpenTableAsync(innerPath, path);
        var tab = Assert.IsType<TableTab>(vm.ActiveCenterTab);
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.Status) || !vm.Status.StartsWith("表已刷新")) return;
            var row = tab.View.Table!.Rows.Cast<DataRow>()
                .SingleOrDefault(r => r["unit_upgrade_group"].ToString() == "dragA");
            if (row?["x"].ToString() == "150" && row["y"].ToString() == "300")
                refreshed.TrySetResult();
        };
        var message = multiple
            ? JsonSerializer.Serialize(new { type = "moveMany", groups = new[] {
                new { group = "dragA", x = 150, y = 300 },
                new { group = "dragB", x = 300, y = 450 } } })
            : JsonSerializer.Serialize(new { type = "move", group = "dragA", x = 150, y = 300 });
        var undoDepth = backend.UndoDepth;
        var canvasRefreshes = 0;
        vm.PackOpened += () => canvasRefreshes++;
        vm.ApplyCanvasEdit(message);
        Assert.DoesNotContain("失败", vm.Status);
        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, canvasRefreshes);
        Assert.Equal(undoDepth + 1, backend.UndoDepth);
        Assert.Equal((150, 300, (string?)null), backend.Edits.InfoEdits["dragA"]);
        if (multiple) Assert.Equal((300, 450, (string?)null), backend.Edits.InfoEdits["dragB"]);
        Assert.True(backend.Undo());
        Assert.Empty(backend.Edits.InfoEdits);
        Assert.True(backend.Redo());
        Assert.Equal((150, 300, (string?)null), backend.Edits.InfoEdits["dragA"]);

        Assert.True(await vm.SavePackByPathAsync(path), vm.Status);
        Assert.Equal(0, backend.EditCount);
        using (var saved = PackArchive.Open(path))
        {
            var table = TableFiles.ReadMerged(saved, infos, _schema)!;
            var keyIndex = table.Columns.FindIndex(c => c.Name == "unit_upgrade_group");
            var xIndex = table.Columns.FindIndex(c => c.Name == "x");
            var yIndex = table.Columns.FindIndex(c => c.Name == "y");
            var categoryIndex = table.Columns.FindIndex(c => c.Name == "category");
            foreach (var row in table.Rows)
            {
                var key = row[keyIndex].ToTsv();
                var expected = key == "dragA" ? (150, 300) : multiple ? (300, 450) : (287, 412);
                Assert.Equal(expected.Item1, row[xIndex].Int);
                Assert.Equal(expected.Item2, row[yIndex].Int);
                Assert.Equal("EMP", row[categoryIndex].ToTsv());
            }
            Assert.Equal(2, table.Rows.Count);
        }
        await backend.OpenPackAsync(path);
        using var json = JsonDocument.Parse(backend.BuildWarbandJson());
        var group = json.RootElement.GetProperty("groups").EnumerateArray()
            .Single(g => g.GetProperty("unit_group").GetString() == "dragA");
        Assert.Equal(150, group.GetProperty("x").GetInt32());
        Assert.Equal(300, group.GetProperty("y").GetInt32());
        Assert.Equal(0, backend.EditCount);
    }
}
