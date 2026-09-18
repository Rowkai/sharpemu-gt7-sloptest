// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Hmd2;

/// <summary>
/// libSceHmd2 — the head-mounted display service. No device is ever attached
/// under SharpEmu. The library coming up and a headset being present are
/// separate things: a title initialises the library, then asks separately what
/// is connected.
/// </summary>
public static class Hmd2Exports
{
    // sceHmd2Initialize(SceHmd2InitializeParam *param): brings the HMD library
    // up. A title loads the module with sceSysmoduleLoadModule first, then
    // calls this, then queries the device — so this returning an error means
    // the library is unavailable, not that the headset is absent.
    //
    // Leaving it unresolved is actively harmful rather than neutral: an
    // unresolved import returns ORBIS_GEN2_ERROR_NOT_FOUND, and a title that
    // branches on the result treats that as "no HMD library" and skips
    // everything it would have set up afterwards. The parameter block is an
    // input the caller has already filled in, so nothing is written back.
    [SysAbiExport(
        Nid = "c812oYs7Vsc",
        ExportName = "sceHmd2Initialize",
        Target = Generation.Gen5,
        LibraryName = "libSceHmd2")]
    public static int Hmd2Initialize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // The record's true size is not known from any SDK header available here,
    // and neither PS5 reference implementation (KytyPS5, prosper) carries this
    // library at all. The bound comes from the calling title: a caller reads
    // offset +0x14, and the next distinct object in a caller's static layout
    // sits 0x1C bytes after the record it passes. 0x18 covers every field
    // observed in use and stays inside that bound, so a short record cannot be
    // overrun.
    private const int DeviceInformationClearedBytes = 0x18;

    // sceHmd2GetDeviceInformation(SceHmd2DeviceInformation *out): reports what
    // is attached. With nothing connected this succeeds and reports an empty
    // record rather than failing.
    //
    // The evidence for that split is the caller's own branch, which is the
    // better source here than a guess at an error code:
    //
    //     call  sceHmd2GetDeviceInformation(&info)
    //     test  eax, eax
    //     jne   no_device
    //     cmp   byte [info+0x14], 0
    //     jne   device_present
    //
    // The presence byte at +0x14 only earns its place if a successful call can
    // still mean "nothing attached" — if absence were reported through the
    // return code, that second test would be dead. Callers read the flag
    // straight out of the record, so it must be cleared rather than left as the
    // caller's stack garbage.
    [SysAbiExport(
        Nid = "bIi4YUfSRys",
        ExportName = "sceHmd2GetDeviceInformation",
        Target = Generation.Gen5,
        LibraryName = "libSceHmd2")]
    public static int Hmd2GetDeviceInformation(CpuContext ctx)
    {
        var informationAddress = ctx[CpuRegister.Rdi];
        if (informationAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> cleared = stackalloc byte[DeviceInformationClearedBytes];
        cleared.Clear();
        if (!ctx.Memory.TryWrite(informationAddress, cleared))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    // PSVR2's panel is 2000x2040 per eye, and the reprojection display buffer is
    // the single surface covering both eyes: 4000x2040. The panel geometry is
    // public; the surface's format and tiling below are inference from what the
    // buffer is for, not a measured firmware layout.
    private const uint ReprojectionPanelWidth = 4000;
    private const uint ReprojectionPanelHeight = 2040;

    // The display buffer is a render target the reprojection engine scans out, so
    // it is 64 KiB-block tiled at 10 bits per channel (4 bytes per pixel). GFX10's
    // thin 64 KiB block for a 4-byte element is 128x128 elements, and a tiled
    // surface is padded up to whole blocks, so 4096 x 2048 x 4 = 32 MiB aligned to
    // one block. Expressed as the derivation rather than the constant because the
    // guest turns both numbers straight into an allocation.
    private const uint ReprojectionTileBlockWidth = 128;
    private const uint ReprojectionTileBlockHeight = 128;
    private const uint ReprojectionDisplayBytesPerPixel = 4;
    private const ulong ReprojectionTileBlockAlignment = 0x10000;

    // The work buffer is scratch the title allocates from its own heap and hands
    // to sceHmd2ReprojectionInitialize. SharpEmu attaches no headset and runs no
    // reprojection engine, so nothing is ever stored there; the size only has to
    // be an allocation the title can actually make. One 16 KiB page. The hardware
    // figure is unknown.
    private const ulong ReprojectionWorkBufferSize = 0x4000;
    private const ulong ReprojectionWorkBufferAlignment = 0x4000;

    private static ulong ReprojectionDisplayBufferSize =>
        (ulong)AlignUp(ReprojectionPanelWidth, ReprojectionTileBlockWidth) *
        AlignUp(ReprojectionPanelHeight, ReprojectionTileBlockHeight) *
        ReprojectionDisplayBytesPerPixel;

    private static uint AlignUp(uint value, uint alignment) =>
        (value + alignment - 1) / alignment * alignment;

    // sceHmd2ReprojectionQueryBufferSizeAlign(void) and its display counterpart
    // return a 16-byte { size, alignment } aggregate — RAX:RDX, no arguments.
    //
    // Leaving either unresolved is not a no-op. GT7's display init reads the pair
    // without checking a status: the work size goes straight to its aligned heap
    // allocator, and the display size becomes
    // sceKernelAllocateDirectMemory(len = roundup(size, max(0x4000, align)),
    // alignment = max(0x200000, align)). With the import unresolved RAX is the
    // sign-extended ORBIS_GEN2_ERROR_NOT_FOUND and RDX is leftover argument
    // state, which is where the observed 6 GiB direct-memory request came from.
    [SysAbiExport(
        Nid = "U-CnbmeyYaA",
        ExportName = "sceHmd2ReprojectionQueryBufferSizeAlign",
        Target = Generation.Gen5,
        LibraryName = "libSceHmd2")]
    public static int Hmd2ReprojectionQueryBufferSizeAlign(CpuContext ctx) =>
        ctx.SetReturnPair(ReprojectionWorkBufferSize, ReprojectionWorkBufferAlignment);

    [SysAbiExport(
        Nid = "-C2nkoEYOnU",
        ExportName = "sceHmd2ReprojectionQueryDisplayBufferSizeAlign",
        Target = Generation.Gen5,
        LibraryName = "libSceHmd2")]
    public static int Hmd2ReprojectionQueryDisplayBufferSizeAlign(CpuContext ctx) =>
        ctx.SetReturnPair(ReprojectionDisplayBufferSize, ReprojectionTileBlockAlignment);
}
