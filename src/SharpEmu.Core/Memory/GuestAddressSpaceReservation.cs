// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpEmu.Core.Memory;

/// <summary>
/// Keeps host allocations out of the address space a PS5 title owns.
/// <para>
/// On hardware the guest has its user address space to itself and maps into it
/// at addresses of its own choosing (GT7 maps <c>MAP_FIXED</c> from
/// <c>0x12_0000_0000</c> upwards). Under Windows the emulator's own thread
/// stacks and runtime heaps are placed by ASLR anywhere in the first terabyte,
/// so a host allocation can already occupy an address the guest later demands —
/// that is what made GT7's 5 GiB streaming-arena mapping fail and deadlock the
/// race load.
/// </para>
/// <para>
/// The launcher therefore creates the emulator child suspended and reserves the
/// guest window in it as placeholders (<c>MEM_RESERVE_PLACEHOLDER</c>) before
/// any of the child's own code runs, so later host allocations must go
/// elsewhere while guest mappings can still replace pieces of the reservation.
/// Whatever the kernel already placed in the window at process creation — the
/// initial thread's stack above all — stays as a hole the allocator has to
/// avoid.
/// </para>
/// </summary>
public static unsafe partial class GuestAddressSpaceReservation
{
    /// <summary>Set to 1 to reserve the guest window in the child process.</summary>
    public const string EnableVariable = "SHARPEMU_RESERVE_GUEST_VA";

    /// <summary>Carries the promised windows from launcher to child.</summary>
    public const string HandoffVariable = "SHARPEMU_GUEST_VA_RESERVATION";

    /// <summary>
    /// The guest-owned window: PS5 flexible memory and the main image sit from
    /// <c>0x6_0000_0000</c>, user space runs to <c>0x100_0000_0000</c>. Guest
    /// mappings below this (SharpEmu picks low addresses for some direct maps)
    /// are not covered yet — moving those is a separate change.
    /// </summary>
    public static (ulong Start, ulong End)[] GuestWindows => SharpEmu.HLE.Host.GuestAddressWindows.All;

    public static bool IsEnabled =>
        OperatingSystem.IsWindows() &&
        string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal);

    /// <summary>The windows the launcher promised, and what it could not reserve.</summary>
    public sealed record Coverage(ulong ReservedBytes, (ulong Start, ulong End)[] Holes);

    public static string DescribeWindows() =>
        string.Join(',', GuestWindows.Select(window => $"{window.Start:x}-{window.End:x}"));

    /// <summary>
    /// Reserves the guest windows inside <paramref name="processHandle"/>, which
    /// must belong to a process created suspended. Every free run in a window
    /// becomes a placeholder; pre-existing allocations are left alone.
    /// </summary>
    public static bool TryReserveInProcess(nint processHandle, out ulong reservedBytes, out string? failure)
    {
        reservedBytes = 0;
        failure = null;
        if (!OperatingSystem.IsWindows())
        {
            failure = "guest address-space reservation is a Windows feature";
            return false;
        }

        foreach (var (start, end) in GuestWindows)
        {
            for (var cursor = start; cursor < end;)
            {
                if (VirtualQueryEx((void*)processHandle, (void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation64)) == 0)
                {
                    failure = $"VirtualQueryEx failed at 0x{cursor:X16} (Win32 error {Marshal.GetLastPInvokeError()})";
                    return false;
                }

                var regionEnd = info.RegionSize > ulong.MaxValue - info.BaseAddress
                    ? ulong.MaxValue
                    : info.BaseAddress + info.RegionSize;
                if (regionEnd <= cursor)
                {
                    failure = $"VirtualQueryEx made no progress at 0x{cursor:X16}";
                    return false;
                }

                if (info.State == MemFree)
                {
                    // Placeholders have to start and end on allocation
                    // granularity even though views inside them may not.
                    var reserveStart = AlignUp(Math.Max(cursor, info.BaseAddress), AllocationGranularity);
                    var reserveEnd = AlignDown(Math.Min(end, regionEnd), AllocationGranularity);
                    if (reserveEnd > reserveStart)
                    {
                        if (VirtualAlloc2(
                                (void*)processHandle,
                                (void*)reserveStart,
                                (nuint)(reserveEnd - reserveStart),
                                MemReserve | MemReservePlaceholder,
                                PageNoAccess,
                                null,
                                0) is null)
                        {
                            failure = $"VirtualAlloc2 failed for 0x{reserveStart:X16}-0x{reserveEnd:X16} (Win32 error {Marshal.GetLastPInvokeError()})";
                            return false;
                        }

                        reservedBytes += reserveEnd - reserveStart;
                    }
                }

                cursor = regionEnd;
            }
        }

        return reservedBytes != 0;
    }

    /// <summary>
    /// Checks in the child that the promised windows really are reserved, and
    /// reports what is not ours. A launch that lost its reservation must not
    /// continue silently: the guest would map over host memory again.
    /// </summary>
    public static bool TryVerify(string handoff, out Coverage coverage, out string? failure)
    {
        coverage = new Coverage(0, []);
        failure = null;
        if (!OperatingSystem.IsWindows())
        {
            failure = "guest address-space reservation is a Windows feature";
            return false;
        }

        ulong reserved = 0;
        var holes = new List<(ulong Start, ulong End)>();
        foreach (var window in handoff.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = window.Split('-');
            if (bounds.Length != 2 ||
                !ulong.TryParse(bounds[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start) ||
                !ulong.TryParse(bounds[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end) ||
                end <= start)
            {
                failure = $"malformed reservation handoff '{window}'";
                return false;
            }

            for (var cursor = start; cursor < end;)
            {
                if (VirtualQuery((void*)cursor, out var info, (nuint)sizeof(MemoryBasicInformation64)) == 0)
                {
                    failure = $"VirtualQuery failed at 0x{cursor:X16} (Win32 error {Marshal.GetLastPInvokeError()})";
                    return false;
                }

                var regionEnd = info.RegionSize > ulong.MaxValue - info.BaseAddress
                    ? ulong.MaxValue
                    : info.BaseAddress + info.RegionSize;
                if (regionEnd <= cursor)
                {
                    failure = $"VirtualQuery made no progress at 0x{cursor:X16}";
                    return false;
                }

                var pieceEnd = Math.Min(end, regionEnd);
                if (info.State == MemReserve && info.AllocationProtect == PageNoAccess)
                {
                    reserved += pieceEnd - cursor;
                }
                else
                {
                    // Anything else in the window belongs to the host: the
                    // initial thread's stack, or a free run the launcher could
                    // not take. Both are addresses the guest cannot be given.
                    holes.Add((cursor, pieceEnd));
                }

                cursor = pieceEnd;
            }
        }

        coverage = new Coverage(reserved, [.. holes]);
        if (reserved == 0)
        {
            failure = "no part of the guest window is reserved";
            return false;
        }

        return true;
    }

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);

    private const ulong AllocationGranularity = 0x10000;
    private const uint MemReserve = 0x2000;
    private const uint MemFree = 0x10000;
    private const uint MemReservePlaceholder = 0x40000;
    private const uint PageNoAccess = 0x01;

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(
        void* process, void* address, nuint size, uint allocationType, uint protection, void* parameters, uint parameterCount);

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQueryEx(void* process, void* address, out MemoryBasicInformation64 buffer, nuint length);

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
