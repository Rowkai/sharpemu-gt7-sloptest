// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

/// <summary>
/// Pins down the Windows behaviour that shared direct-memory backing would be
/// built on — a pagefile-backed <c>SEC_RESERVE</c> section mapped into
/// placeholders — before any allocator depends on it. On hardware a physical
/// direct-memory range mapped at two virtual addresses is the same pages; these
/// tests check which parts of that, and of unmapping, reuse and reclamation,
/// the host primitives can and cannot provide.
/// </summary>
public sealed unsafe partial class SharedBackingSectionTests
{
    private const ulong Page = 0x4000;      // PS5 direct-memory page
    private const ulong Granule = 0x10000;  // Windows allocation granularity
    private const ulong SectionSize = 0x100000;

    [Fact]
    public void AliasViewsAtPageOffsetShareBytesInBothDirections()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var section = new Section(SectionSize);
        using var space = new AddressSpace();
        var region = space.ReservePlaceholder(2 * Granule);
        SplitPlaceholder(region, Granule);

        var first = space.MapView(section, region, Page, Granule);
        var second = space.MapView(section, region + Granule, Page, Granule);
        Commit(first, Granule);

        ((byte*)first)[0] = 0xA5;
        Assert.Equal(0xA5, ((byte*)second)[0]);
        ((byte*)second)[Granule - 1] = 0x5A;
        Assert.Equal(0x5A, ((byte*)first)[Granule - 1]);
    }

    [Fact]
    public void UnmappedViewKeepsAddressOwnershipAndSectionKeepsData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var section = new Section(SectionSize);
        using var space = new AddressSpace();
        var region = space.ReservePlaceholder(Granule);
        var view = space.MapView(section, region, Page, Granule);
        Commit(view, Granule);
        ((byte*)view)[7] = 0x77;

        space.UnmapView(view);

        // The address stays a placeholder, so an ordinary host allocation
        // cannot take it while the guest has nothing mapped there.
        Assert.Equal(MEM_RESERVE, QueryState(region));
        Assert.True(VirtualAlloc((void*)region, (nuint)Granule, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE) == null);

        var remapped = space.MapView(section, region, Page, Granule);
        Assert.Equal(0x77, ((byte*)remapped)[7]);
    }

    [Fact]
    public void PartialUnmapBySplittingKeepsSurvivingPiecesAliased()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var section = new Section(SectionSize);
        using var space = new AddressSpace();
        var region = space.ReservePlaceholder(3 * Granule);
        var aliasRegion = space.ReservePlaceholder(3 * Granule);
        var view = space.MapView(section, region, Page, 3 * Granule);
        var alias = space.MapView(section, aliasRegion, Page, 3 * Granule);
        Commit(view, 3 * Granule);
        for (ulong i = 0; i < 3 * Granule; i += Page)
        {
            ((byte*)view)[i] = (byte)(i / Page + 1);
        }

        // UnmapViewOfFile2 removes the whole view. Unmapping the middle third
        // means: restore the placeholder, split it, and map the survivors again.
        space.UnmapView(view);
        SplitPlaceholder(region, Granule);
        SplitPlaceholder(region + Granule, Granule);
        var left = space.MapView(section, region, Page, Granule);
        var right = space.MapView(section, region + 2 * Granule, Page + 2 * Granule, Granule);

        for (ulong i = 0; i < Granule; i += Page)
        {
            Assert.Equal(((byte*)alias)[i], ((byte*)left)[i]);
            Assert.Equal(((byte*)alias)[2 * Granule + i], ((byte*)right)[i]);
        }

        Assert.Equal(MEM_RESERVE, QueryState(region + Granule));
        ((byte*)alias)[2 * Granule + 5] = 0xC3;
        Assert.Equal(0xC3, ((byte*)right)[5]);
    }

    [Fact]
    public void PlaceholderSplitsAndMapsAtPageGranularity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // GT7 unmaps and maps direct memory in 16 KiB pages, finer than the
        // 64 KiB allocation granularity.
        using var section = new Section(SectionSize);
        using var space = new AddressSpace();
        var region = space.ReservePlaceholder(Granule);
        SplitPlaceholder(region, Page);

        var view = space.MapView(section, region, Page, Page);
        var rest = space.MapView(section, region + Page, 2 * Page, Granule - Page);
        Commit(view, Page);
        Commit(rest, Granule - Page);
        ((byte*)view)[0] = 0x11;
        ((byte*)rest)[0] = 0x22;

        using var check = new AddressSpace();
        var whole = check.MapView(section, check.ReservePlaceholder(Granule), Page, Granule);
        Assert.Equal(0x11, ((byte*)whole)[0]);
        Assert.Equal(0x22, ((byte*)whole)[Page]);
    }

    [Fact]
    public void CommittedSectionPagesCannotBeDecommittedAndOutliveTheirViews()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var section = new Section(SectionSize);
        using var space = new AddressSpace();
        var region = space.ReservePlaceholder(Granule);
        var view = space.MapView(section, region, 0, Granule);
        Commit(view, Granule);
        ((byte*)view)[0] = 0x99;

        // Documented for SEC_RESERVE sections: commitment made through a view
        // cannot be handed back with VirtualFree, so releasing guest direct
        // memory does not reclaim its commit charge.
        Assert.False(VirtualFree((void*)view, (nuint)Granule, MEM_DECOMMIT));

        // Nothing clears the pages when every view is gone: a later mapping of
        // the same physical offset sees the old bytes, so the allocator has to
        // decide what a reused physical range contains.
        space.UnmapView(view);
        var reused = space.MapView(section, region, 0, Granule);
        Assert.Equal(0x99, ((byte*)reused)[0]);
    }

    [Fact]
    public void ProtectionChangesStayPerViewAtPageGranularity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // On hardware protection belongs to a mapping, not to the physical
        // pages behind it: mprotect through one alias leaves the other alone.
        using var section = new Section(SectionSize);
        using var space = new AddressSpace();
        var first = space.MapView(section, space.ReservePlaceholder(Granule), 0, Granule);
        var second = space.MapView(section, space.ReservePlaceholder(Granule), 0, Granule);
        Commit(first, Granule);

        Assert.True(VirtualProtect((void*)(first + Page), (nuint)Page, PAGE_READONLY, out _));

        Assert.Equal(PAGE_READWRITE, QueryProtect(first));
        Assert.Equal(PAGE_READONLY, QueryProtect(first + Page));
        Assert.Equal(PAGE_READWRITE, QueryProtect(first + 2 * Page));
        Assert.Equal(PAGE_READWRITE, QueryProtect(second + Page));
        ((byte*)second)[Page] = 0x3C;
        Assert.Equal(0x3C, ((byte*)first)[Page]);
    }

    [Fact]
    public void ExecutableSectionAcceptsExecutableViews()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A view can never be more permissive than its section, so a pool
        // that may hold guest code has to be created executable.
        using var section = new Section(SectionSize, PAGE_EXECUTE_READWRITE);
        using var space = new AddressSpace();
        var view = space.MapView(section, space.ReservePlaceholder(Granule), 0, Granule, PAGE_EXECUTE_READWRITE);
        Assert.True(
            VirtualAlloc((void*)view, (nuint)Granule, MEM_COMMIT, PAGE_EXECUTE_READWRITE) != null,
            $"executable commit failed: {Marshal.GetLastPInvokeError()}");
        Assert.Equal(PAGE_EXECUTE_READWRITE, QueryProtect(view));

        using var readWriteSection = new Section(SectionSize);
        var denied = MapViewOfFile3(
            readWriteSection.Handle, GetCurrentProcess(), (void*)space.ReservePlaceholder(Granule), 0, (nuint)Granule,
            MEM_REPLACE_PLACEHOLDER, PAGE_EXECUTE_READWRITE, null, 0);
        Assert.True(denied == null);
    }

    [Fact]
    public void PlaceholderReservedInSuspendedProcessCanBeSplitAndReplaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The launcher reserves guest address space in the emulator child from
        // outside, before it runs. Those have to be real placeholders the child
        // can split and replace, not plain reservations.
        var child = StartSuspended(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        try
        {
            using var section = new Section(SectionSize);
            var region = (ulong)VirtualAlloc2(
                child.Process, null, (nuint)(2 * Granule), MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
            Assert.True(region != 0, $"remote placeholder failed: {Marshal.GetLastPInvokeError()}");
            Assert.True(
                VirtualFreeEx(child.Process, (void*)region, (nuint)Page, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER),
                $"remote split failed: {Marshal.GetLastPInvokeError()}");

            var remote = (ulong)MapViewOfFile3(
                section.Handle, child.Process, (void*)(region + Page), Page, (nuint)(2 * Granule - Page),
                MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, null, 0);
            Assert.True(remote == region + Page, $"remote view failed: {Marshal.GetLastPInvokeError()}");

            using var space = new AddressSpace();
            var localRegion = space.ReservePlaceholder(2 * Granule);
            SplitPlaceholder(localRegion, 2 * Granule - Page);
            var local = space.MapView(section, localRegion, Page, 2 * Granule - Page);
            Commit(local, 2 * Granule - Page);
            ((byte*)local)[3] = 0xE7;

            byte observed = 0;
            Assert.True(ReadProcessMemory(child.Process, (void*)(remote + 3), &observed, 1, out _));
            Assert.Equal(0xE7, observed);
        }
        finally
        {
            TerminateProcess(child.Process, 1);
            CloseHandle(child.Thread);
            CloseHandle(child.Process);
        }
    }

    [Fact]
    public void PrivateAllocationCanClaimTheMiddleOfAPlaceholder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // What the allocator does when the guest asks for a fixed address inside
        // the launcher's reservation: split that piece out and replace it with
        // ordinary private memory, in one step so nothing else can take it.
        using var space = new AddressSpace();
        var region = space.ReservePlaceholder(8 * Granule);
        var claimAt = region + 3 * Granule;

        Assert.True(
            VirtualFree((void*)claimAt, (nuint)(2 * Granule), MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER),
            $"middle split failed: {Marshal.GetLastPInvokeError()}");
        var claimed = VirtualAlloc2(
            null, (void*)claimAt, (nuint)(2 * Granule),
            MEM_RESERVE | MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, null, 0);
        Assert.True((ulong)claimed == claimAt, $"private replace failed: {Marshal.GetLastPInvokeError()}");

        Commit(claimAt, 2 * Granule);
        ((byte*)claimAt)[0] = 0x2B;
        Assert.Equal(0x2B, ((byte*)claimAt)[0]);

        // Neighbours on both sides must still be placeholders.
        Assert.Equal(MEM_RESERVE, QueryState(region));
        Assert.Equal(MEM_RESERVE, QueryState(claimAt + 2 * Granule));
    }

    private static uint QueryProtect(ulong address)
    {
        Assert.NotEqual(0u, (nuint)VirtualQuery((void*)address, out var info, (nuint)sizeof(MemoryBasicInformation64)));
        return info.Protect;
    }

    private static ProcessInformation StartSuspended(string path)
    {
        var startup = new StartupInfo { Size = sizeof(StartupInfo) };
        Assert.True(
            CreateProcessW(path, null, null, null, false, CREATE_SUSPENDED | CREATE_NO_WINDOW, null, null, &startup, out var info),
            $"CreateProcessW failed: {Marshal.GetLastPInvokeError()}");
        return info;
    }

    private static void SplitPlaceholder(ulong address, ulong size)
    {
        Assert.True(
            VirtualFree((void*)address, (nuint)size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER),
            $"split placeholder at 0x{address:X} size 0x{size:X} failed: {Marshal.GetLastPInvokeError()}");
    }

    private static void Commit(ulong address, ulong size)
    {
        Assert.True(
            VirtualAlloc((void*)address, (nuint)size, MEM_COMMIT, PAGE_READWRITE) != null,
            $"commit at 0x{address:X} size 0x{size:X} failed: {Marshal.GetLastPInvokeError()}");
    }

    private static uint QueryState(ulong address)
    {
        Assert.NotEqual(0u, (nuint)VirtualQuery((void*)address, out var info, (nuint)sizeof(MemoryBasicInformation64)));
        return info.State;
    }

    private sealed class Section : IDisposable
    {
        public Section(ulong size, uint protect = PAGE_READWRITE)
        {
            Handle = CreateFileMappingW(
                (void*)-1,
                null,
                protect | SEC_RESERVE,
                (uint)(size >> 32),
                (uint)size,
                null);
            Assert.True(Handle != null, $"CreateFileMappingW failed: {Marshal.GetLastPInvokeError()}");
        }

        public void* Handle { get; }

        public void Dispose() => CloseHandle(Handle);
    }

    /// <summary>Tracks placeholders and views so every test releases what it took.</summary>
    private sealed class AddressSpace : IDisposable
    {
        private readonly List<(ulong Address, ulong Size)> _ranges = [];
        private readonly HashSet<ulong> _views = [];

        public ulong ReservePlaceholder(ulong size)
        {
            var address = (ulong)VirtualAlloc2(
                null, null, (nuint)size, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0);
            Assert.True(address != 0, $"VirtualAlloc2 placeholder failed: {Marshal.GetLastPInvokeError()}");
            _ranges.Add((address, size));
            return address;
        }

        public ulong MapView(Section section, ulong address, ulong offset, ulong size, uint protect = PAGE_READWRITE)
        {
            var view = (ulong)MapViewOfFile3(
                section.Handle, GetCurrentProcess(), (void*)address, offset, (nuint)size,
                MEM_REPLACE_PLACEHOLDER, protect, null, 0);
            Assert.True(
                view == address,
                $"MapViewOfFile3 at 0x{address:X} offset 0x{offset:X} size 0x{size:X} failed: {Marshal.GetLastPInvokeError()}");
            _views.Add(view);
            return view;
        }

        public void UnmapView(ulong view)
        {
            Assert.True(
                UnmapViewOfFile2(GetCurrentProcess(), (void*)view, MEM_PRESERVE_PLACEHOLDER),
                $"UnmapViewOfFile2 at 0x{view:X} failed: {Marshal.GetLastPInvokeError()}");
            _views.Remove(view);
        }

        public void Dispose()
        {
            foreach (var view in _views)
            {
                UnmapViewOfFile2(GetCurrentProcess(), (void*)view, MEM_PRESERVE_PLACEHOLDER);
            }

            foreach (var (address, size) in _ranges)
            {
                for (var cursor = address; cursor < address + size;)
                {
                    if (VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation64)) == 0)
                    {
                        break;
                    }

                    if (info.State == MEM_RESERVE)
                    {
                        VirtualFree((void*)info.BaseAddress, 0, MEM_RELEASE);
                    }

                    cursor = info.BaseAddress + info.RegionSize;
                }
            }
        }
    }

    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_DECOMMIT = 0x4000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
    private const uint MEM_RESERVE_PLACEHOLDER = 0x40000;
    private const uint MEM_PRESERVE_PLACEHOLDER = 0x2;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READONLY = 0x02;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint SEC_RESERVE = 0x4000000;
    private const uint CREATE_SUSPENDED = 0x4;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string path, char* commandLine, void* processAttributes, void* threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, void* environment,
        string? currentDirectory, StartupInfo* startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(void* process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(void* process, void* address, nuint size, uint type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(void* process, void* address, byte* buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(void* address, nuint size, uint protect, out uint oldProtect);

    private struct StartupInfo
    {
        public int Size;
        public void* Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public void* Reserved2, StdInput, StdOutput, StdError;
    }

    private struct ProcessInformation
    {
        public void* Process;
        public void* Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial void* CreateFileMappingW(void* file, void* attributes, uint protect, uint sizeHigh, uint sizeLow, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(void* handle);

    [LibraryImport("kernel32.dll")]
    private static partial void* GetCurrentProcess();

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(void* process, void* address, nuint size, uint type, uint protect, void* parameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* MapViewOfFile3(void* mapping, void* process, void* address, ulong offset, nuint size, uint type, uint protect, void* parameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile2(void* process, void* address, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* address, out MemoryBasicInformation64 buffer, nuint length);

    private struct MemoryBasicInformation64
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public uint Alignment1;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Alignment2;
    }
}
