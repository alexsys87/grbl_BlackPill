using System.ComponentModel;
using System.IO;
using System.Windows;
using GrblHost.ViewModels;

namespace GrblHost;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Drop += OnDrop;
        DragOver += OnDragOver;
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
        _vm.SaveSettings();
        _vm.Dispose();
        base.OnClosing(e);
    }
}
