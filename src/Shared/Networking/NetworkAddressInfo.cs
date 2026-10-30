using System;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteKSP.Shared.Networking;

/// <summary>本机某个网络接口上的一个单播地址</summary>
public sealed class NetworkAddressInfo
{
    public string InterfaceId { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public string InterfaceDescription { get; init; } = "";
    public NetworkInterfaceType InterfaceType { get; init; }
    public OperationalStatus OperationalStatus { get; init; }
    public IPAddress Address { get; init; } = IPAddress.None;
    public bool IsLoopback { get; init; }
    public bool IsLinkLocal { get; init; }
    public bool IsDhcpAssigned { get; init; }

    /// <summary>地址是否值得在主列表展示（否则折叠到「其他地址」）</summary>
    public bool IsPrimary { get; init; }

    public AddressFamily AddressFamily => Address.AddressFamily;
    public bool IsIPv6 => AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// 纯地址文本。Link-local IPv6 的 Zone ID（%6）是本机接口编号，对其他机器无意义，展示和复制时去掉；
    /// 生成本机 URL 时仍保留，见 <see cref="BuildHttpsUrl(int)"/>
    /// </summary>
    public string AddressText
    {
        get
        {
            var text = Address.ToString();
            var zone = text.IndexOf('%');
            return zone >= 0 ? text[..zone] : text;
        }
    }

    public string FamilyText => IsIPv6 ? "IPv6" : "IPv4";

    /// <summary>系统报告的接口类型，用于详情；不据此推断 VPN/虚拟网卡</summary>
    public string InterfaceTypeText => InterfaceType switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
        NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "以太网",
        NetworkInterfaceType.Wireless80211 => "无线",
        // 环回由 ScopeText 标注，这里不再重复
        NetworkInterfaceType.Loopback => "",
        NetworkInterfaceType.Tunnel => "隧道",
        NetworkInterfaceType.Ppp => "PPP",
        // 虚拟网卡/VPN 常报告枚举里未定义的类型值（如 53）；不显示数字，也不猜测分类，接口名已足够识别
        _ when !Enum.IsDefined(InterfaceType) || InterfaceType == NetworkInterfaceType.Unknown => "",
        _ => InterfaceType.ToString()
    };

    /// <summary>辅助说明，例如「链路本地」「环回」</summary>
    public string ScopeText => IsLoopback ? "环回" : IsLinkLocal ? "链路本地" : "";

    /// <summary>
    /// 生成 https URL。IPv6 用方括号包裹；Link-local 的 Zone ID 按 RFC 6874 编码为 %25。
    /// 注意：Zone ID 只在本机有意义，其他机器需要使用自己的接口编号。
    /// </summary>
    public string BuildHttpsUrl(int port) => BuildHttpsUrl(Address, port);

    public static string BuildHttpsUrl(IPAddress address, int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        var portText = port.ToString(CultureInfo.InvariantCulture);
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return $"https://{address}:{portText}";

        // IPAddress.ToString() 对带 ScopeId 的地址输出 "fe80::1%12"，URL 中 % 必须写作 %25
        var text = address.ToString();
        var zoneIndex = text.IndexOf('%');
        if (zoneIndex >= 0)
            text = text[..zoneIndex] + "%25" + Uri.EscapeDataString(text[(zoneIndex + 1)..]);
        return $"https://[{text}]:{portText}";
    }
}
