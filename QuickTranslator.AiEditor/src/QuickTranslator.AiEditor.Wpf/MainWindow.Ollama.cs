using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private GpuManagerWindow? _gpuManagerWindow;
    private readonly ObservableCollection<OllamaProfileItem> _ollamaProfileItems = [];
    private Task? _ollamaLoadTask;
    private bool _ollamaProfilesReady;
    private bool _syncingOllamaProfile;
    private Guid? _selectedOllamaProfileId;
    private bool _projectOllamaProfileMissing;

    private Guid SelectedOllamaProfileId => _selectedOllamaProfileId ?? _ollamaBackends.ActiveProfile.Id;

    private void InitializeOllamaBackendUi()
    {
        OllamaProfileInput.ItemsSource = _ollamaProfileItems;
        OllamaProfileInput.SelectionChanged += OnOllamaProfileSelected;
        OllamaBackendKindInput.SelectionChanged += OnOllamaBackendKindChanged;
        OllamaProfileNameInput.TextChanged += OnSettingsInputChanged;
        OllamaEndpointInput.TextChanged += OnSettingsInputChanged;
        OllamaTokenInput.PasswordChanged += OnSettingsInputChanged;
        ModelInput.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnSettingsInputChanged));
        _ollamaBackends.ActiveProfileChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RefreshOllamaProfileList(_ollamaBackends.ActiveProfile.Id);
            UpdateOllamaBackendStatus();
        });
    }

    private async void OnOpenGpuManager(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureOllamaBackendsLoadedAsync();
            if (_gpuManagerWindow is { IsLoaded: true }) { _gpuManagerWindow.Activate(); return; }
            _gpuManagerWindow = new GpuManagerWindow(_ollamaBackends, _gpuConnections, _gpuPreferences) { Owner = this };
            _gpuManagerWindow.Closed += (_, _) => _gpuManagerWindow = null;
            _gpuManagerWindow.Show();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task EnsureOllamaBackendsLoadedAsync()
    {
        _ollamaLoadTask ??= _ollamaBackends.LoadAsync();
        await _ollamaLoadTask;
        if (_ollamaProfilesReady) return;
        _ollamaProfilesReady = true;
        RefreshOllamaProfileList(_ollamaBackends.ActiveProfile.Id);
        UpdateOllamaBackendStatus();
    }

    private async Task PrepareOllamaProfileForProjectAsync(Guid? profileId)
    {
        await EnsureOllamaBackendsLoadedAsync();
        Guid requested = profileId ?? OllamaBackendProfile.DefaultLocalId;
        _selectedOllamaProfileId = requested;
        OllamaBackendProfile profile;
        try { profile = _ollamaBackends.GetProfile(requested); }
        catch (InvalidOperationException)
        {
            _projectOllamaProfileMissing = true;
            RefreshOllamaProfileList(requested);
            UpdateOllamaBackendStatus();
            return;
        }
        if (_ollamaBackends.ActiveProfile.Id != profile.Id)
            await _ollamaBackends.ActivateAsync(profile.Id);
        _projectOllamaProfileMissing = false;
        RefreshOllamaProfileList(requested);
        UpdateOllamaBackendStatus();
    }

    private void SelectOllamaProfileInUi(Guid? profileId)
    {
        Guid requested = profileId ?? OllamaBackendProfile.DefaultLocalId;
        _selectedOllamaProfileId = requested;
        if (!_ollamaProfilesReady) return;
        OllamaProfileInput.SelectedItem = _ollamaProfileItems.FirstOrDefault(item => item.Id == requested);
        if (OllamaProfileInput.SelectedItem is null) _projectOllamaProfileMissing = true;
        UpdateOllamaBackendStatus();
    }

    private void RefreshOllamaProfileList(Guid? selectedId = null)
    {
        if (!_ollamaProfilesReady || OllamaProfileInput is null) return;
        _syncingOllamaProfile = true;
        try
        {
            Guid requested = selectedId ?? _selectedOllamaProfileId ?? _ollamaBackends.ActiveProfile.Id;
            _ollamaProfileItems.Clear();
            foreach (OllamaBackendProfile profile in _ollamaBackends.Profiles)
                _ollamaProfileItems.Add(new(profile.Id,
                    $"{profile.Name} · {(profile.Kind == OllamaBackendKind.Local ? "Local" : "Remote")}"));
            OllamaProfileInput.SelectedItem = _ollamaProfileItems.FirstOrDefault(item => item.Id == requested)
                ?? _ollamaProfileItems.FirstOrDefault(item => item.Id == _ollamaBackends.ActiveProfile.Id);
            if (OllamaProfileInput.SelectedItem is OllamaProfileItem selected)
                LoadOllamaProfileEditor(_ollamaBackends.GetProfile(selected.Id));
        }
        finally { _syncingOllamaProfile = false; }
    }

    private void OnOllamaProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingOllamaProfile || OllamaProfileInput.SelectedItem is not OllamaProfileItem selected) return;
        LoadOllamaProfileEditor(_ollamaBackends.GetProfile(selected.Id));
    }

    private void LoadOllamaProfileEditor(OllamaBackendProfile profile)
    {
        _syncingOllamaProfile = true;
        try
        {
            OllamaProfileNameInput.Text = profile.Name;
            OllamaBackendKindInput.SelectedIndex = profile.Kind == OllamaBackendKind.Local ? 0 : 1;
            OllamaEndpointInput.Text = profile.Endpoint;
            OllamaTokenInput.Password = profile.Token ?? "";
            AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
            ModelInput.Text = runtime.ModelOptions.Model;
            TemperatureInput.Text = runtime.ModelOptions.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ContextInput.Text = runtime.ModelOptions.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            OutputTokensInput.Text = runtime.ModelOptions.MaximumOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TimeoutInput.Text = runtime.ModelOptions.RequestTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            MinimumInput.Text = runtime.SegmentationOptions.MinimumHanCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TargetInput.Text = runtime.SegmentationOptions.TargetHanCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
            MaximumInput.Text = runtime.SegmentationOptions.MaximumHanCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApplyOllamaKindUi(profile.Kind);
            OllamaConnectionStatus.Text = profile.Id == _ollamaBackends.ActiveProfile.Id ? "Đang sử dụng" : "Đã lưu";
            OllamaConnectionStatus.Foreground = (Brush)FindResource("MutedBrush");
        }
        finally { _syncingOllamaProfile = false; }
    }

    private void OnOllamaBackendKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingOllamaProfile) return;
        OllamaBackendKind kind = SelectedOllamaKind();
        if (kind == OllamaBackendKind.Local)
        {
            OllamaEndpointInput.Text = OllamaClient.DefaultEndpoint.ToString();
            OllamaTokenInput.Password = "";
        }
        else if (OllamaEndpointInput.Text.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            OllamaEndpointInput.Text = "https://";
        ApplyOllamaKindUi(kind);
        OnSettingsInputChanged(sender, e);
    }

    private void ApplyOllamaKindUi(OllamaBackendKind kind)
    {
        bool remote = kind == OllamaBackendKind.Remote;
        OllamaEndpointInput.IsReadOnly = !remote;
        OllamaTokenInput.IsEnabled = remote;
        OllamaRemotePrivacyText.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
    }

    private OllamaBackendKind SelectedOllamaKind() => OllamaBackendKindInput.SelectedIndex == 1
        ? OllamaBackendKind.Remote : OllamaBackendKind.Local;

    private OllamaBackendProfile ReadOllamaProfileEditor()
    {
        OllamaBackendProfile? original = OllamaProfileInput.SelectedItem is OllamaProfileItem selected
            ? _ollamaBackends.GetProfile(selected.Id) : null;
        OllamaBackendProfile profile = original?.Clone() ?? new OllamaBackendProfile { Id = Guid.NewGuid() };
        profile.Name = OllamaProfileNameInput.Text;
        profile.Kind = SelectedOllamaKind();
        profile.Endpoint = profile.Kind == OllamaBackendKind.Local
            ? OllamaClient.DefaultEndpoint.ToString() : OllamaEndpointInput.Text.Trim();
        profile.Token = profile.Kind == OllamaBackendKind.Remote ? OllamaTokenInput.Password : null;
        profile.Runtime = new AiRuntimeProfile
        {
            ModelOptions = ReadModelOptions(),
            SegmentationOptions = ReadSegmenterOptions()
        };
        profile.Model = profile.Runtime.ModelOptions.Model;
        profile.Validate();
        // Consent belongs to one concrete remote destination. Editing a saved
        // profile to point at another server must show the disclosure again.
        if (profile.Kind == OllamaBackendKind.Remote && (original?.Kind != OllamaBackendKind.Remote ||
            !string.Equals(original.Endpoint, profile.Endpoint, StringComparison.OrdinalIgnoreCase)))
            profile.RemoteDisclosureAccepted = false;
        return profile;
    }

    private void OnNewOllamaProfile(object sender, RoutedEventArgs e)
    {
        _syncingOllamaProfile = true;
        try
        {
            OllamaProfileInput.SelectedItem = null;
            OllamaProfileNameInput.Text = "GPU Remote mới";
            OllamaBackendKindInput.SelectedIndex = 1;
            OllamaEndpointInput.Text = "https://";
            OllamaTokenInput.Password = "";
            ModelInput.Text = "translategemma:12b";
            TemperatureInput.Text = "0.1"; ContextInput.Text = "4096"; OutputTokensInput.Text = "2048";
            TimeoutInput.Text = "300"; MinimumInput.Text = "350"; TargetInput.Text = "550"; MaximumInput.Text = "750";
            ApplyOllamaKindUi(OllamaBackendKind.Remote);
            OllamaConnectionStatus.Text = "Profile mới chưa lưu";
        }
        finally { _syncingOllamaProfile = false; }
    }

    private async void OnSaveOllamaProfile(object sender, RoutedEventArgs e)
    {
        if (WorkActive || _activeRequest is { IsCompleted: false } || _projectTranslationCoordinator?.IsRunning == true)
        {
            SetStatus("Không đổi backend khi đang dịch. Hãy Pause queue và chờ checkpoint hoàn tất trước.");
            return;
        }
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            OllamaBackendProfile profile = ReadOllamaProfileEditor();
            if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted)
            {
                MessageBoxResult confirmed = MessageBox.Show(this,
                    $"QT Studio sẽ gửi prompt và nguyên văn segment tới server remote “{profile.Name}” tại {profile.Endpoint}. " +
                    "Token chỉ lưu mã hóa bằng Windows DPAPI và không nằm trong Project. Tiếp tục?",
                    "Xác nhận gửi nội dung tới Remote Ollama", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmed != MessageBoxResult.Yes) return;
                profile.RemoteDisclosureAccepted = true;
            }
            ProjectTranslationJob? job = _projectTranslationCoordinator?.CurrentJob;
            OllamaBackendSnapshot proposed = BackendSnapshot(profile, profile.Model);
            if (job is not null && job.Status is not (ProjectTranslationJobStatus.Completed or
                ProjectTranslationJobStatus.CompletedWithErrors or ProjectTranslationJobStatus.Cancelled) &&
                job.Backend is not null && !SameBackend(job.Backend, proposed) &&
                MessageBox.Show(this,
                    "Queue hiện tại đã chốt backend/model khác. Sau khi đổi, job đó sẽ không Resume cho tới khi bạn tạo Queue Plan mới. Đổi backend?",
                    "Queue cần lập lại kế hoạch", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await _ollamaBackends.SaveProfileAsync(profile, activate: true);
            _selectedOllamaProfileId = profile.Id;
            _projectOllamaProfileMissing = false;
            RefreshOllamaProfileList(profile.Id);
            if (_projectSession is not null) await SaveProjectAssetsAsync();
            _settingsDirty = false;
            UpdateOllamaBackendStatus();
            SetStatus($"Đã lưu và chuyển sang {BackendLabel(profile)}. Request mới sẽ dùng backend này.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async Task SaveRuntimeProfileEditorAsync()
    {
        if (!_ollamaProfilesReady || OllamaProfileInput.SelectedItem is not OllamaProfileItem) return;
        OllamaBackendProfile profile = ReadOllamaProfileEditor();
        if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted)
            throw new InvalidOperationException("Kết nối Remote đã thay đổi hoặc chưa được xác nhận. Hãy dùng “Lưu & sử dụng” để xem và xác nhận nơi nhận nguyên văn.");
        bool active = _ollamaBackends.ActiveProfile.Id == profile.Id;
        await _ollamaBackends.SaveProfileAsync(profile, activate: active);
        RefreshOllamaProfileList(profile.Id);
    }

    private async void OnRefreshOllamaModels(object sender, RoutedEventArgs e) => await TestOllamaProfileAsync(refreshModels: true);

    private async void OnTestOllamaConnection(object sender, RoutedEventArgs e) => await TestOllamaProfileAsync(refreshModels: false);

    private async Task TestOllamaProfileAsync(bool refreshModels)
    {
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        OllamaConnectionStatus.Text = refreshModels ? "Đang đọc /api/tags…" : "Đang kiểm tra…";
        OllamaConnectionStatus.Foreground = (Brush)FindResource("MutedBrush");
        try
        {
            OllamaBackendProfile profile = ReadOllamaProfileEditor();
            using IOllamaBackendClient client = OllamaClient.Create(profile);
            IReadOnlyList<string> models = await client.ListModelsAsync(TimeSpan.FromSeconds(10));
            ModelInput.ItemsSource = models;
            if (!refreshModels)
                await client.CheckConnectionAsync(ModelInput.Text, TimeSpan.FromSeconds(10));
            OllamaConnectionStatus.Text = refreshModels
                ? $"Đã tìm thấy {models.Count:N0} model cục bộ trên server"
                : "Đã kết nối · model sẵn sàng";
            OllamaConnectionStatus.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            OllamaConnectionStatus.Text = "Không sẵn sàng";
            OllamaConnectionStatus.Foreground = (Brush)FindResource("DangerBrush");
            ShowError(ex);
        }
        finally { EndUiOperation(); }
    }

    private void EnsureOllamaBackendReady()
    {
        if (_projectOllamaProfileMissing)
            throw new InvalidOperationException("Project tham chiếu profile Ollama không còn tồn tại. Hãy chọn và lưu lại backend trong AI & hiệu năng.");
        if (_projectSession is not null && _ollamaBackends.ActiveProfile.Id != SelectedOllamaProfileId)
            throw new InvalidOperationException("Backend đang hoạt động không khớp cấu hình Project. Hãy chọn lại profile trước khi dịch.");
    }

    private static OllamaBackendSnapshot BackendSnapshot(OllamaBackendProfile profile, string model) =>
        new(profile.Id, profile.Kind, profile.Name, profile.Endpoint, model);

    private static bool SameBackend(OllamaBackendSnapshot left, OllamaBackendSnapshot right) =>
        left.ProfileId == right.ProfileId && left.Kind == right.Kind &&
        string.Equals(left.Endpoint, right.Endpoint, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Model, right.Model, StringComparison.Ordinal);

    private void UpdateOllamaBackendStatus()
    {
        if (OllamaBackendStatus is null) return;
        if (_projectOllamaProfileMissing)
        {
            OllamaBackendStatus.Text = "Ollama · thiếu profile Project";
            OllamaBackendStatus.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }
        OllamaBackendProfile profile = _ollamaBackends.ActiveProfile;
        string model = string.IsNullOrWhiteSpace(ModelInput?.Text)
            ? (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions.Model
            : ModelInput.Text.Trim();
        OllamaBackendStatus.Text = $"{BackendLabel(profile)} · {model}";
        OllamaBackendStatus.Foreground = (Brush)FindResource("MutedBrush");
    }

    private static string BackendLabel(OllamaBackendProfile profile) =>
        $"{(profile.Kind == OllamaBackendKind.Local ? "Local" : "Remote")} · {profile.Name}";

    private sealed record OllamaProfileItem(Guid Id, string Label);
}
