extern alias worker;

using System.Diagnostics;
using System.Text;

namespace OrderSystem.IntegrationTests.Worker;

[Collection(WorkerCollectionDefinition.Name)]
public sealed class WorkerHostStartupTests
{
    [Fact]
    public async Task Executable_WithoutApiOnlyConfiguration_StartsHostedWorkersSuccessfully()
    {
        var workerAssemblyPath = typeof(worker::OrderSystem.Worker.ReservationExpirationWorker).Assembly.Location;
        var output = new StringBuilder();
        var reservationWorkerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconciliationWorkerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(workerAssemblyPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add(workerAssemblyPath);
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
        process.StartInfo.Environment.Remove("Jwt__SigningKey");
        process.StartInfo.Environment.Remove("Payment__FakeWebhookSecret");

        void CaptureOutput(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
            }

            if (line.Contains("OrderSystem Worker started", StringComparison.Ordinal))
            {
                reservationWorkerStarted.TrySetResult();
            }

            if (line.Contains("Payment reconciliation Worker started", StringComparison.Ordinal))
            {
                reconciliationWorkerStarted.TrySetResult();
            }
        }

        process.OutputDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => CaptureOutput(eventArgs.Data);
        process.Exited += (_, _) => exited.TrySetResult(process.ExitCode);

        try
        {
            Assert.True(process.Start(), "The Worker process could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var hostedWorkersStarted = Task.WhenAll(reservationWorkerStarted.Task, reconciliationWorkerStarted.Task);
            var timeout = Task.Delay(TimeSpan.FromSeconds(10));
            var completed = await Task.WhenAny(hostedWorkersStarted, exited.Task, timeout);

            Assert.True(
                completed == hostedWorkersStarted,
                $"Hosted workers did not start without API-only configuration{Environment.NewLine}{output}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
    }
}
