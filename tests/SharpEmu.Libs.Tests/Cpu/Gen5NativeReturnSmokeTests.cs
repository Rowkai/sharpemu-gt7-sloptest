// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class Gen5NativeReturnSmokeTests
{
    private const ulong CallbackReturnValue = 0x1234_5678_9ABC_DEF0UL;

    [Fact]
    public async Task SyntheticGen5Entry_ReturnsToHost()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        if (IsolatedTestWorker.IsWorker)
        {
            ExecuteSyntheticGuest();
            return;
        }

        await IsolatedTestWorker.AssertPassesInIsolation(
            typeof(Gen5NativeReturnSmokeTests),
            nameof(SyntheticGen5Entry_ReturnsToHost));
    }

    // An import-free callback never passes through import dispatch, so nothing
    // but the native return boundary can carry its RAX back to the caller.
    [Fact]
    public async Task ImportFreeGuestCallback_ReturnsFull64BitRax()
    {
        if (!IsSupportedHost)
        {
            return;
        }

        if (IsolatedTestWorker.IsWorker)
        {
            ExecuteSyntheticCallbacks();
            return;
        }

        await IsolatedTestWorker.AssertPassesInIsolation(
            typeof(Gen5NativeReturnSmokeTests),
            nameof(ImportFreeGuestCallback_ReturnsFull64BitRax));
    }

    private static bool IsSupportedHost =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS());

    private static void ExecuteSyntheticGuest()
    {
        using var memory = new PhysicalVirtualMemory();
        var image = new SelfLoader().Load(BuildSyntheticElf(), memory);
        Assert.Equal((byte)2, image.ElfHeader.AbiVersion);
        Assert.Equal(0x0000_0008_0000_1000UL, image.EntryPoint);

        var moduleManager = new ModuleManager();
        moduleManager.Freeze();

        var backend = new DirectExecutionBackend(moduleManager);
        using var dispatcher = new CpuDispatcher(memory, moduleManager, backend);
        var result = dispatcher.DispatchEntry(
            image.EntryPoint,
            Generation.Gen5,
            image.ImportStubs,
            image.RuntimeSymbols,
            "synthetic-native-return",
            new CpuExecutionOptions
            {
                CpuEngine = CpuExecutionEngine.NativeOnly,
                EnableDisasmDiagnostics = false,
                StrictDynlibResolution = true,
                ImportTraceLimit = 0,
                DebugHook = null
            });

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, dispatcher.LastSessionSummary.Result);
        Assert.Equal(CpuExitReason.ReturnedToHost, dispatcher.LastSessionSummary.Reason);
        Assert.Equal(0, dispatcher.LastSessionSummary.ImportsHit);
        Assert.Equal(0, dispatcher.LastSessionSummary.UniqueNidsHit);
        Assert.Null(dispatcher.LastTrapInfo);
        Assert.Null(dispatcher.LastMemoryFaultInfo);
        Assert.Null(dispatcher.LastNotImplementedInfo);
    }

    private static void ExecuteSyntheticCallbacks()
    {
        using var memory = new PhysicalVirtualMemory();
        var moduleManager = new ModuleManager();
        moduleManager.Freeze();
        var backend = new DirectExecutionBackend(moduleManager);
        var context = new CpuContext(memory, Generation.Gen5);
        var code = memory.AllocateAt(0x0000_0008_0000_0000UL, 0x1000);

        // Ordinary return: the callback returns straight into its entry stub.
        var direct = new byte[11];
        direct[0] = 0x48; direct[1] = 0xB8; // mov rax, imm64
        BinaryPrimitives.WriteUInt64LittleEndian(direct.AsSpan(2), CallbackReturnValue);
        direct[10] = 0xC3; // ret
        Assert.True(memory.TryWrite(code, direct));
        AssertCallbackReturnsValue(backend, context, code);

        // Resumed continuations leave guest code through the shared guest return
        // stub instead, which calls TlsGetValue before restoring the host stack.
        var returnStub = (nint)typeof(DirectExecutionBackend)
            .GetField("_guestReturnStub", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(backend)!;
        Assert.NotEqual(0, returnStub);
        var viaReturnStub = new byte[25];
        viaReturnStub[0] = 0x48; viaReturnStub[1] = 0xB9; // mov rcx, imm64
        BinaryPrimitives.WriteUInt64LittleEndian(viaReturnStub.AsSpan(2), (ulong)returnStub);
        viaReturnStub[10] = 0x48; viaReturnStub[11] = 0x89; viaReturnStub[12] = 0x0C; viaReturnStub[13] = 0x24; // mov [rsp], rcx
        viaReturnStub[14] = 0x48; viaReturnStub[15] = 0xB8; // mov rax, imm64
        BinaryPrimitives.WriteUInt64LittleEndian(viaReturnStub.AsSpan(16), CallbackReturnValue);
        viaReturnStub[24] = 0xC3; // ret
        Assert.True(memory.TryWrite(code + 0x100, viaReturnStub));
        AssertCallbackReturnsValue(backend, context, code + 0x100);
    }

    private static void AssertCallbackReturnsValue(DirectExecutionBackend backend, CpuContext context, ulong entry)
    {
        Assert.True(
            backend.TryCallGuestFunction(context, entry, 0, 0, 0, 0, 0, 0, "synthetic-callback", out var returnValue, out var error),
            error);
        Assert.Equal(CallbackReturnValue, returnValue);
    }

    private static byte[] BuildSyntheticElf()
    {
        const int elfHeaderSize = 0x40;
        const int programHeaderSize = 0x38;
        const int fileOffset = 0x1000;
        const ulong entryPoint = 0x1000;
        ReadOnlySpan<byte> payload = [0x31, 0xC0, 0xC3]; // xor eax, eax; ret
        var image = new byte[fileOffset + payload.Length];

        image[0] = 0x7F;
        image[1] = (byte)'E';
        image[2] = (byte)'L';
        image[3] = (byte)'F';
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        image[7] = 9;
        image[8] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x20), elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x36), programHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x38), 1);

        var programHeader = image.AsSpan(elfHeaderSize, programHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[0x04..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x08..], fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x10..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x18..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x20..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x28..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[0x30..], 0x1000);
        payload.CopyTo(image.AsSpan(fileOffset));

        return image;
    }
}
