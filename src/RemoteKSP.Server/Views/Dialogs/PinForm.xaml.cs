using System.Windows.Controls;

namespace RemoteKSP.Server.Views.Dialogs;

public partial class PinForm : UserControl
{
    public string Pin => PinBox.Password;

    public PinForm(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
        Loaded += (_, _) => PinBox.Focus();
    }
}
