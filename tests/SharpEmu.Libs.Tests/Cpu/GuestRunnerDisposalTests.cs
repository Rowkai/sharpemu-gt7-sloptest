// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// A guest thread is marked Exited before its host executor has unwound, so a
// runner can be stopped while its thread is still inside the last slice. The
// runner used to destroy its wait handle after a 500 ms join timed out, and the
// thread then faulted the process re-entering that wait. Each case holds work
// across disposal with a gate and releases it afterwards; a regression crashes
// or hangs, so the cases run in an isolated process.
public sealed class GuestRunnerDisposalTests
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunnersSurviveDisposalWhileWorkIsHeld()
    {
        if (!IsolatedTestWorker.IsWorker)
        {
            await IsolatedTestWorker.AssertPassesInIsolation(
                typeof(GuestRunnerDisposalTests),
                nameof(RunnersSurviveDisposalWhileWorkIsHeld));
            return;
        }

        ExecutionRunnerFinishesHeldWorkAfterDispose();
        ContinuationWaiterCompletesWhenStoppedMidRun();
    }

    private static void ExecutionRunnerFinishesHeldWorkAfterDispose()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread? host = null;
        var runner = new DirectExecutionBackend.GuestExecutionRunner(0x1000, "dispose-test", ThreadPriority.Normal);
        runner.Schedule(() =>
        {
            host = Thread.CurrentThread;
            entered.Set();
            release.Wait();
        });
        Assert.True(entered.Wait(GateTimeout));

        // The join inside Dispose times out: the slice is still held.
        runner.Dispose();
        runner.Dispose();
        var ranAfterStop = false;
        runner.Schedule(() => ranAfterStop = true);
        release.Set();

        Assert.True(host!.Join(GateTimeout), "execution runner thread did not exit after its held work was released");
        Assert.False(ranAfterStop);
    }

    private static void ContinuationWaiterCompletesWhenStoppedMidRun()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Thread? host = null;
        var runner = new DirectExecutionBackend.GuestContinuationRunner(0x2000, ThreadPriority.Normal);
        var waiter = Task.Run(() => runner.TryRun(() =>
        {
            host = Thread.CurrentThread;
            entered.Set();
            release.Wait();
        }));
        Assert.True(entered.Wait(GateTimeout));

        runner.Dispose();
        release.Set();

        Assert.True(waiter.Wait(GateTimeout), "continuation waiter was abandoned by a stopped runner");
        Assert.True(waiter.Result);
        Assert.True(host!.Join(GateTimeout), "continuation runner thread did not exit after its held work was released");

        var late = Task.Run(() => runner.TryRun(() => { }));
        Assert.True(late.Wait(GateTimeout), "submitting to a stopped continuation runner hung");
        Assert.False(late.Result);
    }
}
