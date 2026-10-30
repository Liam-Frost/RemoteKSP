using System.Collections.Generic;
using System.Linq;

namespace RemoteKSP.Shared.Ui;

/// <summary>
/// 卡片上的状态标记。只表达真实状态：没有可靠依据时用 <see cref="None"/>，不要为了好看填一个颜色。
/// 卡片图标的颜色表示功能分类，与这里的状态无关
/// </summary>
public enum StatusKind
{
    /// <summary>不显示状态标记</summary>
    None,
    Success,
    Caution,
    Critical,
    /// <summary>中性信息，例如服务未安装</summary>
    Neutral,
    /// <summary>正在加载或正在执行操作</summary>
    Busy
}

/// <summary>首页卡片上的名称摘要</summary>
public static class TextSummary
{
    /// <summary>「sign-01、sign-02」；超过 <paramref name="max"/> 个时显示「A、B、C +2」</summary>
    public static string Names(IEnumerable<string> names, int max = 3)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        if (list.Count <= max)
            return string.Join(Localization.SharedStrings.List_Separator, list);
        return $"{string.Join(Localization.SharedStrings.List_Separator, list.Take(max))} +{list.Count - max}";
    }
}
