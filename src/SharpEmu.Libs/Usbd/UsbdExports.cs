// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Usbd;

/// <summary>
/// libSceUsbd exposes raw USB access to the title. SharpEmu does not pass
/// host USB devices through to the guest, so the library reports an empty bus:
/// enumeration succeeds and yields no devices, and the event pump waits out its
/// timeout instead of returning immediately. Returning immediately matters —
/// titles drive this from a dedicated polling thread, so a zero-cost call turns
/// into a spin that starves the rest of the guest.
/// </summary>
public static class UsbdExports
{
    // libusb's handle_events_timeout takes an unbounded timeout, but a guest
    // thread parked in an HLE export cannot be woken by the scheduler. Cap the
    // wait so shutdown and thread rescheduling stay responsive; the caller is a
    // polling loop and simply calls again.
    private const long MaxWaitMicroseconds = 50_000;

    [SysAbiExport(
        Nid = "TOhg7P6kTH4",
        ExportName = "sceUsbdInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdInit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Fq6+0Fm55xU",
        ExportName = "sceUsbdExit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdExit(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Writes a null device array and reports zero devices, so the caller sees a
    /// well-formed empty list rather than whatever its uninitialised local held.
    /// </summary>
    [SysAbiExport(
        Nid = "8qB9Ar4P5nc",
        ExportName = "sceUsbdGetDeviceList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdGetDeviceList(CpuContext ctx)
    {
        var listAddress = ctx[CpuRegister.Rdi];
        if (listAddress != 0 && !ctx.TryWriteUInt64(listAddress, 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EQ6SCLMqzkM",
        ExportName = "sceUsbdFreeDeviceList",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdFreeDeviceList(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "EkqGLxWC-S0",
        ExportName = "sceUsbdHandleEvents",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdHandleEvents(CpuContext ctx)
    {
        WaitForEvents(ctx, MaxWaitMicroseconds);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Honours the caller's timeval so the polling thread sleeps rather than
    /// spinning. With no devices attached there is never anything to dispatch,
    /// so the call always runs the timeout out and reports success.
    /// </summary>
    [SysAbiExport(
        Nid = "+wU6CGuZcWk",
        ExportName = "sceUsbdHandleEventsTimeout",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceUsbd")]
    public static int UsbdHandleEventsTimeout(CpuContext ctx)
    {
        WaitForEvents(ctx, ReadTimeoutMicroseconds(ctx, ctx[CpuRegister.Rdi]));
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    /// <summary>
    /// Reads a 64-bit <c>timeval</c>. A null or unreadable pointer means "poll",
    /// which libusb defines as a zero timeout.
    /// </summary>
    private static long ReadTimeoutMicroseconds(CpuContext ctx, ulong timevalAddress)
    {
        if (timevalAddress == 0)
        {
            return 0;
        }

        Span<byte> timeval = stackalloc byte[sizeof(long) * 2];
        if (!ctx.Memory.TryRead(timevalAddress, timeval))
        {
            return 0;
        }

        var seconds = BinaryPrimitives.ReadInt64LittleEndian(timeval);
        var microseconds = BinaryPrimitives.ReadInt64LittleEndian(timeval[sizeof(long)..]);
        if (seconds < 0 || microseconds < 0)
        {
            return 0;
        }

        // Clamp before multiplying: the guest supplies these and a hostile or
        // stale value would otherwise overflow.
        seconds = Math.Min(seconds, MaxWaitMicroseconds);
        microseconds = Math.Min(microseconds, MaxWaitMicroseconds);
        return Math.Min((seconds * 1_000_000) + microseconds, MaxWaitMicroseconds);
    }

    private static void WaitForEvents(CpuContext ctx, long micros)
    {
        GuestThreadExecution.Scheduler?.Pump(ctx, "sceUsbdHandleEvents");
        if (micros <= 0)
        {
            Thread.Yield();
            return;
        }

        HostTiming.SleepMicroseconds(micros);
    }
}
