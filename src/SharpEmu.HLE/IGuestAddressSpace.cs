// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE;

/// <summary>
/// Guest address-space manipulation beyond plain allocation: fixed-address
/// mapping and page-protection changes. Guest addresses are identity-mapped
/// onto host pages by the implementing memory, so HLE exports (mmap, mprotect)
/// reach these operations through <c>ctx.Memory</c> instead of calling host
/// APIs directly. Member signatures deliberately mirror the implementation in
/// SharpEmu.Core so existing call sites migrate call-for-call.
/// </summary>
public interface IGuestAddressSpace : IGuestMemoryAllocator
{
    ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true);

    /// <summary>
    /// Backs an entire fixed-address range, matching the guest's
    /// <c>SCE_KERNEL_MAP_FIXED</c> contract. Unlike <see cref="AllocateAt"/>, which
    /// reserves the range in one all-or-nothing host call, this walks the range and
    /// fills only the sub-ranges that are not already backed. That keeps a fixed
    /// mapping whole when part of the requested window is already occupied — the
    /// partial-overlap case where the single-call reservation fails outright and
    /// leaves the remainder unmapped for the guest to fault into.
    /// </summary>
    bool TryBackFixedRange(ulong address, ulong size, bool executable);

    bool TryAllocateAtOrAbove(ulong desiredAddress, ulong size, bool executable, ulong alignment, out ulong actualAddress);

    bool TryProtect(ulong address, ulong size, GuestPageProtection protection);

    /// <summary>
    /// Attaches a guest range to a physical offset of the direct-memory pool so
    /// every mapping of that physical range is the same pages, which is what a
    /// direct mapping is on hardware. Returns false when the host cannot back the
    /// range that way, leaving whatever backing the caller already had — a caller
    /// must never report that as a shared mapping.
    /// </summary>
    bool TryMapSharedDirect(ulong address, ulong size, ulong physicalOffset, bool executable) => false;

    /// <summary>
    /// Removes a shared attachment, keeping the addresses reserved for the guest.
    /// Returns false when the range holds no shared mapping.
    /// </summary>
    bool TryUnmapShared(ulong address, ulong size) => false;
}
