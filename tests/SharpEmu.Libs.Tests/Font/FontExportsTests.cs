// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong LayoutAddress = Base + 0x100;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    // SceFontHorizontalLayout is three floats; the sentinel directly after
    // them must survive the call.
    [Fact]
    public void GetHorizontalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }

    [Fact]
    public void GetVerticalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetVerticalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(8.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }

    [Fact]
    public void GetVerticalLayout_NullBuffer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            FontExports.GetVerticalLayout(_ctx));
    }

    // SceFontKerning is four floats - offsetX, offsetY, positionX, positionY.
    // A result that defines only three leaves the caller's fourth field holding
    // whatever was on its stack.
    [Fact]
    public void GetKerning_WritesExactlyFourFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 16, sentinelBytes));

        _ctx[CpuRegister.Rcx] = LayoutAddress;
        Assert.Equal(0, FontExports.GetKerning(_ctx));

        for (ulong offset = 0; offset < 16; offset += sizeof(uint))
        {
            Assert.True(_ctx.TryReadUInt32(LayoutAddress + offset, out var written));
            Assert.Equal(0u, written);
        }

        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 16, out var beyond));
        Assert.Equal(Sentinel, beyond);
    }

    // The scissor rectangle is clamped to the surface: the stored bounds are
    // unsigned, so a rectangle starting left of the surface must clip to zero
    // rather than wrap to a huge value.
    [Fact]
    public void RenderSurfaceSetScissor_ClampsToTheSurface()
    {
        InitSurface(width: 100, height: 50);

        _ctx[CpuRegister.Rdi] = LayoutAddress;
        _ctx[CpuRegister.Rsi] = unchecked((ulong)(long)(-10));
        _ctx[CpuRegister.Rdx] = 10;
        _ctx[CpuRegister.Rcx] = 40;
        _ctx[CpuRegister.R8] = 400;

        Assert.Equal(0, FontExports.RenderSurfaceSetScissor(_ctx));

        AssertScissor(x0: 0, y0: 10, x1: 30, y1: 50);
    }

    [Fact]
    public void RenderSurfaceSetScissor_StartingPastTheSurface_LeavesAnEmptyRectangle()
    {
        InitSurface(width: 100, height: 50);

        _ctx[CpuRegister.Rdi] = LayoutAddress;
        _ctx[CpuRegister.Rsi] = 200;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 10;
        _ctx[CpuRegister.R8] = 10;

        Assert.Equal(0, FontExports.RenderSurfaceSetScissor(_ctx));

        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 0x18, out var x0));
        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 0x20, out var x1));
        Assert.Equal(x0, x1);
    }

    // A one-glyph OpenType CFF font: 'A' is a 500-unit square from (100,100)
    // in a 1000-unit em. At 10 px per em it is a 5 px square whose top sits
    // 6 px above the baseline, with a 7 px advance.
    [Fact]
    public void CffFont_ReportsOutlineMetricsAndRendersCoverage()
    {
        var memory = new FakeCpuMemory(Base, 0x10000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var font = BuildSquareFont();
        Assert.True(memory.TryWrite(Base + 0x2000, font));

        ctx[CpuRegister.Rsi] = Base + 0x2000;
        ctx[CpuRegister.Rdx] = (ulong)font.Length;
        ctx[CpuRegister.R8] = Base + 0x100;
        Assert.Equal(0, FontExports.OpenFontMemory(ctx));
        Assert.True(ctx.TryReadUInt64(Base + 0x100, out var handle));

        ctx[CpuRegister.Rdi] = handle;
        SetXmmSingle(ctx, 0, 10f);
        SetXmmSingle(ctx, 1, 10f);
        Assert.Equal(0, FontExports.SetScalePixel(ctx));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 'A';
        ctx[CpuRegister.Rdx] = Base + 0x180;
        Assert.Equal(0, FontExports.GetCharGlyphMetrics(ctx));
        float[] expected = [5, 5, 1, 6, 7];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(ctx.TryReadUInt32(Base + 0x180 + (ulong)(index * 4), out var bits));
            Assert.Equal(expected[index], BitConverter.UInt32BitsToSingle(bits), 0.001f);
        }

        // 16x16 one-byte surface; a pen at (0, 10) covers columns 1-5, rows 4-8.
        ctx[CpuRegister.Rdi] = Base + 0x200;
        ctx[CpuRegister.Rsi] = Base + 0x800;
        ctx[CpuRegister.Rdx] = 16;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = 16;
        ctx[CpuRegister.R9] = 16;
        Assert.Equal(0, FontExports.RenderSurfaceInit(ctx));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 'A';
        ctx[CpuRegister.Rdx] = Base + 0x200;
        ctx[CpuRegister.Rcx] = Base + 0x180;
        ctx[CpuRegister.R8] = 0;
        SetXmmSingle(ctx, 0, 0f);
        SetXmmSingle(ctx, 1, 10f);
        Assert.Equal(0, FontExports.RenderCharGlyphImageHorizontal(ctx));

        byte Pixel(int x, int y)
        {
            Assert.True(ctx.TryReadByte(Base + 0x800 + (ulong)(y * 16 + x), out var value));
            return value;
        }

        Assert.Equal(255, Pixel(3, 6));
        Assert.Equal(255, Pixel(1, 4));
        Assert.Equal(0, Pixel(0, 6));
        Assert.Equal(0, Pixel(6, 6));
        Assert.Equal(0, Pixel(3, 3));
        Assert.Equal(0, Pixel(3, 9));
    }

    private static void SetXmmSingle(CpuContext ctx, int index, float value) =>
        ctx.SetXmmRegister(index, BitConverter.SingleToUInt32Bits(value), 0);

    private static byte[] BuildSquareFont()
    {
        // 100 100 rmoveto 500 hlineto 500 vlineto -500 hlineto endchar
        byte[] square = [0xEF, 0xEF, 0x15, 0xF8, 0x88, 0x06, 0xF8, 0x88, 0x07, 0xFC, 0x88, 0x06, 0x0E];
        // Header(4) + Name INDEX(6) + Top DICT INDEX(22) + String and Global
        // Subr INDEXes(4) put CharStrings at 36; the empty Private DICT follows.
        const int CharStringsOffset = 36;
        var privateOffset = CharStringsOffset + 6 + 1 + square.Length;
        var cff = new List<byte> { 1, 0, 4, 1, 0, 1, 1, 1, 2, (byte)'A' };
        byte[] topDict = [29, .. BigEndian(CharStringsOffset), 17, 29, .. BigEndian(0), 29, .. BigEndian(privateOffset), 18];
        cff.AddRange([0, 1, 1, 1, (byte)(topDict.Length + 1)]);
        cff.AddRange(topDict);
        cff.AddRange([0, 0, 0, 0]);
        cff.AddRange([0, 2, 1, 1, 2, (byte)(2 + square.Length), 0x0E]);
        cff.AddRange(square);

        var head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), 2);
        byte[] hmtx = [0x01, 0xF4, 0, 0, 0x02, 0xBC, 0, 100];
        // cmap: one (3,1) format 4 subtable mapping U+0041 to glyph 1.
        byte[] cmap =
        [
            0, 0, 0, 1, 0, 3, 0, 1, 0, 0, 0, 12,
            0, 4, 0, 32, 0, 0, 0, 4, 0, 4, 0, 1, 0, 0,
            0, 0x41, 0xFF, 0xFF, 0, 0, 0, 0x41, 0xFF, 0xFF, 0xFF, 0xC0, 0, 1, 0, 0, 0, 0,
        ];

        (uint Tag, byte[] Data)[] tables =
        [
            (0x636D6170, cmap), (0x68656164, head), (0x68686561, hhea), (0x686D7478, hmtx), (0x43464620, cff.ToArray()),
        ];
        var directorySize = 12 + 16 * tables.Length;
        var font = new byte[directorySize + tables.Sum(table => table.Data.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(font, 0x4F54544F);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4), (ushort)tables.Length);
        var offset = directorySize;
        for (var index = 0; index < tables.Length; index++)
        {
            var record = 12 + 16 * index;
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record), tables[index].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 12), (uint)tables[index].Data.Length);
            tables[index].Data.CopyTo(font, offset);
            offset += tables[index].Data.Length;
        }

        return font;
    }

    private static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private void InitSurface(uint width, uint height)
    {
        _ctx[CpuRegister.Rdi] = LayoutAddress;
        _ctx[CpuRegister.Rsi] = Base + 0x800;
        _ctx[CpuRegister.Rdx] = width * 4;
        _ctx[CpuRegister.Rcx] = 4;
        _ctx[CpuRegister.R8] = width;
        _ctx[CpuRegister.R9] = height;
        Assert.Equal(0, FontExports.RenderSurfaceInit(_ctx));
    }

    private void AssertScissor(uint x0, uint y0, uint x1, uint y1)
    {
        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 0x18, out var storedX0));
        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 0x1C, out var storedY0));
        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 0x20, out var storedX1));
        Assert.True(_ctx.TryReadUInt32(LayoutAddress + 0x24, out var storedY1));
        Assert.Equal((x0, y0, x1, y1), (storedX0, storedY0, storedX1, storedY1));
    }
}
