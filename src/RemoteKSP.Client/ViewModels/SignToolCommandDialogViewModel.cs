using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using RemoteKSP.Client.Core;
using RemoteKSP.Client.Services;
using RemoteKSP.Shared.Services;

namespace RemoteKSP.Client.ViewModels;

/// <summary>「生成签名命令」对话框：选择 SignTool、目标文件、时间戳，实时预览命令</summary>
public partial class SignToolCommandDialogViewModel : ObservableObject
{
    private readonly DialogService _dialogService;
    private readonly SignToolSettings _settings;
    private readonly StoreLocation? _storeLocation;
    private bool _loading = true;
    // 刚打开对话框时目标文件为空属于正常状态，不标红；用户点击「复制命令」后才显示全部错误
    private bool _showAllErrors;

    // 证书（只读展示）
    public string CertName { get; }
    public string CertSubject { get; }
    public string CertServer { get; }
    public string CertThumbprint { get; }
    public string CertExpiry { get; }
    public bool CertIsTest { get; }
    public string CertStoreText { get; }

    // SignTool 来源
    [ObservableProperty] private bool _isDiscovering;
    [ObservableProperty] private ObservableCollection<SignToolLocation> _discoveredTools = [];
    [ObservableProperty] private SignToolLocation? _selectedTool;
    [ObservableProperty] private string _customPath = "";
    [ObservableProperty] private bool _isDiscoveredMode = true;
    [ObservableProperty] private bool _isCustomMode;
    [ObservableProperty] private bool _isPathMode;
    [ObservableProperty] private string _discoveryText = "";

    // 签名选项
    [ObservableProperty] private string _targetFile = "";
    [ObservableProperty] private bool _useTimestamp = true;
    [ObservableProperty] private string _timestampUrl = "";
    [ObservableProperty] private bool _verbose;

    // 预览与校验
    [ObservableProperty] private string _commandPreview = "";
    [ObservableProperty] private bool _canCopy;
    [ObservableProperty] private string _toolError = "";
    [ObservableProperty] private string _toolHint = "";
    [ObservableProperty] private string _targetError = "";
    [ObservableProperty] private string _targetHint = "";
    [ObservableProperty] private string _timestampError = "";
    [ObservableProperty] private string _certError = "";

    public SignToolCommandDialogViewModel(DialogService dialogService, ClientCertItem cert)
    {
        _dialogService = dialogService;
        _settings = SignToolSettings.Load();

        CertName = cert.Name;
        CertSubject = cert.Subject ?? Strings.St_CertNotInStore;
        CertServer = cert.ServerText;
        CertThumbprint = cert.Thumbprint;
        CertExpiry = cert.ExpiryText;
        CertIsTest = cert.IsTest;

        // 证书实际所在存储决定 /sm 与否；打开对话框时查一次
        (_storeLocation, _) = CertificateBinder.FindCertificateStore(cert.Thumbprint);
        CertStoreText = _storeLocation switch
        {
            StoreLocation.LocalMachine => Strings.St_StoreLocalMachine,
            StoreLocation.CurrentUser => @"CurrentUser\My",
            _ => Strings.St_StoreNotFound
        };

        IsDiscoveredMode = _settings.Mode == SignToolMode.Discovered;
        IsCustomMode = _settings.Mode == SignToolMode.Custom;
        IsPathMode = _settings.Mode == SignToolMode.PathCommand;
        CustomPath = _settings.CustomToolPath;
        UseTimestamp = _settings.UseTimestamp;
        TimestampUrl = _settings.TimestampUrl;
        Verbose = _settings.Verbose;
        _loading = false;
        UpdatePreview();
    }

    /// <summary>对话框加载后调用：后台扫描 SignTool，不阻塞 UI 线程</summary>
    public async Task DiscoverToolsAsync()
    {
        IsDiscovering = true;
        DiscoveryText = Strings.St_Searching;
        try
        {
            var tools = await Task.Run(SignToolDiscovery.Discover);
            DiscoveredTools = new ObservableCollection<SignToolLocation>(tools);
            // 上次选择的路径仍存在则沿用，否则用推荐项；不会静默替换用户的自定义路径
            SelectedTool = tools.FirstOrDefault(t =>
                               string.Equals(t.Path, _settings.SelectedDiscoveredPath, StringComparison.OrdinalIgnoreCase))
                           ?? SignToolDiscovery.Recommend(tools);
            DiscoveryText = tools.Count == 0
                ? Strings.St_NotFound
                : string.Format(Strings.St_Found, tools.Count);
        }
        catch (Exception ex)
        {
            DiscoveryText = string.Format(Strings.St_SearchFailed, ex.Message);
        }
        finally
        {
            IsDiscovering = false;
            UpdatePreview();
        }
    }

