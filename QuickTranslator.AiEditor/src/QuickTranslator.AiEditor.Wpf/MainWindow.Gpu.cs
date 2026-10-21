using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private GpuManagerWindow? _gpuManagerWindow;
    private readonly ObservableCollection<GpuProfileItem> _gpuProfileItems = [];
    private Task? _gpuLoadTask;
    private bool _gpuProfilesReady;
    private bool _syncingGpuProfile;
    private Guid? _selectedGpuProfileId;
    private bool _projectGpuProfileMissing;

    private Guid SelectedGpuProfileId => _selectedGpuProfileId ?? _gpuBackends.ActiveProfile.Id;

    private void InitializeGpuBackendUi()
    {
        GpuProfileInput.ItemsSource = _gpuProfileItems;
        GpuProfileInput.SelectionChanged += OnGpuProfileSelected;
        GpuBackendKindInput.SelectionChanged += OnGpuBackendKindChanged;
        GpuProfileNameInput.TextChanged += OnSettingsInputChanged;
        GpuEndpointInput.TextChanged += OnSettingsInputChanged;
        GpuTokenInput.PasswordChanged += OnSettingsInputChanged;
        ModelInput.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(OnSettingsInputChanged));
        _gpuBackends.ActiveProfileChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RefreshGpuProfileList(_gpuBackends.ActiveProfile.Id);
            UpdateGpuBackendStatus();
        });
    }

    private async void OnOpenGpuManager(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureGpuBackendsLoadedAsync();
            if (_gpuManagerWindow is { IsLoaded: true }) { _gpuManagerWindow.Activate(); return; }
            _gpuManagerWindow = new GpuManagerWindow(_gpuBackends, _gpuConnections, _gpuPreferences, _localLlama) { Owner = this };
            ClampWindowToWorkArea(_gpuManagerWindow, 1180, 820);
            _gpuManagerWindow.Closed += (_, _) => _gpuManagerWindow = null;
            _gpuManagerWindow.Show();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task EnsureGpuBackendsLoadedAsync()
    {
        _gpuLoadTask ??= _gpuBackends.LoadAsync();
        await _gpuLoadTask;
        if (_gpuProfilesReady) return;
        _gpuProfilesReady = true;
        RefreshGpuProfileList(_gpuBackends.ActiveProfile.Id);
        UpdateGpuBackendStatus();
    }

    private async Task PrepareGpuProfileForProjectAsync(Guid? profileId)
    {
        await EnsureGpuBackendsLoadedAsync();
        Guid requested = profileId ?? GpuBackendProfile.DefaultLocalId;
        _selectedGpuProfileId = requested;
        GpuBackendProfile profile;
        try { profile = _gpuBackends.GetProfile(requested); }
        catch (InvalidOperationException)
        {
            _projectGpuProfileMissing = true;
            RefreshGpuProfileList(requested);
            UpdateGpuBackendStatus();
            return;
        }
        if (_gpuBackends.ActiveProfile.Id != profile.Id)
            await _gpuBackends.ActivateAsync(profile.Id);
        _projectGpuProfileMissing = false;
        RefreshGpuProfileList(requested);
        UpdateGpuBackendStatus();
    }

    private void SelectGpuProfileInUi(Guid? profileId)
    {
        Guid requested = profileId ?? GpuBackendProfile.DefaultLocalId;
        _selectedGpuProfileId = requested;
        if (!_gpuProfilesReady) return;
        GpuProfileInput.SelectedItem = _gpuProfileItems.FirstOrDefault(item => item.Id == requested);
        if (GpuProfileInput.SelectedItem is null) _projectGpuProfileMissing = true;
        UpdateGpuBackendStatus();
    }

    private void RefreshGpuProfileList(Guid? selectedId = null)
    {
        if (!_gpuProfilesReady || GpuProfileInput is null) return;
        _syncingGpuProfile = true;
        try
        {
            Guid requested = selectedId ?? _selectedGpuProfileId ?? _gpuBackends.ActiveProfile.Id;
            _gpuProfileItems.Clear();
            foreach (GpuBackendProfile profile in _gpuBackends.Profiles)
                _gpuProfileItems.Add(new(profile.Id,
                    $"{profile.Name} · {(profile.Kind == GpuBackendKind.Local ? "Local" : "Remote")}"));
            GpuProfileInput.SelectedItem = _gpuProfileItems.FirstOrDefault(item => item.Id == requested)
                ?? _gpuProfileItems.FirstOrDefault(item => item.Id == _gpuBackends.ActiveProfile.Id);
            if (GpuProfileInput.SelectedItem is GpuProfileItem selected)
                LoadGpuProfileEditor(_gpuBackends.GetProfile(selected.Id));
        }
        finally { _syncingGpuProfile = false; }
    }

    private void OnGpuProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGpuProfile || GpuProfileInput.SelectedItem is not GpuProfileItem selected) return;
        LoadGpuProfileEditor(_gpuBackends.GetProfile(selected.Id));
    }

    private void LoadGpuProfileEditor(GpuBackendProfile profile)
    {
        _syncingGpuProfile = true;
        try
        {
            GpuProfileNameInput.Text = profile.Name;
            GpuBackendKindInput.SelectedIndex = profile.Kind == GpuBackendKind.Local ? 0 : 1;
            GpuEndpointInput.Text = profile.Endpoint;
            GpuTokenInput.Password = profile.Token ?? "";
            AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
            ModelInput.Text = runtime.ModelOptions.Model;
            TemperatureInput.Text = runtime.ModelOptions.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ContextInput.Text = runtime.ModelOptions.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            OutputTokensInput.Text = runtime.ModelOptions.MaximumOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TimeoutInput.Text = runtime.ModelOptions.RequestTimeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
            MinimumInput.Text = runtime.SegmentationOptions.MinimumHanCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TargetInput.Text = runtime.SegmentationOptions.TargetHanCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
            MaximumInput.Text = runtime.SegmentationOptions.MaximumHanCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ApplyGpuKindUi(profile.Kind);
            GpuConnectionStatus.Text = profile.Id == _gpuBackends.ActiveProfile.Id ? "Đang sử dụng" : "Đã lưu";
            GpuConnectionStatus.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
        }
        finally { _syncingGpuProfile = false; }
    }

    private void OnGpuBackendKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGpuProfile) return;
        GpuBackendKind kind = SelectedGpuKind();
        if (kind == GpuBackendKind.Local)
        {
            GpuEndpointInput.Text = "http://127.0.0.1:11450/api/chat";
            GpuTokenInput.Password = "";
        }
        else if (GpuEndpointInput.Text.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
            GpuEndpointInput.Text = "https://";
        ApplyGpuKindUi(kind);
        OnSettingsInputChanged(sender, e);
    }

    private void ApplyGpuKindUi(GpuBackendKind kind)
    {
        bool remote = kind == GpuBackendKind.Remote;
        GpuEndpointInput.IsReadOnly = !remote;
        GpuTokenInput.IsEnabled = remote;
        GpuRemotePrivacyText.Visibility = remote ? Visibility.Visible : Visibility.Collapsed;
    }

    private GpuBackendKind SelectedGpuKind() => GpuBackendKindInput.SelectedIndex == 1
        ? GpuBackendKind.Remote : GpuBackendKind.Local;

    private GpuBackendProfile ReadGpuProfileEditor()
    {
        GpuBackendProfile? original = GpuProfileInput.SelectedItem is GpuProfileItem selected
            ? _gpuBackends.GetProfile(selected.Id) : null;
        GpuBackendProfile profile = original?.Clone() ?? new GpuBackendProfile { Id = Guid.NewGuid() };
        profile.Name = GpuProfileNameInput.Text;
        profile.Kind = SelectedGpuKind();
        profile.Endpoint = profile.Kind == GpuBackendKind.Local
            ? "http://127.0.0.1:11450/api/chat" : GpuEndpointInput.Text.Trim();
        profile.Token = profile.Kind == GpuBackendKind.Remote ? GpuTokenInput.Password : null;
        profile.Runtime = new AiRuntimeProfile
        {
            ModelOptions = ReadModelOptions(),
            SegmentationOptions = ReadSegmenterOptions()
        };
        profile.Model = profile.Runtime.ModelOptions.Model;
        profile.Validate();
        if (profile.Kind == GpuBackendKind.Remote && (original?.Kind != GpuBackendKind.Remote ||
            !string.Equals(original.Endpoint, profile.Endpoint, StringComparison.OrdinalIgnoreCase)))
            profile.RemoteDisclosureAccepted = false;
        return profile;
    }

    private void OnNewGpuProfile(object sender, RoutedEventArgs e)
    {
        _syncingGpuProfile = true;
        try
        {
            GpuProfileInput.SelectedItem = null;
            GpuProfileNameInput.Text = "GPU Remote mới";
            GpuBackendKindInput.SelectedIndex = 1;
            GpuEndpointInput.Text = "https://";
            GpuTokenInput.Password = "";
            ModelInput.Text = "translategemma:12b";
            TemperatureInput.Text = "0.1"; ContextInput.Text = "4096"; OutputTokensInput.Text = "2048";
            TimeoutInput.Text = "300"; MinimumInput.Text = "350"; TargetInput.Text = "550"; MaximumInput.Text = "750";
            ApplyGpuKindUi(GpuBackendKind.Remote);
            GpuConnectionStatus.Text = "Profile mới chưa lưu";
        }
        finally { _syncingGpuProfile = false; }
    }

    private async void OnSaveGpuProfile(object sender, RoutedEventArgs e)
    {
        if (WorkActive || _activeRequest is { IsCompleted: false } || _projectTranslationCoordinator?.IsRunning == true)
        {
            SetStatus("Không đổi backend khi đang dịch. Hãy Pause queue và chờ checkpoint hoàn tất trước.");
            return;
        }
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            GpuBackendProfile profile = ReadGpuProfileEditor();
            if (profile.Kind == GpuBackendKind.Remote && !profile.RemoteDisclosureAccepted)
            {
                MessageBoxResult confirmed = ThemedDialog.Show(this,
                    $"QT Studio sẽ gửi prompt và nguyên văn segment tới server remote “{profile.Name}” tại {profile.Endpoint}. " +
                    "Token chỉ lưu mã hóa bằng Windows DPAPI và không nằm trong Project. Tiếp tục?",
                    "Xác nhận gửi nội dung tới GPU thuê", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirmed != MessageBoxResult.Yes) return;
                profile.RemoteDisclosureAccepted = true;
            }
            await _gpuBackends.SaveProfileAsync(profile, activate: true);
            _selectedGpuProfileId = profile.Id;
            _projectGpuProfileMissing = false;
            RefreshGpuProfileList(profile.Id);
            if (_projectSession is not null) await SaveProjectAssetsAsync();
            MarkSettingsSaved();
            UpdateGpuBackendStatus();
            SetStatus($"Đã chuyển sang {BackendLabel(profile)}. Lần dịch mới hoặc Tiếp tục dịch sẽ dùng cấu hình này.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async Task SaveRuntimeProfileEditorAsync()
    {
        if (!_gpuProfilesReady || GpuProfileInput.SelectedItem is not GpuProfileItem) return;
        GpuBackendProfile profile = ReadGpuProfileEditor();
        if (profile.Kind == GpuBackendKind.Remote && !profile.RemoteDisclosureAccepted)
            throw new InvalidOperationException("Kết nối Remote đã thay đổi hoặc chưa được xác nhận. Hãy dùng “Lưu & sử dụng” để xem và xác nhận nơi nhận nguyên văn.");
        bool active = _gpuBackends.ActiveProfile.Id == profile.Id;
        await _gpuBackends.SaveProfileAsync(profile, activate: active);
        RefreshGpuProfileList(profile.Id);
    }

    private async void OnRefreshGpuModels(object sender, RoutedEventArgs e) => await TestGpuProfileAsync(refreshModels: true);

    private async void OnTestGpuConnection(object sender, RoutedEventArgs e) => await TestGpuProfileAsync(refreshModels: false);

    private async Task TestGpuProfileAsync(bool refreshModels)
    {
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        GpuConnectionStatus.Text = refreshModels ? "Đang đọc /api/tags…" : "Đang kiểm tra…";
        GpuConnectionStatus.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
        try
        {
            GpuBackendProfile profile = ReadGpuProfileEditor();
            using ILlmBackendClient client = LlamaServerClient.Create(profile);
            IReadOnlyList<string> models = await client.ListModelsAsync(TimeSpan.FromSeconds(10));
            ModelInput.ItemsSource = models;
            if (!refreshModels)
                await client.CheckConnectionAsync(ModelInput.Text, TimeSpan.FromSeconds(10));
            GpuConnectionStatus.Text = refreshModels
                ? $"Đã tìm thấy {models.Count:N0} model cục bộ trên server"
                : "Đã kết nối · model sẵn sàng";
            GpuConnectionStatus.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SuccessBrush");
        }
        catch (Exception ex)
        {
            GpuConnectionStatus.Text = "Không sẵn sàng";
            GpuConnectionStatus.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
            ShowError(ex);
        }
        finally { EndUiOperation(); }
    }

    private async Task EnsureGpuBackendReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_projectGpuProfileMissing)
            throw new InvalidOperationException("Project tham chiếu profile GPU không còn tồn tại. Hãy chọn và lưu lại backend trong AI & hiệu năng.");
        if (_projectSession is not null && _gpuBackends.ActiveProfile.Id != SelectedGpuProfileId)
            throw new InvalidOperationException("Backend đang hoạt động không khớp cấu hình Project. Hãy chọn lại profile trước khi dịch.");
        GpuBackendProfile profile = _gpuBackends.ActiveProfile;
        if (profile.Kind == GpuBackendKind.Local && _translationClient is GpuBackendManager)
        {
            Uri endpoint = await _localLlama.StartAsync(profile,
                new Progress<string>(SetStatus), cancellationToken);
            if (!string.Equals(endpoint.ToString(), profile.Endpoint, StringComparison.OrdinalIgnoreCase))
            {
                var client = new LlamaServerClient(new HttpClient(), endpoint, profile.ParallelRequests,
                    ownsHttpClient: true);
                await _gpuBackends.AttachRuntimeClientAsync(profile.Id, client, cancellationToken);
            }
        }
    }

    private static GpuBackendSnapshot BackendSnapshot(GpuBackendProfile profile, string model) =>
        new(profile.Id, profile.Kind, profile.Name, profile.Endpoint, model);

    private static bool SameBackend(GpuBackendSnapshot left, GpuBackendSnapshot right) =>
        left.ProfileId == right.ProfileId && left.Kind == right.Kind &&
        string.Equals(left.Endpoint, right.Endpoint, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Model, right.Model, StringComparison.Ordinal);

    private void UpdateGpuBackendStatus()
    {
        if (GpuBackendStatus is null) return;
        if (_projectGpuProfileMissing)
        {
            GpuBackendStatus.Text = "GPU · thiếu profile Project";
            GpuBackendStatus.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
            return;
        }
        GpuBackendProfile profile = _gpuBackends.ActiveProfile;
        string model = string.IsNullOrWhiteSpace(ModelInput?.Text)
            ? (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions.Model
            : ModelInput.Text.Trim();
        GpuBackendStatus.Text = $"{BackendLabel(profile)} · {model}";
        GpuBackendStatus.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
    }

    private static string BackendLabel(GpuBackendProfile profile) =>
        $"{(profile.Kind == GpuBackendKind.Local ? "Local" : "Remote")} · {profile.Name}";

    private sealed record GpuProfileItem(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }
}
