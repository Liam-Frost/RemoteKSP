using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteKSP.Client.Core;

namespace RemoteKSP.Client.ViewModels;

/// <summary>连接诊断：选择一台服务端和它名下的一张证书，逐层测试链路</summary>
public partial class DiagnosticsViewModel : ViewModelBase
{
    private List<CertificateBinding> _allBindings = [];

    [ObservableProperty] private ObservableCollection<TestResultModel> _testResults = [];
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private ObservableCollection<ServerEntry> _servers = [];
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunTestsCommand))]
    private ServerEntry? _selectedServer;
    [ObservableProperty] private ObservableCollection<CertificateBinding> _certificates = [];
    [ObservableProperty] private CertificateBinding? _selectedCertificate;
    [ObservableProperty] private bool _hasServers;

    public Task InitializeAsync()
    {
        var selectedId = SelectedServer?.Id;
        _allBindings = ClientRegistry.LoadCertificates();
        Servers = new ObservableCollection<ServerEntry>(ClientRegistry.LoadServers());
        HasServers = Servers.Count > 0;
        SelectedServer = Servers.FirstOrDefault(s => s.Id == selectedId) ?? Servers.FirstOrDefault();
        return Task.CompletedTask;
    }

    partial void OnSelectedServerChanged(ServerEntry? value)
    {
        Certificates = new ObservableCollection<CertificateBinding>(
            _allBindings.Where(b => b.ServerId == value?.Id).OrderBy(b => b.Name));
        SelectedCertificate = Certificates.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanRunTests))]
    private async Task RunTestsAsync()
    {
        IsTesting = true;
        TestResults.Clear();
        Summary = Strings.Diag_Testing;

        try
        {
            using var tester = new ConnectionTester(SelectedServer!, SelectedCertificate);

            var connectivity = await tester.TestConnectivityAsync();
            TestResults.Add(new TestResultModel(connectivity));
            if (connectivity.Outcome == TestOutcome.Fail)
            {
                // 连不上服务端时后续测试没有意义
                foreach (var name in new[] { Strings.Test_Hmac, Strings.Test_RemoteSign, Strings.Test_LocalKsp })
                    TestResults.Add(new TestResultModel(new TestResult(name, TestOutcome.Skip, Strings.Diag_SkippedPrereq)));
            }
            else
            {
                TestResults.Add(new TestResultModel(await tester.TestAuthAsync()));
                TestResults.Add(new TestResultModel(await tester.TestSigningAsync()));
                TestResults.Add(new TestResultModel(await tester.TestLocalKspAsync()));
            }

            int pass = TestResults.Count(r => r.Outcome == TestOutcome.Pass);
            int warn = TestResults.Count(r => r.Outcome == TestOutcome.Warn);
            int fail = TestResults.Count(r => r.Outcome == TestOutcome.Fail);
            Summary = string.Format(Strings.Diag_Done, pass, warn, fail);
        }
        catch (System.Exception ex)
        {
            Summary = string.Format(Strings.Diag_Failed, ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanRunTests() => SelectedServer != null;
}

public class TestResultModel
{
    public string Name { get; }
    public TestOutcome Outcome { get; }
    public string OutcomeText { get; }
    public string Message { get; }

    public TestResultModel(TestResult result)
    {
        Name = result.Name;
        Outcome = result.Outcome;
        OutcomeText = result.Outcome switch
        {
            TestOutcome.Pass => Strings.Diag_Pass,
            TestOutcome.Warn => Strings.Diag_Warn,
            TestOutcome.Fail => Strings.Diag_Fail,
            TestOutcome.Skip => Strings.Diag_Skip,
            _ => "?"
        };
        Message = result.Message;
    }
}
