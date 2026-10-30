using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Shared.Networking;

/// <summary>
/// 首页网络卡片（Client「本机网络」、Server「网络地址」）。地址按网络接口分组：
/// 默认只列出主要地址（非环回、非链路本地、非临时），其余地址展开后并入各自接口下，每个接口只出现一次。
/// Server 传入监听端口时每个地址额外提供「复制连接地址」；Client 不传端口，只展示和复制本机 IP。
/// </summary>
public partial class NetworkAddressesViewModel : ObservableObject, IDisposable
{
    private readonly NetworkAddressService _service = new();
    private readonly DialogService _dialogService;
    private readonly Func<int?> _getPort;
    private List<NetworkAddressItem> _items = [];
    private bool _active;

    [ObservableProperty] private ObservableCollection<NetworkInterfaceGroup> _groups = [];
    [ObservableProperty] private bool _hasAddresses;
    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private bool _hasHidden;
    [ObservableProperty] private bool _showOther;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _footnote = "";
    [ObservableProperty] private string _footnoteDetail = "";

    /// <param name="getPort">返回服务端监听端口；Client 传 null 时不生成连接地址</param>
    /// <param name="footnote">标题下的一行简短说明</param>
    /// <param name="footnoteDetail">详细说明，放在标题旁的信息图标 ToolTip 里</param>
    public NetworkAddressesViewModel(DialogService dialogService, Func<int?>? getPort, string footnote, string footnoteDetail)
    {
        _dialogService = dialogService;
        _getPort = getPort ?? (() => null);
        Footnote = footnote;
        FootnoteDetail = footnoteDetail;
        _service.AddressesChanged += OnAddressesChanged;
    }

    public bool ShowsServerUrl => _getPort() != null;

    /// <summary>折叠按钮文字：N 是被隐藏的地址条数（不是网卡数）</summary>
    public string ToggleText => ShowOther ? "收起其他网络地址" : $"其他网络地址（{HiddenCount}）";

    /// <summary>页面加载时调用：刷新一次并开始监听网络变化</summary>
    public async Task ActivateAsync()
    {
        _active = true;
        _service.StartMonitoring();
        await RefreshAsync();
    }

    /// <summary>页面卸载时调用，取消静态 NetworkChange 事件订阅；之后的网络变化不再更新这个已失效的视图</summary>
    public void Deactivate()
    {
        _active = false;
        _service.StopMonitoring();
    }

    private void OnAddressesChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            if (_active)
                await RefreshAsync();
        });

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        try
        {
            var addresses = await Task.Run(_service.GetAddresses);
            Show(addresses);
        }
        catch (Exception ex)
        {
            // 保留上一次的结果，同时把真实错误显示出来，不当作「没有地址」
            ErrorText = $"读取网络接口失败：{ex.Message}";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>显示一组地址（按 <see cref="NetworkAddressService.GetAddresses"/> 的顺序）</summary>
    public void Show(IReadOnlyList<NetworkAddressInfo> addresses)
    {
        var port = _getPort();
        _items = addresses.Select(a => new NetworkAddressItem(a, port)).ToList();
        ErrorText = "";
        EmptyText = _items.Count == 0
            ? "未检测到已启用的网络接口"
            : "没有可供局域网访问的地址，仅有环回或链路本地地址";
        Rebuild();
        OnPropertyChanged(nameof(ShowsServerUrl));
    }

    partial void OnShowOtherChanged(bool value) => Rebuild();

    /// <summary>按接口分组。顺序沿用服务给出的稳定排序：先出现主要地址的接口排在前面</summary>
    private void Rebuild()
    {
        var visible = ShowOther ? _items : _items.Where(i => i.Info.IsPrimary).ToList();
        Groups = new ObservableCollection<NetworkInterfaceGroup>(visible
            .GroupBy(i => i.Info.InterfaceId)
            .Select(g => new NetworkInterfaceGroup(g.First().Info, g
                .OrderBy(i => i.Info.IsIPv6)
                .ThenByDescending(i => i.Info.IsPrimary)
                .ToList())));
        HiddenCount = _items.Count(i => !i.Info.IsPrimary);
        HasHidden = HiddenCount > 0;
        HasAddresses = _items.Any(i => i.Info.IsPrimary) || (ShowOther && _items.Count > 0);
        OnPropertyChanged(nameof(ToggleText));
    }

    [RelayCommand]
    private void ToggleOther() => ShowOther = !ShowOther;

    [RelayCommand]
    private Task CopyAddressAsync(NetworkAddressItem? item) =>
        item == null ? Task.CompletedTask : CopyAsync(item.Info.AddressText, "IP 地址");

    [RelayCommand(CanExecute = nameof(CanCopyUrl))]
    private Task CopyUrlAsync(NetworkAddressItem? item) =>
        item?.ServerUrl == null ? Task.CompletedTask : CopyAsync(item.ServerUrl, "连接地址");

    private static bool CanCopyUrl(NetworkAddressItem? item) => item?.CanCopyServerUrl == true;

    /// <summary>复制并用统一的成功对话框反馈；剪贴板被其他程序占用时报告真实错误</summary>
    private async Task CopyAsync(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            await _dialogService.ErrorAsync("复制失败", $"无法写入剪贴板：{ex.Message}");
            return;
        }
        await _dialogService.SuccessAsync("复制成功", $"{what}已复制到剪贴板。", new DialogField(what, text));
    }

    public void Dispose()
    {
        _service.AddressesChanged -= OnAddressesChanged;
        _service.Dispose();
    }
}

