using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace OtaTool.App.Controls;

public partial class PasswordInput : UserControl
{
    private bool _updating;
    private int _caretIndex = -1;

    public PasswordInput()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password),
        typeof(string),
        typeof(PasswordInput),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordPropertyChanged));

    public string Password
    {
        get => (string)GetValue(PasswordProperty);
        set => SetValue(PasswordProperty, value);
    }

    private static void OnPasswordPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (PasswordInput)dependencyObject;
        if (control._updating || control.PasswordEditor is null) return;

        var password = args.NewValue as string ?? string.Empty;
        if (!string.Equals(control.PasswordEditor.Password, password, StringComparison.Ordinal))
        {
            control.PasswordEditor.Password = password;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs args)
    {
        if (_updating) return;
        _updating = true;
        SetCurrentValue(PasswordProperty, PasswordEditor.Password);
        if (PasswordEditor.IsKeyboardFocused)
        {
            _caretIndex = PasswordEditor.Password.Length;
        }
        _updating = false;
    }

    private void OnTogglePassword(object sender, RoutedEventArgs args)
    {
        var revealPassword = PasswordEditor.Visibility == Visibility.Visible;
        var selectionStart = PlainTextEditor.IsKeyboardFocused
            ? PlainTextEditor.SelectionStart
            : _caretIndex >= 0
                ? Math.Min(_caretIndex, Password.Length)
                : Password.Length;
        var selectionLength = PlainTextEditor.IsKeyboardFocused
            ? PlainTextEditor.SelectionLength
            : 0;

        _caretIndex = selectionStart;

        PasswordEditor.Visibility = revealPassword ? Visibility.Collapsed : Visibility.Visible;
        PlainTextEditor.Visibility = revealPassword ? Visibility.Visible : Visibility.Collapsed;
        ToggleButton.ToolTip = revealPassword ? "隐藏密码" : "显示密码";

        if (ToggleButton.Template.FindName("EyeSlash", ToggleButton) is UIElement eyeSlash)
        {
            eyeSlash.Visibility = revealPassword ? Visibility.Visible : Visibility.Collapsed;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (revealPassword)
            {
                PlainTextEditor.Focus();
                PlainTextEditor.Select(Math.Min(selectionStart, PlainTextEditor.Text.Length), selectionLength);
            }
            else
            {
                PasswordEditor.Focus();
                EditingCommands.MoveToLineEnd.Execute(null, PasswordEditor);
                for (var index = PasswordEditor.Password.Length; index > selectionStart; index--)
                {
                    EditingCommands.MoveLeftByCharacter.Execute(null, PasswordEditor);
                }
            }
        }, DispatcherPriority.Input);
    }
}
