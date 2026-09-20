using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Snook.UI;
using Button = Avalonia.Controls.Button;

namespace Snook.Android;

// Development host for the complete shared workspace. The Avalonia top level
// applies the platform safe area while Android's resize mode keeps the focused
// editor reachable above the IME.
internal sealed class AndroidHostView : UserControl
{
    private readonly AndroidWorkspace _workspace;
    private readonly SpikeProbe _probe;
    private readonly ContentControl _body = new();
    private int _renderVersion;
    private bool _attached;
    private global::Android.App.AlertDialog? _discardDialog;
    private readonly TextBlock _shareMessage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _reviewShare = new() { Content = "Review shared task", MinHeight = 48 };
    private readonly Button _discardShare = new() { Content = "Discard share", MinHeight = 48 };
    private readonly TextBlock _recoveryMessage = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _discardRecovery = new() { Content = "Discard recovery copy", MinHeight = 48 };
    private readonly StackPanel _configuredHeader;
    private readonly TextBlock _hostLabel;

    internal AndroidHostView(AndroidWorkspace workspace, SpikeProbe probe)
    {
        TopLevel.SetAutoSafeAreaPadding(this, true);
        _workspace = workspace;
        _probe = probe;
        _discardRecovery.Click += async (_, _) => await _workspace.DiscardRecoveredCaptureAsync();
        _reviewShare.Click += async (_, _) =>
        {
            _workspace.IsWorkspaceSelected = true;
            try { await _workspace.ReviewShareAsync(); }
            catch (Exception) { _shareMessage.Text = "Shared task could not be opened. Nothing was saved; retry or discard."; }
            await RenderAsync();
        };
        _discardShare.Click += (_, _) => _workspace.DiscardShare();
        var open = new Button { Content = "Open workspace", MinHeight = 48 };
        var checks = new Button { Content = "Platform checks", MinHeight = 48 };
        var switchWorkspace = new Button { Content = "Switch workspace…", MinHeight = 48 };
        var tools = new WrapPanel
        {
            IsVisible = !_workspace.IsWorkspaceSelected,
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children = { open, switchWorkspace, checks }
        };
        var toolsButton = new Button { Content = "Tools", MinHeight = 48, MinWidth = 64 };
        toolsButton.Click += (_, _) => tools.IsVisible = !tools.IsVisible;
        open.Click += async (_, _) =>
        {
            _workspace.IsWorkspaceSelected = true;
            tools.IsVisible = false;
            await RenderAsync();
        };
        checks.Click += async (_, _) =>
        {
            _workspace.IsWorkspaceSelected = false;
            await RenderAsync();
        };
        switchWorkspace.Click += async (_, _) => await _workspace.SelectAnotherHostAsync();
        _hostLabel = new TextBlock
        {
            Text = "Snook · On this device",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(toolsButton, 1);
        toolbar.Children.Add(_hostLabel);
        toolbar.Children.Add(toolsButton);
        _configuredHeader = new StackPanel
        {
            Margin = new Thickness(8, 8, 8, 4),
            Children =
            {
                toolbar,
                tools,
                _recoveryMessage,
                _discardRecovery,
                _shareMessage,
                new WrapPanel { Children = { _reviewShare, _discardShare } }
            }
        };
        DockPanel.SetDock(_configuredHeader, Dock.Top);
        Content = new DockPanel { Children = { _configuredHeader, _body } };
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _workspace.SharedCaptureChanged += OnSharedCaptureChanged;
        _workspace.ProjectionChanged += OnProjectionChanged;
        OnSharedCaptureChanged(this, EventArgs.Empty);
        await RenderAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        MainActivity.Current?.SetSensitiveScreen(false);
        _discardDialog?.Dismiss();
        _workspace.SharedCaptureChanged -= OnSharedCaptureChanged;
        _workspace.ProjectionChanged -= OnProjectionChanged;
        _renderVersion++;
        _body.Content = null;
        base.OnDetachedFromVisualTree(e);
    }

    internal bool HasOpenEditor => _attached && _body.Content is WorkspaceView { DataContext: MainWindowViewModel model }
        && (model.IsUtilityEditorOpen || model.IsTaskEditorOpen);

    internal bool HandleBack()
    {
        if (!_attached || _body.Content is not WorkspaceView { DataContext: MainWindowViewModel model }) return false;
        var discard = model.CreateEditorDiscardRequest();
        if (discard is null) return false;
        if (_discardDialog is not null || MainActivity.Current is not { } activity) return true;
        using var builder = new global::Android.App.AlertDialog.Builder(activity);
        builder.SetTitle("Discard editor changes?");
        builder.SetMessage("Unsaved changes in this editor will be lost. This does not undo saved changes or cancel a save already requested.");
        builder.SetNegativeButton("Keep editing", (_, _) => { });
        builder.SetPositiveButton("Discard changes", (_, _) =>
        {
            if (_attached) discard();
        });
        var dialog = builder.Create()!;
        _discardDialog = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_discardDialog, dialog)) _discardDialog = null;
            dialog.Dispose();
        };
        // Back or outside-tap dismisses only the confirmation, retaining the draft.
        dialog.Show();
        return true;
    }

    private async Task RenderAsync()
    {
        var version = ++_renderVersion;
        MainActivity.Current?.SetSensitiveScreen(
            _workspace.HostMode == AndroidHostMode.Unconfigured && _workspace.PairingPreview is not null);
        _configuredHeader.IsVisible = _workspace.HostMode != AndroidHostMode.Unconfigured;
        _hostLabel.Text = _workspace.HostMode == AndroidHostMode.Attached
            ? _workspace.ActiveHostLabel : "Snook · On this device";
        if (_workspace.HostMode == AndroidHostMode.Unconfigured)
        {
            _body.Content = CreateOnboardingView();
            return;
        }
        if (!_workspace.IsWorkspaceSelected)
        {
            _body.Content = new SpikeView(_probe);
            return;
        }
        _body.Content = new TextBlock { Text = "Opening private workspace…", Margin = new Thickness(16) };
        try
        {
            var viewModel = await _workspace.OpenAsync();
            if (_attached && version == _renderVersion)
                _body.Content = new WorkspaceView(viewModel);
        }
        catch (Exception)
        {
            if (_attached && version == _renderVersion)
            {
                var retry = new Button { Content = "Retry connection", MinHeight = 48 };
                retry.Click += async (_, _) =>
                {
                    retry.IsEnabled = false;
                    _workspace.IsWorkspaceSelected = true;
                    await RenderAsync();
                };
                var forget = new Button
                {
                    Content = "Forget pairing…",
                    MinHeight = 48,
                    IsVisible = _workspace.HostMode == AndroidHostMode.Attached
                };
                forget.Click += async (_, _) =>
                {
                    forget.IsEnabled = false;
                    await _workspace.ForgetAttachedAsync();
                    forget.IsEnabled = true;
                };
                _body.Content = new StackPanel
                {
                    Margin = new Thickness(16),
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = _workspace.HostMode == AndroidHostMode.Attached
                                ? "Paired daemon unavailable. Retry after restoring Tailscale/Serve access. Draft recovery remains on this device; no local workspace or alternate network path was opened."
                                : "Workspace unavailable. Retry when private storage is available. No alternate host was opened.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        retry,
                        forget
                    }
                };
            }
        }
    }

    private ScrollViewer CreateOnboardingView()
    {
        var independent = new Button
        {
            Content = AndroidWorkspace.HasIndependentWorkspace
                ? "Open device workspace" : "Independent workspace",
            MinHeight = 48,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var attached = new Button
        {
            Content = "Connect to a Snook daemon",
            MinHeight = 48,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        var message = new TextBlock
        {
            Text = _workspace.HostConfigurationMessage,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = !string.IsNullOrEmpty(_workspace.HostConfigurationMessage)
        };
        independent.Click += async (_, _) =>
        {
            independent.IsEnabled = false;
            attached.IsEnabled = false;
            await _workspace.ConfigureIndependentAsync();
        };
        var origin = new TextBox
        {
            PlaceholderText = "https://host.tailnet-name.ts.net/",
            MinHeight = 48
        };
        var code = new TextBox
        {
            PlaceholderText = "Short-lived pairing code",
            PasswordChar = '●',
            MinHeight = 48
        };
        var deviceName = new TextBox
        {
            Text = global::Android.OS.Build.Model ?? "Android device",
            PlaceholderText = "Device name",
            MinHeight = 48
        };
        var pairingMessage = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var pairingForm = new StackPanel
        {
            Spacing = 8,
            IsVisible = false,
            Children =
            {
                new TextBlock
                {
                    Text = "Tailscale Serve pairing",
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Enter the exact HTTPS *.ts.net origin and a fresh code created by the daemon administrator. Snook will not try HTTP, an IP address, LAN discovery, redirects, proxies, or an embedded fallback.",
                    TextWrapping = TextWrapping.Wrap
                },
                origin,
                code,
                deviceName,
                pairingMessage
            }
        };
        var beginPairing = new Button
        {
            Content = "Review pairing identity",
            MinHeight = 48,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        pairingForm.Children.Add(beginPairing);
        attached.Click += (_, _) =>
        {
            pairingForm.IsVisible = true;
            MainActivity.Current?.SetSensitiveScreen(true);
        };
        beginPairing.Click += async (_, _) =>
        {
            beginPairing.IsEnabled = false;
            independent.IsEnabled = false;
            pairingMessage.Text = "Contacting the Tailnet-only Serve origin…";
            try
            {
                await _workspace.BeginAttachedPairingAsync(origin.Text ?? string.Empty,
                    code.Text ?? string.Empty, deviceName.Text ?? string.Empty);
                await RenderAsync();
            }
            catch (Exception exception)
            {
                pairingMessage.Text = exception is Snook.Domain.SnookException snook
                    ? snook.Message
                    : "Pairing could not be reviewed. Check Tailscale, the Serve origin, and the short-lived code. No local workspace was opened.";
                beginPairing.IsEnabled = true;
                independent.IsEnabled = true;
            }
        };
        Control pairingSection = pairingForm;
        if (_workspace.PairingPreview is { } preview)
        {
            var confirm = new Button
            {
                Content = "Confirm and connect",
                MinHeight = 48,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            var cancel = new Button
            {
                Content = "Cancel pairing",
                MinHeight = 48,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
            };
            confirm.Click += async (_, _) =>
            {
                confirm.IsEnabled = false;
                cancel.IsEnabled = false;
                try { await _workspace.ConfigureAttachedAsync(); }
                catch (Exception) { /* The workspace renders the retry-safe state. */ }
            };
            cancel.Click += async (_, _) =>
            {
                _workspace.CancelAttachedPairing();
                await RenderAsync();
            };
            pairingSection = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Confirm the daemon identity",
                        FontSize = 20,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = $"Serve origin: {preview.ServeOrigin}\nTailnet login: {preview.TailnetLogin}\nDevice: {preview.DeviceName}\nDaemon instance: {preview.InstanceId}\nWorkspace: {preview.WorkspaceId}\nExpires: {preview.ExpiresAtUtc:u}",
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "Confirm only if these values match the daemon you intended to pair. Confirmation issues one device credential and protects it with Android Keystore.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    confirm,
                    cancel
                }
            };
        }
        var savedProfiles = new StackPanel { Spacing = 8 };
        foreach (var profile in _workspace.SavedAttachedProfiles)
        {
            var profileName = new TextBox
            {
                Text = profile.DeviceName,
                MinHeight = 48,
                MaxLength = 80,
                PlaceholderText = "Device name"
            };
            var openSaved = new Button
            {
                Content = "Connect",
                MinHeight = 48,
                MinWidth = 100
            };
            var forgetSaved = new Button
            {
                Content = "Forget…",
                MinHeight = 48,
                MinWidth = 100
            };
            var testSaved = new Button { Content = "Test", MinHeight = 48, MinWidth = 88 };
            var renameSaved = new Button { Content = "Rename", MinHeight = 48, MinWidth = 88 };
            var repairSaved = new Button { Content = "Repair", MinHeight = 48, MinWidth = 88 };
            var revokeSaved = new Button { Content = "Revoke…", MinHeight = 48, MinWidth = 88 };
            openSaved.Click += async (_, _) =>
            {
                openSaved.IsEnabled = false;
                forgetSaved.IsEnabled = false;
                try { await _workspace.RetrySavedAttachedAsync(profile.CredentialId); }
                catch (Exception) { /* The workspace renders a fail-closed retry state. */ }
            };
            testSaved.Click += async (_, _) =>
            {
                testSaved.IsEnabled = false;
                try { await _workspace.TestSavedAttachedAsync(profile.CredentialId); }
                catch (Exception) { /* Status explains the fail-closed result. */ }
            };
            renameSaved.Click += async (_, _) =>
            {
                renameSaved.IsEnabled = false;
                try
                {
                    await _workspace.RenameSavedAttachedAsync(profile.CredentialId,
                        profileName.Text ?? string.Empty);
                }
                catch (Exception) { /* Status explains uncertainty. */ }
            };
            repairSaved.Click += async (_, _) =>
            {
                repairSaved.IsEnabled = false;
                try { await _workspace.RepairSavedAttachedAsync(profile.CredentialId); }
                catch (Exception) { /* Status explains the fail-closed repair result. */ }
            };
            revokeSaved.Click += async (_, _) =>
            {
                revokeSaved.IsEnabled = false;
                try { await _workspace.RevokeSavedAttachedAsync(profile.CredentialId); }
                catch (Exception) { /* Status explains uncertainty. */ }
            };
            forgetSaved.Click += async (_, _) =>
            {
                openSaved.IsEnabled = false;
                forgetSaved.IsEnabled = false;
                try { await _workspace.ForgetSavedAttachedAsync(profile.CredentialId); }
                catch (Exception) { /* Stored metadata remains fail closed. */ }
            };
            savedProfiles.Children.Add(new Border
            {
                Padding = new Thickness(12),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = profile.ServeOrigin.Host,
                            FontWeight = FontWeight.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = $"{profile.DeviceName} · {profile.TailnetLogin}\nWorkspace {profile.WorkspaceId}",
                            Classes = { "muted" },
                            TextWrapping = TextWrapping.Wrap
                        },
                        profileName,
                        new WrapPanel
                        {
                            Children = { openSaved, testSaved, renameSaved, repairSaved, revokeSaved, forgetSaved }
                        }
                    }
                }
            });
        }
        if (_workspace.HasSavedAttachedProfile && savedProfiles.Children.Count == 0)
            savedProfiles.Children.Add(new TextBlock
            {
                Text = "Saved pairing metadata is unavailable. Retry will fail closed; app data repair or re-pairing is required.",
                TextWrapping = TextWrapping.Wrap
            });
        return new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(20, 24),
                MaxWidth = 560,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Choose where Snook stores your work",
                        FontSize = 24,
                        FontWeight = FontWeight.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "This choice selects a workspace; it does not synchronize or merge data. Snook will not create a database until you confirm an independent workspace.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    independent,
                    new TextBlock
                    {
                        Text = AndroidWorkspace.HasIndependentWorkspace
                            ? "Existing private workspace on this device. Separate from every paired daemon."
                            : "Private storage on this device. Works offline. You manage verified backups.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    attached,
                    new TextBlock
                    {
                        Text = "Tailnet-only HTTPS attachment to an explicitly paired daemon. No local workspace or network fallback.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    pairingSection,
                    savedProfiles,
                    message
                }
            }
        };
    }

    private void OnSharedCaptureChanged(object? sender, EventArgs e)
    {
        IsEnabled = !_workspace.IsRestoring;
        _recoveryMessage.Text = _workspace.CaptureRecoveryMessage;
        _recoveryMessage.IsVisible = !string.IsNullOrEmpty(_recoveryMessage.Text);
        _discardRecovery.IsVisible = _recoveryMessage.IsVisible;
        _discardRecovery.Content = _workspace.HasBlockedRecovery ? "Discard recovery copy" : "Dismiss recovery notice";
        _shareMessage.Text = _workspace.SharedCaptureMessage;
        _shareMessage.IsVisible = !string.IsNullOrEmpty(_shareMessage.Text);
        _reviewShare.IsVisible = _workspace.SharedCapture.Text is not null;
        _discardShare.IsVisible = _shareMessage.IsVisible;
    }

    private async void OnProjectionChanged(object? sender, EventArgs e) => await RenderAsync();
}