/// <summary>一个网络接口及其（当前可见的）地址</summary>
public sealed class NetworkInterfaceGroup
{
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<NetworkAddressItem> Addresses { get; }

    public NetworkInterfaceGroup(NetworkAddressInfo nic, IReadOnlyList<NetworkAddressItem> addresses)
    {
        Name = nic.InterfaceName;
        Description = string.IsNullOrEmpty(nic.InterfaceTypeText)
            ? nic.InterfaceDescription
            : $"{nic.InterfaceDescription}（{nic.InterfaceTypeText}）";
        Addresses = addresses;
    }
}

/// <summary>列表中的一行</summary>
public sealed class NetworkAddressItem
{
    public NetworkAddressInfo Info { get; }

    /// <summary>服务端连接地址；Client 为 null</summary>
    public string? ServerUrl { get; }

    public NetworkAddressItem(NetworkAddressInfo info, int? port)
    {
        Info = info;
        ServerUrl = port is { } p ? info.BuildHttpsUrl(p) : null;
    }

    public string AddressText => Info.AddressText;

    /// <summary>「IPv4 · DHCP」「IPv6 · 链路本地」这类辅助说明；接口名和类型显示在分组标题里</summary>
    public string MetaText => string.Join(" · ", new[]
    {
        Info.FamilyText,
        Info.IsLoopback ? "环回，仅本机" : Info.IsLinkLocal ? "链路本地" : "",
        Info.IsDhcpAssigned ? "DHCP" : ""
    }.Where(s => s.Length > 0));

    public bool HasServerUrl => ServerUrl != null;

    /// <summary>
    /// 链路本地 IPv6 的连接地址带本机接口编号（%25N），其他机器要换成自己的编号才能用，
    /// 生成的 URL 不能直接用在其他设备上，所以不提供复制
    /// </summary>
    public bool CanCopyServerUrl => ServerUrl != null && !(Info.IsIPv6 && Info.IsLinkLocal);

    /// <summary>「复制连接地址」按钮的提示：可复制时显示 URL，不可复制时说明原因</summary>
    public string ServerUrlToolTip => !HasServerUrl ? ""
        : !CanCopyServerUrl ? "链路本地地址带有本机接口编号，生成的 URL 在其他设备上无法直接使用，因此不提供复制"
        : Info.IsLoopback ? $"{ServerUrl}\n环回地址只能在本机使用"
        : ServerUrl!;

    /// <summary>悬停详情：完整地址；链路本地地址额外说明 Zone ID 的含义</summary>
    public string DetailText
    {
        get
        {
            var lines = new List<string> { Info.Address.ToString() };
            // ScopeId 只对 IPv6 有效，IPv4 地址读取会抛 SocketException
            if (Info.IsIPv6 && Info.Address.ScopeId != 0)
                lines.Add($"链路本地地址只在同一网段有效；%{Info.Address.ScopeId} 是本机接口编号，其他机器需使用自己的编号");
            return string.Join("\n", lines);
        }
    }
}
