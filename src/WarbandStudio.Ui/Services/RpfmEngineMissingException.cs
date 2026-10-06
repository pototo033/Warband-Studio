namespace WarbandStudio.Ui.Services;

/// <summary>
/// 没找到可用的 RPFM 引擎（rpfm_server.exe）。
/// 本工具是 RPFM 的衍生工具 —— 需要用户先装好 RPFM 5.x（或让它保持运行），
/// 界面上要给出"去哪装 / 怎么指路径"的引导，而不是一句报错。
/// </summary>
public sealed class RpfmEngineMissingException(string message) : Exception(message);
