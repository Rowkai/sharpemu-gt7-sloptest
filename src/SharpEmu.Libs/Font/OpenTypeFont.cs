// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SharpEmu.Libs.Font;

/// <summary>
/// A glyph outline flattened to line segments in pixel space: x to the right,
/// y downwards, origin at the pen position on the baseline.
/// </summary>
internal sealed class GlyphOutline
{
    public readonly List<(float X0, float Y0, float X1, float Y1)> Lines = new();
    public float MinX = float.MaxValue;
    public float MinY = float.MaxValue;
    public float MaxX = float.MinValue;
    public float MaxY = float.MinValue;
    public float Advance;
    public float EmHeight;

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>
    /// SceFontGlyphMetrics: width, height, horizontal bearingX / bearingY /
    /// advance, vertical bearingX / bearingY / advance.
    /// </summary>
    public float[] ToMetrics() => IsEmpty
        ? [0, 0, 0, 0, Advance, 0, 0, EmHeight]
        : [MaxX - MinX, MaxY - MinY, MinX, -MinY, Advance, -(MaxX - MinX) / 2, 0, EmHeight];

    public void AddLine(float x0, float y0, float x1, float y1)
    {
        Lines.Add((x0, y0, x1, y1));
        MinX = MathF.Min(MinX, MathF.Min(x0, x1));
        MaxX = MathF.Max(MaxX, MathF.Max(x0, x1));
        MinY = MathF.Min(MinY, MathF.Min(y0, y1));
        MaxY = MathF.Max(MaxY, MathF.Max(y0, y1));
    }

    /// <summary>
    /// Anti-aliased coverage (0-255) of a width x height grid whose top-left
    /// corner sits at (-offsetX, -offsetY) in outline space. Signed-area
    /// accumulation, so contour direction does not matter.
    /// </summary>
    public byte[] Rasterize(float offsetX, float offsetY, int width, int height)
    {
        var stride = width + 2;
        var accumulation = new float[stride * height];
        foreach (var (x0, y0, x1, y1) in Lines)
        {
            AccumulateLine(accumulation, stride, height, x0 + offsetX, y0 + offsetY, x1 + offsetX, y1 + offsetY);
        }

        var coverage = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sum = 0f;
            for (var x = 0; x < width; x++)
            {
                sum += accumulation[y * stride + x];
                coverage[y * width + x] = (byte)(MathF.Min(1f, MathF.Abs(sum)) * 255f + 0.5f);
            }
        }

        return coverage;
    }

    private static void AccumulateLine(float[] acc, int stride, int height, float x0, float y0, float x1, float y1)
    {
        if (y0 == y1)
        {
            return;
        }

        var direction = 1f;
        if (y0 > y1)
        {
            direction = -1f;
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
        }

        var dxdy = (x1 - x0) / (y1 - y0);
        var x = x0 - (y0 < 0 ? y0 * dxdy : 0);
        var yEnd = Math.Min(height, (int)MathF.Ceiling(y1));
        for (var y = Math.Max(0, (int)MathF.Floor(y0)); y < yEnd; y++)
        {
            var row = y * stride;
            var dy = MathF.Min(y + 1, y1) - MathF.Max(y, y0);
            var xNext = x + dxdy * dy;
            var d = dy * direction;
            var left = MathF.Min(x, xNext);
            var right = MathF.Max(x, xNext);
            var leftFloor = MathF.Floor(left);
            var li = (int)leftFloor;
            var rightCeil = MathF.Ceiling(right);
            var ri = (int)rightCeil;
            if (ri <= li + 1)
            {
                var mid = 0.5f * (x + xNext) - leftFloor;
                Add(acc, row, stride, li, d - d * mid);
                Add(acc, row, stride, li + 1, d * mid);
            }
            else
            {
                var inverse = 1 / (right - left);
                var leftFrac = left - leftFloor;
                var a0 = 0.5f * inverse * (1 - leftFrac) * (1 - leftFrac);
                var rightFrac = right - rightCeil + 1;
                var am = 0.5f * inverse * rightFrac * rightFrac;
                Add(acc, row, stride, li, d * a0);
                if (ri == li + 2)
                {
                    Add(acc, row, stride, li + 1, d * (1 - a0 - am));
                }
                else
                {
                    var a1 = inverse * (1.5f - leftFrac);
                    Add(acc, row, stride, li + 1, d * (a1 - a0));
                    for (var xi = li + 2; xi < ri - 1; xi++)
                    {
                        Add(acc, row, stride, xi, d * inverse);
                    }

                    var a2 = a1 + (ri - li - 3) * inverse;
                    Add(acc, row, stride, ri - 1, d * (1 - a2 - am));
                }

                Add(acc, row, stride, ri, d * am);
            }

            x = xNext;
        }
    }

    private static void Add(float[] acc, int row, int stride, int x, float value) =>
        acc[row + Math.Clamp(x, 0, stride - 1)] += value;
}

