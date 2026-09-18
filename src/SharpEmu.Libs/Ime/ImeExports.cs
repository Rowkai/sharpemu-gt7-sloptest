// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    private const int Busy = unchecked((int)0x80BC0001);
    private const int NotOpened = unchecked((int)0x80BC0002);
    private const int NoMemory = unchecked((int)0x80BC0003);
    private const int ConnectionFailed = unchecked((int)0x80BC0004);
    private const int InvalidUser = unchecked((int)0x80BC0010);
    private const int InvalidOption = unchecked((int)0x80BC0015);
    private const int InvalidHandler = unchecked((int)0x80BC0022);
    private const int InvalidAddress = unchecked((int)0x80BC0031);
    private const int InvalidReserved = unchecked((int)0x80BC0032);
    private const int InternalError = unchecked((int)0x80BC00FF);
    private static readonly Dictionary<int, Keyboard> Keyboards = new();

    private sealed class Keyboard(ulong argument)
    {
        public ulong Argument { get; } = argument;
        public bool PendingOpen { get; set; } = true;
    }

    public static void ResetRuntimeState()
    {
        lock (Keyboards) Keyboards.Clear();
    }

    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        var handler = ctx[CpuRegister.Rdi];
        KeyValuePair<int, Keyboard>[] pending;
        lock (Keyboards)
            pending = Keyboards.Where(pair => pair.Value.PendingOpen).ToArray();
        if (pending.Length == 0) return ctx.SetReturn(0);
        if (handler == 0) return ctx.SetReturn(InvalidHandler);
        var scheduler = GuestThreadExecution.Scheduler;
        if (scheduler is null) return ctx.SetReturn(InternalError);
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory(72, 8, out var address))
            return ctx.SetReturn(NoMemory);

        try
        {
            // SceImeEvent: 32-bit id, padding, then a 64-byte aligned union.
            // No host keyboard is exposed, but opening still completes with an empty list.
            Span<byte> data = stackalloc byte[72];
            foreach (var (user, keyboard) in pending)
            {
                data.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(data, 0x100); // KEYBOARD_OPEN
                BinaryPrimitives.WriteInt32LittleEndian(data[8..], user);
                if (!ctx.Memory.TryWrite(address, data)) return ctx.SetReturn(InvalidAddress);
                lock (Keyboards)
                {
                    if (!Keyboards.TryGetValue(user, out var current) ||
                        current != keyboard || !keyboard.PendingOpen) continue;
                    keyboard.PendingOpen = false;
                }
                // Call outside the lock: handlers may close or reopen their keyboard.
                if (!scheduler.TryCallGuestFunction(ctx, handler, keyboard.Argument, address,
                        0, 0, "sceImeUpdate keyboard open", out _))
                {
                    lock (Keyboards) keyboard.PendingOpen = true;
                    return ctx.SetReturn(InternalError);
                }
            }
            return ctx.SetReturn(0);
        }
        finally
        {
            allocator.TryFreeGuestMemory(address);
        }
    }

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        var user = unchecked((int)ctx[CpuRegister.Rdi]);
        if (user < 0 || user == 0xFF) return ctx.SetReturn(InvalidUser);
        Span<byte> param = stackalloc byte[32];
        if (ctx[CpuRegister.Rsi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rsi], param))
            return ctx.SetReturn(InvalidAddress);
        if (BinaryPrimitives.ReadUInt64LittleEndian(param[16..]) == 0)
            return ctx.SetReturn(InvalidHandler);
        if ((BinaryPrimitives.ReadUInt32LittleEndian(param) & ~0x3Fu) != 0)
            return ctx.SetReturn(InvalidOption);
        if (BinaryPrimitives.ReadUInt32LittleEndian(param[4..]) != 0 ||
            BinaryPrimitives.ReadUInt64LittleEndian(param[24..]) != 0)
            return ctx.SetReturn(InvalidReserved);
        lock (Keyboards)
        {
            if (!Keyboards.TryAdd(user, new Keyboard(BinaryPrimitives.ReadUInt64LittleEndian(param[8..]))))
                return ctx.SetReturn(Busy);
        }
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "PMVehSlfZ94",
        ExportName = "sceImeKeyboardClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardClose(CpuContext ctx)
    {
        var user = unchecked((int)ctx[CpuRegister.Rdi]);
        if (user < 0 || user == 0xFF) return ctx.SetReturn(InvalidUser);
        lock (Keyboards) return ctx.SetReturn(Keyboards.Remove(user) ? 0 : NotOpened);
    }

    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        var user = unchecked((int)ctx[CpuRegister.Rdi]);
        if (user < 0 || user == 0xFF) return ctx.SetReturn(InvalidUser);
        Span<byte> resources = stackalloc byte[24];
        resources.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(resources, user);
        if (ctx[CpuRegister.Rsi] == 0 || !ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], resources))
            return ctx.SetReturn(InvalidAddress);
        lock (Keyboards) return ctx.SetReturn(Keyboards.ContainsKey(user) ? ConnectionFailed : NotOpened);
    }
}
