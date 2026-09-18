// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

/// <summary>
/// A type-3 header's COUNT field holds body-dwords-minus-one in 14 bits, so
/// COUNT 0x3FFF wraps to a zero-dword body: the packet is the header alone.
/// AGC emits that as a no-payload marker (sceAgcGetDataPacketPayloadAddress
/// reports no payload for exactly this encoding), and titles place one ahead of
/// the REWIND / INDIRECT_BUFFER tail that carries the frame. Reading it as a
/// 16385-dword packet overruns the submission, so the parser abandons every
/// packet after it and the frame never reaches the GPU.
/// </summary>
public sealed class AgcPaddingNopPacketTests
{
    private const ulong BaseAddress = 0x2_0000_0000;
    private const ulong SubmitPacketAddress = BaseAddress + 0x40;
    private const ulong CommandAddress = BaseAddress + 0x200;

    private const uint ItNop = 0x10;
    private const uint ItSetContextReg = 0x69;
    private const uint CbTargetMask = 0x8E;

    /// <summary>The padding NOP GT7 writes: opcode 0x10, COUNT 0x3FFF.</summary>
    private const uint PaddingNopHeader = 0xFFFF_1000u;

    private static uint Pm4Header(uint dwords, uint opcode) =>
        0xC000_0000u | ((dwords - 2) << 16) | (opcode << 8);

    [Fact]
    public void PaddingNopConsumesOneDwordAndTheRestOfTheSubmissionStillParses()
    {
        var ctx = CreateContext(out var memory);
        WriteDwords(
            memory,
            CommandAddress,
            PaddingNopHeader,
            Pm4Header(3, ItSetContextReg),
            CbTargetMask,
            0x0000_000Fu);
        Submit(ctx, memory, dwordCount: 4);

        Assert.True(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbTargetMask, out var value));
        Assert.Equal(0x0000_000Fu, value);
    }

    /// <summary>
    /// The wrap applies only at COUNT 0x3FFF. An ordinary NOP still covers its
    /// body, so a register write hidden inside one must not be executed.
    /// </summary>
    [Fact]
    public void OrdinaryNopStillCoversItsBody()
    {
        var ctx = CreateContext(out var memory);
        WriteDwords(
            memory,
            CommandAddress,
            Pm4Header(4, ItNop),
            Pm4Header(3, ItSetContextReg),
            CbTargetMask,
            0x0000_000Fu);
        Submit(ctx, memory, dwordCount: 4);

        Assert.False(
            AgcExports.TryGetGraphicsContextRegisterForTests(ctx, CbTargetMask, out _));
    }

    private static void Submit(CpuContext ctx, FakeCpuMemory memory, uint dwordCount)
    {
        WriteUInt64(memory, SubmitPacketAddress, CommandAddress);
        WriteUInt32(memory, SubmitPacketAddress + 8, dwordCount);
        ctx[CpuRegister.Rdi] = SubmitPacketAddress;
        AgcExports.DriverSubmitDcb(ctx);
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void WriteDwords(FakeCpuMemory memory, ulong address, params uint[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            WriteUInt32(memory, address + ((ulong)index * sizeof(uint)), values[index]);
        }
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
