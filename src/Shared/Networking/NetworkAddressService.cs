using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace RemoteKSP.Shared.Networking;

/// <summary>
/// 枚举本机网络接口上的单播地址，并在网络变化时（节流后）通知。
/// 只读取本机接口信息，不访问外部网络，也不代表地址已被验证可从其他机器访问。
/// </summary>
public sealed class NetworkAddressService : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private Timer? _debounceTimer;
    private bool _monitoring;

    /// <summary>网络变化后触发；在线程池线程上调用，订阅方需自行切回 UI 线程</summary>
    public event EventHandler? AddressesChanged;

    /// <summary>当前运行中接口的全部 IPv4/IPv6 单播地址，按稳定顺序排列</summary>
    public IReadOnlyList<NetworkAddressInfo> GetAddresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        var results = new List<NetworkAddressInfo>();
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            var nicIsLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback;
            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    continue;

                // 仍在冲突检测或已判定无效的地址实际不可用
                var dad = unicast.DuplicateAddressDetectionState;
                if (dad is DuplicateAddressDetectionState.Duplicate or DuplicateAddressDetectionState.Invalid
                    or DuplicateAddressDetectionState.Tentative)
                    continue;

                var isLoopback = nicIsLoopback || IPAddress.IsLoopback(address);
                var isLinkLocal = address.IsIPv6LinkLocal ||
                                  (address.AddressFamily == AddressFamily.InterNetwork && IsIPv4LinkLocal(address));
                // 已弃用地址与 IPv6 临时隐私地址仍可用，但会变化，折叠到「其他地址」
                var isDeprecated = dad == DuplicateAddressDetectionState.Deprecated;
                var isTemporary = address.AddressFamily == AddressFamily.InterNetworkV6 &&
                                  unicast.SuffixOrigin == SuffixOrigin.Random;

                results.Add(new NetworkAddressInfo
                {
                    InterfaceId = nic.Id,
                    InterfaceName = nic.Name,
                    InterfaceDescription = nic.Description,
                    InterfaceType = nic.NetworkInterfaceType,
                    OperationalStatus = nic.OperationalStatus,
                    Address = address,
                    IsLoopback = isLoopback,
                    IsLinkLocal = isLinkLocal,
                    IsDhcpAssigned = unicast.PrefixOrigin == PrefixOrigin.Dhcp,
                    IsPrimary = !isLoopback && !isLinkLocal && !isDeprecated && !isTemporary
                });
            }
        }

        // 主地址在前，IPv4 优先；同类按接口名、地址字节排序，刷新后顺序不跳动
        return results
            .OrderByDescending(a => a.IsPrimary)
            .ThenBy(a => a.IsIPv6)
            .ThenBy(a => a.InterfaceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.InterfaceId, StringComparer.Ordinal)
            .ThenBy(a => Convert.ToHexString(a.Address.GetAddressBytes()), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>169.254.0.0/16（APIPA）</summary>
    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>开始监听系统网络变化。连续多次变化合并为一次通知</summary>
    public void StartMonitoring()
    {
        lock (_lock)
        {
            if (_monitoring)
                return;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            _monitoring = true;
        }
    }

    /// <summary>停止监听并取消挂起的通知。NetworkChange 是静态事件，不取消订阅会让订阅方无法回收</summary>
    public void StopMonitoring()
    {
        lock (_lock)
        {
            if (!_monitoring)
                return;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _monitoring = false;
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        OnNetworkChanged(sender, e);

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (!_monitoring)
                return;
            // 每次变化重置计时器：网卡切换时系统会连续触发多次事件
            _debounceTimer ??= new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged()
    {
        lock (_lock)
        {
            if (!_monitoring)
                return;
        }
        AddressesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => StopMonitoring();
}
