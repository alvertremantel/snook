using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Snook.UI;

public partial class ConnectionWindow : Window
{
    private bool _canClose;
    public ConnectionViewModel ViewModel { get; }

    public ConnectionWindow() : this(new ConnectionViewModel(
        new Snook.Application.ClientProfileStore(Snook.Application.ClientProfileStore.LaunchDataDirectory()), new(null, null),
        new(1, "daemon", Snook.Application.ClientProfileStore.LaunchDataDirectory()))) { }

    public ConnectionWindow(ConnectionViewModel viewModel, bool autoConnect = false)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Opened += async (_, _) => { if (autoConnect) await ViewModel.ConnectAsync(); };
        Closing += async (_, args) =>
        {
            if (_canClose) return;
            args.Cancel = true;
            await ViewModel.DisposeAsync();
            _canClose = true;
            Close();
        };
        viewModel.Saved += (_, _) => { if (viewModel.SettingsOnly) Close(); };
    }

    private void OnCancel(object? sender, RoutedEventArgs args) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
