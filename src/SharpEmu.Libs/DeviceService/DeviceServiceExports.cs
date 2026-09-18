// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.DeviceService;

/// <summary>
/// libSceDeviceService — enumeration of attached peripherals. SharpEmu exposes
/// no devices through this service, so every class enumerates empty. That is a
/// different answer from the one an unresolved import gives: reporting zero
/// devices means "nothing of that class is attached", while
/// ORBIS_GEN2_ERROR_NOT_FOUND means "this library does not exist", and callers
/// distinguish the two.
/// </summary>
public static class DeviceServiceExports
{
    // sceDeviceServiceQueryDeviceInfo_(uint32_t deviceClass, uint32_t, uint32_t,
    //     void *records, uint32_t maxRecords, uint32_t *outCount,
    //     uint32_t *outExtraCount, size_t recordSize)
    //
    // The signature is recovered from the only caller in GT7, which asks for
    // class 0x7001 with room for a single 0x70-byte record:
    //
    //     lea  r9,  [rbp-0x5b0]      ; outCount, pre-zeroed by the caller
    //     lea  r10, [rbp-0x600]      ; outExtraCount, pre-zeroed
    //     lea  rcx, [rbp-0x3f0]      ; record buffer
    //     mov  edi, 0x7001           ; device class
    //     mov  r8d, 1                ; room for one record
    //     push 0x70                  ; record size
    //     push r10
    //     call sceDeviceServiceQueryDeviceInfo_
    //     test eax, eax
    //     js   give_up               ; a negative return abandons the pass
    //     cmp  dword [rbp-0x5b0], 0  ; only then is the count examined
    //
    // A negative return is therefore not equivalent to an empty result: it
    // skips the count entirely, and with it the "no devices" notification the
    // caller sends on the zero path.
    //
    // No record is written because none is reported. What device class 0x7001
    // denotes, and the layout of the record, are both still unknown — nothing
    // here depends on either, and neither should be invented until a device is
    // actually modelled.
    [SysAbiExport(
        Nid = "UNMEa+5lrUA",
        ExportName = "sceDeviceServiceQueryDeviceInfo_",
        Target = Generation.Gen5,
        LibraryName = "libSceDeviceService")]
    public static int DeviceServiceQueryDeviceInfo(CpuContext ctx)
    {
        var deviceClass = unchecked((uint)ctx[CpuRegister.Rdi]);
        var maxRecords = unchecked((uint)ctx[CpuRegister.R8]);
        var countAddress = ctx[CpuRegister.R9];
        var extraCountAddress = ReadStackArg64(ctx, 0);
        var recordSize = ReadStackArg64(ctx, 1);

        if (countAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryWriteUInt32(ctx, countAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (extraCountAddress != 0 && !TryWriteUInt32(ctx, extraCountAddress, 0))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceDeviceService(
            $"query_device_info class=0x{deviceClass:X} max={maxRecords} " +
            $"record_size=0x{recordSize:X} count=0");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // sceDeviceServiceGetEventState(uint32_t group): reports pending
    // device-change events for a group. Nothing is ever attached or detached
    // under SharpEmu, so no event is ever pending.
    //
    // GT7's caller polls this once a second and re-enumerates whenever any of
    // the low two bits is set:
    //
    //     mov  edi, 1
    //     call sceDeviceServiceGetEventState
    //     test al, 3
    //     jne  enumerate_again
    //
    // Unresolved, the NOT_FOUND error code happens to have one of those bits
    // set, so the title re-enumerated every second in response to an error it
    // was reading as an event mask. Zero is the only value here that cannot
    // invent a hotplug; what the individual bits mean is not known, and nothing
    // should depend on that until a device is actually modelled.
    [SysAbiExport(
        Nid = "9ddRUOV8Q5A",
        ExportName = "sceDeviceServiceGetEventState",
        Target = Generation.Gen5,
        LibraryName = "libSceDeviceService")]
    public static int DeviceServiceGetEventState(CpuContext ctx)
    {
        var group = unchecked((uint)ctx[CpuRegister.Rdi]);
        TraceDeviceService($"get_event_state group={group} state=0");
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static ulong ReadStackArg64(CpuContext ctx, int index)
    {
        // The return address occupies [rsp]; stack arguments start above it.
        var address = ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong));
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        return ctx.Memory.TryRead(address, buffer)
            ? BinaryPrimitives.ReadUInt64LittleEndian(buffer)
            : 0;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static void TraceDeviceService(string message)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_DEVICE_SERVICE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] device_service.{message}");
    }
}
