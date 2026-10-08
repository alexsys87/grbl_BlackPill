using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GrblHost.ViewModels;

namespace GrblHost.Views;

public partial class ControlPanel : UserControl
{
    public ControlPanel()
    {
        InitializeComponent();
        // Continuous jog: the jog buttons move while pressed. Their commands
        // only do steps, so press and release are taken here.
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnJogPress), true);
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnJogRelease), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnJogLost), true);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>The jog button under the mouse, with its "X+" / "XY-+" parameter.</summary>
    private ButtonBase? JogButton(object source, out string what)
    {
        what = "";
        for (var d = source as DependencyObject; d != null && d != this; d = ParentOf(d))
        {
            if (d is ButtonBase b && Vm != null && b.Command == Vm.JogCommand && b.CommandParameter is string p)
            {
                what = p;
                return b;
            }
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private ButtonBase? _held;

    private void OnJogPress(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        if (vm is not { JogContinuous: true } || JogButton(e.OriginalSource, out string what) is not { } b)
            return;
        _held = b;
        vm.JogStart(what);
    }

    private void OnJogRelease(object sender, MouseButtonEventArgs e) => Release();

    private void OnJogLost(object sender, MouseEventArgs e)
    {
        if (_held != null && !_held.IsMouseCaptured)
            Release();
    }

    private void Release()
    {
        if (_held == null)
            return;
        _held = null;
        Vm?.JogStop();
    }
}
