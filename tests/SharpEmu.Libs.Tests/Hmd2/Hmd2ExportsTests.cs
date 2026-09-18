// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Hmd2;
using Xunit;

namespace SharpEmu.Libs.Tests.Hmd2;

public sealed class Hmd2ExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong DirectMemoryPageSize = 0x4000;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public Hmd2ExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    // 4000x2040 padded to 128x128 blocks is 4096x2048 at 4 bytes per pixel.
    [Fact]
    public void ReprojectionQueryDisplayBufferSizeAlign_ReturnsTheTiledPanelSurface()
    {
        Assert.Equal(0, Hmd2Exports.Hmd2ReprojectionQueryDisplayBufferSizeAlign(_ctx));
        Assert.Equal(0x2000000UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0x10000UL, _ctx[CpuRegister.Rdx]);
    }

    [Fact]
    public void ReprojectionQueryBufferSizeAlign_ReturnsAnAllocatableWorkBuffer()
    {
        Assert.Equal(0, Hmd2Exports.Hmd2ReprojectionQueryBufferSizeAlign(_ctx));
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(0x4000UL, _ctx[CpuRegister.Rdx]);
    }

    // Both queries return { size, alignment } as a 16-byte aggregate, which SysV
    // splits across RAX:RDX. Without the flag the import trampoline restores the
    // guest's own RDX and the alignment never arrives.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReprojectionQueries_ReportAPairReturn(bool display)
    {
        _ctx.ClearRaxWriteFlag();
        Assert.Equal(
            0,
            display
                ? Hmd2Exports.Hmd2ReprojectionQueryDisplayBufferSizeAlign(_ctx)
                : Hmd2Exports.Hmd2ReprojectionQueryBufferSizeAlign(_ctx));
        Assert.True(_ctx.WasReturnPairWritten);
        Assert.True(_ctx.WasRaxWritten);
    }

    // The regression this export exists for: GT7's display init (sub_800FEE780)
    // turns the display pair into
    // sceKernelAllocateDirectMemory(len = roundup(size, max(0x4000, align)),
    // alignment = max(0x200000, align)) without checking a status. Both have to be
    // multiples of the 16 KiB direct-memory page or the kernel rejects the call —
    // which is what the unresolved import used to produce.
    [Fact]
    public void ReprojectionDisplayQuery_YieldsADirectMemoryRequestTheKernelAccepts()
    {
        Assert.Equal(0, Hmd2Exports.Hmd2ReprojectionQueryDisplayBufferSizeAlign(_ctx));
        var size = _ctx[CpuRegister.Rax];
        var alignment = _ctx[CpuRegister.Rdx];

        var roundTo = Math.Max(DirectMemoryPageSize, alignment);
        var length = (size + roundTo - 1) & ~(roundTo - 1);
        var requestedAlignment = Math.Max(0x200000UL, alignment);

        Assert.Equal(0UL, length % DirectMemoryPageSize);
        Assert.Equal(0UL, requestedAlignment % DirectMemoryPageSize);
        Assert.InRange(length, size, size * 2);
    }
}
