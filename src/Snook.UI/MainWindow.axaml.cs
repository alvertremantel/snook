using Avalonia.Controls;
using Avalonia.Interactivity;
using Snook.Contracts;

namespace Snook.UI;

public partial class MainWindow : Window, IAsyncDisposable
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
        : this(App.ConfiguredBackend ?? throw new InvalidOperationException("The desktop backend was not configured."))
    {
    }

    public MainWindow(IBackendClient backend)
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(backend);
        DataContext = _viewModel;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _viewModel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