    partial void OnIsDiscoveredModeChanged(bool value)
    {
        if (value) { IsCustomMode = false; IsPathMode = false; }
        if (!_loading) UpdatePreview();
    }

    partial void OnIsCustomModeChanged(bool value)
    {
        if (value) { IsDiscoveredMode = false; IsPathMode = false; }
        if (!_loading) UpdatePreview();
    }

    partial void OnIsPathModeChanged(bool value)
    {
        if (value) { IsDiscoveredMode = false; IsCustomMode = false; }
        if (!_loading) UpdatePreview();
    }

    partial void OnSelectedToolChanged(SignToolLocation? value) { if (!_loading) UpdatePreview(); }
    partial void OnCustomPathChanged(string value) { if (!_loading) UpdatePreview(); }
    partial void OnTargetFileChanged(string value) { if (!_loading) UpdatePreview(); }
    partial void OnUseTimestampChanged(bool value) { if (!_loading) UpdatePreview(); }
    partial void OnTimestampUrlChanged(string value) { if (!_loading) UpdatePreview(); }
    partial void OnVerboseChanged(bool value) { if (!_loading) UpdatePreview(); }

    private void UpdatePreview()
    {
        ToolError = ToolHint = TargetError = TargetHint = TimestampError = CertError = "";
        // 输入变了，上一次的执行结果对应的是另一条命令，不再显示
        if (RunStatus != RunState.Running)
            ShowRunResult(RunState.None, "", "");

        if (_storeLocation == null)
        {
            CertError = Strings.St_CertMissing;
            CommandPreview = Strings.St_CertMissingPreview;
            CanCopy = false;
            return;
        }

        var mode = IsCustomMode ? SignToolMode.Custom : IsPathMode ? SignToolMode.PathCommand : SignToolMode.Discovered;
        var toolPath = mode switch
        {
            SignToolMode.Discovered => SelectedTool?.Path,
            SignToolMode.Custom => CustomPath.Trim(),
            _ => null
        };

        var input = new SignToolCommandInput
        {
            Mode = mode,
            ToolPath = toolPath,
            Thumbprint = CertThumbprint,
            StoreLocation = _storeLocation.Value,
            StoreName = "My",
            TargetFile = TargetFile.Trim(),
            UseTimestamp = UseTimestamp,
            TimestampUrl = TimestampUrl.Trim(),
            Verbose = Verbose
        };

        var result = SignToolCommandBuilder.Build(input);
        CanCopy = result.IsValid;
        CommandPreview = result.Command ?? (string.IsNullOrWhiteSpace(TargetFile) && result.Errors.Count == 1
            ? Strings.St_PickTargetPreview
            : Strings.St_FixFieldsPreview);

        // 字段级错误与提示
        result.Errors.TryGetValue(SignToolCommandBuilder.FieldTool, out var te);
        result.Errors.TryGetValue(SignToolCommandBuilder.FieldTarget, out var fe);
        result.Errors.TryGetValue(SignToolCommandBuilder.FieldTimestamp, out var tse);
        ToolError = te ?? "";
        TimestampError = tse ?? "";
        // 目标文件为空是刚打开时的正常状态，点击「复制命令」前不标红
        TargetError = string.IsNullOrWhiteSpace(TargetFile) && !_showAllErrors ? "" : fe ?? "";

        // 本地 SignTool 必须存在：自定义路径不存在时阻止复制，并保留用户输入供修正
        if (string.IsNullOrEmpty(ToolError) && toolPath != null && !File.Exists(toolPath))
        {
            ToolError = mode == SignToolMode.Custom
                ? Strings.St_NoToolAtPath
                : Strings.St_ToolGone;
            CanCopy = false;
            CommandPreview = Strings.St_FixFieldsPreview;
        }
        if (mode == SignToolMode.Discovered && SelectedTool == null && !IsDiscovering && DiscoveredTools.Count == 0)
            ToolError = Strings.St_NoToolUseOther;
        if (mode == SignToolMode.PathCommand && !SignToolDiscovery.FindOnPath().Any())
            ToolHint = Strings.St_NotOnPath;

        // 目标文件不存在不阻止复制：命令可能用于其他机器或尚未生成的构建产物
        if (string.IsNullOrEmpty(TargetError) && !string.IsNullOrWhiteSpace(TargetFile) && !File.Exists(TargetFile.Trim()))
            TargetHint = Strings.St_TargetMissing;
    }

    /// <summary>用户尝试复制但输入无效时调用：把尚未展示的必填项错误也显示出来</summary>
    public void ShowAllErrors()
    {
        _showAllErrors = true;
        UpdatePreview();
    }

    public enum RunState { None, Running, Succeeded, Failed }

