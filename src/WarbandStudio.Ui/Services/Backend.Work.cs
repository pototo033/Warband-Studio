using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public readonly record struct ReadContext(PackSession? Session, int Revision);
    public ReadContext CaptureReadContext() { lock (StateSync) return new(_pack, _rev); }
    public bool IsCurrent(ReadContext context)
    {
        lock (StateSync) return ReferenceEquals(_pack, context.Session) && _rev == context.Revision && (context.Session is null || context.Session.IsOpen);
    }
    public T Read<T>(ReadContext context, Func<T> read)
    {
        lock (StateSync)
        {
            if (!IsCurrent(context)) throw new OperationCanceledException("读取所属的包或修订已改变。");
            using var iconBatch = Icons.BeginBatch();
            return read();
        }
    }
    public void BindProject(PackSession session, string? directory, string? key)
    {
        lock (StateSync) { session.ProjectDirectory = directory; session.ProjectKey = key; }
    }
    private void RefreshSourceScope()
    {
        Loc.SetSources(_packs.Where(s => s.IsUnitPack && !ReferenceEquals(s, _pack)).Concat(_pack is null ? [] : [_pack]));
        _icons?.InvalidateSources();
        _landUnitByUnit = null;
        _costIndexArchive = null; _costTables.Clear(); _costListCache = null; _resourceListCache = null;
        _milGroups = null;
        _raceMilGroups = null;
        InvalidateCanvas();
    }
}
