// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Shared backing for guest direct memory.
/// <para>
/// On hardware a physical direct-memory range mapped at two virtual addresses is
/// the same pages, so a write through one is visible through the other. Guest
/// code runs natively here, so guest VA is host VA and that can only be provided
/// by OS sections: the whole direct pool is one pagefile-backed
/// <c>SEC_RESERVE</c> section, a physical offset *is* a section offset, and every
/// mapping is a view of it.
/// </para>
/// <para>
/// Views are mapped into the launcher's guest-window placeholders, which is also
/// the only way to map at sub-64 KiB offsets. See
/// <c>docs/gt7/boot-investigation.md</c> (Appendix G) for the contract and
/// <c>SharedBackingSectionTests</c> for the host behaviour this relies on.
/// </para>
/// </summary>
public static unsafe partial class GuestSharedBacking
{
    /// <summary>PS5 direct-memory page; every address, length and offset is a multiple of it.</summary>
    public const ulong PageSize = 0x4000;

    /// <summary>
    /// Largest single view. Unmaps that land on a view boundary need no remap, and
    /// titles map direct memory 2 MiB aligned, so this keeps the common partial
    /// unmap free without paying for a view per guest page.
    /// </summary>
    private const ulong MaximumViewBytes = 0x20_0000;

    private static readonly object _gate = new();
    private static readonly SortedList<ulong, View> _views = new();
    private static void* _section;
    private static ulong _sectionSize;
    private static ulong _configuredPoolSize;

    // Published while a partial unmap has torn a view down and not yet remapped
    // the survivors. Read by the vectored exception handler, which must not take
    // a lock, so this is a plain atomic pair rather than anything blocking.
    private static ulong _pendingRemapStart;
    private static ulong _pendingRemapEnd;

    private static readonly bool TraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SHARED_BACKING"), "1", StringComparison.Ordinal);

    private readonly record struct View(ulong Size, ulong Offset, uint Protection);

    /// <summary>Shared backing is a Windows section API; POSIX hosts stay on private memory.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Sets the pool size before the section is created. The direct pool size is
    /// the kernel layer's number, so it is passed in rather than duplicated here.
    /// Idempotent; ignored once the section exists.
    /// </summary>
    public static void ConfigurePoolSize(ulong bytes)
    {
        lock (_gate)
        {
            if (_section is null && bytes != 0)
            {
                _configuredPoolSize = bytes;
            }
        }
    }

    /// <summary>
    /// Maps <paramref name="size"/> bytes of the pool at <paramref name="physicalOffset"/>
    /// onto <paramref name="address"/>, sharing pages with every other mapping of
    /// the same physical range. Returns false without changing anything the caller
    /// can observe when the range cannot be mapped as views, so the caller can keep
    /// its private backing.
    /// </summary>
    public static bool TryMap(ulong address, ulong size, ulong physicalOffset, uint rawProtection)
    {
        if (!IsSupported ||
            size == 0 ||
            !IsPageAligned(address) ||
            !IsPageAligned(size) ||
            !IsPageAligned(physicalOffset) ||
            ulong.MaxValue - address < size ||
            ulong.MaxValue - physicalOffset < size)
        {
            return false;
        }

        lock (_gate)
        {
            if (!TryEnsureSection() || physicalOffset + size > _sectionSize)
            {
                return false;
            }

            if (!TryReclaimToPlaceholderLocked(address, size))
            {
                return false;
            }

            return TryMapRangeLocked(address, size, physicalOffset, rawProtection);
        }
    }