/// <summary>
/// An OpenType font with CFF outlines, parsed from the bytes a title hands to
/// sceFontOpenFontMemory. Covers what glyph metrics and coverage need: cmap,
/// hmtx, and Type 2 charstrings, including CID-keyed fonts.
/// </summary>
internal sealed class OpenTypeFont
{
    private const uint TagOtto = 0x4F54544F;

    private readonly byte[] _data;
    private readonly int _cmap;
    private readonly int _cmapFormat;
    private readonly int _hmtx;
    private readonly int _numberOfHMetrics;
    private readonly int _unitsPerEm;
    private readonly CffIndex _charStrings;
    private readonly CffIndex _globalSubrs;
    private readonly CffIndex[] _localSubrs;
    private readonly byte[]? _fdSelect;

    private OpenTypeFont(
        byte[] data,
        (int Offset, int Format) cmap,
        int hmtx,
        int numberOfHMetrics,
        int unitsPerEm,
        CffIndex charStrings,
        CffIndex globalSubrs,
        CffIndex[] localSubrs,
        byte[]? fdSelect)
    {
        _data = data;
        (_cmap, _cmapFormat) = cmap;
        _hmtx = hmtx;
        _numberOfHMetrics = numberOfHMetrics;
        _unitsPerEm = unitsPerEm == 0 ? 1000 : unitsPerEm;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _localSubrs = localSubrs;
        _fdSelect = fdSelect;
    }

