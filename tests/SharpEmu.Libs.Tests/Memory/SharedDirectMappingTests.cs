// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// The allocate/map/unmap/release contract for direct memory, through the layer
/// that implements it and through the guest exports that reach it. On hardware a
/// physical range mapped twice is the same pages; these tests fail if a mapping
/// falls back to private memory, which is the bug they exist to catch.
/// See <c>docs/gt7/boot-investigation.md</c> (Appendix G).
/// </summary>
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class SharedDirectMappingTests
{
    // The real direct pool size, so a test that runs before the kernel exports
    // does not cap the section below the offsets a title would use.
    private const ulong PoolSizeBytes = (13824UL - 448UL) * 1024 * 1024;
    private const ulong Page = 0x4000;

    [Fact]
    public void TwoMappingsOfOnePhysicalRangeShareBytesBothWays()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var first = Reserve(memory, 0x10000);
        var second = Reserve(memory, 0x10000);

        Assert.True(memory.TryMapSharedDirect(first, 0x10000, 0x40_0000, executable: false));
        Assert.True(memory.TryMapSharedDirect(second, 0x10000, 0x40_0000, executable: false));

        Assert.True(memory.TryWrite(first, [0xA5, 0x5A]));
        Assert.Equal([0xA5, 0x5A], Read(memory, second, 2));

        Assert.True(memory.TryWrite(second + 0x10000 - 2, [0xC3, 0x3C]));
        Assert.Equal([0xC3, 0x3C], Read(memory, first + 0x10000 - 2, 2));
    }

    [Fact]
    public void OverlappingMappingsAtDifferentOffsetsShareTheOverlap()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var low = Reserve(memory, 0x20000);
        var high = Reserve(memory, 0x20000);

        // The second mapping starts one 64 KiB block into the first, so guest
        // offset 0x10000 of one is guest offset 0 of the other.
        Assert.True(memory.TryMapSharedDirect(low, 0x20000, 0x80_0000, executable: false));
        Assert.True(memory.TryMapSharedDirect(high, 0x20000, 0x81_0000, executable: false));

        Assert.True(memory.TryWrite(low + 0x10000, [0x11, 0x22]));
        Assert.Equal([0x11, 0x22], Read(memory, high, 2));

        Assert.True(memory.TryWrite(high + 0x8000, [0x33]));
        Assert.Equal([0x33], Read(memory, low + 0x18000, 1));
    }

    [Fact]
    public void UnmapKeepsTheAddressAndRemappingSeesTheData()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var address = Reserve(memory, 0x10000);

        Assert.True(memory.TryMapSharedDirect(address, 0x10000, 0xC0_0000, executable: false));
        Assert.True(memory.TryWrite(address + 7, [0x77]));

        Assert.True(memory.TryUnmapShared(address, 0x10000));
        Assert.False(GuestSharedBacking.IsShared(address));

        // Physical ownership is untouched by an unmap, so the bytes are still
        // there when the guest maps the same physical range again.
        Assert.True(memory.TryMapSharedDirect(address, 0x10000, 0xC0_0000, executable: false));
        Assert.Equal([0x77], Read(memory, address + 7, 1));
    }

    [Fact]
    public void PartialUnmapKeepsSurvivorsAtTheirPhysicalOffsets()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var mapped = Reserve(memory, 0x60000);
        var alias = Reserve(memory, 0x60000);
        const ulong physical = 0x100_0000;

        Assert.True(memory.TryMapSharedDirect(mapped, 0x60000, physical, executable: false));
        Assert.True(memory.TryMapSharedDirect(alias, 0x60000, physical, executable: false));
        for (ulong offset = 0; offset < 0x60000; offset += Page)
        {
            Assert.True(memory.TryWrite(mapped + offset, [(byte)(offset / Page + 1)]));
        }

        // Punch the middle out. The tail survivor must come back at its own
        // physical offset, not at the view's base offset.
        Assert.True(memory.TryUnmapShared(mapped + 0x20000, 0x20000));

        Assert.Equal([1], Read(memory, mapped, 1));
        Assert.Equal([(byte)(0x40000 / Page + 1)], Read(memory, mapped + 0x40000, 1));
        Assert.Equal(Read(memory, alias + 0x40000, 1), Read(memory, mapped + 0x40000, 1));

        // The hole is gone, and writes through the alias still reach the survivor.
        Assert.False(memory.IsAccessible(mapped + 0x20000, 1));
        Assert.True(memory.TryWrite(alias + 0x40000 + 5, [0xC3]));
        Assert.Equal([0xC3], Read(memory, mapped + 0x40000 + 5, 1));
    }

    [Fact]
    public void UnmapAcrossTwoViewsLeavesOnlyTheRequestedHole()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        // Larger than the 2 MiB view cap, so this mapping is two views and the
        // unmap below cuts both of them.
        var mapped = Reserve(memory, 0x30_0000);
        var alias = Reserve(memory, 0x30_0000);
        const ulong physical = 0x200_0000;

        Assert.True(memory.TryMapSharedDirect(mapped, 0x30_0000, physical, executable: false));
        Assert.True(memory.TryMapSharedDirect(alias, 0x30_0000, physical, executable: false));
        Assert.True(memory.TryWrite(alias + 0x0F_0000, [0xE1]));
        Assert.True(memory.TryWrite(alias + 0x29_0000, [0xE2]));

        Assert.True(memory.TryUnmapShared(mapped + 0x10_0000, 0x18_0000));

        Assert.Equal([0xE1], Read(memory, mapped + 0x0F_0000, 1));
        Assert.Equal([0xE2], Read(memory, mapped + 0x29_0000, 1));
        Assert.False(memory.IsAccessible(mapped + 0x10_0000, 1));
        Assert.False(memory.IsAccessible(mapped + 0x27_0000, 1));
    }

    [Fact]
    public void ReusedPhysicalRangeIsClearedBeforeItIsHandedOutAgain()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var address = Reserve(memory, 0x10000);
        const ulong physical = 0x300_0000;

        Assert.True(memory.TryMapSharedDirect(address, 0x10000, physical, executable: false));
        Assert.True(memory.TryWrite(address, [0xDE, 0xAD]));
        Assert.True(memory.TryUnmapShared(address, 0x10000));

        // Released section pages keep their bytes and cannot be decommitted, so
        // the next owner only sees zero because the allocator clears the range.
        GuestSharedBacking.ZeroPhysicalRange(physical, 0x10000);

        Assert.True(memory.TryMapSharedDirect(address, 0x10000, physical, executable: false));
        Assert.Equal([0x00, 0x00], Read(memory, address, 2));
    }

    [Fact]
    public void MisalignedRequestIsRefusedAndLeavesThePrivateBacking()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var address = Reserve(memory, 0x10000);

        // A physical offset finer than a guest page cannot be a section offset.
        Assert.False(memory.TryMapSharedDirect(address, 0x10000, 0x400_0000 + 0x1000, executable: false));
        Assert.False(GuestSharedBacking.IsShared(address));

        // The backing the caller already had is still usable, so a refused
        // shared mapping never costs the guest its memory.
        Assert.True(memory.TryWrite(address, [0x5A]));
        Assert.Equal([0x5A], Read(memory, address, 1));
    }

    [Fact]
    public void MemcpyAcrossTwoAdjacentMappingsSucceeds()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        // GT7 boot (race32): memcpy of 0x5000 bytes ending 0x2000 past the start
        // of a 2 MiB mapping that sits directly after another one. Each mapping
        // is its own tracked region, and a span crossing the seam is ordinary
        // guest memory that must not fault.
        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var arena = Reserve(memory, 0x40000);
        var scratch = Reserve(memory, 0x10000);
        // Two separately tracked mappings that touch: map the arena, give its
        // second half back, and map that half again on its own.
        Assert.True(memory.TryMapSharedDirect(arena, 0x40000, 0x700_0000, executable: false));
        Assert.True(memory.TryUnmapShared(arena + 0x20000, 0x20000));
        Assert.True(memory.TryMapSharedDirect(arena + 0x20000, 0x20000, 0x702_0000, executable: false));

        var payload = new byte[0x5000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 7 + 1);
        }

        Assert.True(memory.TryWrite(scratch, payload));
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = arena + 0x20000 - 0x3000;
        context[CpuRegister.Rsi] = scratch;
        context[CpuRegister.Rdx] = (ulong)payload.Length;

        Assert.Equal(0, KernelMemoryCompatExports.Memcpy(context));
        Assert.Equal(payload, Read(memory, arena + 0x20000 - 0x3000, payload.Length));
    }

    [Fact]
    public void HostReadersNeverSeeSurvivorsVanishDuringRepeatedSplits()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        // Host workers reach guest memory through PhysicalVirtualMemory's reader
        // lock, so a split (unmap whole view, remap survivors) must be invisible
        // to them: head and tail stay readable with their bytes on every pass,
        // and neither side deadlocks.
        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var mapped = Reserve(memory, 0x60000);
        const ulong physical = 0x600_0000;
        Assert.True(memory.TryMapSharedDirect(mapped, 0x60000, physical, executable: false));
        Assert.True(memory.TryWrite(mapped, [0x48]));
        Assert.True(memory.TryWrite(mapped + 0x50000, [0x54]));

        using var stop = new CancellationTokenSource();
        var failures = 0;
        var reads = 0;
        var reader = Task.Run(() =>
        {
            var buffer = new byte[1];
            while (!stop.IsCancellationRequested)
            {
                if (!memory.TryRead(mapped, buffer) || buffer[0] != 0x48 ||
                    !memory.TryRead(mapped + 0x50000, buffer) || buffer[0] != 0x54)
                {
                    Interlocked.Increment(ref failures);
                }

                Interlocked.Increment(ref reads);
            }
        });

        var writer = Task.Run(() =>
        {
            for (var pass = 0; pass < 300; pass++)
            {
                // Remapping the whole range merges the pieces back into one view,
                // so every middle unmap below is a real split, not a boundary hit.
                Assert.True(memory.TryMapSharedDirect(mapped, 0x60000, physical, executable: false));
                Assert.True(memory.TryUnmapShared(mapped + 0x20000, 0x20000));
            }
        });

        Assert.True(writer.Wait(TimeSpan.FromSeconds(30)), "splitting deadlocked against the reader");
        stop.Cancel();
        Assert.True(reader.Wait(TimeSpan.FromSeconds(30)), "reader deadlocked against splitting");
        writer.GetAwaiter().GetResult();

        Assert.Equal(0, failures);
        Assert.True(reads > 0);
        Assert.False(memory.IsAccessible(mapped + 0x20000, 1));
    }

    [Fact]
    public unsafe void MappedViewsAreCommittedBeforeFirstTouch()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        // GT7 (race33, race36): a lazily committed view took its first page fault
        // inside a SysV leaf loop, and Windows dispatched it on the guest stack,
        // over the red zone holding the loop's array base. Guest code must never
        // fault just to commit a direct mapping — every view, split survivors
        // included, has to be committed before the guest can reach it.
        GuestSharedBacking.ConfigurePoolSize(PoolSizeBytes);
        using var memory = new PhysicalVirtualMemory();
        var mapped = Reserve(memory, 0x30_0000);

        Assert.True(memory.TryMapSharedDirect(mapped, 0x30_0000, 0x900_0000, executable: false));
        for (ulong offset = 0; offset < 0x30_0000; offset += 0x10_0000)
        {
            Assert.Equal(HostMemory.MEM_COMMIT, QueryState(mapped + offset));
        }

        Assert.True(memory.TryUnmapShared(mapped + 0x10_0000, 0x18_0000));
        Assert.Equal(HostMemory.MEM_COMMIT, QueryState(mapped));
        Assert.Equal(HostMemory.MEM_COMMIT, QueryState(mapped + 0x28_0000));

        static uint QueryState(ulong address)
        {
            Assert.NotEqual(0u, (uint)HostMemory.Query((void*)address, out var info));
            return info.State;
        }
    }

    [Fact]
    public void FaultWaitReturnsImmediatelyWhenNoRemapIsPending()
    {
        // The vectored handler calls this on every access violation; with no
        // split in progress it must decline at once rather than spin.
        Assert.False(GuestSharedBacking.WaitForPendingRemap(0x7_0000_0000));
    }

    [Fact]
    public void MapDirectMemoryExportAliasesOnePhysicalRange()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        using var memory = new PhysicalVirtualMemory();
        var scratch = Reserve(memory, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong physical = 0x500_0000;
        const ulong length = 0x20000;

        var first = MapDirect(context, scratch, physical, length);
        var second = MapDirect(context, scratch + 0x100, physical, length);

        Assert.NotEqual(0UL, first);
        Assert.NotEqual(0UL, second);
        Assert.NotEqual(first, second);
        Assert.True(GuestSharedBacking.IsShared(first));
        Assert.True(GuestSharedBacking.IsShared(second));

        Assert.True(memory.TryWrite(first + 0x40, [0x1F, 0x2F]));
        Assert.Equal([0x1F, 0x2F], Read(memory, second + 0x40, 2));

        Assert.True(memory.TryWrite(second + length - 2, [0x3F, 0x4F]));
        Assert.Equal([0x3F, 0x4F], Read(memory, first + length - 2, 2));

        // Munmap through the export detaches the view but not the alias.
        context[CpuRegister.Rdi] = first;
        context[CpuRegister.Rsi] = length;
        Assert.Equal(0, KernelMemoryCompatExports.KernelMunmap(context));
        Assert.False(GuestSharedBacking.IsShared(first));
        Assert.Equal([0x1F, 0x2F], Read(memory, second + 0x40, 2));
    }

    [Fact]
    public void ReleaseDetachesMappingsAndReuseStartsZeroed()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        // GT7 (race33): allocate, map, release, allocate the same physical range
        // again and map it somewhere new — never calling munmap. Release has to
        // detach the old address, and the next owner has to see zero.
        using var memory = new PhysicalVirtualMemory();
        var scratch = Reserve(memory, 0x10000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong length = 0x20000;

        var physical = AllocateDirect(context, scratch + 0x200, length);
        var first = MapDirect(context, scratch, physical, length);
        Assert.True(memory.TryWrite(first + 0x10, [0xBE, 0xEF]));

        context[CpuRegister.Rdi] = physical;
        context[CpuRegister.Rsi] = length;
        Assert.Equal(0, KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(context));

        Assert.False(GuestSharedBacking.IsShared(first));
        Assert.False(memory.IsAccessible(first + 0x10, 1));

        var reused = AllocateDirect(context, scratch + 0x200, length);
        Assert.Equal(physical, reused);
        var second = MapDirect(context, scratch + 0x100, reused, length);
        Assert.Equal([0x00, 0x00], Read(memory, second + 0x10, 2));
    }

    [Fact]
    public void FixedMapOverHolesFromTwoReleasesSucceeds()
    {
        if (!GuestSharedBacking.IsSupported)
        {
            return;
        }

        // GT7 (race34): two adjacent mappings each lose a piece to a release —
        // the tail of one, the head of the next — and the title then maps new
        // memory at a fixed address spanning both holes, and copies across it.
        using var memory = new PhysicalVirtualMemory();
        var scratch = Reserve(memory, 0x10000);
        var arena = Reserve(memory, 0x80000);
        var context = new CpuContext(memory, Generation.Gen5);
        // Hand the arena back to the reservation first, the state a title's own
        // address window is in before it maps into it at fixed addresses.
        Assert.True(memory.TryMapSharedDirect(arena, 0x80000, 0x800_0000, executable: false));
        Assert.True(memory.TryUnmapShared(arena, 0x80000));

        var lowPhysical = AllocateDirect(context, scratch + 0x200, 0x40000);
        var highPhysical = AllocateDirect(context, scratch + 0x200, 0x40000);
        Assert.Equal(arena, MapDirect(context, scratch, lowPhysical, 0x40000, arena, fixedAddress: true));
        Assert.Equal(arena + 0x40000, MapDirect(context, scratch, highPhysical, 0x40000, arena + 0x40000, fixedAddress: true));

        Release(context, lowPhysical + 0x20000, 0x20000);
        Release(context, highPhysical, 0x20000);
        Assert.False(memory.IsAccessible(arena + 0x20000, 1));
        Assert.False(memory.IsAccessible(arena + 0x40000, 1));

        var fresh = AllocateDirect(context, scratch + 0x200, 0x40000);
        Assert.Equal(arena + 0x20000, MapDirect(context, scratch, fresh, 0x40000, arena + 0x20000, fixedAddress: true));
        Assert.True(GuestSharedBacking.IsShared(arena + 0x20000));

        // A copy from the surviving head of the low mapping into the new one.
        var payload = new byte[0x8000];
        Array.Fill(payload, (byte)0x6B);
        Assert.True(memory.TryWrite(arena + 0x1C000, payload));
        Assert.Equal(payload, Read(memory, arena + 0x1C000, payload.Length));
    }

    private static void Release(CpuContext context, ulong physical, ulong length)
    {
        context[CpuRegister.Rdi] = physical;
        context[CpuRegister.Rsi] = length;
        Assert.Equal(0, KernelMemoryCompatExports.KernelCheckedReleaseDirectMemory(context));
    }

    private static ulong MapDirect(
        CpuContext context,
        ulong inOutAddress,
        ulong physical,
        ulong length,
        ulong requestedAddress,
        bool fixedAddress)
    {
        Assert.True(context.TryWriteUInt64(inOutAddress, requestedAddress));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = 0x03;
        context[CpuRegister.Rcx] = fixedAddress ? 0x10UL : 0UL;
        context[CpuRegister.R8] = physical;
        context[CpuRegister.R9] = 0;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(context.TryReadUInt64(inOutAddress, out var mapped));
        return mapped;
    }

    private static ulong AllocateDirect(CpuContext context, ulong outAddress, ulong length)
    {
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = PoolSizeBytes;
        context[CpuRegister.Rdx] = length;
        context[CpuRegister.Rcx] = 0x10000;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = outAddress;

        Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
        Assert.True(context.TryReadUInt64(outAddress, out var physical));
        return physical;
    }

    private static ulong MapDirect(CpuContext context, ulong inOutAddress, ulong physical, ulong length)
    {
        Assert.True(context.TryWriteUInt64(inOutAddress, 0));
        context[CpuRegister.Rdi] = inOutAddress;
        context[CpuRegister.Rsi] = length;
        context[CpuRegister.Rdx] = 0x03; // CPU read | write
        context[CpuRegister.Rcx] = 0;
        context[CpuRegister.R8] = physical;
        context[CpuRegister.R9] = 0x10000;

        Assert.Equal(0, KernelMemoryCompatExports.KernelMapDirectMemory(context));
        Assert.True(context.TryReadUInt64(inOutAddress, out var mapped));
        return mapped;
    }

    private static ulong Reserve(PhysicalVirtualMemory memory, ulong size)
    {
        var address = memory.AllocateAt(0, size, executable: false);
        Assert.NotEqual(0UL, address);
        return address;
    }

    private static byte[] Read(PhysicalVirtualMemory memory, ulong address, int length)
    {
        var buffer = new byte[length];
        Assert.True(memory.TryRead(address, buffer));
        return buffer;
    }
}
