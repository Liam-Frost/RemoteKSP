using System;
using System.Windows.Controls;
using RemoteKSP.Client.Core;

namespace LayoutProbe;

/// <summary>Probe-only sample rows. Never referenced by the shipping apps.</summary>
public static class SampleData
{
    public static void Inject(Page page, int rows)
    {
        switch (page)
        {
            case RemoteKSP.Server.Views.Pages.ActivityLogPage log:
                log.ViewModel.LogEntries.Clear();
                for (int i = 0; i < rows; i++)
                    log.ViewModel.LogEntries.Add(new RemoteKSP.Server.ViewModels.LogEntry(
                        new RemoteKSP.Server.Services.ActivityLogEvent
                        {
                            Level = i % 5 == 3 ? "WARN" : "INFO",
                            Category = "probe",
                            Message = i % 4 == 2
                                ? $"probe row {i}: a long message that should wrap across the available width of the message column without pushing other columns"
                                : $"probe row {i}"
                        }));
                break;

            case RemoteKSP.Client.Views.Pages.DiagnosticsPage diag:
                diag.ViewModel.TestResults.Clear();
                var samples = new[]
                {
                    new TestResult("连通性 (HTTPS /info)", TestOutcome.Fail,
                        "无法连接: 由于目标计算机积极拒绝，无法连接。 (192.168.1.100:8443) — probe long message to verify wrapping across the row width"),
                    new TestResult("HMAC 认证 (/auth/check)", TestOutcome.Skip, "前置测试失败，跳过"),
                    new TestResult("远程签名 (/sign)", TestOutcome.Skip, "前置测试失败，跳过"),
                    new TestResult("本地 KSP (Native)", TestOutcome.Pass, "通过 Remote KSP 签名成功"),
                };
                for (int i = 0; i < Math.Min(rows, samples.Length); i++)
                    diag.ViewModel.TestResults.Add(new RemoteKSP.Client.ViewModels.TestResultModel(samples[i]));
                break;
        }
    }
}
