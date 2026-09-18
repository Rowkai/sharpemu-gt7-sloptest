// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// <c>sceVideoOutGetFlipStatus</c> reports flip <em>retirement</em>: the count is
/// flips completed, and a title recycles a display buffer when that count catches
/// up with its own submit counter. Reporting a submitted flip as completed hands
/// the buffer back a frame early.
/// <para>
/// The PS5 struct is also not the PS4 one — <c>currentBuffer</c> is at +0x38 and
/// +0x20 is reserved — so a buffer index written at +0x20 both lies about a
/// reserved field and leaves +0x38 holding the caller's stack.
/// </para>
/// </summary>
public sealed class VideoOutFlipStatusTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x200;
    private const int SceVideoOutBusTypeMain = 0;

    [Fact]
    public void FlipStatusUsesThePs5FieldOffsets()
    {
        var (memory, ctx, handle) = OpenPort();
        FillStatusBuffer(memory);

        ctx[CpuRegister.Rdi] = unchecked((ulong)(long)handle);
        ctx[CpuRegister.Rsi] = StatusAddress;
        Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(ctx));

        // currentBuffer belongs at +0x38, and is -1 before any flip retires.
        Assert.Equal(0xFFFF_FFFFUL, Read(memory, StatusAddress + 0x38) & 0xFFFF_FFFFUL);
        // +0x20 is reserved; the old layout wrote the buffer index here.
        Assert.Equal(0UL, Read(memory, StatusAddress + 0x20));
        Assert.Equal(0UL, Read(memory, StatusAddress + 0x00));
        Assert.Equal(unchecked((ulong)-1L), Read(memory, StatusAddress + 0x18));
    }

    [Fact]
    public void CountAndFlipArgTrackTheCompletedFlip()
    {
        var (memory, ctx, handle) = OpenPort();
        FillStatusBuffer(memory);

        // Headless has no render queue to order against, so the flip completes
        // inline — which is the path that must move the count.
        Assert.Equal(0, VideoOutExports.SubmitFlipFromAgc(ctx, handle, -1, 0, 0x1234));

        ctx[CpuRegister.Rdi] = unchecked((ulong)(long)handle);
        ctx[CpuRegister.Rsi] = StatusAddress;
        Assert.Equal(0, VideoOutExports.VideoOutGetFlipStatus(ctx));

        Assert.Equal(1UL, Read(memory, StatusAddress + 0x00));
        Assert.Equal(0x1234UL, Read(memory, StatusAddress + 0x18));
        // gcQueueNum and flipPendingNum share this qword: nothing is outstanding.
        Assert.Equal(0UL, Read(memory, StatusAddress + 0x30));
    }

    [Fact]
    public void NoFlipIsPendingOnceItHasRetired()
    {
        var (_, ctx, handle) = OpenPort();
        Assert.Equal(0, VideoOutExports.SubmitFlipFromAgc(ctx, handle, -1, 0, 7));

        ctx[CpuRegister.Rdi] = unchecked((ulong)(long)handle);
        Assert.Equal(0, VideoOutExports.VideoOutIsFlipPending(ctx));
    }

    private static (FakeCpuMemory Memory, CpuContext Context, int Handle) OpenPort()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = SceVideoOutBusTypeMain;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        var handle = VideoOutExports.VideoOutOpen(ctx);
        Assert.True(handle > 0, $"sceVideoOutOpen returned {handle}");
        return (memory, ctx, handle);
    }

    /// <summary>
    /// The caller never pre-zeroes the status buffer, so an unwritten field reads
    /// back as stack garbage. Seeding it makes "never written here" visible.
    /// </summary>
    private static void FillStatusBuffer(FakeCpuMemory memory)
    {
        Span<byte> seed = stackalloc byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(seed, 0xCCCC_CCCC_CCCC_CCCCUL);
        for (var offset = 0UL; offset < 0x48; offset += 8)
        {
            Assert.True(memory.TryWrite(StatusAddress + offset, seed));
        }
    }

    private static ulong Read(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
