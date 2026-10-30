using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Client.Core;
using RemoteKSP.Shared.Services;
using Wpf.Ui;

namespace RemoteKSP.Client.ViewModels;

/// <summary>证书管理页：来自所有服务端、已绑定到 Remote KSP 的证书</summary>
public partial class CertificatesViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;
    private readonly DialogService _dialogService;
    private readonly IContentDialogService _contentDialogService;

    [ObservableProperty] private ObservableCollection<ClientCertItem> _certificates = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateSignCommandCommand), nameof(RemoveCommand))]
    private ClientCertItem? _selectedCertificate;

    [ObservableProperty] private bool _hasCertificates;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncAllCommand))]
    private bool _hasServers;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncAllCommand))]
    private bool _isSyncing;

    public CertificatesViewModel(NotificationService notificationService, DialogService dialogService,
        IContentDialogService contentDialogService)
    {
        _notificationService = notificationService;
        _dialogService = dialogService;
        _contentDialogService = contentDialogService;
    }

    public Task InitializeAsync() => RefreshAsync();

    /// <summary>有证书被服务端停用（PIN 错误或 eToken 已锁）</summary>
    [ObservableProperty] private bool _hasDisabled;
    [ObservableProperty] private string _disabledSummary = "";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // 先显示本地列表，再向服务端查询停用状态（离线服务端最多等几秒，不阻塞列表）
        var servers = await Task.Run(ClientRegistry.LoadServers);
        await ShowAsync(servers, new());
        var disabled = await ServerManager.GetDisabledCertificatesAsync(servers, TimeSpan.FromSeconds(5));
        if (disabled.Count > 0)
            await ShowAsync(servers, disabled);
    }

    private async Task ShowAsync(List<ServerEntry> servers, Dictionary<(string, string), string> disabled)
    {
        var selected = SelectedCertificate?.Thumbprint;
        var names = servers.ToDictionary(s => s.Id, s => s.ServerName);
        var items = await Task.Run(() => ClientRegistry.LoadCertificates()
            .Select(b => new ClientCertItem(b, names.GetValueOrDefault(b.ServerId),
                disabled.GetValueOrDefault((b.ServerId, b.Thumbprint))))
            .OrderBy(c => c.ServerName).ThenBy(c => c.Name)
            .ToList());
        Certificates = new ObservableCollection<ClientCertItem>(items);
        SelectedCertificate = Certificates.FirstOrDefault(c => c.Thumbprint == selected) ?? Certificates.FirstOrDefault();
        HasCertificates = Certificates.Any();
        HasServers = servers.Count > 0;

        var disabledItems = items.Where(c => c.IsServerDisabled).ToList();
        HasDisabled = disabledItems.Count > 0;
        DisabledSummary = string.Join("\n", disabledItems.Select(c => $"「{c.Name}」（{c.ServerText}）：{c.DisabledReason}"));
    }

    /// <summary>依次从所有服务端同步证书，最后汇总一次结果</summary>
    [RelayCommand(CanExecute = nameof(CanSync))]
    private async Task SyncAllAsync()
    {
        var servers = ClientRegistry.LoadServers();
        if (servers.Count == 0)
        {
            await _notificationService.ShowWarningAsync("尚未配对", "请先在「服务端管理」页配对服务端。");
            return;
        }

        IsSyncing = true;
        var results = new List<(bool success, int added, int updated, int removed)>();
        var errors = new List<(string serverName, string error)>();

        try
        {
            foreach (var server in servers)
            {
                try
                {
                    var result = await ServerManager.SyncCertificatesAsync(server,
                        SyncUi.ConflictResolver(_notificationService));
                    results.Add((true, result.Added, result.Updated, result.Removed));
                }
                catch (Exception ex)
                {
                    results.Add((false, 0, 0, 0));
                    errors.Add((server.ServerName, ex.Message));
                }
            }
        }
        finally
        {
            IsSyncing = false;
        }

        await RefreshAsync();

        // Show results
        var succeeded = results.Count(r => r.success);
        if (errors.Count == 0)
        {
            // All succeeded - use modal Success Dialog
            var totalAdded = results.Sum(r => r.added);
            var totalUpdated = results.Sum(r => r.updated);
            var totalRemoved = results.Sum(r => r.removed);
            await _notificationService.ShowMultiServerSyncSuccessAsync(servers.Count, totalAdded, totalUpdated, totalRemoved);
        }
        else
        {
            // Some failed - show error dialog with details
            var fields = new List<DialogField>();
            foreach (var (serverName, error) in errors)
            {
                fields.Add(new DialogField(serverName, $"失败：{error}", Monospace: false));
            }
            await _notificationService.ShowWarningAsync("部分服务端同步失败",
                $"{succeeded} 台成功，{errors.Count} 台失败。失败的服务端已绑定的证书保持不变。",
                fields.ToArray());
        }
    }

    /// <summary>打开「生成 SignTool 命令」对话框</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task GenerateSignCommandAsync()
    {
        if (SelectedCertificate == null)
            return;

        var vm = new SignToolCommandDialogViewModel(_dialogService, SelectedCertificate);
        var form = new Views.Dialogs.SignToolCommandForm { DataContext = vm };
        var dialog = new Wpf.Ui.Controls.ContentDialog
        {
            Title = "生成 SignTool 签名命令",
            Content = form,
            PrimaryButtonText = "复制命令",
            SecondaryButtonText = "执行签名",
            CloseButtonText = "关闭",
            // 回车只复制，不会意外改动文件
            DefaultButton = Wpf.Ui.Controls.ContentDialogButton.Primary,
            DialogMaxWidth = 800
        };

        // 注意：WPF-UI 的 ContentDialog 在 IsPrimaryButtonEnabled=False 时会把按钮整列折叠（不是置灰），
        // 所以按钮始终显示；输入无效或正在执行时点击留在对话框，由对话框内的提示说明原因
        var copied = false;
        dialog.Closing += (s, e) =>
        {
            if (copied)
                return;
            // 执行中禁止关闭（包括 Esc），否则 SignTool 会在后台继续运行而用户看不到结果
            if (vm.IsRunning)
            {
                e.Cancel = true;
                return;
            }
            switch (e.Result)
            {
                case Wpf.Ui.Controls.ContentDialogResult.Primary:
                    e.Cancel = true;
                    if (!vm.CanCopy)
                        vm.ShowAllErrors();
                    else if (vm.TryCopy())
                    {
                        copied = true;
                        dialog.Hide(Wpf.Ui.Controls.ContentDialogResult.Primary);
                    }
                    break;

                case Wpf.Ui.Controls.ContentDialogResult.Secondary:
                    // 执行结果显示在对话框内，执行完不关闭，方便查看输出或重试
                    e.Cancel = true;
                    _ = vm.ExecuteAsync();
                    break;
            }
        };

        // 后台查找 SignTool，不阻塞对话框显示
        _ = vm.DiscoverToolsAsync();

        var result = await _contentDialogService.ShowAsync(dialog, default);
        if (result == Wpf.Ui.Controls.ContentDialogResult.Primary)
        {
            await _dialogService.SuccessAsync("命令已复制",
                "SignTool 签名命令已复制到剪贴板，可在 CMD 终端中执行。");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveAsync()
    {
        var cert = SelectedCertificate!;
        var confirmed = await _notificationService.ConfirmAsync(
            "解绑证书",
            $"确定要从本机解绑证书「{cert.Name}」吗？\n\n证书会从 LocalMachine\\My 删除，应用无法再用它签名。服务端的证书不受影响，下次\"更新证书\"时会重新下载绑定。",
            "解绑",
            destructive: true,
            new DialogField("服务端", cert.ServerName ?? "(已移除)", Monospace: false),
            new DialogField("指纹", cert.Thumbprint));
        if (!confirmed)
            return;

        try
        {
            ServerManager.UnbindCertificate(cert.Thumbprint);
            await RefreshAsync();
            // 解绑成功后不显示任何通知，直接刷新列表即可
        }
        catch (Exception ex)
        {
            await _notificationService.ShowErrorAsync("解绑失败", ex.Message);
        }
    }

    private bool HasSelection() => SelectedCertificate != null;
    private bool CanSync() => HasServers && !IsSyncing;
}

/// <summary>客户端证书列表的一行</summary>
public class ClientCertItem
{
    private readonly CertificateBinding _binding;

    public string Thumbprint => _binding.Thumbprint;
    public string Name => _binding.Name;
    public string? ServerName { get; }
    public bool IsTest => _binding.IsTest;
    public DateTime AddedAt => _binding.AddedAt.ToLocalTime();

    public ClientCertItem(CertificateBinding binding, string? serverName, string? disabledReason = null)
    {
        _binding = binding;
        ServerName = serverName;
        DisabledReason = disabledReason;

        using var local = CertificateBinder.GetCertificate(binding.Thumbprint);
        if (local != null)
        {
            Subject = local.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
            Issuer = local.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, true);
            NotAfter = local.NotAfter;
            IsInstalled = true;
        }
    }

    public string? Subject { get; }
    public string? Issuer { get; }
    public DateTime? NotAfter { get; }
    public bool IsInstalled { get; }

    public string ServerText => ServerName ?? "(服务端已移除)";
    public string ExpiryText => NotAfter?.ToString("yyyy-MM-dd") ?? "-";

    public bool IsExpired => NotAfter is { } t && t < DateTime.Now;
    public bool HasNoServer => ServerName == null;

    /// <summary>服务端停用原因（PIN 错误或 eToken 已锁）；null 表示未停用或服务端不可达</summary>
    public string? DisabledReason { get; }
    public bool IsServerDisabled => DisabledReason != null;

    /// <summary>状态：正常 / 测试 / 未安装 / 已过期 / 服务端已移除 / 服务端已停用</summary>
    public string StatusText => this switch
    {
        _ when HasNoServer => "服务端已移除",
        _ when IsServerDisabled => "服务端已停用",
        _ when IsExpired => "已过期",
        _ when !IsInstalled => "本地缺失",
        _ when IsTest => "测试证书",
        _ => "正常"
    };

    public string StatusKind => StatusText switch
    {
        "正常" => "Success",
        "测试证书" => "Caution",
        _ => "Critical"
    };
}