    // 执行状态。执行期间禁用输入，确保执行的就是开始时预览里的那条命令
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputEnabled), nameof(IsRunning))]
    private RunState _runStatus = RunState.None;
    [ObservableProperty] private string _runMessage = "";
    [ObservableProperty] private string _runOutput = "";

    public bool IsRunning => RunStatus == RunState.Running;
    public bool IsInputEnabled => RunStatus != RunState.Running;

    private void ShowRunResult(RunState state, string message, string output)
    {
        RunStatus = state;
        RunMessage = message;
        RunOutput = output;
    }

    /// <summary>在本机执行预览中的命令。执行前检查目标文件和 SignTool 确实可用，避免得到含糊的 CMD 报错</summary>
    public async Task ExecuteAsync()
    {
        if (IsRunning)
            return;
        if (!CanCopy)
        {
            ShowAllErrors();
            return;
        }
        var target = TargetFile.Trim();
        if (!File.Exists(target))
        {
            ShowRunResult(RunState.Failed, Strings.St_RunNoTarget, "");
            return;
        }
        if (IsPathMode && !SignToolDiscovery.FindOnPath().Any())
        {
            ShowRunResult(RunState.Failed,
                Strings.St_RunNotOnPath, "");
            return;
        }

        var command = CommandPreview;
        SaveSettings();
        ShowRunResult(RunState.Running, UseTimestamp
            ? Strings.St_SigningWithTimestamp
            : Strings.St_Signing, "");
        try
        {
            var result = await SignToolRunner.RunAsync(command, Path.GetDirectoryName(target));
            var output = result.Output.Trim();
            // signtool 会把路径中的中文等字符输出成空格，这是它自身的行为，不影响签名结果
            if (target.Any(c => c > 127))
                output += Strings.St_NonAsciiNote;
            if (result.TimedOut)
                ShowRunResult(RunState.Failed,
                    string.Format(Strings.St_TimedOut, SignToolRunner.DefaultTimeout.TotalMinutes.ToString("0")), output);
            else if (result.Succeeded)
                ShowRunResult(RunState.Succeeded, string.Format(Strings.St_Succeeded, result.Elapsed.TotalSeconds.ToString("0.0")), output);
            else
                // 测试证书被服务端拒绝时，SignTool 只会报 "No certificates were found"，这里说明真正原因
                ShowRunResult(RunState.Failed, string.Format(Strings.St_Failed, result.ExitCode) +
                    (CertIsTest ? Strings.St_FailedTestCert : ""), output);
        }
        catch (Exception ex)
        {
            ShowRunResult(RunState.Failed, string.Format(Strings.St_CannotStart, ex.Message), "");
        }
    }

    [RelayCommand]
    private void BrowseTool()
    {
        var dlg = new OpenFileDialog
        {
            Filter = Strings.St_ToolFilter,
            Title = Strings.St_ToolPickTitle
        };
        if (dlg.ShowDialog() == true)
        {
            CustomPath = dlg.FileName;
            if (!IsCustomMode)
                IsCustomMode = true;
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dlg = new OpenFileDialog
        {
            Filter = Strings.St_TargetFilter,
            Title = Strings.St_TargetPickTitle,
            InitialDirectory = string.IsNullOrWhiteSpace(_settings.LastTargetDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _settings.LastTargetDirectory
        };
        if (dlg.ShowDialog() == true)
        {
            TargetFile = dlg.FileName;
            try { _settings.LastTargetDirectory = Path.GetDirectoryName(dlg.FileName) ?? ""; }
            catch { /* 无效路径时忽略 */ }
        }
    }

    /// <summary>
    /// 复制命令到剪贴板并保存偏好。失败原因显示在对话框内：
    /// 此时 SignTool 对话框仍打开，WPF-UI 的对话框宿主不能再叠一个错误对话框
    /// </summary>
    public bool TryCopy()
    {
        if (!CanCopy)
            return false;
        try
        {
            Clipboard.SetText(CommandPreview);
        }
        catch (Exception ex)
        {
            ShowRunResult(RunState.Failed, string.Format(Strings.St_CopyFailed, ex.Message), "");
            return false;
        }
        SaveSettings();
        return true;
    }

    /// <summary>保存偏好；待签名文件只记目录，不记文件本身</summary>
    private void SaveSettings()
    {
        _settings.Mode = IsCustomMode ? SignToolMode.Custom
            : IsPathMode ? SignToolMode.PathCommand
            : SignToolMode.Discovered;
        _settings.CustomToolPath = CustomPath.Trim();
        _settings.SelectedDiscoveredPath = SelectedTool?.Path ?? "";
        _settings.UseTimestamp = UseTimestamp;
        _settings.TimestampUrl = TimestampUrl.Trim();
        _settings.Verbose = Verbose;
        _settings.Save();
    }
}
