// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.CommonDialog;
using SharpEmu.Libs.DeviceService;
using SharpEmu.Libs.Hmd2;
using SharpEmu.Libs.Network;
using SharpEmu.Libs.Np;
using SharpEmu.Libs.Share;
using Xunit;

namespace SharpEmu.Libs.Tests;

/// <summary>
/// Exports GT7 calls every frame that previously resolved to nothing. An
/// unresolved import returns ORBIS_GEN2_ERROR_NOT_FOUND and writes no output,
/// so each of these has a caller that either stored a failure code where a
/// packet pointer belonged or tested uninitialised stack.
/// </summary>
public sealed class MissingGt7ExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong OutputAddress = BaseAddress + 0x600;
    private const ulong StackAddress = BaseAddress + 0x800;

    private const uint RewindValidBit = 1u << 31;

    [Fact]
    public void AcbRewind_EmitsClosedGate_AndAsyncPatchOpensIt()
    {
        var memory = CreateMemory(out var ctx);

        // GT7 calls this as rewind(acb, 0, 0): a gate that starts closed.
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.AcbRewind(ctx));
        Assert.Equal(PacketAddress, ctx[CpuRegister.Rax]);

        // IT_REWIND (0x59), 2 dwords, body clear while the gate is shut.
        Assert.Equal(0x59u, (ReadUInt32(memory, PacketAddress) >> 8) & 0xFFu);
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress + 4) & RewindValidBit);

        ctx[CpuRegister.Rdi] = PacketAddress;
        ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.AsyncRewindPatchSetRewindState(ctx));
        Assert.Equal(RewindValidBit, ReadUInt32(memory, PacketAddress + 4) & RewindValidBit);
    }

    [Fact]
    public void Hmd2GetDeviceInformation_ClearsTheRecord_WithNoHeadsetAttached()
    {
        var memory = CreateMemory(out var ctx);
        for (var offset = 0u; offset < 0x18; offset += 4)
        {
            WriteUInt32(memory, OutputAddress + offset, 0xDEAD_BEEF);
        }

        ctx[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            Hmd2Exports.Hmd2GetDeviceInformation(ctx));

        // The caller tests a presence flag at +0x14 only after the call
        // succeeds, so garbage left there would read as a headset being
        // attached.
        for (var offset = 0u; offset < 0x18; offset += 4)
        {
            Assert.Equal(0u, ReadUInt32(memory, OutputAddress + offset));
        }
    }

    [Fact]
    public void Hmd2GetDeviceInformation_RejectsNullRecord()
    {
        CreateMemory(out var ctx);
        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Hmd2Exports.Hmd2GetDeviceInformation(ctx));
    }

    [Fact]
    public void ShareGetRunningStatus_ReportsNothingRunning()
    {
        var memory = CreateMemory(out var ctx);
        WriteUInt32(memory, OutputAddress, 0xDEAD_BEEF);

        ctx[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            ShareExports.ShareGetRunningStatus(ctx));
        Assert.Equal(0u, ReadUInt32(memory, OutputAddress));
    }

    [Fact]
    public void NetInetNtop_FormatsIpv4_AndReturnsTheDestination()
    {
        var memory = CreateMemory(out var ctx);
        Assert.True(memory.TryWrite(OutputAddress, new byte[] { 192, 168, 1, 40 }));

        ctx[CpuRegister.Rdi] = 2; // AF_INET
        ctx[CpuRegister.Rsi] = OutputAddress;
        ctx[CpuRegister.Rdx] = OutputAddress + 0x40;
        ctx[CpuRegister.Rcx] = 32;

        Assert.Equal(0, NetExports.NetInetNtop(ctx));

        // BSD returns dst, not an error code.
        Assert.Equal(OutputAddress + 0x40, ctx[CpuRegister.Rax]);
        Assert.Equal("192.168.1.40", ReadCString(memory, OutputAddress + 0x40));
    }

    [Fact]
    public void NetInetNtop_ReturnsNull_WhenDestinationIsTooSmall()
    {
        CreateMemory(out var ctx);

        ctx[CpuRegister.Rdi] = 2;
        ctx[CpuRegister.Rsi] = OutputAddress;
        ctx[CpuRegister.Rdx] = OutputAddress + 0x40;
        ctx[CpuRegister.Rcx] = 4; // "192.168.1.40" plus NUL does not fit

        // inet_ntop signals failure with a NULL return, not an error code.
        Assert.Equal(0, NetExports.NetInetNtop(ctx));
        Assert.Equal(0uL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void DeviceServiceQueryDeviceInfo_ReportsZeroDevices_WithoutTouchingTheRecord()
    {
        var memory = CreateMemory(out var ctx);
        var countAddress = OutputAddress;
        var extraCountAddress = OutputAddress + 8;
        var recordAddress = OutputAddress + 0x100;

        WriteUInt32(memory, countAddress, 0xDEAD_BEEF);
        WriteUInt32(memory, extraCountAddress, 0xDEAD_BEEF);
        WriteUInt32(memory, recordAddress, 0xFEED_FACE);

        ctx[CpuRegister.Rdi] = 0x7001;
        ctx[CpuRegister.Rcx] = recordAddress;
        ctx[CpuRegister.R8] = 1;
        ctx[CpuRegister.R9] = countAddress;
        WriteUInt64(memory, StackAddress + 8, extraCountAddress);   // stack arg 0
        WriteUInt64(memory, StackAddress + 16, 0x70);               // stack arg 1

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            DeviceServiceExports.DeviceServiceQueryDeviceInfo(ctx));

        // A non-negative return is what lets the caller look at the count at
        // all; the count then has to read as empty.
        Assert.Equal(0u, ReadUInt32(memory, countAddress));
        Assert.Equal(0u, ReadUInt32(memory, extraCountAddress));

        // No device is reported, so no record may be invented.
        Assert.Equal(0xFEED_FACEu, ReadUInt32(memory, recordAddress));
    }

    private static string ReadCString(FakeCpuMemory memory, ulong address)
    {
        var bytes = new List<byte>();
        Span<byte> one = stackalloc byte[1];
        for (var index = 0; index < 64; index++)
        {
            Assert.True(memory.TryRead(address + (ulong)index, one));
            if (one[0] == 0)
            {
                break;
            }

            bytes.Add(one[0]);
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    // GT7 initialises the sign-in dialog and terminates it straight after when
    // Initialize fails; unresolved, Initialize returned NOT_FOUND.
    [Fact]
    public void SigninDialog_OpensAndFinishesAsUserCanceled()
    {
        var memory = CreateMemory(out var ctx);

        Assert.Equal(0, SigninDialogExports.SigninDialogInitialize(ctx));
        Assert.Equal(unchecked((int)0x80B80004), SigninDialogExports.SigninDialogInitialize(ctx));
        Assert.Equal(1, SigninDialogExports.SigninDialogGetStatus(ctx));

        ctx[CpuRegister.Rdi] = OutputAddress;
        WriteUInt32(memory, OutputAddress, 0xDEAD_BEEF);
        Assert.Equal(unchecked((int)0x80B80005), SigninDialogExports.SigninDialogGetResult(ctx));

        ctx[CpuRegister.Rdi] = PacketAddress;
        Assert.Equal(0, SigninDialogExports.SigninDialogOpen(ctx));
        Assert.Equal(3, SigninDialogExports.SigninDialogUpdateStatus(ctx));

        ctx[CpuRegister.Rdi] = OutputAddress;
        Assert.Equal(0, SigninDialogExports.SigninDialogGetResult(ctx));
        Assert.Equal(1u, ReadUInt32(memory, OutputAddress));

        Assert.Equal(0, SigninDialogExports.SigninDialogTerminate(ctx));
        Assert.Equal(0, SigninDialogExports.SigninDialogGetStatus(ctx));
        Assert.Equal(unchecked((int)0x80B80003), SigninDialogExports.SigninDialogTerminate(ctx));
    }

    [Fact]
    public void NpHasSignedUp_ReportsFalseOffline()
    {
        var memory = CreateMemory(out var ctx);
        WriteUInt32(memory, OutputAddress, 0xFFFF_FFFF);

        ctx[CpuRegister.Rdi] = 0x10000000;
        ctx[CpuRegister.Rsi] = OutputAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, NpManagerExports.NpHasSignedUp(ctx));
        Assert.Equal(0u, ReadUInt32(memory, OutputAddress) & 0xFFu);

        ctx[CpuRegister.Rsi] = 0;
        Assert.NotEqual((int)OrbisGen2Result.ORBIS_GEN2_OK, NpManagerExports.NpHasSignedUp(ctx));
    }

    private static FakeCpuMemory CreateMemory(out CpuContext ctx)
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rsp] = StackAddress;
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 0x100);
        return memory;
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
