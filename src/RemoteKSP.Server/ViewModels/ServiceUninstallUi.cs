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
            Strings.Un_Title,
            Strings.Un_Message,
            string.Format(Strings.Un_RemoveData, Core.ServerPaths.DataDir),
            Strings.Un_Uninstall,
            destructive: true);
        if (removeData != true)
            return removeData;

        // 删除数据不可恢复：再确认一次
        var sure = await dialogs.ConfirmAsync(
            Strings.Un_ConfirmDataTitle,
            string.Format(Strings.Un_ConfirmData, Core.ServerPaths.DataDir),
            Strings.Un_DeleteAndUninstall,
            destructive: true);
        return sure ? true : null;
    }

    public static Task ShowResultAsync(DialogService dialogs, Services.ServiceManager.UninstallResult result)
    {
        var message = result.DataRemoved ? Strings.Un_DoneDataRemoved : string.Format(Strings.Un_DoneDataKept, Core.ServerPaths.DataDir);
        if (result.ProgramKeptAt != null)
            message += string.Format(Strings.Un_ProgramKept, result.ProgramKeptAt);
        return dialogs.SuccessAsync(Strings.Un_DoneTitle, message);
    }
}