    public static bool TryParse(byte[] data, out OpenTypeFont font)
    {
        font = null!;
        try
        {
            var parsed = Parse(data);
            if (parsed is null)
            {
                return false;
            }

            font = parsed;
            return true;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Flattens the glyph for a character code at the given pixel size. Fails
    /// for codes the cmap does not map, so callers can keep their fallback.
    /// </summary>
    public bool TryGetGlyph(uint code, float pixelWidth, float pixelHeight, out GlyphOutline glyph)
    {
        glyph = null!;
        try
        {
            var gid = GlyphId(code);
            if (gid <= 0 || gid >= _charStrings.Count)
            {
                return false;
            }

            var scaleX = pixelWidth / _unitsPerEm;
            var scaleY = pixelHeight / _unitsPerEm;
            var outline = new GlyphOutline { Advance = Advance(gid) * scaleX, EmHeight = pixelHeight };
            var local = _fdSelect is null ? _localSubrs[0] : _localSubrs[_fdSelect[gid]];
            new CharStringRunner(this, local, scaleX, scaleY, outline).Run(_charStrings[gid], 0);
            glyph = outline;
            return true;
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            return false;
        }
    }

    private static OpenTypeFont? Parse(byte[] d)
    {
        if (U32(d, 0) != TagOtto)
        {
            return null;
        }

        int cmap = -1, head = -1, hhea = -1, hmtx = -1, cff = -1;
        var numTables = U16(d, 4);
        for (var i = 0; i < numTables; i++)
        {
            var record = 12 + 16 * i;
            var offset = (int)U32(d, record + 8);
            switch (U32(d, record))
            {
                case 0x636D6170: cmap = offset; break;
                case 0x68656164: head = offset; break;
                case 0x68686561: hhea = offset; break;
                case 0x686D7478: hmtx = offset; break;
                case 0x43464620: cff = offset; break;
            }
        }

        if (cmap < 0 || head < 0 || hhea < 0 || hmtx < 0 || cff < 0)
        {
            return null;
        }

        var cmapSubtable = FindCmapSubtable(d, cmap);
        if (cmapSubtable.Offset < 0)
        {
            return null;
        }

        var position = cff + d[cff + 2];
        ReadIndex(d, ref position);
        var topDicts = ReadIndex(d, ref position);
        ReadIndex(d, ref position);
        var globalSubrs = ReadIndex(d, ref position);
        var (topStart, topLength) = topDicts[0];
        var top = ReadDict(d, topStart, topStart + topLength);

        var charStringsPosition = cff + (int)top[17][0];
        var charStrings = ReadIndex(d, ref charStringsPosition);
        CffIndex[] localSubrs;
        byte[]? fdSelect = null;
        if (top.TryGetValue(1236, out var fdArrayOffset))
        {
            var fdArrayPosition = cff + (int)fdArrayOffset[0];
            var fdArray = ReadIndex(d, ref fdArrayPosition);
            localSubrs = new CffIndex[fdArray.Count];
            for (var i = 0; i < fdArray.Count; i++)
            {
                var (start, length) = fdArray[i];
                localSubrs[i] = LocalSubrs(d, cff, ReadDict(d, start, start + length));
            }

            fdSelect = ReadFdSelect(d, cff + (int)top[1237][0], charStrings.Count);
        }
        else
        {
            localSubrs = [LocalSubrs(d, cff, top)];
        }

        return new OpenTypeFont(
            d,
            cmapSubtable,
            hmtx,
            U16(d, hhea + 34),
            U16(d, head + 18),
            charStrings,
            globalSubrs,
            localSubrs,
            fdSelect);
    }

    private static (int Offset, int Format) FindCmapSubtable(byte[] d, int cmap)
    {
        var best = -1;
        var count = U16(d, cmap + 2);
        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + 8 * i;
            var platform = U16(d, record);
            var encoding = U16(d, record + 2);
            if (platform != 0 && !(platform == 3 && (encoding == 1 || encoding == 10)))
            {
                continue;
            }

            var offset = cmap + (int)U32(d, record + 4);
            var format = U16(d, offset);
            if (format == 12)
            {
                return (offset, 12);
            }

            if (format == 4 && best < 0)
            {
                best = offset;
            }
        }

        return (best, 4);
    }

    private int GlyphId(uint code)
    {
        var d = _data;
        if (_cmapFormat == 12)
        {
            var groups = U32(d, _cmap + 12);
            for (var g = 0u; g < groups; g++)
            {
                var record = _cmap + 16 + 12 * (int)g;
                if (code >= U32(d, record) && code <= U32(d, record + 4))
                {
                    return (int)(U32(d, record + 8) + code - U32(d, record));
                }
            }

            return 0;
        }

        if (code > 0xFFFF)
        {
            return 0;
        }

        var segCountX2 = U16(d, _cmap + 6);
        var ends = _cmap + 14;
        var starts = ends + segCountX2 + 2;
        var deltas = starts + segCountX2;
        var rangeOffsets = deltas + segCountX2;
        for (var s = 0; s < segCountX2; s += 2)
        {
            if (code > U16(d, ends + s))
            {
                continue;
            }

            var start = U16(d, starts + s);
            if (code < start)
            {
                return 0;
            }

            var delta = U16(d, deltas + s);
            var rangeOffset = U16(d, rangeOffsets + s);
            if (rangeOffset == 0)
            {
                return (int)((code + delta) & 0xFFFF);
            }

            var glyph = U16(d, rangeOffsets + s + rangeOffset + 2 * (int)(code - start));
            return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
        }

        return 0;
    }

    private int Advance(int gid) =>
        _numberOfHMetrics == 0 ? 0 : U16(_data, _hmtx + 4 * Math.Min(gid, _numberOfHMetrics - 1));

    private static CffIndex LocalSubrs(byte[] d, int cff, Dictionary<int, double[]> dict)
    {
        if (!dict.TryGetValue(18, out var privateEntry) || privateEntry.Length < 2)
        {
            return CffIndex.Empty;
        }

        var start = cff + (int)privateEntry[1];
        var privateDict = ReadDict(d, start, start + (int)privateEntry[0]);
        if (!privateDict.TryGetValue(19, out var subrs))
        {
            return CffIndex.Empty;
        }

        var position = start + (int)subrs[0];
        return ReadIndex(d, ref position);
    }

    private static byte[] ReadFdSelect(byte[] d, int position, int glyphCount)
    {
        var fds = new byte[glyphCount];
        if (d[position] == 0)
        {
            d.AsSpan(position + 1, glyphCount).CopyTo(fds);
            return fds;
        }

        if (d[position] != 3)
        {
            throw new InvalidDataException("unsupported FDSelect format");
        }

        var ranges = U16(d, position + 1);
        for (var r = 0; r < ranges; r++)
        {
            var record = position + 3 + r * 3;
            var next = Math.Min(U16(d, record + 3), glyphCount);
            for (var gid = U16(d, record); gid < next; gid++)
            {
                fds[gid] = d[record + 2];
            }
        }

        return fds;
    }

    private static CffIndex ReadIndex(byte[] d, ref int position)
    {
        var count = U16(d, position);
        if (count == 0)
        {
            position += 2;
            return CffIndex.Empty;
        }

        var offSize = d[position + 2];
        var offsets = position + 3;
        var dataBase = offsets + (count + 1) * offSize - 1;
        var starts = new int[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var value = 0;
            for (var k = 0; k < offSize; k++)
            {
                value = value << 8 | d[offsets + i * offSize + k];
            }

            starts[i] = dataBase + value;
        }

        position = starts[count];
        return new CffIndex(starts);
    }

    private static Dictionary<int, double[]> ReadDict(byte[] d, int start, int end)
    {
        var dict = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var i = start;
        while (i < end)
        {
            int b = d[i++];
            if (b <= 21)
            {
                dict[b == 12 ? 1200 + d[i++] : b] = operands.ToArray();
                operands.Clear();
            }
            else if (b == 28)
            {
                operands.Add((short)(d[i] << 8 | d[i + 1]));
                i += 2;
            }
            else if (b == 29)
            {
                operands.Add((int)U32(d, i));
                i += 4;
            }
            else if (b == 30)
            {
                operands.Add(ReadReal(d, ref i));
            }
            else if (b is >= 32 and <= 246)
            {
                operands.Add(b - 139);
            }
            else if (b is >= 247 and <= 250)
            {
                operands.Add((b - 247) * 256 + d[i++] + 108);
            }
            else if (b is >= 251 and <= 254)
            {
                operands.Add(-(b - 251) * 256 - d[i++] - 108);
            }
            else
            {
                throw new InvalidDataException("reserved DICT byte");
            }
        }

        return dict;
    }

    private static double ReadReal(byte[] d, ref int i)
    {
        var text = new StringBuilder();
        while (true)
        {
            int b = d[i++];
            foreach (var nibble in new[] { b >> 4, b & 0xF })
            {
                switch (nibble)
                {
                    case < 10: text.Append((char)('0' + nibble)); break;
                    case 0xA: text.Append('.'); break;
                    case 0xB: text.Append('E'); break;
                    case 0xC: text.Append("E-"); break;
                    case 0xE: text.Append('-'); break;
                    case 0xF:
                        return double.TryParse(text.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                            ? value
                            : 0;
                }
            }
        }
    }

    private static int U16(byte[] d, int offset) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(offset, 2));

    private static uint U32(byte[] d, int offset) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(offset, 4));

