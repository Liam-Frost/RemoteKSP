using System.Windows;
using System.Windows.Controls;

namespace RemoteKSP.Shared.Networking;

/// <summary>首页网络地址卡片。DataContext 为 <see cref="NetworkAddressesViewModel"/></summary>
public partial class NetworkAddressesCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(NetworkAddressesCard),
            new PropertyMetadata(Localization.SharedStrings.Net_CardTitle));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public NetworkAddressesCard()
    {
        InitializeComponent();
    }
}
