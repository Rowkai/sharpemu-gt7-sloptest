// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Disasm;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class IcedDecoderTests
{
    [Theory]
    [InlineData(0, 15, 15)]
    [InlineData(14, 15, 1)]
    [InlineData(15, 15, 0)]
    [InlineData(0, 100, 15)]
    [InlineData(0, 0, 1)]
    public void ReadsInstructionWindowOrReadablePrefix(int offset, int requested, int expectedLength)
    {
        const ulong address = 0x10000;
        byte[] code = Enumerable.Range(1, 15).Select(value => (byte)value).ToArray();
        IVirtualMemory memory = new VirtualMemory();
        memory.Map(address, (ulong)code.Length, 0, code, ProgramHeaderFlags.Read);
        var counted = new CountingMemory(memory);

        Assert.Equal(expectedLength != 0,
            IcedDecoder.TryReadGuestBytes(counted, address + (ulong)offset, requested, out var bytes));
        Assert.Equal(code.AsSpan(offset, expectedLength).ToArray(), bytes);
        if (offset == 0)
        {
            Assert.Equal(1, counted.Reads);
        }

        Assert.Equal(expectedLength != 0,
            IcedDecoder.TryReadGuestBytes(memory, address + (ulong)offset, requested, out var virtualBytes));
        Assert.Equal(bytes, virtualBytes);
    }

    private sealed class CountingMemory(ICpuMemory memory) : ICpuMemory
    {
        public int Reads { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            Reads++;
            return memory.TryRead(virtualAddress, destination);
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => memory.TryWrite(virtualAddress, source);
    }
}
