using System.Windows;
using System.Windows.Input;

namespace OtaTool.App;

public partial class AppMessageDialog : Window
{
    private AppMessageDialog(string title, string message)
    {
        InitializeComponent();
        DialogTitle = title;
        DialogMessage = message;
        DataContext = this;
    }

    public string DialogTitle { get; }

    public string DialogMessage { get; }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        NativeWindowShadow.Apply(this);
    }

    public static void Show(string title, string message)
    {
        var dialog = new AppMessageDialog(title, message);
        if (Application.Current?.MainWindow is { IsLoaded: true, IsVisible: true } owner &&
            !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }
        dialog.ShowDialog();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs eventArgs)
    {
        DialogResult = true;
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
