namespace WarbandStudio.Rpfm;

/// <summary>
/// rpfm_server 回的 <c>{"Error": "…"}</c>。消息原文对开发者有用，
/// 界面上要再包一层人话（照 ws-protocol.md 的建议）。
/// </summary>
public sealed class RpfmException : Exception
{
    public RpfmException(string message) : base(message) { }
}
