// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerFileReplacementTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong PathAddress = Base + 0x100;
    private const ulong BufferAddress = Base + 0x1000;
    private const int BufferLength = 2048;

    // A title whose media lives inside its own archive serves it through the
    // SceAvPlayerFileReplacement callbacks; the bytes must arrive unchanged.
    [Fact]
    public void SourceIsReadBackThroughTheGuestFileCallbacks()
    {
        var memory = new FakeCpuMemory(Base, 0x20000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var media = new byte[5000];
        new System.Random(7).NextBytes(media);
        var scheduler = new FileScheduler(media);
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            using var destination = new MemoryStream();
            Assert.True(AvPlayerExports.TryReadGuestSourceThroughCallbacks(
                ctx,
                fileObject: 0x1234,
                openCallback: FileScheduler.Open,
                closeCallback: FileScheduler.Close,
                readOffsetCallback: FileScheduler.ReadOffset,
                sizeCallback: FileScheduler.Size,
                PathAddress,
                BufferAddress,
                BufferLength,
                destination,
                out var error));

            Assert.Null(error);
            Assert.Equal(media, destination.ToArray());
            Assert.Equal(0x1234UL, scheduler.FileObject);
            Assert.Equal(PathAddress, scheduler.PathAddress);
            Assert.Equal(1, scheduler.Opens);
            Assert.Equal(1, scheduler.Closes);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void RefusedOpenFails()
    {
        var memory = new FakeCpuMemory(Base, 0x20000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var scheduler = new FileScheduler([1, 2, 3]) { OpenResult = -1 };
        var previous = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            using var destination = new MemoryStream();
            Assert.False(AvPlayerExports.TryReadGuestSourceThroughCallbacks(
                ctx,
                fileObject: 0x1234,
                openCallback: FileScheduler.Open,
                closeCallback: FileScheduler.Close,
                readOffsetCallback: FileScheduler.ReadOffset,
                sizeCallback: FileScheduler.Size,
                PathAddress,
                BufferAddress,
                BufferLength,
                destination,
                out var error));

            Assert.Contains("open returned -1", error);
            Assert.Empty(destination.ToArray());
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    private sealed class FileScheduler(byte[] media) : IGuestThreadScheduler
    {
        public const ulong Open = 0x8000_1000;
        public const ulong Close = 0x8000_2000;
        public const ulong ReadOffset = 0x8000_3000;
        public const ulong Size = 0x8000_4000;

        public int OpenResult { get; init; }

        public int Opens { get; private set; }

        public int Closes { get; private set; }

        public ulong FileObject { get; private set; }

        public ulong PathAddress { get; private set; }

        public bool SupportsGuestContextTransfer => false;

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong arg3,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            error = null;
            returnValue = 0;
            FileObject = arg0;
            switch (entryPoint)
            {
                case Open:
                    Opens++;
                    PathAddress = arg1;
                    returnValue = unchecked((ulong)(long)OpenResult);
                    return true;
                case Close:
                    Closes++;
                    return true;
                case Size:
                    returnValue = (ulong)media.Length;
                    return true;
                case ReadOffset:
                {
                    var position = (int)arg2;
                    var length = Math.Min((int)arg3, media.Length - position);
                    if (length <= 0)
                    {
                        return true;
                    }

                    Assert.True(callerContext.Memory.TryWrite(arg1, media.AsSpan(position, length)));
                    returnValue = (ulong)length;
                    return true;
                }
                default:
                    error = "unexpected callback";
                    return false;
            }
        }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(
            CpuContext creatorContext,
            GuestThreadStartRequest request,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
