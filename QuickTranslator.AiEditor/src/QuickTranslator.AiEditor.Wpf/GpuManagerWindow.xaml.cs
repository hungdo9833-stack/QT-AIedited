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
    private readonly RemoteHostProbe _probe = new();
    private readonly RemoteOllamaAutoSetup _autoSetup = new();
    private int? _rdpPort;
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
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(StatusText, (_, _) => Accessibility.Announce(StatusText));
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
        DownloadModelInput.Text = runtime.ModelOptions.Model;
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
            _rdpPort = ssh.RdpPort;
        }
        else
        {
            HostInput.Text = ""; PortInput.Text = "22"; UsernameInput.Text = "ubuntu"; AuthenticationInput.SelectedIndex = 0;
            PasswordInput.Password = PrivateKeyInput.Password = PrivateKeyPassphraseInput.Password = ""; _loadedPrivateKey = null;
            FingerprintInput.Text = ""; RemoteOllamaPortInput.Text = "11434";
            _rdpPort = null;
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
        // A new rental is set up by typing its address, account and password, so show those fields right away.
        AdvancedConnectionExpander.IsExpanded = true;
        HostInput.Focus();
    }

    private async void OnSaveProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OllamaBackendProfile profile = ReadProfile();
        if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted &&
            ThemedDialog.Show(this, "Remote GPU sẽ nhận prompt và nguyên văn segment. Xác nhận lưu profile Remote?",
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
        if (ThemedDialog.Show(this, $"Xóa profile “{profile.Name}”?", "Xóa profile",
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
        await ConnectSshAsync(profile);
    });

    /// <summary>Opens the SSH tunnel for the profile, asking once to trust a new host key. Returns whether it is connected.</summary>
    private async Task<bool> ConnectSshAsync(OllamaBackendProfile profile, bool trustNewHostKey = false)
    {
        try
        {
            await _backends.SaveProfileAsync(profile, activate: false);
            GpuConnectionHandle handle = await _connections.ConnectAsync(profile);
            await _backends.AttachRuntimeClientAsync(profile.Id, handle.Client);
            await _backends.ActivateAsync(profile.Id);
            _preferences.RemoteProfileId = profile.Id;
            RefreshProfiles(profile.Id); StatusText.Text = "Đã kết nối SSH tunnel loopback. Profile đang sẵn sàng.";
            return true;
        }
        catch (SshHostKeyVerificationException error)
        {
            FingerprintInput.Text = error.ObservedFingerprint;
            StatusText.Text = error.Message + " Fingerprint quan sát: " + error.ObservedFingerprint;
            // The SSH server on this machine was created by QT Studio's own setup a moment ago, so its first key is trusted.
            if (trustNewHostKey || IsVisible && ThemedDialog.Show(this,
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
                return true;
            }
            return false;
        }
    }

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
        InspectionText.Text = DescribeInspection(await _setup.InspectAsync(handle.Session));
    });

    private static string DescribeInspection(RemoteGpuInspection result) =>
        $"{result.OperatingSystem}\n{result.GpuName ?? "Không đọc được GPU"} · " +
        $"VRAM {(result.VramBytes ?? 0) / 1024d / 1024d / 1024d:0.0} GB · Trống {(result.FreeDiskBytes ?? 0) / 1024d / 1024d / 1024d:0.0} GB\n" +
        $"Ollama: {(result.OllamaInstalled ? "đã cài" : "chưa cài")} · Service: {(result.OllamaServiceActive ? "active" : "inactive")}\n" +
        $"Models: {(result.Models.Count == 0 ? "chưa có" : string.Join(", ", result.Models))}" +
        (result.BlockedReason is null ? "" : "\n" + result.BlockedReason);

    private void OnImportRdp(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Remote Desktop|*.rdp|Tất cả tệp|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try { ApplyRdpFile(RdpConnectionFile.Load(dialog.FileName)); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        { StatusText.Text = error.Message; }
    }

    /// <summary>Fills the connection fields from the provider's .rdp file; the password still has to be typed.</summary>
    internal void ApplyRdpFile(RdpConnectionFile file)
    {
        if (ProfileList.SelectedItem is not OllamaBackendProfile { Kind: OllamaBackendKind.Remote })
        { StatusText.Text = "Hãy chọn hoặc thêm một profile GPU Remote trước khi nhập file RDP."; return; }
        // A different machine has a different host key, so the pinned fingerprint must not carry over.
        if (!string.Equals(HostInput.Text.Trim(), file.Host, StringComparison.OrdinalIgnoreCase)) FingerprintInput.Text = "";
        ConnectionTypeInput.SelectedIndex = 0;
        HostInput.Text = file.Host;
        // Providers that forward only this port get SSH on it too (see WindowsPortMux), so it is also the SSH port.
        PortInput.Text = file.Port.ToString(CultureInfo.InvariantCulture);
        UsernameInput.Text = file.SshUsername ?? "Administrator";
        AuthenticationInput.SelectedIndex = 0;
        _rdpPort = file.Port;
        AdvancedConnectionExpander.IsExpanded = true;
        StatusText.Text = $"Đã đọc file RDP: {file.Host} (RDP cổng {file.Port}), tài khoản {UsernameInput.Text}. " +
            "Nhập mật khẩu quản trị rồi bấm “Tự nhận diện & cài tự động”." +
            (file.GatewayHost is null ? "" : $" Lưu ý: file dùng RD Gateway ({file.GatewayHost}); QT Studio không đi qua gateway nên cổng SSH phải truy cập trực tiếp được.");
    }

    private async void OnAutoSetup(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        AutoSetupButton.IsEnabled = false;
        CancelSetupButton.IsEnabled = true;
        ModelPullProgress.Visibility = Visibility.Visible;
        ModelPullProgress.IsIndeterminate = true;
        using var cancellation = new CancellationTokenSource();
        _setupCancellation = cancellation;
        try
        {
            OllamaBackendProfile profile = ReadProfile();
            if (profile.Kind != OllamaBackendKind.Remote || profile.Ssh is null)
            { StatusText.Text = "Cài đặt tự động dành cho profile GPU Remote qua SSH."; return; }
            string model = profile.Model;
            SshReadiness readiness = await EnsureSshReachableAsync(profile, cancellation.Token);
            if (readiness == SshReadiness.Failed) return;
            // A machine QT Studio just prepared was confirmed in one dialog already, and its SSH server is our own.
            if (readiness == SshReadiness.AlreadyAvailable && ThemedDialog.Show(this,
                    $"QT Studio sẽ kết nối SSH tới {profile.Ssh.Host}, nhận diện Ubuntu hoặc Windows, cài Ollama nếu máy chưa có và tải model “{model}”.\n\n" +
                    "Không cài hay nâng cấp driver NVIDIA. Nhà cung cấp có thể tính phí mạng và thời gian thuê trong lúc cài. Tiếp tục?",
                    "Xác nhận cài đặt tự động", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            profile = ReadProfile(); // The port may have been corrected while checking.
            StatusText.Text = "Đang kết nối SSH…";
            if (!await ConnectSshAsync(profile, trustNewHostKey: readiness == SshReadiness.Bootstrapped)) return;
            if (!_connections.TryGetConnection(profile.Id, out GpuConnectionHandle? handle) || handle is null)
            { StatusText.Text = "Chưa có kết nối SSH."; return; }

            var progress = new Progress<AutoSetupProgress>(UpdateAutoSetupProgress);
            AutoSetupResult result = await _autoSetup.RunAsync(handle.Session, handle.TunnelEndpoint, model,
                () => Dispatcher.Invoke(() => RequestSudoPassword(profile)), progress, cancellation.Token);
            if (result.Inspection is not null) InspectionText.Text = DescribeInspection(result.Inspection);
            ModelPullProgress.IsIndeterminate = false;
            ModelPullProgress.Value = result.Success ? 100 : ModelPullProgress.Value;
            StatusText.Text = result.Message;
        }
        catch (OperationCanceledException) { StatusText.Text = "Đã hủy cài đặt tự động. Máy thuê giữ nguyên những gì đã cài xong."; }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally
        {
            if (ReferenceEquals(_setupCancellation, cancellation)) _setupCancellation = null;
            AutoSetupButton.IsEnabled = true; CancelSetupButton.IsEnabled = false; _busy = false;
        }
    }

    private void UpdateAutoSetupProgress(AutoSetupProgress step)
    {
        if (step.Percent is double percent)
        {
            ModelPullProgress.IsIndeterminate = false;
            ModelPullProgress.Value = percent;
        }
        else ModelPullProgress.IsIndeterminate = true;
        StatusText.Text = step.Message + " · có thể hủy";
    }

    /// <summary>
    /// Makes sure SSH answers. A Windows rental that only forwards the Remote Desktop port is prepared without any
    /// manual step: QT Studio signs in over Remote Desktop with the account the user typed, and the session runs the
    /// setup command from the clipboard. It puts SSH on that same public port next to Remote Desktop, and this check
    /// continues by itself once SSH answers there.
    /// </summary>
    private async Task<SshReadiness> EnsureSshReachableAsync(OllamaBackendProfile profile, CancellationToken cancellationToken)
    {
        GpuSshSettings ssh = profile.Ssh!;
        int rdpPort = ssh.RdpPort ?? RdpConnectionFile.DefaultPort;
        StatusText.Text = $"Đang nhận diện {ssh.Host}:{ssh.Port}…";
        RemoteHostProbeResult probe = await _probe.ProbeAsync(ssh.Host, ssh.Port, cancellationToken);
        if (probe.Kind == RemoteEndpointKind.Ssh) { StatusText.Text = probe.Message; return SshReadiness.AlreadyAvailable; }
        if (probe.Kind == RemoteEndpointKind.Rdp) rdpPort = ssh.Port; // The Remote Desktop port was typed as the SSH port.
        else
        {
            if (rdpPort == ssh.Port) { StatusText.Text = probe.Message; return SshReadiness.Failed; }
            RemoteHostProbeResult rdp = await _probe.ProbeAsync(ssh.Host, rdpPort, cancellationToken);
            if (rdp.Kind == RemoteEndpointKind.Ssh)
            { PortInput.Text = rdpPort.ToString(CultureInfo.InvariantCulture); return SshReadiness.AlreadyAvailable; }
            if (rdp.Kind != RemoteEndpointKind.Rdp)
            { StatusText.Text = probe.Message + $" Cổng RDP {rdpPort}: {rdp.Message}"; return SshReadiness.Failed; }
        }
        // SSH will share the public Remote Desktop port, so that is the SSH port from now on.
        _rdpPort = rdpPort; PortInput.Text = rdpPort.ToString(CultureInfo.InvariantCulture);

        // The only confirmation of the whole flow: everything below happens without further questions.
        if (ThemedDialog.Show(this,
                $"{ssh.Host} chỉ mở cổng Remote Desktop {rdpPort}. QT Studio sẽ tự làm tiếp, bạn không cần thao tác trên máy thuê:\n\n" +
                "1. Mở cửa sổ Remote Desktop, đăng nhập bằng tài khoản bạn vừa nhập và chạy lệnh cài đặt (lệnh cũng được chép vào clipboard). " +
                "Nếu Windows hỏi “Unknown publisher” hoặc quyền (UAC), bấm Connect hoặc Yes. Đừng đóng cửa sổ và đừng sao chép gì khác trong lúc này.\n" +
                $"2. Bật OpenSSH Server (chỉ nghe nội bộ) và chia cổng {rdpPort} cho cả Remote Desktop lẫn SSH. Remote Desktop ngắt vài giây. " +
                "Nếu có lỗi hoặc QT Studio không xác nhận được SSH trong 12 phút, máy tự trả cổng lại cho Remote Desktop.\n" +
                $"3. Nhận diện GPU, cài Ollama nếu thiếu và tải model “{profile.Model}”. Không cài hay nâng cấp driver NVIDIA.\n\n" +
                "Nhà cung cấp tính phí theo giờ trong lúc cài. Sau bước này tài khoản quản trị cũng đăng nhập được bằng SSH qua cổng " +
                $"{rdpPort}. Tiếp tục?",
                "Cài đặt tự động trên máy Windows", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { StatusText.Text = "Đã hủy."; return SshReadiness.Failed; }

        string command = WindowsPortMux.BuildBootstrapCommand();
        try { Clipboard.SetDataObject(command, true); }
        catch (System.Runtime.InteropServices.ExternalException)
        { StatusText.Text = "Không sao chép được vào clipboard. Đóng ứng dụng đang giữ clipboard rồi thử lại."; return SshReadiness.Failed; }

        string rdpFile = Path.Combine(Path.GetTempPath(), "qtstudio-" + Guid.NewGuid().ToString("N") + ".rdp");
        System.Diagnostics.Process? remoteDesktop = null;
        try
        {
            File.WriteAllText(rdpFile, RdpLaunchFile.Build(ssh.Host, rdpPort, ssh.Username, ssh.Password ?? ""), System.Text.Encoding.ASCII);
            try { remoteDesktop = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mstsc.exe", [rdpFile])); }
            catch (System.ComponentModel.Win32Exception)
            { StatusText.Text = "Không mở được Remote Desktop (mstsc.exe)."; return SshReadiness.Failed; }

            // The same steps a person would take in that window: confirm, open an administrator PowerShell, paste.
            string? stopped;
            try { stopped = await RemoteDesktopAutomation.RunAsync(remoteDesktop!, text => StatusText.Text = text + " · có thể hủy", cancellationToken); }
            catch (InvalidOperationException error) { stopped = error.Message; }
            TryDelete(rdpFile);

            DateTime started = DateTime.UtcNow, deadline = started.AddMinutes(15);
            const string manual = " Hãy tự mở PowerShell bằng Run as administrator trong cửa sổ Remote Desktop, nhấn Ctrl+V rồi Enter (lệnh đang ở clipboard).";
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan elapsed = DateTime.UtcNow - started;
                StatusText.Text = stopped is not null
                    ? stopped + manual + $" · đang chờ SSH ở cổng {rdpPort} · có thể hủy"
                    : elapsed < TimeSpan.FromSeconds(120)
                        ? $"Đã gửi lệnh cài đặt vào máy thuê, đang chờ SSH ở cổng {rdpPort}… · có thể hủy"
                        : "Chưa thấy SSH." + manual + $" · đang chờ cổng {rdpPort} · có thể hủy";
                RemoteHostProbeResult check = await _probe.ProbeAsync(ssh.Host, rdpPort, cancellationToken);
                if (check.Kind == RemoteEndpointKind.Ssh) { StatusText.Text = "Đã thấy SSH trên máy thuê."; return SshReadiness.Bootstrapped; }
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            StatusText.Text = $"Chưa thấy SSH ở cổng {rdpPort} sau 15 phút. Xem C:\\ProgramData\\QTStudio\\logs\\mux-switch.log trên máy thuê.";
            return SshReadiness.Failed;
        }
        finally
        {
            TryDelete(rdpFile);
            try { if (remoteDesktop is { HasExited: false }) remoteDesktop.Kill(); } catch (InvalidOperationException) { }
            remoteDesktop?.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private enum SshReadiness { Failed, AlreadyAvailable, Bootstrapped }

    private async void OnInstallOllama(object sender, RoutedEventArgs e) => await ConfirmSetupAsync(RemoteSetupAction.InstallOllamaUbuntu,
        "Cài Ollama bằng script chính thức trên Ubuntu? QT Studio sẽ không cài hoặc nâng cấp driver NVIDIA.");
    private async void OnStartOllama(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is OllamaBackendProfile { Kind: OllamaBackendKind.Local } local)
        { await RunAsync(async () => StatusText.Text = (await _local.StartAsync(local.Model)).Message); return; }
        await ConfirmSetupAsync(RemoteSetupAction.StartOllamaService,
            "Bật Ollama service trên GPU thuê? Nếu cần quyền sudo, QT Studio sẽ hỏi mật khẩu ngay tại đây.");
    }
    private async void OnPullModel(object sender, RoutedEventArgs e)
    {
        string model = DownloadModelInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            StatusText.Text = "Hãy nhập tên model Ollama cần tải."; return;
        }
        await ConfirmSetupAsync(RemoteSetupAction.PullModel,
            $"Tải model “{model}” qua SSH tunnel? Dung lượng tải sẽ được hiển thị; nhà cung cấp có thể tính phí mạng.",
            model);
    }
    private void OnCancelSetup(object sender, RoutedEventArgs e)
    {
        _setupCancellation?.Cancel();
        StatusText.Text = "Đang yêu cầu hủy thao tác Remote…";
    }

    private async Task ConfirmSetupAsync(RemoteSetupAction action, string message, string? model = null)
    {
        if (ThemedDialog.Show(this, message, "Xác nhận thao tác Remote GPU", MessageBoxButton.YesNo,
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
            MinWidth = 420
        };
        window.SetResourceReference(BackgroundProperty, "PanelBrush");
        window.SetResourceReference(ForegroundProperty, "TextBrush");
        var stack = new StackPanel { Margin = new Thickness(24) };
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
        var cancel = new Button { Content = "Hủy", MinWidth = 76, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var confirm = new Button { Content = "Tiếp tục", MinWidth = 92, IsDefault = true };
        confirm.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        cancel.Click += (_, _) => window.DialogResult = false;
        confirm.Click += (_, _) => window.DialogResult = true;
        var root = new DockPanel();
        Border footer = ThemedDialog.Footer(confirm, cancel);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(stack);
        window.Content = root;
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
        // The provider's panel shows "address:port" as one text, so accept it pasted into the address box.
        (string host, int? pastedPort) = RemoteAddress.Split(HostInput.Text);
        if (pastedPort is int port) { HostInput.Text = host; PortInput.Text = port.ToString(CultureInfo.InvariantCulture); }
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
                RemoteOllamaPort = int.Parse(RemoteOllamaPortInput.Text, CultureInfo.InvariantCulture),
                RdpPort = _rdpPort
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
