using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GrblHost.Services;
using GrblHost.ViewModels;

namespace GrblHost;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly GamepadController _gamepad;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Drop += OnDrop;
        DragOver += OnDragOver;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        // A continuous jog must not run on when the keys go to another window.
        Deactivated += (_, _) => _vm.JogStop();
        // Gamepad or joystick jog (setting "Gamepad" in the Axes tab).
        _gamepad = new GamepadController(vm, this);
    }

    /// <summary>Keys typed into a text field are not jog keys.</summary>
    private static bool TypingText() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || TypingText())
            return;
        if (_vm.JogKey(e.Key, true, e.IsRepeat))
            e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // A key up always stops a continuous jog, even after the focus moved to a text field.
        if (_vm.JogKey(e.Key, false, false) && !TypingText())
            e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && File.Exists(files[0]))
            _ = _vm.LoadFileAsync(files[0]);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.IsJobRunning &&
            MessageBox.Show(Services.Loc.T("S.Ask.CloseWhileRunning"),
                "Grbl Host", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _gamepad.Dispose();
        _vm.SaveSettings();
        _vm.Dispose();
        base.OnClosing(e);
    }
}
