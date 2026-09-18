// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

[Collection("AmprFileRegistry")]
public sealed class AmprGatherScatterTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathListAddress = MemoryBase + 0x100;
    private const ulong PathAddress = MemoryBase + 0x200;
    private const ulong IdsAddress = MemoryBase + 0x800;
    private const ulong CommandBufferAddress = MemoryBase + 0x1000;
    private const ulong RecordBufferAddress = MemoryBase + 0x1100;
    private const ulong FirstDestination = MemoryBase + 0x2000;
    private const ulong ScatterDestination = MemoryBase + 0x2100;
    private const ulong StackAddress = MemoryBase + 0x3000;

    // The measure only has to answer "how much room does this command need", and
    // the guest folds it into an unsigned free-space test. A non-zero, plausible
    // size is the whole contract; an error code there is what wedged GT7's
    // streamer in a submit-and-retry loop.
    [Fact]
    public void MeasureReadFileGatherScatter_ReportsTheRecordSize()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x4000), Generation.Gen5);
        Assert.Equal(0, AmprExports.MeasureCommandSizeReadFileGatherScatter(context));
        Assert.Equal(0x30UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void ReadFileGatherScatter_ContinuesTheFileTheBuffersLastReadSelected()
    {
        byte[] fileContents = [10, 11, 12, 13, 14, 15, 16, 17];
        RunWithRegisteredFile(fileContents, (memory, context, fileId) =>
        {
            OpenCommandBuffer(context);
            ReadFile(context, memory, fileId, FirstDestination, size: 2, offset: 0);

            // rdi=buffer, rsi/rdx are the visible pointers the guest wrapper
            // fills in, rcx=destination, r8=size, r9=file offset.
            context[CpuRegister.Rdi] = CommandBufferAddress;
            context[CpuRegister.Rcx] = ScatterDestination;
            context[CpuRegister.R8] = 4;
            context[CpuRegister.R9] = 3;
            Assert.Equal(0, AmprExports.AprCommandBufferReadFileGatherScatter(context));

            Span<byte> scattered = stackalloc byte[4];
            Assert.True(memory.TryRead(ScatterDestination, scattered));
            Assert.Equal(fileContents.AsSpan(3, 4), scattered);

            // Second record in the buffer, naming the same file.
            Span<byte> record = stackalloc byte[0x30];
            Assert.True(memory.TryRead(RecordBufferAddress + 0x30, record));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(record));
            Assert.Equal(fileId, BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
            Assert.Equal(ScatterDestination, BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]));
            Assert.Equal(4UL, BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]));
            Assert.Equal(3UL, BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]));
            Assert.Equal(4UL, BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]));
        });
    }

    [Fact]
    public void ReadFileGatherScatter_WithoutASelectedFile_Fails()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x4000), Generation.Gen5);
        OpenCommandBuffer(context);

        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rcx] = ScatterDestination;
        context[CpuRegister.R8] = 4;
        context[CpuRegister.R9] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AmprExports.AprCommandBufferReadFileGatherScatter(context));
    }

    [Fact]
    public void ResetGatherScatterState_DropsTheSelectedFile()
    {
        byte[] fileContents = [1, 2, 3, 4, 5, 6, 7, 8];
        RunWithRegisteredFile(fileContents, (memory, context, fileId) =>
        {
            OpenCommandBuffer(context);
            ReadFile(context, memory, fileId, FirstDestination, size: 2, offset: 0);

            context[CpuRegister.Rdi] = CommandBufferAddress;
            Assert.Equal(0, AmprExports.AprCommandBufferResetGatherScatterState(context));

            context[CpuRegister.Rdi] = CommandBufferAddress;
            context[CpuRegister.Rcx] = ScatterDestination;
            context[CpuRegister.R8] = 4;
            context[CpuRegister.R9] = 0;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                AmprExports.AprCommandBufferReadFileGatherScatter(context));
        });
    }

    private static void OpenCommandBuffer(CpuContext context)
    {
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rsi] = RecordBufferAddress;
        context[CpuRegister.Rdx] = 0x200;
        Assert.Equal(0, AmprExports.CommandBufferConstructor(context));
    }

    private static void ReadFile(
        CpuContext context,
        FakeCpuMemory memory,
        uint fileId,
        ulong destination,
        ulong size,
        ulong offset)
    {
        Span<byte> offsetBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(offsetBytes, offset);
        Assert.True(memory.TryWrite(StackAddress + sizeof(ulong), offsetBytes));

        context[CpuRegister.Rsp] = StackAddress;
        context[CpuRegister.Rdi] = CommandBufferAddress;
        context[CpuRegister.Rcx] = fileId;
        context[CpuRegister.R8] = destination;
        context[CpuRegister.R9] = size;
        Assert.Equal(0, AmprExports.AprCommandBufferReadFile(context));
    }

    private static void RunWithRegisteredFile(
        byte[] contents,
        Action<FakeCpuMemory, CpuContext, uint> body)
    {
        var mountRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-apr-gs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mountRoot);
        var mountPoint = $"/sharpemu_apr_gs_{Guid.NewGuid():N}";
        const string fileName = "asset.bin";
        var hostPath = Path.Combine(mountRoot, fileName);

        try
        {
            File.WriteAllBytes(hostPath, contents);
            KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, mountRoot);

            var memory = new FakeCpuMemory(MemoryBase, 0x4000);
            var context = new CpuContext(memory, Generation.Gen5);
            memory.WriteCString(PathAddress, $"{mountPoint}/{fileName}");
            Span<byte> pointer = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(pointer, PathAddress);
            Assert.True(memory.TryWrite(PathListAddress, pointer));

            context[CpuRegister.Rdi] = PathListAddress;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = IdsAddress;
            Assert.Equal(0, KernelMemoryCompatExports.KernelAprResolveFilepathsToIds(context));

            Span<byte> idBytes = stackalloc byte[sizeof(uint)];
            Assert.True(memory.TryRead(IdsAddress, idBytes));
            body(memory, context, BinaryPrimitives.ReadUInt32LittleEndian(idBytes));
        }
        finally
        {
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            if (Directory.Exists(mountRoot))
            {
                Directory.Delete(mountRoot, recursive: true);
            }
        }
    }
}