    private readonly struct CffIndex
    {
        public static readonly CffIndex Empty = new([0]);

        private readonly int[] _starts;

        public CffIndex(int[] starts) => _starts = starts;

        public int Count => _starts.Length - 1;

        public (int Start, int Length) this[int index] => (_starts[index], _starts[index + 1] - _starts[index]);
    }

    /// <summary>Type 2 charstring interpreter emitting flattened pixel-space lines.</summary>
    private sealed class CharStringRunner(
        OpenTypeFont font,
        CffIndex localSubrs,
        float scaleX,
        float scaleY,
        GlyphOutline glyph)
    {
        private readonly double[] _stack = new double[48];
        private int _sp;
        private int _stems;
        private bool _widthParsed;
        private bool _open;
        private bool _done;
        private double _x;
        private double _y;
        private double _startX;
        private double _startY;

        public void Run((int Start, int Length) charString, int depth)
        {
            if (depth > 10)
            {
                throw new InvalidDataException("subroutine nesting too deep");
            }

            var d = font._data;
            var s = _stack;
            var i = charString.Start;
            var end = charString.Start + charString.Length;
            while (i < end && !_done)
            {
                int b = d[i++];
                if (b >= 32 || b == 28)
                {
                    double value;
                    if (b == 28)
                    {
                        value = (short)(d[i] << 8 | d[i + 1]);
                        i += 2;
                    }
                    else if (b <= 246)
                    {
                        value = b - 139;
                    }
                    else if (b <= 250)
                    {
                        value = (b - 247) * 256 + d[i++] + 108;
                    }
                    else if (b <= 254)
                    {
                        value = -(b - 251) * 256 - d[i++] - 108;
                    }
                    else
                    {
                        value = (int)U32(d, i) / 65536.0;
                        i += 4;
                    }

                    if (_sp == s.Length)
                    {
                        throw new InvalidDataException("charstring stack overflow");
                    }

                    s[_sp++] = value;
                    continue;
                }

                switch (b)
                {
                    case 1: case 3: case 18: case 23:
                        Stems();
                        break;
                    case 19: case 20:
                        Stems();
                        i += (_stems + 7) / 8;
                        break;
                    case 21:
                    {
                        var a = Width(2);
                        MoveTo(_x + s[a], _y + s[a + 1]);
                        break;
                    }
                    case 22:
                    {
                        var a = Width(1);
                        MoveTo(_x + s[a], _y);
                        break;
                    }
                    case 4:
                    {
                        var a = Width(1);
                        MoveTo(_x, _y + s[a]);
                        break;
                    }
                    case 5:
                        for (var a = 0; _sp - a >= 2; a += 2)
                        {
                            LineTo(_x + s[a], _y + s[a + 1]);
                        }

                        _sp = 0;
                        break;
                    case 6: case 7:
                    {
                        var horizontal = b == 6;
                        for (var a = 0; a < _sp; a++, horizontal = !horizontal)
                        {
                            LineTo(horizontal ? _x + s[a] : _x, horizontal ? _y : _y + s[a]);
                        }

                        _sp = 0;
                        break;
                    }
                    case 8:
                        for (var a = 0; _sp - a >= 6; a += 6)
                        {
                            Curve(s[a], s[a + 1], s[a + 2], s[a + 3], s[a + 4], s[a + 5]);
                        }

                        _sp = 0;
                        break;
                    case 24:
                    {
                        var a = 0;
                        for (; _sp - a >= 8; a += 6)
                        {
                            Curve(s[a], s[a + 1], s[a + 2], s[a + 3], s[a + 4], s[a + 5]);
                        }

                        if (_sp - a >= 2)
                        {
                            LineTo(_x + s[a], _y + s[a + 1]);
                        }

                        _sp = 0;
                        break;
                    }
                    case 25:
                    {
                        var a = 0;
                        for (; _sp - a >= 8; a += 2)
                        {
                            LineTo(_x + s[a], _y + s[a + 1]);
                        }

                        if (_sp - a >= 6)
                        {
                            Curve(s[a], s[a + 1], s[a + 2], s[a + 3], s[a + 4], s[a + 5]);
                        }

                        _sp = 0;
                        break;
                    }
                    case 26:
                    {
                        var a = 0;
                        var dx1 = _sp % 2 == 1 ? s[a++] : 0;
                        for (; _sp - a >= 4; a += 4, dx1 = 0)
                        {
                            Curve(dx1, s[a], s[a + 1], s[a + 2], 0, s[a + 3]);
                        }

                        _sp = 0;
                        break;
                    }
                    case 27:
                    {
                        var a = 0;
                        var dy1 = _sp % 2 == 1 ? s[a++] : 0;
                        for (; _sp - a >= 4; a += 4, dy1 = 0)
                        {
                            Curve(s[a], dy1, s[a + 1], s[a + 2], s[a + 3], 0);
                        }

                        _sp = 0;
                        break;
                    }
                    case 30: case 31:
                    {
                        var vertical = b == 30;
                        for (var a = 0; _sp - a >= 4; a += 4, vertical = !vertical)
                        {
                            var last = _sp - a == 5 ? s[a + 4] : 0;
                            if (vertical)
                            {
                                Curve(0, s[a], s[a + 1], s[a + 2], s[a + 3], last);
                            }
                            else
                            {
                                Curve(s[a], 0, s[a + 1], s[a + 2], last, s[a + 3]);
                            }
                        }

                        _sp = 0;
                        break;
                    }
                    case 10:
                        Run(localSubrs[(int)s[--_sp] + Bias(localSubrs.Count)], depth + 1);
                        break;
                    case 29:
                        Run(font._globalSubrs[(int)s[--_sp] + Bias(font._globalSubrs.Count)], depth + 1);
                        break;
                    case 11:
                        return;
                    case 14:
                        ClosePath();
                        _done = true;
                        return;
                    case 12:
                        Escape(d[i++]);
                        break;
                    default:
                        _sp = 0;
                        break;
                }
            }
        }

        private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        private void Stems()
        {
            _widthParsed = true;
            _stems += _sp / 2;
            _sp = 0;
        }

        /// <summary>Index of the first real argument: skips the optional leading advance width.</summary>
        private int Width(int arguments)
        {
            var first = !_widthParsed && _sp > arguments ? 1 : 0;
            _widthParsed = true;
            return first;
        }

        // ponytail: the arithmetic and storage escape operators (and, add, put, ...)
        // are not interpreted; fonts in practice only use the four flex forms.
        private void Escape(int op)
        {
            var s = _stack;
            switch (op)
            {
                case 35 when _sp >= 13:
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    Curve(s[6], s[7], s[8], s[9], s[10], s[11]);
                    break;
                case 34 when _sp >= 7:
                    Curve(s[0], 0, s[1], s[2], s[3], 0);
                    Curve(s[4], 0, s[5], -s[2], s[6], 0);
                    break;
                case 36 when _sp >= 9:
                    Curve(s[0], s[1], s[2], s[3], s[4], 0);
                    Curve(s[5], 0, s[6], s[7], s[8], -(s[1] + s[3] + s[7]));
                    break;
                case 37 when _sp >= 11:
                {
                    var dx = s[0] + s[2] + s[4] + s[6] + s[8];
                    var dy = s[1] + s[3] + s[5] + s[7] + s[9];
                    Curve(s[0], s[1], s[2], s[3], s[4], s[5]);
                    if (Math.Abs(dx) > Math.Abs(dy))
                    {
                        Curve(s[6], s[7], s[8], s[9], s[10], -dy);
                    }
                    else
                    {
                        Curve(s[6], s[7], s[8], s[9], -dx, s[10]);
                    }

                    break;
                }
            }

            _sp = 0;
        }

        private void MoveTo(double x, double y)
        {
            ClosePath();
            _x = _startX = x;
            _y = _startY = y;
            _open = true;
            _sp = 0;
        }

        private void LineTo(double x, double y)
        {
            if (_open)
            {
                glyph.AddLine(Px(_x), Py(_y), Px(x), Py(y));
            }

            _x = x;
            _y = y;
        }

        private void ClosePath()
        {
            if (_open && (_x != _startX || _y != _startY))
            {
                glyph.AddLine(Px(_x), Py(_y), Px(_startX), Py(_startY));
            }

            _open = false;
        }

        private void Curve(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            double x1 = _x + dx1, y1 = _y + dy1;
            double x2 = x1 + dx2, y2 = y1 + dy2;
            double x3 = x2 + dx3, y3 = y2 + dy3;
            if (_open)
            {
                float p0x = Px(_x), p0y = Py(_y), p1x = Px(x1), p1y = Py(y1);
                float p2x = Px(x2), p2y = Py(y2), p3x = Px(x3), p3y = Py(y3);
                var length = Distance(p0x, p0y, p1x, p1y) + Distance(p1x, p1y, p2x, p2y) + Distance(p2x, p2y, p3x, p3y);
                // ponytail: uniform subdivision by control-polygon length; adaptive
                // flattening if large glyphs ever show visible facets.
                var steps = Math.Clamp((int)MathF.Ceiling(length / 1.5f), 1, 64);
                float previousX = p0x, previousY = p0y;
                for (var k = 1; k <= steps; k++)
                {
                    var t = k / (float)steps;
                    var u = 1 - t;
                    var x = u * u * u * p0x + 3 * u * u * t * p1x + 3 * u * t * t * p2x + t * t * t * p3x;
                    var y = u * u * u * p0y + 3 * u * u * t * p1y + 3 * u * t * t * p2y + t * t * t * p3y;
                    glyph.AddLine(previousX, previousY, x, y);
                    previousX = x;
                    previousY = y;
                }
            }

            _x = x3;
            _y = y3;
        }

        private float Px(double x) => (float)(x * scaleX);

        private float Py(double y) => (float)(-y * scaleY);

        private static float Distance(float x0, float y0, float x1, float y1) =>
            MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
    }
}
