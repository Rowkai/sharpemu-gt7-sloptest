// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Runs one test method in a child `dotnet test`, so a native fault, an
// unhandled exception on a host thread, or a hang fails that test instead of
// taking down the whole test host.
internal static class IsolatedTestWorker
{
    private const string WorkerEnvironmentVariable = "SHARPEMU_ISOLATED_TEST_WORKER";
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(30);

    public static bool IsWorker =>
        string.Equals(
            Environment.GetEnvironmentVariable(WorkerEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static async Task AssertPassesInIsolation(Type testClass, string methodName)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetHost(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(testClass.Assembly.Location);
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add($"FullyQualifiedName={testClass.FullName}.{methodName}");
        startInfo.Environment[WorkerEnvironmentVariable] = "1";
        startInfo.Environment["SHARPEMU_SENTINEL_PROBE"] = null;

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException($"Could not start the isolated worker for {methodName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(WorkerTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail(
                $"isolated worker {methodName} did not exit within {WorkerTimeout.TotalSeconds:F0} seconds\n" +
                await stdout + await stderr);
        }

        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, $"isolated worker {methodName} exited with code {process.ExitCode}\n{output}");
    }

    private static string ResolveDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            return configuredHost;
        }

        var processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }
}
