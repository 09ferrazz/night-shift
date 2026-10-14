using System.Windows;

namespace NightShift.App;

public partial class MainWindow
{
    private SlimWindow? _slim;
    private bool _slimExiting;
    private void InitializeSlim()
    {
        Closed += (_, _) => CloseSlimForExit();
    }
    private void SlimClick(object sender, RoutedEventArgs e)
    {
        if (_closing || _slimExiting) return;
        if (_slim is null)
        {
            // Do not set Owner: hiding an owner also hides its owned windows.
            _slim = new SlimWindow(_view);
            _slim.Closing += (_, _) =>
            {
                if (_slimExiting || _closing) return;
                Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
            _slim.Closed += (_, _) => _slim = null;
        }
        _slim.Show(); Hide();
    }
    private void CloseSlimForExit()
    {
        _slimExiting = true;
        _slim?.CloseForExit(); _slim = null;
    }
}
