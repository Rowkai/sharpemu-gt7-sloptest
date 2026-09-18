// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class ImeKeyboardTests
{
    [Fact]
    public void KeyboardOpenCompletesOnceWithNoDevicesAndCloseCancelsPendingEvent()
    {
        using var memory = new PhysicalVirtualMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryAllocateGuestMemory(32, 8, out var parameter));
        var data = new byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 0x5678);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 0x9999);
        Assert.True(memory.TryWrite(parameter, data));
        var previous = GuestThreadExecution.Scheduler;
        var scheduler = new KeyboardScheduler();
        GuestThreadExecution.Scheduler = scheduler;
        ImeExports.ResetRuntimeState();
        try
        {
            ctx[CpuRegister.Rdi] = 0xFE;
            ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(unchecked((int)0x80BC0031), ImeExports.ImeKeyboardOpen(ctx));
            ctx[CpuRegister.Rsi] = parameter;
            Assert.Equal(0, ImeExports.ImeKeyboardOpen(ctx));
            Assert.Equal(unchecked((int)0x80BC0001), ImeExports.ImeKeyboardOpen(ctx));
            ctx[CpuRegister.Rdi] = 0x1234;
            scheduler.Fail = true;
            Assert.Equal(unchecked((int)0x80BC00FF), ImeExports.ImeUpdate(ctx));
            scheduler.Fail = false;
            ctx[CpuRegister.Rdi] = 0x1234;
            Assert.Equal(0, ImeExports.ImeUpdate(ctx));
            Assert.Equal(0, ImeExports.ImeUpdate(ctx));
            Assert.Equal(1, scheduler.CallCount);
            ctx[CpuRegister.Rdi] = 0xFE;
            ctx[CpuRegister.Rsi] = parameter;
            Assert.Equal(unchecked((int)0x80BC0004), ImeExports.ImeKeyboardGetResourceId(ctx));
            Assert.True(memory.TryRead(parameter, data.AsSpan(0, 24)));
            Assert.Equal(0xFE, BinaryPrimitives.ReadInt32LittleEndian(data));
            Assert.All(data[4..24], value => Assert.Equal((byte)0, value));
            Assert.Equal(0, ImeExports.ImeKeyboardClose(ctx));
            Assert.Equal(unchecked((int)0x80BC0002), ImeExports.ImeKeyboardClose(ctx));
            data.AsSpan().Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 0x9999);
            Assert.True(memory.TryWrite(parameter, data));
            Assert.Equal(0, ImeExports.ImeKeyboardOpen(ctx));
            Assert.Equal(0, ImeExports.ImeKeyboardClose(ctx));
            ctx[CpuRegister.Rdi] = 0x1234;
            Assert.Equal(0, ImeExports.ImeUpdate(ctx));
            Assert.Equal(1, scheduler.CallCount);
        }
        finally
        {
            ImeExports.ResetRuntimeState();
            GuestThreadExecution.Scheduler = previous;
            memory.TryFreeGuestMemory(parameter);
        }
    }

    private sealed class KeyboardScheduler : IGuestThreadScheduler
    {
        public int CallCount { get; private set; }
        public bool Fail { get; set; }

        public bool SupportsGuestContextTransfer => false;

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
            error = null;
            if (Fail) return false;
            Assert.Equal(0x1234UL, entryPoint);
            Assert.Equal(0x5678UL, arg0);
            var data = new byte[72];
            Assert.True(callerContext.Memory.TryRead(arg1, data));
            Assert.Equal(0x100u, BinaryPrimitives.ReadUInt32LittleEndian(data));
            Assert.Equal(0xFE, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8)));
            Assert.All(data[12..], value => Assert.Equal((byte)0, value));
            CallCount++;
            return true;
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
            CallCount++;
            returnValue = 0;
            error = "allocator rejected the request";
            return false;
        }

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
