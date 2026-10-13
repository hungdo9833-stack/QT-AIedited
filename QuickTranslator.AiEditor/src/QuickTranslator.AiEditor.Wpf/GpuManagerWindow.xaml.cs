using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class GpuManagerWindow : Window
{
    private readonly OllamaBackendManager _backends;
    private readonly GpuConnectionManager _connections;
    private readonly RemoteOllamaSetupService _setup = new();
    private readonly LocalOllamaService _local = new();
    private readonly ObservableCollection<OllamaBackendProfile> _profiles = [];
    private string? _loadedPrivateKey;
    private bool _busy;

    internal GpuManagerWindow(OllamaBackendManager backends, GpuConnectionManager connections)
    {
        _backends = backends; _connections = connections;
        InitializeComponent();
        ProfileList.ItemsSource = _profiles;
        RefreshProfiles();
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
        NameInput.Text = profile.Name; ModelInput.Text = profile.Model;
        bool remote = profile.Kind == OllamaBackendKind.Remote;
        HostInput.IsEnabled = PortInput.IsEnabled = UsernameInput.IsEnabled = AuthenticationInput.IsEnabled = remote;
        FingerprintInput.IsEnabled = RemoteOllamaPortInput.IsEnabled = HourlyPriceInput.IsEnabled = remote;
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
    }

    private void OnAuthenticationChanged(object sender, SelectionChangedEventArgs e) => ApplyAuthenticationUi();
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
            Endpoint = "http://127.0.0.1:11434", Model = "translategemma:12b", Ssh = new() };
        _profiles.Add(profile); ProfileList.SelectedItem = profile;
    }

    private async void OnSaveProfile(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OllamaBackendProfile profile = ReadProfile();
        if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted &&
            MessageBox.Show(this, "Remote GPU sẽ nhận prompt và nguyên văn segment. Xác nhận lưu profile Remote?",
                "Quyền gửi dữ liệu", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        profile.RemoteDisclosureAccepted = profile.Kind == OllamaBackendKind.Remote;
        await _backends.SaveProfileAsync(profile, activate: false);
        RefreshProfiles(profile.Id); StatusText.Text = "Đã lưu profile. Credential được bảo vệ bằng Windows DPAPI.";
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
        try
        {
            await _backends.SaveProfileAsync(profile, activate: false);
            GpuConnectionHandle handle = await _connections.ConnectAsync(profile);
            await _backends.AttachRuntimeClientAsync(profile.Id, handle.Client);
            await _backends.ActivateAsync(profile.Id);
            RefreshProfiles(profile.Id); StatusText.Text = "Đã kết nối SSH tunnel loopback. Profile đang sẵn sàng.";
        }
        catch (SshHostKeyVerificationException error)
        {
            FingerprintInput.Text = error.ObservedFingerprint;
            StatusText.Text = error.Message + " Fingerprint quan sát: " + error.ObservedFingerprint;
        }
    });

    private async void OnDisconnect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (ProfileList.SelectedItem is not OllamaBackendProfile profile) return;
        await _backends.DetachRuntimeClientAsync(profile.Id);
        await _connections.DisconnectAsync(profile.Id);
        StatusText.Text = "Đã ngắt tunnel. Máy thuê có thể vẫn tiếp tục tính phí.";
    });

    private async void OnInspect(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        OllamaBackendProfile profile = ReadProfile();
        if (profile.Kind == OllamaBackendKind.Local)
        {
            InspectionText.Text = (await _local.CheckAsync(profile.Model)).Message; return;
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
            "Bật Ollama service bằng sudo -n? Nếu server yêu cầu password, thao tác sẽ dừng an toàn.");
    }
    private async void OnPullModel(object sender, RoutedEventArgs e) => await ConfirmSetupAsync(RemoteSetupAction.PullModel,
        $"Tải model “{ModelInput.Text}” trên server? Dung lượng và tính phí mạng do nhà cung cấp quyết định.", ModelInput.Text);

    private async Task ConfirmSetupAsync(RemoteSetupAction action, string message, string? model = null)
    {
        if (MessageBox.Show(this, message, "Xác nhận thao tác Remote GPU", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunAsync(async () =>
        {
            if (ProfileList.SelectedItem is not OllamaBackendProfile profile ||
                !_connections.TryGetConnection(profile.Id, out GpuConnectionHandle? handle) || handle is null)
                throw new InvalidOperationException("Hãy kết nối SSH trước.");
            SshCommandResult result = await _setup.ExecuteConfirmedAsync(handle.Session, action, true, model);
            StatusText.Text = result.Success ? "Thao tác hoàn tất." : "Thao tác bị chặn/thất bại: " + result.Error;
        });
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
        if (original.Kind == OllamaBackendKind.Local)
        {
            OllamaBackendProfile local = original.Clone(); local.Name = NameInput.Text; local.Model = ModelInput.Text; local.Validate(); return local;
        }
        var profile = original.Clone(); profile.Name = NameInput.Text; profile.Model = ModelInput.Text;
        profile.Endpoint = "http://127.0.0.1:11434"; profile.HourlyPrice = decimal.Parse(HourlyPriceInput.Text, CultureInfo.InvariantCulture);
        profile.Ssh = new()
        {
            Host = HostInput.Text, Port = int.Parse(PortInput.Text, CultureInfo.InvariantCulture), Username = UsernameInput.Text,
            Authentication = AuthenticationInput.SelectedIndex == 1 ? SshAuthenticationKind.PrivateKey : SshAuthenticationKind.Password,
            Password = PasswordInput.Password, PrivateKey = _loadedPrivateKey,
            PrivateKeyPassphrase = PrivateKeyPassphraseInput.Password, HostKeyFingerprint = FingerprintInput.Text,
            RemoteOllamaPort = int.Parse(RemoteOllamaPortInput.Text, CultureInfo.InvariantCulture)
        };
        profile.Validate(); return profile;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return; _busy = true; IsEnabled = false;
        try { await action(); }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { IsEnabled = true; _busy = false; }
    }
}
