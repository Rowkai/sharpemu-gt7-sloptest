// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Font;

public static class FontExports
{
    private const ushort GlyphMagic = 0x0F03;
    private const int GlyphSize = 0x100;
    private const int GlyphMetricsSize = 8 * sizeof(float);
    private const int RenderOutputSize = 0x40;

    // Metrics reported for handles with no parsed font (system font sets) and
    // for character codes the font does not map.
    private static readonly float[] PlaceholderMetrics = [8.0f, 16.0f, 0.0f, 12.0f, 8.0f, 0.0f, 0.0f, 16.0f];

    private static readonly object AllocationGate = new();
    private static readonly Stack<ulong> FreeGlyphs = new();
    private static readonly Dictionary<ulong, LoadedFont> Fonts = new();

    // SHARPEMU_LOG_FONT=1: which handles carry parsed outlines, and what each
    // glyph render resolved to.
    private static readonly bool LogFont = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_FONT"),
        "1",
        StringComparison.Ordinal);
    private static ulong _librarySelectionAddress;
    private static ulong _rendererSelectionAddress;

    [SysAbiExport(
        Nid = "whrS4oksXc4",
        ExportName = "sceFontMemoryInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int MemoryInit(CpuContext ctx)
    {
        var descriptorAddress = ctx[CpuRegister.Rdi];
        var regionAddress = ctx[CpuRegister.Rsi];
        var regionSize = (uint)ctx[CpuRegister.Rdx];
        var interfaceAddress = ctx[CpuRegister.Rcx];
        var mspaceAddress = ctx[CpuRegister.R8];
        var destroyCallback = ctx[CpuRegister.R9];
        if (descriptorAddress == 0 ||
            !TryWriteUInt32(ctx, descriptorAddress, 0x00000F00) ||
            !TryWriteUInt32(ctx, descriptorAddress + 0x04, regionSize) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x08, regionAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x10, mspaceAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x18, interfaceAddress) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x20, destroyCallback) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x28, 0) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x30, 0) ||
            !ctx.TryWriteUInt64(descriptorAddress + 0x38, mspaceAddress))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "oM+XCzVG3oM",
        ExportName = "sceFontSelectLibraryFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectLibraryFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _librarySelectionAddress, 0x38);

    [SysAbiExport(
        Nid = "Xx974EW-QFY",
        ExportName = "sceFontSelectRendererFt",
        Target = Generation.Gen5,
        LibraryName = "libSceFontFt")]
    public static int SelectRendererFt(CpuContext ctx) =>
        ReturnSelection(ctx, ref _rendererSelectionAddress, 0x100);

    [SysAbiExport(
        Nid = "n590hj5Oe-k",
        ExportName = "sceFontCreateLibraryWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateLibraryWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], 0x100, magic: 0x0F01);

    [SysAbiExport(
        Nid = "WaSFJoRWXaI",
        ExportName = "sceFontCreateRendererWithEdition",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CreateRendererWithEdition(CpuContext ctx) =>
        CreateOpaqueHandle(ctx, ctx[CpuRegister.Rcx], 0x100, magic: 0x0F07);

    [SysAbiExport(
        Nid = "3OdRkSjOcog",
        ExportName = "sceFontBindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int BindRenderer(CpuContext ctx) => SetSuccess(ctx);

    // Swaps the renderer attached to an already-open font. Nothing in the
    // stubbed pipeline is tied to a particular renderer instance, so this is
    // the same no-op acknowledgement as the initial bind.
    [SysAbiExport(
        Nid = "Z2cdsqJH+5k",
        ExportName = "sceFontRebindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RebindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "N1EBMeGhf7E",
        ExportName = "sceFontSetScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetScalePixel(CpuContext ctx)
    {
        // sceFontSetScalePixel(font, float w, float h): the em size in pixels.
        if (TryGetFont(ctx[CpuRegister.Rdi], out var font))
        {
            font.ScaleWidth = ReadXmmSingle(ctx, 0);
            font.ScaleHeight = ReadXmmSingle(ctx, 1);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "TMtqoFQjjbA",
        ExportName = "sceFontSetEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectSlant(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "v0phZwa4R5o",
        ExportName = "sceFontSetEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetEffectWeight(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "6vGCkkQJOcI",
        ExportName = "sceFontSetupRenderScalePixel",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderScalePixel(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "lz9y9UFO2UU",
        ExportName = "sceFontSetupRenderEffectSlant",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectSlant(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "XIGorvLusDQ",
        ExportName = "sceFontSetupRenderEffectWeight",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SetupRenderEffectWeight(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "imxVx8lm+KM",
        ExportName = "sceFontGetHorizontalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetHorizontalLayout(CpuContext ctx)
    {
        var layoutAddress = ctx[CpuRegister.Rsi];
        if (layoutAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Baseline, line advance, decoration extent: the same invented geometry
        // as GetRenderCharGlyphMetrics.
        var values = new[] { 12.0f, 16.0f, 0.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    layoutAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "3BrWWFU+4ts",
        ExportName = "sceFontGetVerticalLayout",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetVerticalLayout(CpuContext ctx)
    {
        var layoutAddress = ctx[CpuRegister.Rsi];
        if (layoutAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Baseline (horizontal offset), line advance, decoration extent.
        // Mirrors the same three-float layout as GetHorizontalLayout, but
        // interpreted for vertical writing (e.g. CJK text rendered top-to-bottom).
        var values = new[] { 8.0f, 16.0f, 0.0f };
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    layoutAddress + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "cKYtVmeSTcw",
        ExportName = "sceFontOpenFontSet",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontSet(CpuContext ctx)
    {
        var result = CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);
        if (LogFont && ctx.TryReadUInt64(ctx[CpuRegister.R8], out var handle))
        {
            Console.Error.WriteLine($"[FONT] open_set handle=0x{handle:X} type=0x{ctx[CpuRegister.Rsi]:X}");
        }

        return result;
    }

    [SysAbiExport(
        Nid = "KXUpebrFk1U",
        ExportName = "sceFontOpenFontMemory",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontMemory(CpuContext ctx)
    {
        // sceFontOpenFontMemory(library, fontAddress, fontSize, openParam, font*).
        // The font bytes are parsed once so metrics and glyph images come from
        // the real outlines; data that does not parse keeps the placeholders.
        var result = CreateOpaqueHandle(ctx, ctx[CpuRegister.R8], 0x100, magic: 0x0F02);
        var size = ctx[CpuRegister.Rdx];
        if (result != 0 || size == 0 || size > int.MaxValue)
        {
            return result;
        }

        var data = new byte[(int)size];
        OpenTypeFont? font = null;
        var parsed = ctx.Memory.TryRead(ctx[CpuRegister.Rsi], data) && OpenTypeFont.TryParse(data, out font);
        if (!ctx.TryReadUInt64(ctx[CpuRegister.R8], out var handle))
        {
            return result;
        }

        if (parsed)
        {
            lock (Fonts)
            {
                Fonts[handle] = new LoadedFont(font!);
            }
        }

        if (LogFont)
        {
            Console.Error.WriteLine($"[FONT] open_memory handle=0x{handle:X} size=0x{size:X} parsed={parsed}");
        }

        return result;
    }

    [SysAbiExport(
        Nid = "JzCH3SCFnAU",
        ExportName = "sceFontOpenFontInstance",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int OpenFontInstance(CpuContext ctx)
    {
        var sourceHandle = ctx[CpuRegister.Rdi];
        var setupHandle = ctx[CpuRegister.Rsi];
        var outputAddress = ctx[CpuRegister.Rdx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (setupHandle != 0)
        {
            return ctx.TryWriteUInt64(outputAddress, setupHandle)
                ? SetSuccess(ctx)
                : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryAllocateOpaque(ctx, 0x100, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (sourceHandle != 0)
        {
            Span<byte> source = stackalloc byte[0x100];
            if (ctx.Memory.TryRead(sourceHandle, source))
            {
                _ = ctx.Memory.TryWrite(handle, source);
            }
        }

        _ = TryWriteUInt16(ctx, handle, 0x0F02);
        return ctx.TryWriteUInt64(outputAddress, handle)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "SsRbbCiWoGw",
        ExportName = "sceFontSupportSystemFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportSystemFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "mz2iTY0MK4A",
        ExportName = "sceFontSupportExternalFonts",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int SupportExternalFonts(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "CUKn5pX-NVY",
        ExportName = "sceFontAttachDeviceCacheBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int AttachDeviceCacheBuffer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "IQtleGLL5pQ",
        ExportName = "sceFontGetRenderCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetRenderCharGlyphMetrics(CpuContext ctx) =>
        WriteGlyphMetrics(ctx, ctx[CpuRegister.Rdi], (uint)ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);

    // The non-render variant reports the same SceFontGlyphMetrics for a
    // character code, without needing a renderer bound. Callers lay text out
    // from these values, so they must be populated even for fonts with no
    // parsed outlines — leaving the struct untouched leaves the caller
    // measuring whatever its stack held.
    [SysAbiExport(
        Nid = "L97d+3OgMlE",
        ExportName = "sceFontGetCharGlyphMetrics",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetCharGlyphMetrics(CpuContext ctx) =>
        WriteGlyphMetrics(ctx, ctx[CpuRegister.Rdi], (uint)ctx[CpuRegister.Rsi], ctx[CpuRegister.Rdx]);

    private static int WriteGlyphMetrics(CpuContext ctx, ulong fontHandle, uint code, ulong metricsAddress)
    {
        if (metricsAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var metrics = FindGlyph(fontHandle, code)?.ToMetrics() ?? PlaceholderMetrics;
        return TryWriteFloats(ctx, metricsAddress, metrics)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    // sceFontGetKerning(font, ucode1, ucode2, kerning*): the kerning offsets for an
    // adjacent character pair. Titles lay text out one pair at a time and treat a
    // non-zero return as a layout failure, so an unresolved import abandons layout
    // for every pair. Both observed call sites read the pair at +0x00 and +0x08,
    // so those are what a successful result has to define.
    // A font with no kerning entry for the pair reports zero offsets and succeeds,
    // which is the correct answer whenever no kerning table is loaded.
    // SceFontKerning is four floats - offsetX, offsetY, positionX, positionY -
    // so a result has to define all sixteen bytes, not the twelve the observed
    // reads cover.
    [SysAbiExport(
        Nid = "sDuhHGNhHvE",
        ExportName = "sceFontGetKerning",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GetKerning(CpuContext ctx)
    {
        var kerningAddress = ctx[CpuRegister.Rcx];
        if (kerningAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        for (var offset = 0ul; offset < 16ul; offset += sizeof(uint))
        {
            if (!TryWriteUInt32(ctx, kerningAddress + offset, 0u))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    // sceFontRenderSurfaceSetScissor(surface, x0, y0, w, h): clips subsequent
    // glyph rendering to a rectangle, stored in the surface's scissor fields at
    // +0x18..+0x24 that sceFontRenderSurfaceInit above fills with the full
    // surface. The rectangle is clamped to the surface, and a rectangle that
    // ends left of or above the surface collapses to an empty one rather than
    // wrapping, because the stored bounds are unsigned.
    [SysAbiExport(
        Nid = "vRxf4d0ulPs",
        ExportName = "sceFontRenderSurfaceSetScissor",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderSurfaceSetScissor(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdi];
        if (surfaceAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt32(surfaceAddress + 0x10, out var surfaceWidth) ||
            !ctx.TryReadUInt32(surfaceAddress + 0x14, out var surfaceHeight))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        ClampScissorAxis(
            unchecked((int)ctx[CpuRegister.Rsi]),
            unchecked((int)ctx[CpuRegister.Rcx]),
            surfaceWidth,
            out var x0,
            out var x1);
        ClampScissorAxis(
            unchecked((int)ctx[CpuRegister.Rdx]),
            unchecked((int)ctx[CpuRegister.R8]),
            surfaceHeight,
            out var y0,
            out var y1);

        if (!TryWriteUInt32(ctx, surfaceAddress + 0x18, x0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x1C, y0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x20, x1) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x24, y1))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    /// <summary>
    /// Clamps one scissor axis to a surface extent. A negative start clips to
    /// zero and loses that much of the length; a start past the extent, or a
    /// length that never reaches zero, leaves an empty range.
    /// </summary>
    private static void ClampScissorAxis(int start, int length, uint extent, out uint low, out uint high)
    {
        if (extent == 0 || length <= 0 || start >= (long)extent)
        {
            low = extent;
            high = extent;
            if (extent == 0)
            {
                low = 0;
                high = 0;
            }

            return;
        }

        var end = (long)start + length;
        if (end <= 0)
        {
            low = 0;
            high = 0;
            return;
        }

        low = start < 0 ? 0u : (uint)start;
        high = end > extent ? extent : (uint)end;
    }

    [SysAbiExport(
        Nid = "gdUCnU0gHdI",
        ExportName = "sceFontRenderSurfaceInit",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderSurfaceInit(CpuContext ctx)
    {
        var surfaceAddress = ctx[CpuRegister.Rdi];
        var bufferAddress = ctx[CpuRegister.Rsi];
        var widthBytes = (uint)ctx[CpuRegister.Rdx];
        var pixelBytes = (uint)ctx[CpuRegister.Rcx] & 0xFF;
        var width = (uint)ctx[CpuRegister.R8];
        var height = (uint)ctx[CpuRegister.R9];
        if (surfaceAddress == 0 ||
            !ctx.TryWriteUInt64(surfaceAddress, bufferAddress) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x08, widthBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x0C, pixelBytes) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x10, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x14, height) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x18, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x1C, 0) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x20, width) ||
            !TryWriteUInt32(ctx, surfaceAddress + 0x24, height))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "C-4Qw5Srlyw",
        ExportName = "sceFontGenerateCharGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GenerateCharGlyph(CpuContext ctx)
    {
        var outputAddress = ctx[CpuRegister.Rcx];
        if (outputAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryRentGlyph(ctx, out var glyph) ||
            !ctx.TryWriteUInt64(outputAddress, glyph))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "8-zmgsxkBek",
        ExportName = "sceFontGlyphDefineAttribute",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int GlyphDefineAttribute(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "LHDoRWVFGqk",
        ExportName = "sceFontDeleteGlyph",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DeleteGlyph(CpuContext ctx)
    {
        var glyphPointerAddress = ctx[CpuRegister.Rsi];
        if (glyphPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!ctx.TryReadUInt64(glyphPointerAddress, out var glyph))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (glyph != 0)
        {
            lock (AllocationGate)
            {
                FreeGlyphs.Push(glyph);
            }
        }

        return ctx.TryWriteUInt64(glyphPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "kAenWy1Zw5o",
        ExportName = "sceFontRenderCharGlyphImageHorizontal",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int RenderCharGlyphImageHorizontal(CpuContext ctx)
    {
        // sceFontRenderCharGlyphImageHorizontal(font, code, surface, float x,
        // float y, metrics*, result*): draws the glyph with its origin on the
        // baseline at (x, y) of the surface, clipped to the surface scissor.
        var glyph = FindGlyph(ctx[CpuRegister.Rdi], (uint)ctx[CpuRegister.Rsi]);
        var surfaceAddress = ctx[CpuRegister.Rdx];
        var metricsAddress = ctx[CpuRegister.Rcx];
        var resultAddress = ctx[CpuRegister.R8];
        if (LogFont)
        {
            var box = glyph is null ? "none" : $"{glyph.MinX:F1},{glyph.MinY:F1},{glyph.MaxX:F1},{glyph.MaxY:F1}";
            Console.Error.WriteLine(
                $"[FONT] render handle=0x{ctx[CpuRegister.Rdi]:X} code=0x{(uint)ctx[CpuRegister.Rsi]:X} " +
                $"glyph={box} pen={ReadXmmSingle(ctx, 0):F1},{ReadXmmSingle(ctx, 1):F1}");
        }

        if (metricsAddress != 0 &&
            !TryWriteFloats(ctx, metricsAddress, glyph?.ToMetrics() ?? PlaceholderMetrics))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (glyph is not null && surfaceAddress != 0 &&
            !TryDrawGlyph(ctx, surfaceAddress, glyph, ReadXmmSingle(ctx, 0), ReadXmmSingle(ctx, 1)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (resultAddress != 0)
        {
            Span<byte> cleared = stackalloc byte[RenderOutputSize];
            cleared.Clear();
            if (!ctx.Memory.TryWrite(resultAddress, cleared))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "vzHs3C8lWJk",
        ExportName = "sceFontCloseFont",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int CloseFont(CpuContext ctx)
    {
        lock (Fonts)
        {
            Fonts.Remove(ctx[CpuRegister.Rdi]);
        }

        return SetSuccess(ctx);
    }

    [SysAbiExport(
        Nid = "1QjhKxrsOB8",
        ExportName = "sceFontUnbindRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int UnbindRenderer(CpuContext ctx) => SetSuccess(ctx);

    [SysAbiExport(
        Nid = "exAxkyVLt0s",
        ExportName = "sceFontDestroyRenderer",
        Target = Generation.Gen5,
        LibraryName = "libSceFont")]
    public static int DestroyRenderer(CpuContext ctx)
    {
        var rendererPointerAddress = ctx[CpuRegister.Rdi];
        if (rendererPointerAddress == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return ctx.TryWriteUInt64(rendererPointerAddress, 0)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool TryGetFont(ulong handle, out LoadedFont font)
    {
        lock (Fonts)
        {
            return Fonts.TryGetValue(handle, out font!);
        }
    }

    private static GlyphOutline? FindGlyph(ulong fontHandle, uint code) =>
        TryGetFont(fontHandle, out var font) ? font.Glyph(code) : null;

    private static float ReadXmmSingle(CpuContext ctx, int registerIndex)
    {
        ctx.GetXmmRegister(registerIndex, out var low, out _);
        return BitConverter.Int32BitsToSingle(unchecked((int)low));
    }

    /// <summary>
    /// Writes glyph coverage into a SceFontRenderSurface (buffer, bytes per row,
    /// bytes per pixel, width, height, scissor x0/y0/x1/y1). Every byte of a
    /// covered pixel receives the coverage; overlapping glyphs keep the maximum.
    /// </summary>
    private static bool TryDrawGlyph(CpuContext ctx, ulong surface, GlyphOutline glyph, float penX, float penY)
    {
        if (!ctx.TryReadUInt64(surface, out var buffer) ||
            !ctx.TryReadUInt32(surface + 0x08, out var widthBytes) ||
            !ctx.TryReadUInt32(surface + 0x0C, out var pixelBytes) ||
            !ctx.TryReadUInt32(surface + 0x10, out var surfaceWidth) ||
            !ctx.TryReadUInt32(surface + 0x14, out var surfaceHeight) ||
            !ctx.TryReadUInt32(surface + 0x18, out var scissorX0) ||
            !ctx.TryReadUInt32(surface + 0x1C, out var scissorY0) ||
            !ctx.TryReadUInt32(surface + 0x20, out var scissorX1) ||
            !ctx.TryReadUInt32(surface + 0x24, out var scissorY1))
        {
            return false;
        }

        if (buffer == 0 || pixelBytes == 0 || glyph.IsEmpty)
        {
            return true;
        }

        var originX = (int)MathF.Floor(penX + glyph.MinX);
        var originY = (int)MathF.Floor(penY + glyph.MinY);
        var width = (int)MathF.Ceiling(penX + glyph.MaxX) - originX;
        var height = (int)MathF.Ceiling(penY + glyph.MaxY) - originY;
        var x0 = Math.Max(originX, (int)Math.Min(scissorX0, surfaceWidth));
        var x1 = Math.Min(originX + width, (int)Math.Min(scissorX1, surfaceWidth));
        var y0 = Math.Max(originY, (int)Math.Min(scissorY0, surfaceHeight));
        var y1 = Math.Min(originY + height, (int)Math.Min(scissorY1, surfaceHeight));
        if (x0 >= x1 || y0 >= y1 || width > 4096 || height > 4096)
        {
            return true;
        }

        var coverage = glyph.Rasterize(penX - originX, penY - originY, width, height);
        var row = new byte[(x1 - x0) * (int)pixelBytes];
        for (var y = y0; y < y1; y++)
        {
            var address = buffer + (ulong)y * widthBytes + (ulong)x0 * pixelBytes;
            if (!ctx.Memory.TryRead(address, row))
            {
                return false;
            }

            for (var x = x0; x < x1; x++)
            {
                var value = coverage[(y - originY) * width + (x - originX)];
                for (var b = 0; b < pixelBytes; b++)
                {
                    ref var target = ref row[(x - x0) * (int)pixelBytes + b];
                    target = Math.Max(target, value);
                }
            }

            if (!ctx.Memory.TryWrite(address, row))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryWriteFloats(CpuContext ctx, ulong address, ReadOnlySpan<float> values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryWriteUInt32(
                    ctx,
                    address + (ulong)(index * sizeof(float)),
                    BitConverter.SingleToUInt32Bits(values[index])))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class LoadedFont(OpenTypeFont font)
    {
        // ponytail: the glyph cache is cleared wholesale at 4096 entries; an LRU
        // if titles thrash it across many sizes.
        private readonly Dictionary<(uint Code, float Width, float Height), GlyphOutline?> _glyphs = new();

        public float ScaleWidth { get; set; } = 16;

        public float ScaleHeight { get; set; } = 16;

        public GlyphOutline? Glyph(uint code)
        {
            lock (_glyphs)
            {
                var key = (code, ScaleWidth, ScaleHeight);
                if (!_glyphs.TryGetValue(key, out var glyph))
                {
                    if (_glyphs.Count >= 4096)
                    {
                        _glyphs.Clear();
                    }

                    glyph = font.TryGetGlyph(code, key.ScaleWidth, key.ScaleHeight, out var outline) ? outline : null;
                    _glyphs[key] = glyph;
                }

                return glyph;
            }
        }
    }

    private static bool TryRentGlyph(CpuContext ctx, out ulong glyph)
    {
        lock (AllocationGate)
        {
            if (FreeGlyphs.Count > 0)
            {
                glyph = FreeGlyphs.Pop();
                return TryWriteUInt16(ctx, glyph, GlyphMagic);
            }
        }

        return TryAllocateOpaque(ctx, GlyphSize, out glyph) &&
               TryWriteUInt16(ctx, glyph, GlyphMagic);
    }

    private static int ReturnSelection(CpuContext ctx, ref ulong selectionAddress, uint objectSize)
    {
        if (ctx[CpuRegister.Rdi] != 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        lock (AllocationGate)
        {
            if (selectionAddress == 0)
            {
                if (!TryAllocateOpaque(ctx, 0x20, out selectionAddress) ||
                    !TryWriteUInt32(ctx, selectionAddress, 0) ||
                    !TryWriteUInt32(ctx, selectionAddress + 4, objectSize))
                {
                    selectionAddress = 0;
                }
            }
        }

        ctx[CpuRegister.Rax] = selectionAddress;
        return 0;
    }

    private static int CreateOpaqueHandle(CpuContext ctx, ulong outputAddress, int size, ushort magic)
    {
        if (outputAddress == 0 || !TryAllocateOpaque(ctx, size, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (!TryWriteUInt16(ctx, handle, magic) || !ctx.TryWriteUInt64(outputAddress, handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetSuccess(ctx);
    }

    private static bool TryAllocateOpaque(CpuContext ctx, int size, out ulong address)
    {
        address = 0;
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)size, 0x10, out address))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[size];
        bytes.Clear();
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt16(CpuContext ctx, ulong address, ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static int SetSuccess(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(int)result);
        return (int)result;
    }
}
