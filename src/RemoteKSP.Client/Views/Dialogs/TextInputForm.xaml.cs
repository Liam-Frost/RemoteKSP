using System.Windows.Controls;

namespace RemoteKSP.Client.Views.Dialogs;

/// <summary>单行文本输入表单（配合 DialogService.ShowFormAsync）</summary>
public partial class TextInputForm : UserControl
{
    public string Value => ValueBox.Text;

    public TextInputForm(string label, string value, string hint = "")
    {
        InitializeComponent();
        LabelText.Text = label;
        ValueBox.Text = value;
        HintText.Text = hint;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }
}
