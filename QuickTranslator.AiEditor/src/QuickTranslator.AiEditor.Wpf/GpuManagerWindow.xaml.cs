using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class GpuManagerWindow : Window
{
    private readonly OllamaBackendManager _backends;
    private readonly GpuConnectionManager _connections;
    private readonly GpuTranslationPreferences _preferences;
    private readonly RemoteOllamaSetupService _setup = new();
    private readonly RemoteOllamaModelPullService _modelPull = new();
    private readonly LocalOllamaService _local = new();
    private readonly ObservableCollection<OllamaBackendProfile> _profiles = [];
    private string? _loadedPrivateKey;
    private CancellationTokenSource? _setupCancellation;
    private bool _busy;
    private bool _loadingMode;

    internal GpuManagerWindow(OllamaBackendManager backends, GpuConnectionManager connections,
        GpuTranslationPreferences? preferences = null)
    {
        _backends = backends; _connections = connections;
        _preferences = preferences ?? new();
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1180, 820);
        _loadingMode = true;
        TranslationModeInput.SelectedIndex = (int)_preferences.Mode;
        _loadingMode = false;
        ProfileList.ItemsSource = _profiles;
        RefreshProfiles();
    }

    private void OnTranslationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranslationModeText is null) return;
        _preferences.Mode = (GpuTranslationMode)Math.Max(0, TranslationModeInput.SelectedIndex);
        if (!_loadingMode) _preferences.ModeExplicitlySelected = true;
        TranslationModeText.Text = _preferences.Mode switch
        {
            GpuTranslationMode.LocalOnly => "Queue dùng một Ollama Local.",
            GpuTranslationMode.RemoteOnly => "Queue dùng profile Remote đang kích hoạt.",
            _ => "Một coordinator phân phối chapter động cho Local và Remote, tối đa hai request đồng thời."
        };
    }

    private void OnUseForRemote(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not OllamaBackendProfile { Kind: OllamaBackendKind.Remote } profile)
        { StatusText.Text = "Hãy chọn một profile Remote."; return; }
        _preferences.RemoteProfileId = profile.Id;
        StatusText.Text = $"Đã chọn “{profile.Name}” làm worker Remote cho chế độ Parallel.";
    }

    private void RefreshProfiles(Guid? selected = null)
    {
        Guid target = selected ?? (ProfileList.SelectedItem as OllamaBackendProfile)?.Id ?? _backends.ActiveProfile.Id;
        _profiles.Clear();
        foreach (OllamaBackendProfile profile in _backends.Profiles) _profiles.Add(profile);
        ProfileList.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == target) ?? _profiles.FirstOrDefault();
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is not OllamaBackendProfile profile) return;
        AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
        NameInput.Text = profile.Name; ModelInput.Text = runtime.ModelOptions.Model;
        RuntimeTemperatureInput.Text = runtime.ModelOptions.Temperature.ToString(CultureInfo.InvariantCulture);
        RuntimeContextInput.Text = runtime.ModelOptions.ContextSize.ToString(CultureInfo.InvariantCulture);
        RuntimeOutputInput.Text = runtime.ModelOptions.MaximumOutputTokens.ToString(CultureInfo.InvariantCulture);
        RuntimeTimeoutInput.Text = runtime.ModelOptions.RequestTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        RuntimeMinimumInput.Text = runtime.SegmentationOptions.MinimumHanCharacters.ToString(CultureInfo.InvariantCulture);
        RuntimeTargetInput.Text = runtime.SegmentationOptions.TargetHanCharacters.ToString(CultureInfo.InvariantCulture);
        RuntimeMaximumInput.Text = runtime.SegmentationOptions.MaximumHanCharacters.ToString(CultureInfo.InvariantCulture);
        RuntimeTextElementsInput.Text = runtime.SegmentationOptions.MaximumTextElements.ToString(CultureInfo.InvariantCulture);
        bool remote = profile.Kind == OllamaBackendKind.Remote;
        AdvancedConnectionExpander.IsEnabled = remote;
        AdvancedConnectionExpander.IsExpanded = false;
        AdvancedRuntimeExpander.IsExpanded = false;
        UseForRemoteButton.IsEnabled = remote;
        ConnectionTypeInput.SelectedIndex = profile.Ssh is null ? 1 : 0;
        EndpointInput.Text = profile.Endpoint;
        TokenInput.Password = profile.Token ?? "";
        if (profile.Ssh is { } ssh)
        {
            HostInput.Text = ssh.Host; PortInput.Text = ssh.Port.ToString(CultureInfo.InvariantCulture);
            UsernameInput.Text = ssh.Username; AuthenticationInput.SelectedIndex = ssh.Authentication == SshAuthenticationKind.Password ? 0 : 1;
            PasswordInput.Password = ssh.Password ?? ""; PrivateKeyInput.Password = string.IsNullOrEmpty(ssh.PrivateKey) ? "" : "••••••••";
            PrivateKeyPassphraseInput.Password = ssh.PrivateKeyPassphrase ?? ""; _loadedPrivateKey = ssh.PrivateKey;
            FingerprintInput.Text = ssh.HostKeyFingerprint ?? ""; RemoteOllamaPortInput.Text = ssh.RemoteOllamaPort.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            HostInput.Text = ""; PortInput.Text = "22"; UsernameInput.Text = "ubuntu"; AuthenticationInput.SelectedIndex = 0;
            PasswordInput.Password = PrivateKeyInput.Password = PrivateKeyPassphraseInput.Password = ""; _loadedPrivateKey = null;
            FingerprintInput.Text = ""; RemoteOllamaPortInput.Text = "11434";
        }
        HourlyPriceInput.Text = (profile.HourlyPrice ?? 0).ToString(CultureInfo.InvariantCulture);
        StatusText.Text = remote ? _connections.GetStatus(profile.Id).Message : "Ollama Local trên máy Windows này.";
        ApplyAuthenticationUi();
        ApplyConnectionUi();
    }

    private void OnAuthenticationChanged(object sender, SelectionChangedEventArgs e) => ApplyAuthenticationUi();
    private void OnConnectionTypeChanged(object sender, SelectionChangedEventArgs e) => ApplyConnectionUi();
    private void ApplyConnectionUi()
    {
        if (SshConnectionPanel is null || DirectConnectionPanel is null) return;
        bool remote = ProfileList.SelectedItem is OllamaBackendProfile { Kind: OllamaBackendKind.Remote };
        bool ssh = remote && ConnectionTypeInput.SelectedIndex != 1;
        SshConnectionPanel.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        DirectConnectionPanel.Visibility = remote && !ssh ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ApplyAuthenticationUi()
    {
        if (PasswordPanel is null) return;
        bool password = AuthenticationInput.SelectedIndex != 1;
        PasswordPanel.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyPanel.Visibility = password ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNewProfile(object sender, RoutedEventArgs e)
    {
        var profile = new OllamaBackendProfile { Name = "GPU Remote mới", Kind = OllamaBackendKind.Remote,
            Endpoint = "http://127.0.0.1:11434", Model = "translategemma:12b",
            Runtime = AiRuntimeProfile.CreateDefault("translategemma:12b"), Ssh = new() };
        _profiles.Add(profile); ProfileList.SelectedItem = profile;
    }

    private async void OnSaveProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OllamaBackendProfile profile = ReadProfile();
        if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted &&
            MessageBox.Show(this, "Remote GPU sẽ nhận prompt và nguyên văn segment. Xác nhận lưu profile Remote?",
                "Quyền gửi dữ liệu", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        profile.RemoteDisclosureAccepted = profile.Kind == OllamaBackendKind.Remote;
        bool isActive = _backends.ActiveProfile.Id == profile.Id;
        await _backends.SaveProfileAsync(profile, activate: isActive);
        RefreshProfiles(profile.Id);
        StatusText.Text = isActive
            ? "Đã lưu và áp dụng profile đang dùng. Request mới sẽ dùng model này."
            : "Đã lưu profile. Credential được bảo vệ bằng Windows DPAPI.";
    });

    private async void OnDeleteProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ProfileList.SelectedItem is not OllamaBackendProfile profile) return;
        if (MessageBox.Show(this, $"Xóa profile “{profile.Name}”?", "Xóa profile",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _backends.DetachRuntimeClientAsync(profile.Id);
        await _connections.DisconnectAsync(profile.Id);
        await _backends.DeleteProfileAsync(profile.Id);
        RefreshProfiles();
    });

    private async void OnConnect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OllamaBackendProfile profile = ReadProfile();
        if (profile.Kind == OllamaBackendKind.Local)
        {
            LocalOllamaStatus status = await _local.CheckAsync(profile.Model);
            StatusText.Text = status.Message; return;
        }
        if (profile.Ssh is null)
        {
            await _backends.SaveProfileAsync(profile, activate: false);
            using IOllamaBackendClient client = OllamaClient.Create(profile);
            await client.CheckConnectionAsync(profile.Model, TimeSpan.FromSeconds(10));
            await _backends.ActivateAsync(profile.Id);
            _preferences.RemoteProfileId = profile.Id;
            RefreshProfiles(profile.Id);
            StatusText.Text = "Connected · Ollama Active · Model Installed · Ready.";
            return;
        }
        try
        {
            await _backends.SaveProfileAsync(profile, activate: false);
            GpuConnectionHandle handle = await _connections.ConnectAsync(profile);
            await _backends.AttachRuntimeClientAsync(profile.Id, handle.Client);
            await _backends.ActivateAsync(profile.Id);
            _preferences.RemoteProfileId = profile.Id;
            RefreshProfiles(profile.Id); StatusText.Text = "Đã kết nối SSH tunnel loopback. Profile đang sẵn sàng.";
        }
        catch (SshHostKeyVerificationException error)
        {
            FingerprintInput.Text = error.ObservedFingerprint;
            StatusText.Text = error.Message + " Fingerprint quan sát: " + error.ObservedFingerprint;
            if (IsVisible && MessageBox.Show(this,
                    $"Máy chủ trả về SSH Host Key Fingerprint:\n{error.ObservedFingerprint}\n\nTin cậy fingerprint này, lưu profile và kết nối ngay?",
                    "Xác nhận SSH Fingerprint (1 chạm)", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                OllamaBackendProfile confirmedProfile = ReadProfile();
                confirmedProfile.RemoteDisclosureAccepted = true;
                await _backends.SaveProfileAsync(confirmedProfile, activate: false);
                GpuConnectionHandle handle = await _connections.ConnectAsync(confirmedProfile);
                await _backends.AttachRuntimeClientAsync(confirmedProfile.Id, handle.Client);
                await _backends.ActivateAsync(confirmedProfile.Id);
                _preferences.RemoteProfileId = confirmedProfile.Id;
                RefreshProfiles(confirmedProfile.Id);
                StatusText.Text = "Đã xác nhận SSH Fingerprint, lưu profile và kết nối tunnel sẵn sàng.";
            }
        }
    });

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ProfileList.SelectedItem is not OllamaBackendProfile profile) return;
        await _backends.DetachRuntimeClientAsync(profile.Id);
        if (profile.Ssh is not null) await _connections.DisconnectAsync(profile.Id);
        StatusText.Text = profile.Ssh is null ? "Đã ngắt runtime client trực tiếp." :
            "Đã ngắt tunnel. Máy thuê có thể vẫn tiếp tục tính phí.";
    });

    private async void OnInspect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OllamaBackendProfile profile = ReadProfile();
        if (profile.Kind == OllamaBackendKind.Local)
        {
            InspectionText.Text = (await _local.CheckAsync(profile.Model)).Message; return;
        }
        if (profile.Ssh is null)
        {
            using IOllamaBackendClient client = OllamaClient.Create(profile);
            IReadOnlyList<string> models = await client.ListModelsAsync(TimeSpan.FromSeconds(10));
            await client.CheckConnectionAsync(profile.Model, TimeSpan.FromSeconds(10));
            InspectionText.Text = $"Endpoint phản hồi · Model sẵn sàng: {profile.Model}\nModels: " +
                (models.Count == 0 ? "server không trả danh sách" : string.Join(", ", models));
            return;
        }
        if (!_connections.TryGetConnection(profile.Id, out GpuConnectionHandle? handle) || handle is null)
            throw new InvalidOperationException("Hãy kết nối SSH trước khi kiểm tra server.");
        RemoteGpuInspection result = await _setup.InspectAsync(handle.Session);
        InspectionText.Text = $"{result.OperatingSystem}\n{result.GpuName ?? "Không đọc được GPU"} · " +
            $"VRAM {(result.VramBytes ?? 0) / 1024d / 1024d / 1024d:0.0} GB · Trống {(result.FreeDiskBytes ?? 0) / 1024d / 1024d / 1024d:0.0} GB\n" +
            $"Ollama: {(result.OllamaInstalled ? "đã cài" : "chưa cài")} · Service: {(result.OllamaServiceActive ? "active" : "inactive")}\n" +
            $"Models: {(result.Models.Count == 0 ? "chưa có" : string.Join(", ", result.Models))}";
    });

    private async void OnInstallOllama(object sender, RoutedEventArgs e) => await ConfirmSetupAsync(RemoteSetupAction.InstallOllamaUbuntu,
        "Cài Ollama bằng script chính thức trên Ubuntu? QT Studio sẽ không cài hoặc nâng cấp driver NVIDIA.");
    private async void OnStartOllama(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is OllamaBackendProfile { Kind: OllamaBackendKind.Local } local)
        { await RunAsync(async () => StatusText.Text = (await _local.StartAsync(local.Model)).Message); return; }
        await ConfirmSetupAsync(RemoteSetupAction.StartOllamaService,
            "Bật Ollama service trên GPU thuê? Nếu cần quyền sudo, QT Studio sẽ hỏi mật khẩu ngay tại đây.");
    }
    private async void OnPullModel(object sender, RoutedEventArgs e) => await ConfirmSetupAsync(RemoteSetupAction.PullModel,
        $"Tải model “{ModelInput.Text}” qua SSH tunnel? QT Studio sẽ hiển thị dung lượng Ollama báo về; tính phí mạng do nhà cung cấp quyết định.", ModelInput.Text);
    private void OnCancelSetup(object sender, RoutedEventArgs e)
    {
        _setupCancellation?.Cancel();
        StatusText.Text = "Đang yêu cầu hủy thao tác Remote…";
    }

    private async Task ConfirmSetupAsync(RemoteSetupAction action, string message, string? model = null)
    {
        if (MessageBox.Show(this, message, "Xác nhận thao tác Remote GPU", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (_busy) return;
        _busy = true;
        CancelSetupButton.IsEnabled = true;
        ModelPullProgress.Visibility = action == RemoteSetupAction.PullModel ? Visibility.Visible : Visibility.Collapsed;
        ModelPullProgress.IsIndeterminate = action == RemoteSetupAction.PullModel;
        ModelPullProgress.Value = 0;
        try
        {
            if (ProfileList.SelectedItem is not OllamaBackendProfile profile ||
                !_connections.TryGetConnection(profile.Id, out GpuConnectionHandle? handle) || handle is null)
                throw new InvalidOperationException("Hãy kết nối SSH trước.");
            using var cancellation = new CancellationTokenSource();
            _setupCancellation = cancellation;
            StatusText.Text = action == RemoteSetupAction.PullModel ? "Đang tải model trên Remote; có thể hủy…" : "Đang thực hiện thao tác Remote…";
            try
            {
                if (action == RemoteSetupAction.PullModel)
                {
                    var progress = new Progress<OllamaModelPullProgress>(UpdateModelPullProgress);
                    await _modelPull.PullAsync(handle.TunnelEndpoint, model ?? "", progress, cancellation.Token);
                    ModelPullProgress.IsIndeterminate = false;
                    ModelPullProgress.Value = 100;
                    StatusText.Text = "Tải model hoàn tất.";
                }
                else
                {
                    SshCommandResult result = await _setup.ExecuteConfirmedAsync(handle.Session, action, true, model, cancellation.Token);
                    if (!result.Success && result.Error.StartsWith("SUDO_PASSWORD_REQUIRED:", StringComparison.Ordinal))
                    {
                        string? password = RequestSudoPassword(profile);
                        if (password is null)
                        {
                            StatusText.Text = "Đã hủy nhập mật khẩu sudo."; return;
                        }
                        result = await _setup.ExecuteConfirmedAsync(handle.Session, action, true, model,
                            cancellation.Token, sudoPassword: password);
                    }
                    StatusText.Text = result.Success ? "Thao tác hoàn tất." : "Thao tác bị chặn/thất bại: " + result.Error;
                }
            }
            finally { if (ReferenceEquals(_setupCancellation, cancellation)) _setupCancellation = null; }
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã hủy thao tác Remote theo yêu cầu."; }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { CancelSetupButton.IsEnabled = false; _busy = false; }
    }

    private string? RequestSudoPassword(OllamaBackendProfile profile)
    {
        // Sudo is often configured with the same password as SSH. Do not persist a separate sudo secret.
        var window = new Window
        {
            Title = "Mật khẩu sudo · Ubuntu",
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            MinWidth = 380
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = "Ubuntu yêu cầu xác thực để cài/khởi động Ollama.\nDùng mật khẩu SSH hoặc nhập mật khẩu sudo riêng:",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var input = new PasswordBox { Margin = new Thickness(0, 0, 0, 12), MinWidth = 345 };
        // For password-based SSH login this is usually the sudo password too.
        if (profile.Ssh?.Authentication == SshAuthenticationKind.Password)
            input.Password = profile.Ssh.Password ?? "";
        stack.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Hủy", MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var confirm = new Button { Content = "Tiếp tục", MinWidth = 92, IsDefault = true };
        cancel.Click += (_, _) => window.DialogResult = false;
        confirm.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); stack.Children.Add(buttons);
        window.Content = stack;
        if (window.ShowDialog() != true) { input.Clear(); return null; }
        string password = input.Password;
        input.Clear();
        return password;
    }

    private void UpdateModelPullProgress(OllamaModelPullProgress progress)
    {
        if (progress.Percentage is double percentage)
        {
            ModelPullProgress.IsIndeterminate = false;
            ModelPullProgress.Value = percentage;
            StatusText.Text = $"{progress.Status} · {FormatBytes(progress.CompletedBytes ?? 0)} / " +
                $"{FormatBytes(progress.TotalBytes ?? 0)} ({percentage:0.0}%) · có thể hủy";
        }
        else StatusText.Text = progress.Status + " · đang chờ Ollama báo dung lượng · có thể hủy";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes); int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private void OnChoosePrivateKey(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SSH private key|id_*;*.pem;*.key|Tất cả tệp|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _loadedPrivateKey = File.ReadAllText(dialog.FileName);
        PrivateKeyInput.Password = "••••••••";
    }

    private OllamaBackendProfile ReadProfile()
    {
        OllamaBackendProfile original = ProfileList.SelectedItem as OllamaBackendProfile
            ?? throw new InvalidOperationException("Chưa chọn profile.");
        AiRuntimeProfile runtime = ReadRuntimeProfile();
        if (original.Kind == OllamaBackendKind.Local)
        {
            OllamaBackendProfile local = original.Clone();
            local.Name = NameInput.Text; local.Runtime = runtime; local.Model = runtime.ModelOptions.Model;
            local.Validate(); return local;
        }
        var profile = original.Clone(); profile.Name = NameInput.Text; profile.Runtime = runtime;
        profile.Runtime = runtime;
        profile.Model = runtime.ModelOptions.Model;
        profile.HourlyPrice = decimal.Parse(HourlyPriceInput.Text, CultureInfo.InvariantCulture);
        if (ConnectionTypeInput.SelectedIndex == 1)
        {
            profile.Endpoint = EndpointInput.Text.Trim();
            profile.Token = TokenInput.Password;
            profile.Ssh = null;
        }
        else
        {
            profile.Endpoint = "http://127.0.0.1:11434";
            profile.Token = null;
            profile.Ssh = new()
            {
                Host = HostInput.Text, Port = int.Parse(PortInput.Text, CultureInfo.InvariantCulture), Username = UsernameInput.Text,
                Authentication = AuthenticationInput.SelectedIndex == 1 ? SshAuthenticationKind.PrivateKey : SshAuthenticationKind.Password,
                Password = PasswordInput.Password, PrivateKey = _loadedPrivateKey,
                PrivateKeyPassphrase = PrivateKeyPassphraseInput.Password, HostKeyFingerprint = FingerprintInput.Text,
                RemoteOllamaPort = int.Parse(RemoteOllamaPortInput.Text, CultureInfo.InvariantCulture)
            };
        }
        profile.Validate(); return profile;
    }

    private AiRuntimeProfile ReadRuntimeProfile()
    {
        var model = new ModelOptions
        {
            Model = ModelInput.Text.Trim(),
            Temperature = ReadDouble(RuntimeTemperatureInput, "Temperature"),
            ContextSize = ReadInt(RuntimeContextInput, "Context"),
            MaximumOutputTokens = ReadInt(RuntimeOutputInput, "Output Tokens"),
            RequestTimeout = TimeSpan.FromSeconds(ReadInt(RuntimeTimeoutInput, "Timeout"))
        };
        var segmentation = new SegmenterOptions
        {
            MinimumHanCharacters = ReadInt(RuntimeMinimumInput, "Minimum Han"),
            TargetHanCharacters = ReadInt(RuntimeTargetInput, "Target Han"),
            MaximumHanCharacters = ReadInt(RuntimeMaximumInput, "Maximum Han"),
            MaximumTextElements = ReadInt(RuntimeTextElementsInput, "Maximum Text Elements")
        };
        var runtime = new AiRuntimeProfile { ModelOptions = model, SegmentationOptions = segmentation };
        runtime.Validate();
        return runtime;
    }

    private static int ReadInt(TextBox input, string name) =>
        int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : throw new ArgumentException($"{name} phải là số nguyên.");

    private static double ReadDouble(TextBox input, string name) =>
        double.TryParse(input.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value : throw new ArgumentException($"{name} phải là số.");

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { IsEnabled = true; _busy = false; }
    }
}