    /// <summary>
    /// Removes the shared attachment over <paramref name="address"/>, keeping the
    /// address reserved for the guest. Views the range only partly covers are split:
    /// the surviving head and tail are remapped at the physical offsets they had,
    /// which is not the view's base offset for the tail. Returns whether any view
    /// was found here.
    /// </summary>
    public static bool TryUnmap(ulong address, ulong size)
    {
        if (!IsSupported || size == 0 || ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = address + size;
        var unmappedAny = false;
        lock (_gate)
        {
            var cursor = address;
            while (cursor < end)
            {
                if (!TryFindViewLocked(cursor, out var viewBase, out var view))
                {
                    cursor = AlignUp(cursor + 1, PageSize);
                    continue;
                }

                var viewEnd = viewBase + view.Size;
                unmappedAny = true;
                if (viewBase >= address && viewEnd <= end)
                {
                    UnmapViewLocked(viewBase, view);
                }
                else
                {
                    SplitViewLocked(viewBase, view, address, end);
                }

                cursor = viewEnd;
            }
        }

        return unmappedAny;
    }

    /// <summary>Whether a shared view currently covers this address.</summary>
    public static bool IsShared(ulong address)
    {
        if (!IsSupported)
        {
            return false;
        }

        lock (_gate)
        {
            return TryFindViewLocked(address, out _, out _);
        }
    }

    /// <summary>
    /// Clears a physical range the pool is about to hand out again. Released
    /// section pages keep their bytes, and they cannot be decommitted, so a
    /// recycled physical offset has to be cleared explicitly — at allocation, never
    /// at release, because at release an alias the guest still holds may be mapped
    /// over it. Only pages that are actually committed are touched, so clearing a
    /// range the guest never wrote costs nothing and charges no commit.
    /// </summary>
    public static void ZeroPhysicalRange(ulong physicalOffset, ulong length)
    {
        if (!IsSupported || length == 0 || ulong.MaxValue - physicalOffset < length)
        {
            return;
        }

        lock (_gate)
        {
            if (!TryEnsureSection() || physicalOffset + length > _sectionSize)
            {
                return;
            }

            // A view the system places for us needs an allocation-granularity
            // offset, so map from the granule below and clear only the requested
            // bytes inside it.
            var viewOffset = physicalOffset & ~(AllocationGranularity - 1);
            var viewSize = physicalOffset - viewOffset + length;
            var view = MapViewOfFile3(_section, GetCurrentProcess(), null, viewOffset, (nuint)viewSize, 0, PAGE_READWRITE, null, 0);
            if (view is null)
            {
                Trace($"zero: view for 0x{viewOffset:X}+0x{viewSize:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
                return;
            }

            var clearStart = (ulong)view + (physicalOffset - viewOffset);
            var clearEnd = clearStart + length;
            var cursor = clearStart;
            while (cursor < clearEnd)
            {
                if (VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
                {
                    break;
                }

                var regionEnd = Math.Min(clearEnd, info.BaseAddress + info.RegionSize);
                if (regionEnd <= cursor)
                {
                    break;
                }

                // Reserved section pages are zero the first time they commit;
                // only committed ones can be carrying a previous owner's bytes.
                if (info.State == MEM_COMMIT)
                {
                    NativeMemory.Clear((void*)cursor, (nuint)(regionEnd - cursor));
                }

                cursor = regionEnd;
            }

            UnmapViewOfFile2(GetCurrentProcess(), view, 0);
        }
    }

    /// <summary>
    /// Called from the vectored exception handler when a guest thread faults: if a
    /// partial unmap is remapping this address right now, wait for it and let the
    /// instruction retry. Takes no lock and allocates nothing, and the spin is
    /// bounded so a remap that never completes degrades to an ordinary fault
    /// instead of hanging the thread.
    /// </summary>
    public static bool WaitForPendingRemap(ulong faultAddress)
    {
        if (!IsSupported || !IsRemapPending(faultAddress))
        {
            return false;
        }

        for (var spin = 0; spin < 1 << 20; spin++)
        {
            if (!IsRemapPending(faultAddress))
            {
                return true;
            }

            Thread.SpinWait(1);
        }

        return false;
    }

    private static bool IsRemapPending(ulong address)
    {
        // Publish order is end-then-start and clear order is start-then-end, so a
        // start that is visible always has its end visible with it.
        var start = Volatile.Read(ref _pendingRemapStart);
        return start != 0 && address >= start && address < Volatile.Read(ref _pendingRemapEnd);
    }

    private static void SplitViewLocked(ulong viewBase, View view, ulong removeStart, ulong removeEnd)
    {
        var viewEnd = viewBase + view.Size;
        var headSize = removeStart > viewBase ? removeStart - viewBase : 0;
        var tailStart = removeEnd < viewEnd ? removeEnd : viewEnd;
        var tailSize = viewEnd - tailStart;

        Volatile.Write(ref _pendingRemapEnd, viewEnd);
        Volatile.Write(ref _pendingRemapStart, viewBase);
        try
        {
            UnmapViewLocked(viewBase, view);
            if (headSize != 0 && !TryMapRangeLocked(viewBase, headSize, view.Offset, view.Protection))
            {
                Trace($"split: head 0x{viewBase:X16}+0x{headSize:X} left unmapped");
            }

            if (tailSize != 0 &&
                !TryMapRangeLocked(tailStart, tailSize, view.Offset + (tailStart - viewBase), view.Protection))
            {
                Trace($"split: tail 0x{tailStart:X16}+0x{tailSize:X} left unmapped");
            }
        }
        finally
        {
            // Cleared even when a remap failed: a thread waiting on this range
            // must fall through to an ordinary fault rather than spin forever.
            Volatile.Write(ref _pendingRemapStart, 0);
            Volatile.Write(ref _pendingRemapEnd, 0);
        }
    }

    private static void UnmapViewLocked(ulong viewBase, View view)
    {
        if (!UnmapViewOfFile2(GetCurrentProcess(), (void*)viewBase, MEM_PRESERVE_PLACEHOLDER))
        {
            Trace($"unmap 0x{viewBase:X16}+0x{view.Size:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
        }

        _views.Remove(viewBase);
    }

    /// <summary>
    /// Maps one contiguous range as a chain of views, each fitting inside a single
    /// placeholder. Rolls every view back on failure so a partial chain is never
    /// left behind for the caller to reason about.
    /// </summary>
    private static bool TryMapRangeLocked(ulong address, ulong size, ulong physicalOffset, uint rawProtection)
    {
        var end = address + size;
        var mapped = new List<ulong>();
        var cursor = address;
        while (cursor < end)
        {
            if (VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0 ||
                info.State != MEM_RESERVE ||
                info.Type != MEM_PRIVATE)
            {
                Trace($"map: 0x{cursor:X16} is not a placeholder (state=0x{info.State:X} type=0x{info.Type:X})");
                goto Rollback;
            }

            var placeholderEnd = info.BaseAddress + info.RegionSize;
            var chunk = Math.Min(Math.Min(end - cursor, placeholderEnd - cursor), MaximumViewBytes);
            if (chunk == 0)
            {
                goto Rollback;
            }

            // MEM_REPLACE_PLACEHOLDER needs a placeholder of exactly this address
            // and size, so cut the piece out first unless it already is one.
            // VirtualQuery's BaseAddress is the queried page, not where the
            // placeholder starts; that is AllocationBase.
            if ((cursor != info.AllocationBase || cursor + chunk != placeholderEnd) &&
                !VirtualFree((void*)cursor, (nuint)chunk, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
            {
                Trace($"map: split 0x{cursor:X16}+0x{chunk:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
                goto Rollback;
            }

            var offset = physicalOffset + (cursor - address);
            var view = MapViewOfFile3(
                _section, GetCurrentProcess(), (void*)cursor, offset, (nuint)chunk,
                MEM_REPLACE_PLACEHOLDER, rawProtection, null, 0);
            if ((ulong)view != cursor)
            {
                Trace($"map: view 0x{cursor:X16}+0x{chunk:X} at 0x{offset:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
                goto Rollback;
            }

            _views[cursor] = new View(chunk, offset, rawProtection);
            mapped.Add(cursor);

            // Commit now, not on first touch. Guest code is SysV and keeps locals
            // in the 128-byte red zone below rsp; Windows dispatches a page fault
            // on that same stack and overwrites them. A lazily committed view made
            // GT7's leaf loop at 0x801B9F010 reload a null array base after its
            // first touch of a new page (race33, race36). The private backing this
            // replaces was committed at map time too, so the commit charge is no
            // larger — aliases share their pages.
            if (VirtualAlloc((void*)cursor, (nuint)chunk, MEM_COMMIT, rawProtection) is null)
            {
                Trace($"map: commit 0x{cursor:X16}+0x{chunk:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
                goto Rollback;
            }

            cursor += chunk;
        }

        Trace($"mapped 0x{address:X16}+0x{size:X} at physical 0x{physicalOffset:X} in {mapped.Count} view(s)");
        return true;

    Rollback:
        foreach (var viewBase in mapped)
        {
            UnmapViewLocked(viewBase, _views[viewBase]);
        }

        return false;
    }

    /// <summary>
    /// Turns the range into placeholders that <see cref="TryMapRangeLocked"/> can
    /// replace. Private or mapped regions that reach outside the range are refused
    /// rather than disturbed — unbacking someone else's neighbouring pages to
    /// satisfy this mapping would be worse than falling back to private memory.
    /// </summary>
    private static bool TryReclaimToPlaceholderLocked(ulong address, ulong size)
    {
        var end = address + size;
        var cursor = address;
        while (cursor < end)
        {
            if (TryFindViewLocked(cursor, out var viewBase, out var view))
            {
                var viewEnd = viewBase + view.Size;
                if (viewBase < address || viewEnd > end)
                {
                    Trace($"reclaim: view 0x{viewBase:X16}+0x{view.Size:X} crosses 0x{address:X16}+0x{size:X}");
                    return false;
                }

                UnmapViewLocked(viewBase, view);
                cursor = viewEnd;
                continue;
            }

            if (VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0)
            {
                return false;
            }

            var regionEnd = info.BaseAddress + info.RegionSize;
            if (regionEnd <= cursor)
            {
                return false;
            }

            if (info.State == MEM_FREE)
            {
                // Outside the launcher's reservation (or a hole in it): take the
                // address as a placeholder of our own so the map can proceed.
                var chunk = Math.Min(end, regionEnd) - cursor;
                if (VirtualAlloc2(null, (void*)cursor, (nuint)chunk, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0) is null)
                {
                    Trace($"reclaim: placeholder 0x{cursor:X16}+0x{chunk:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
                    return false;
                }
            }
            else if (info.State == MEM_RESERVE && info.Type == MEM_PRIVATE)
            {
                // Already a placeholder (or a plain reservation inside the guest
                // window, which is the same thing here).
            }
            else
            {
                // BaseAddress is the queried page; the allocation starts at
                // AllocationBase and may run past this query region.
                if (!TryMeasureAllocation(info.AllocationBase, out var allocationSize) ||
                    info.AllocationBase < address ||
                    info.AllocationBase + allocationSize > end)
                {
                    Trace($"reclaim: allocation at 0x{info.AllocationBase:X16} crosses 0x{address:X16}+0x{size:X}");
                    return false;
                }

                if (!VirtualFree((void*)info.AllocationBase, (nuint)allocationSize, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER) &&
                    !TryRetakeAsPlaceholder(info.AllocationBase, address, end))
                {
                    return false;
                }

                regionEnd = info.AllocationBase + allocationSize;
            }

            cursor = regionEnd;
        }

        return true;
    }

    /// <summary>
    /// Releases an ordinary allocation and takes its address straight back as a
    /// placeholder. Only placeholder-replaced memory can be freed with
    /// <c>MEM_PRESERVE_PLACEHOLDER</c>, so without this a range the emulator
    /// allocated the ordinary way — every range, when the launcher's guest-window
    /// reservation is off — could never become a shared mapping. The address is
    /// unowned in between, which is safe only because every guest allocation path
    /// runs under the mapping lock this is called with.
    /// </summary>
    private static bool TryRetakeAsPlaceholder(ulong allocationBase, ulong rangeStart, ulong rangeEnd)
    {
        if (!TryMeasureAllocation(allocationBase, out var allocationSize) ||
            allocationBase < rangeStart ||
            allocationBase + allocationSize > rangeEnd)
        {
            Trace($"reclaim: allocation at 0x{allocationBase:X16} is not contained in 0x{rangeStart:X16}-0x{rangeEnd:X16}");
            return false;
        }

        if (!VirtualFree((void*)allocationBase, 0, MEM_RELEASE))
        {
            Trace($"reclaim: release 0x{allocationBase:X16} failed (Win32 error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        if (VirtualAlloc2(null, (void*)allocationBase, (nuint)allocationSize, MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, null, 0) is null)
        {
            Trace($"reclaim: retake 0x{allocationBase:X16}+0x{allocationSize:X} failed (Win32 error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Total size of the host allocation starting at <paramref name="allocationBase"/>.
    /// One allocation shows up as several query regions when its pages differ in
    /// state or protection, so releasing it needs the sum, not the first region.
    /// </summary>
    private static bool TryMeasureAllocation(ulong allocationBase, out ulong size)
    {
        size = 0;
        var cursor = allocationBase;
        while (true)
        {
            if (VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0 ||
                info.AllocationBase != allocationBase ||
                info.RegionSize == 0)
            {
                return size != 0;
            }

            size += info.RegionSize;
            cursor = info.BaseAddress + info.RegionSize;
        }
    }

    private static bool TryFindViewLocked(ulong address, out ulong viewBase, out View view)
    {
        viewBase = 0;
        view = default;
        var keys = _views.Keys;
        var low = 0;
        var high = keys.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (keys[middle] <= address)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (found < 0)
        {
            return false;
        }

        var candidateBase = keys[found];
        var candidate = _views.Values[found];
        if (address - candidateBase >= candidate.Size)
        {
            return false;
        }

        viewBase = candidateBase;
        view = candidate;
        return true;
    }

    private static bool TryEnsureSection()
    {
        if (_section is not null)
        {
            return true;
        }

        var size = _configuredPoolSize;
        if (size == 0)
        {
            return false;
        }

        // Executable, because guest code may live in direct memory and a view can
        // never be more permissive than its section. SEC_RESERVE so the pool's
        // commit charge follows the pages the guest actually touches.
        _section = CreateFileMappingW(
            (void*)-1, null, PAGE_EXECUTE_READWRITE | SEC_RESERVE, (uint)(size >> 32), (uint)size, null);
        if (_section is null)
        {
            Trace($"section of 0x{size:X} bytes failed (Win32 error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        _sectionSize = size;
        Trace($"pool section created: 0x{size:X} bytes");
        return true;
    }

    private static bool IsPageAligned(ulong value) => (value & (PageSize - 1)) == 0;

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] shared_backing: {message}");
        }
    }

    private const ulong AllocationGranularity = 0x10000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MEM_FREE = 0x10000;
    private const uint MEM_PRIVATE = 0x20000;
    private const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
    private const uint MEM_RESERVE_PLACEHOLDER = 0x40000;
    private const uint MEM_PRESERVE_PLACEHOLDER = 0x2;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READWRITE = 0x40;
    private const uint SEC_RESERVE = 0x400_0000;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial void* CreateFileMappingW(void* file, void* attributes, uint protect, uint sizeHigh, uint sizeLow, string? name);

    [LibraryImport("kernel32.dll")]
    private static partial void* GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial void* VirtualAlloc(void* address, nuint size, uint type, uint protect);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(
        void* process, void* address, nuint size, uint type, uint protect, void* parameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* MapViewOfFile3(
        void* mapping, void* process, void* address, ulong offset, nuint size, uint type, uint protect,
        void* parameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile2(void* process, void* address, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint type);

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(void* address, out MemoryBasicInformation buffer, nuint length);

    private struct MemoryBasicInformation
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
