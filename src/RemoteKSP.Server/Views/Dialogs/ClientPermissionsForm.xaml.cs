using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteKSP.Server.ViewModels;

namespace RemoteKSP.Server.Views.Dialogs;

public partial class ClientPermissionsForm : UserControl
{
    public ClientPermissionsModel Model { get; }

    public ClientPermissionsForm(string message, IEnumerable<CertificateItem> certificates, List<string>? allowedCertIds)
    {
        Model = new ClientPermissionsModel
        {
            AllowAll = allowedCertIds == null,
            Items = new ObservableCollection<PermissionItem>(certificates.Select(c => new PermissionItem
            {
                Id = c.Id,
                Name = c.Name,
                Caption = c.IsShared ? $"{c.TypeText} · {c.Thumbprint}" : $"{c.TypeText} · {Strings.Perm_NotShared}",
                IsShared = c.IsShared,
                IsChecked = allowedCertIds == null ? c.IsShared : allowedCertIds.Contains(c.Id)
            }))
        };
        DataContext = Model;
        InitializeComponent();
        MessageText.Text = message;
    }

    /// <summary>null = 全部允许；否则为勾选的证书 ID</summary>
    public List<string>? Result =>
        Model.AllowAll ? null : Model.Items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
}

public partial class ClientPermissionsModel : ObservableObject
{
    [ObservableProperty] private bool _allowAll;
    public ObservableCollection<PermissionItem> Items { get; init; } = [];
}

public partial class PermissionItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Caption { get; init; } = "";
    public bool IsShared { get; init; }
    [ObservableProperty] private bool _isChecked;
}
