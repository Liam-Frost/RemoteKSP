using System.Threading.Tasks;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Server.ViewModels;

/// <summary>概览页和设置页共用的「卸载服务」确认和结果提示</summary>
public static class ServiceUninstallUi
{
    /// <summary>询问是否卸载、是否同时删除数据目录；取消返回 null</summary>
    public static async Task<bool?> ConfirmAsync(DialogService dialogs)
    {
        var removeData = await dialogs.ConfirmWithOptionAsync(
            "卸载服务",
            "将停止并删除 RemoteKSP 服务，删除防火墙规则和 Program Files\\RemoteKSP 里的服务程序。\n\n" +
            "卸载后所有已配对的客户端都无法签名。eToken 里的私钥不受影响。",
            $"同时删除数据目录 {Core.ServerPaths.DataDir}（证书配置、保存的 PIN、客户端配对、TLS 证书、复制进来的 PFX）。删除后无法恢复，重新安装后需要重新添加证书并让所有客户端重新配对",
            "卸载",
            destructive: true);
        if (removeData != true)
            return removeData;

        // 删除数据不可恢复：再确认一次
        var sure = await dialogs.ConfirmAsync(
            "确认删除数据目录",
            $"{Core.ServerPaths.DataDir} 将被永久删除，无法恢复。\n\n" +
            "其中复制进来的 PFX 文件也会删除；如果没有其他备份，请先取消并备份。",
            "删除并卸载",
            destructive: true);
        return sure ? true : null;
    }

    public static Task ShowResultAsync(DialogService dialogs, Services.ServiceManager.UninstallResult result)
    {
        var message = result.DataRemoved ? "服务已卸载，数据目录已删除。" : $"服务已卸载。数据目录 {Core.ServerPaths.DataDir} 已保留，重新安装后继续使用。";
        if (result.ProgramKeptAt != null)
            message += $"\n\n当前程序正从 {result.ProgramKeptAt} 运行，这个文件没有删除，关闭程序后可以手动删除。";
        return dialogs.SuccessAsync("卸载完成", message);
    }
}
