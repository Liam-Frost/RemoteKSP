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
        CertSubject = cert.Subject ?? "（本机证书存储中未找到）";
        CertServer = cert.ServerText;
        CertThumbprint = cert.Thumbprint;
        CertExpiry = cert.ExpiryText;
        CertIsTest = cert.IsTest;

        // 证书实际所在存储决定 /sm 与否；打开对话框时查一次
        (_storeLocation, _) = CertificateBinder.FindCertificateStore(cert.Thumbprint);
        CertStoreText = _storeLocation switch
        {
            StoreLocation.LocalMachine => @"LocalMachine\My（命令使用 /sm）",
            StoreLocation.CurrentUser => @"CurrentUser\My",
            _ => "本机证书存储中未找到"
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
        DiscoveryText = "正在查找 SignTool…";
        try
        {
            var tools = await Task.Run(SignToolDiscovery.Discover);
            DiscoveredTools = new ObservableCollection<SignToolLocation>(tools);
            // 上次选择的路径仍存在则沿用，否则用推荐项；不会静默替换用户的自定义路径
            SelectedTool = tools.FirstOrDefault(t =>
                               string.Equals(t.Path, _settings.SelectedDiscoveredPath, StringComparison.OrdinalIgnoreCase))
                           ?? SignToolDiscovery.Recommend(tools);
            DiscoveryText = tools.Count == 0
                ? "未找到 SignTool。请安装 Windows SDK，或改用自定义路径 / PATH 命令。"
                : $"找到 {tools.Count} 个 SignTool";
        }
        catch (Exception ex)
        {
            DiscoveryText = $"查找 SignTool 失败：{ex.Message}";
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
            CertError = "证书未安装在本机，无法生成签名命令。请先在「证书管理」更新证书。";
            CommandPreview = "# 证书缺失";
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
            ? "（选择待签名文件后显示完整命令）"
            : "（请先修正上方标红的字段）");

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
                ? "该路径下没有 signtool.exe，请检查路径"
                : "之前选择的 SignTool 已不存在，请重新选择";
            CanCopy = false;
            CommandPreview = "（请先修正上方标红的字段）";
        }
        if (mode == SignToolMode.Discovered && SelectedTool == null && !IsDiscovering && DiscoveredTools.Count == 0)
            ToolError = "未找到 SignTool，请改用自定义路径或 PATH 命令";
        if (mode == SignToolMode.PathCommand && !SignToolDiscovery.FindOnPath().Any())
            ToolHint = "当前程序的 PATH 中没有 signtool.exe，请在已配置 SignTool 的终端（如 Developer Command Prompt）中执行";

        // 目标文件不存在不阻止复制：命令可能用于其他机器或尚未生成的构建产物
        if (string.IsNullOrEmpty(TargetError) && !string.IsNullOrWhiteSpace(TargetFile) && !File.Exists(TargetFile.Trim()))
            TargetHint = "本机找不到该文件。命令仍可复制，供其他机器或后续构建使用";
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
            ShowRunResult(RunState.Failed, "无法执行：本机找不到待签名文件。命令仍可复制到文件所在的机器上执行。", "");
            return;
        }
        if (IsPathMode && !SignToolDiscovery.FindOnPath().Any())
        {
            ShowRunResult(RunState.Failed,
                "无法执行：本程序的 PATH 中没有 signtool.exe。请改用「自动检测」或「自定义路径」，或复制命令到已配置 SignTool 的终端执行。", "");
            return;
        }

        var command = CommandPreview;
        SaveSettings();
        ShowRunResult(RunState.Running, UseTimestamp
            ? "正在签名…（需要连接签名服务端和时间戳服务器，可能需要一些时间）"
            : "正在签名…（需要连接签名服务端）", "");
        try
        {
            var result = await SignToolRunner.RunAsync(command, Path.GetDirectoryName(target));
            var output = result.Output.Trim();
            // signtool 会把路径中的中文等字符输出成空格，这是它自身的行为，不影响签名结果
            if (target.Any(c => c > 127))
                output += "\n\n（说明：SignTool 输出中，路径里的非 ASCII 字符会显示为空格，以退出码为准）";
            if (result.TimedOut)
                ShowRunResult(RunState.Failed,
                    $"执行超时（{SignToolRunner.DefaultTimeout.TotalMinutes:0} 分钟），已结束 SignTool 进程", output);
            else if (result.Succeeded)
                ShowRunResult(RunState.Succeeded, $"签名成功，用时 {result.Elapsed.TotalSeconds:0.0} 秒", output);
            else
                // 测试证书被服务端拒绝时，SignTool 只会报 "No certificates were found"，这里说明真正原因
                ShowRunResult(RunState.Failed, $"签名失败，SignTool 退出码 {result.ExitCode}" +
                    (CertIsTest ? "。这是测试证书，服务端会拒绝签名请求，请改用正式证书" : ""), output);
        }
        catch (Exception ex)
        {
            ShowRunResult(RunState.Failed, $"无法启动 SignTool：{ex.Message}", "");
        }
    }

    [RelayCommand]
    private void BrowseTool()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SignTool 可执行文件|signtool.exe|所有文件|*.*",
            Title = "选择 SignTool.exe"
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
            Filter = "可执行文件|*.exe;*.dll;*.sys;*.ocx;*.msi;*.msix;*.appx;*.appxbundle|所有文件|*.*",
            Title = "选择待签名文件",
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
            ShowRunResult(RunState.Failed, $"复制失败：无法写入剪贴板（{ex.Message}）", "");
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
