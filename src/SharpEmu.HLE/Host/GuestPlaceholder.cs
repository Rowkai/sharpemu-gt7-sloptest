// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host;

/// <summary>
/// Claims addresses out of the launcher's guest-window reservation.
/// <para>
/// The launcher reserves the windows a PS5 title owns as Windows placeholders
/// before the emulator runs any of its own code, so a host allocation can never
/// take an address the guest may later map at a fixed address. Ordinary
/// VirtualAlloc cannot allocate into a placeholder: the piece has to be split
/// out and replaced in one step, which is also what stops another allocation
/// slipping in between.
/// </para>
/// </summary>
public static unsafe partial class GuestPlaceholder
{
    /// <summary>
    /// Replaces part of a placeholder with private memory. Fails (returning
    /// false) for anything that is not a placeholder in a guest window, so
    /// callers can fall through to their ordinary allocation path.
    /// </summary>
    public static bool TryClaim(ulong address, ulong size, uint rawProtection, bool commit, out ulong claimed)
    {
        claimed = 0;
        if (!OperatingSystem.IsWindows() ||
            address == 0 ||
            size == 0 ||
            (address & (AllocationGranularity - 1)) != 0 ||
            ulong.MaxValue - address < size)
        {
            return false;
        }

        var end = AlignUp(address + size, AllocationGranularity);
        if (!IsPlaceholder(address, out var placeholderBase, out var placeholderSize))
        {
            return false;
        }

        if (end > placeholderBase + placeholderSize)
        {
            Trace($"request 0x{address:X16}-0x{end:X16} exceeds placeholder 0x{placeholderBase:X16}+0x{placeholderSize:X}");
            return false;
        }

        // Replacing needs an exact match, so split the piece out first unless it
        // already is the whole placeholder.
        if ((address != placeholderBase || end != placeholderBase + placeholderSize) &&
            !VirtualFree((void*)address, (nuint)(end - address), MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
        {
            Trace($"split failed for 0x{address:X16}-0x{end:X16} (Win32 error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        var allocationType = MEM_RESERVE | MEM_REPLACE_PLACEHOLDER | (commit ? MEM_COMMIT : 0u);
        var replaced = VirtualAlloc2(null, (void*)address, (nuint)(end - address), allocationType, rawProtection, null, 0);
        if (replaced is null)
        {
            Trace($"replace failed for 0x{address:X16}-0x{end:X16} type=0x{allocationType:X} prot=0x{rawProtection:X} " +
                $"(Win32 error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        Trace($"claimed 0x{address:X16}-0x{end:X16} commit={commit} prot=0x{rawProtection:X}");
        claimed = (ulong)replaced;
        return true;
    }

    /// <summary>
    /// Frees an allocation inside a guest window back to a placeholder, so the
    /// address stays reserved for the guest instead of becoming free for the
    /// host to take.
    /// </summary>
    public static bool TryRestore(ulong address)
    {
        if (!OperatingSystem.IsWindows() ||
            VirtualQuery((void*)address, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0 ||
            info.State == MemFree ||
            info.Type != MemPrivate ||
            !GuestAddressWindows.Contains(info.BaseAddress, info.RegionSize))
        {
            return false;
        }

        if (!VirtualFree((void*)info.BaseAddress, (nuint)info.RegionSize, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
        {
            Trace($"restore failed for 0x{info.BaseAddress:X16}+0x{info.RegionSize:X} (Win32 error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        Trace($"restored placeholder 0x{info.BaseAddress:X16}+0x{info.RegionSize:X}");
        return true;
    }

    /// <summary>
    /// A reserved private range inside a guest window is the launcher's
    /// placeholder: nothing else reserves there, because the reservation is
    /// taken before this process runs its own code. The allocation protection is
    /// not a usable marker — a reserved region reports protection 0.
    /// </summary>
    public static bool IsPlaceholder(ulong address, out ulong placeholderBase, out ulong placeholderSize)
    {
        placeholderBase = 0;
        placeholderSize = 0;
        if (!OperatingSystem.IsWindows() ||
            VirtualQuery((void*)address, out var info, (nuint)sizeof(MemoryBasicInformation)) == 0 ||
            info.State != MEM_RESERVE ||
            info.Type != MemPrivate ||
            !GuestAddressWindows.Contains(info.BaseAddress, info.RegionSize))
        {
            return false;
        }

        // VirtualQuery's BaseAddress is the queried page, not the placeholder's
        // start; reporting it made a claim that ends at the placeholder's end look
        // exact, skip its split, and fail the replace.
        placeholderBase = info.AllocationBase;
        placeholderSize = info.BaseAddress + info.RegionSize - info.AllocationBase;
        return true;
    }

    /// <summary>
    /// Claims the window around a faulting address. The lazy-commit fault
    /// handler runs here, so this must stay allocation- and lock-free: a
    /// placeholder cannot be committed in place, it has to be replaced.
    /// </summary>
    public static bool TryClaimForFault(ulong faultAddress, uint rawProtection, out ulong claimedBase, out ulong claimedSize)
    {
        claimedBase = 0;
        claimedSize = 0;
        if (!IsPlaceholder(faultAddress, out var placeholderBase, out var placeholderSize))
        {
            return false;
        }

        var windowBase = faultAddress & ~(AllocationGranularity - 1);
        var available = placeholderBase + placeholderSize - windowBase;
        var size = (available < LazyClaimBytes ? available : LazyClaimBytes) & ~(AllocationGranularity - 1);
        if (size == 0 || !TryClaim(windowBase, size, rawProtection, commit: true, out var claimed))
        {
            return false;
        }

        claimedBase = claimed;
        claimedSize = size;
        return true;
    }

    // Read once: the fault handler must not allocate, and reading an
    // environment variable does.
    private static readonly bool TraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PLACEHOLDER"), "1", StringComparison.Ordinal);

    private static void Trace(string message)
    {
        if (TraceEnabled)
        {
            Console.Error.WriteLine($"[LOADER][TRACE] placeholder: {message}");
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);

    private const ulong AllocationGranularity = 0x10000;

    // Claim 2 MiB around a fault, matching the lazy-commit window the handler
    // already uses for ordinary reserved memory.
    private const ulong LazyClaimBytes = 0x200000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint MemFree = 0x10000;
    private const uint MemPrivate = 0x20000;
    private const uint MEM_REPLACE_PLACEHOLDER = 0x4000;
    private const uint MEM_PRESERVE_PLACEHOLDER = 0x2;

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial void* VirtualAlloc2(
        void* process, void* address, nuint size, uint allocationType, uint protection, void* parameters, uint parameterCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(void* address, nuint size, uint freeType);

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
