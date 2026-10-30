using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteKSP.Server.Services;
using RemoteKSP.Server.Views.Dialogs;
using RemoteKSP.Shared.Services;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace RemoteKSP.Server.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly PipeClient _pipeClient;
    private readonly DialogService _dialogService;
    private readonly IContentDialogService _contentDialogService;
    private readonly DispatcherTimer _pairingRequestCheckTimer;
    private readonly HashSet<string> _processedRequestIds = new();
    private string? _currentDialogRequestId;

    [ObservableProperty]
    private string _applicationTitle = "Remote KSP Server";

    [ObservableProperty]
    private string _currentPageTitle = Strings.Nav_Overview;

    [ObservableProperty]
    private ObservableCollection<object> _menuItems = [];

    [ObservableProperty]
    private ObservableCollection<object> _footerMenuItems = [];

    public MainWindowViewModel(PipeClient pipeClient, DialogService dialogService, IContentDialogService contentDialogService)
    {
        _pipeClient = pipeClient;
        _dialogService = dialogService;
        _contentDialogService = contentDialogService;

        InitializeMenuItems();

        // 配对请求检测定时器（每3秒检查一次）
        _pairingRequestCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _pairingRequestCheckTimer.Tick += async (s, e) => await CheckForNewPairingRequestsAsync();
    }

    public void StartPairingRequestMonitoring()
    {
        _pairingRequestCheckTimer.Start();
    }

    public void StopPairingRequestMonitoring()
    {
        _pairingRequestCheckTimer.Stop();
    }

    private async Task CheckForNewPairingRequestsAsync()
    {
        try
        {
            // 如果当前已有弹窗显示，跳过检查
            if (_currentDialogRequestId != null)
                return;

            var requests = await _pipeClient.SendCommandAsync<PairingRequestDto[]>("ListPairingRequests");
            if (requests == null || requests.Length == 0)
                return;

            // 找到第一个未处理且未过期的请求
            var newRequest = requests
                .Where(r => !_processedRequestIds.Contains(r.RequestId))
                .Where(r => r.ExpiresAt > DateTime.UtcNow)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefault();

            if (newRequest != null)
            {
                await ShowPairingApprovalDialogAsync(newRequest);
            }
        }
        catch
        {
            // 静默失败，避免干扰用户
        }
    }

    private async Task ShowPairingApprovalDialogAsync(PairingRequestDto request)
    {
        _currentDialogRequestId = request.RequestId;

        var vm = new PairingApprovalDialogViewModel
        {
            RequestId = request.RequestId,
            MachineName = request.MachineName,
            ClientId = request.RequestId, // 使用 RequestId 作为标识
            IpAddress = request.IpAddress,
            VerificationCode = request.VerificationCode,
            CreatedAt = request.CreatedAt,
            ExpiresAt = request.ExpiresAt
        };
        vm.CheckExpiration();

        var form = new PairingApprovalForm { DataContext = vm };

        var dialog = new ContentDialog
        {
            Title = Strings.Pairing_NewRequestTitle,
            Content = form,
            PrimaryButtonText = Strings.Pairing_Approve,
            SecondaryButtonText = Strings.Pairing_Reject,
            CloseButtonText = Strings.Pairing_Later,
            DefaultButton = ContentDialogButton.Close,
            DialogMaxWidth = 680
        };

        // 禁用过期请求的批准按钮
        if (vm.IsExpired)
        {
            dialog.IsPrimaryButtonEnabled = false;
        }

        var approved = false;
        var rejected = false;

        dialog.Closing += async (s, e) =>
        {
            if (e.Result == ContentDialogResult.Primary && !approved)
            {
                // 批准配对
                e.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;

                try
                {
                    var requestData = new { RequestId = request.RequestId };
                    await _pipeClient.SendCommandAsync<object>("ApprovePairingRequest", requestData);
                    _processedRequestIds.Add(request.RequestId);
                    approved = true;
                    dialog.Hide(ContentDialogResult.Primary);

                    await _dialogService.SuccessAsync(Strings.Pairing_SuccessTitle, string.Format(Strings.Pairing_Success, request.MachineName));
                }
                catch (Exception ex)
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    dialog.IsSecondaryButtonEnabled = true;
                    await _dialogService.ErrorAsync(Strings.Pairing_ApproveFailed, ex.Message);
                }
            }
            else if (e.Result == ContentDialogResult.Secondary && !rejected)
            {
                // 拒绝配对
                e.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;

                try
                {
                    var requestData = new { RequestId = request.RequestId };
                    await _pipeClient.SendCommandAsync<object>("RejectPairingRequest", requestData);
                    _processedRequestIds.Add(request.RequestId);
                    rejected = true;
                    dialog.Hide(ContentDialogResult.Secondary);
                }
                catch (Exception ex)
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    dialog.IsSecondaryButtonEnabled = true;
                    await _dialogService.ErrorAsync(Strings.Pairing_RejectFailed, ex.Message);
                }
            }
            else if (e.Result == ContentDialogResult.None)
            {
                // 稍后处理 - 标记为已处理，避免重复弹出
                _processedRequestIds.Add(request.RequestId);
                _currentDialogRequestId = null;
            }
        };

        dialog.Closed += (s, e) =>
        {
            _currentDialogRequestId = null;
        };

        await _contentDialogService.ShowAsync(dialog, default);
    }

    private void InitializeMenuItems()
    {
        MenuItems =
        [
            new NavigationViewItem
            {
                Content = Strings.Nav_Overview,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Home24 },
                TargetPageType = typeof(Views.Pages.OverviewPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_Certificates,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Certificate24 },
                TargetPageType = typeof(Views.Pages.CertificatesPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_PairingRequests,
                Icon = new SymbolIcon { Symbol = SymbolRegular.PersonAdd24 },
                TargetPageType = typeof(Views.Pages.PairingRequestsPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_Clients,
                Icon = new SymbolIcon { Symbol = SymbolRegular.People24 },
                TargetPageType = typeof(Views.Pages.ClientsPage)
            },
            new NavigationViewItem
            {
                Content = Strings.Nav_ActivityLog,
                Icon = new SymbolIcon { Symbol = SymbolRegular.DocumentBulletList24 },
                TargetPageType = typeof(Views.Pages.ActivityLogPage)
            }
        ];

        FooterMenuItems =
        [
            new NavigationViewItem
            {
                Content = Strings.Nav_Settings,
                Icon = new SymbolIcon { Symbol = SymbolRegular.Settings24 },
                TargetPageType = typeof(Views.Pages.SettingsPage)
            }
        ];
    }
}
