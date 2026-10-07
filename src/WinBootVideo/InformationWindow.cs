using System.Windows;
using System.Windows.Controls;

namespace WinBootVideo;

internal static class InformationWindow
{
    internal static void Show(Window owner, string title, string text)
    {
        var window = new Window
        {
            Title = title, Owner = owner, Width = 780, Height = 660, MinWidth = 540, MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)owner.FindResource("Surface")
        };
        var layout = new Grid { Margin = new Thickness(28) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) };
        var content = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(18),
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        var close = new Button { Content = "关闭", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        close.Click += (_, _) => window.Close();
        Grid.SetRow(content, 1); Grid.SetRow(close, 2);
        layout.Children.Add(heading); layout.Children.Add(content); layout.Children.Add(close);
        window.Content = layout; window.ShowDialog();
    }
}
