using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteKSP.Server.Views.Dialogs;

public partial class EditCertificateForm : UserControl
{
    public EditCertificateModel Model { get; }

    public EditCertificateForm(string name, bool isShared)
    {
        Model = new EditCertificateModel { Name = name, IsShared = isShared };
        DataContext = Model;
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }
}

public partial class EditCertificateModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isShared;
}
