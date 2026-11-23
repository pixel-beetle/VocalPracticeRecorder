using System.Windows;
using System.Windows.Controls;

namespace VocalPracticeRecorder.Views;

public sealed class RenameDialog : Window
{
    private readonly TextBox _box;
    private readonly CheckBox _all;

    public string Value => _box.Text;
    public bool ApplyToGroup => _all.IsChecked == true;

    public RenameDialog(string current, int groupCount)
    {
        Title = "重命名曲名";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _box = new TextBox { Text = current, Margin = new Thickness(0, 0, 0, 10) };
        _all = new CheckBox { Content = $"同时修改这首歌的全部 {groupCount} 遍", IsChecked = true, Visibility = groupCount > 1 ? Visibility.Visible : Visibility.Collapsed };
        var ok = new Button { Content = "确定", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(_box);
        panel.Children.Add(_all);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
