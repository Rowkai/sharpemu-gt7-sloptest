// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// The address ranges a PS5 title owns. A guest maps into these at addresses of
/// its own choosing, so the launcher reserves them in the emulator process
/// before anything else can be placed there, and the host-memory backends treat
/// addresses inside them as belonging to the reservation.
/// </summary>
public static class GuestAddressWindows
{
    /// <summary>
    /// PS5 flexible memory and the main image start at <c>0x6_0000_0000</c>;
    /// user space runs to <c>0x100_0000_0000</c>. Guest mappings SharpEmu still
    /// places below this are not covered yet.
    /// </summary>
    public static readonly (ulong Start, ulong End)[] All =
    [
        (0x6_0000_0000UL, 0x100_0000_0000UL),
    ];

    public static bool Contains(ulong address, ulong size)
    {
        foreach (var (start, end) in All)
        {
            if (address >= start && address < end && size <= end - address)
            {
                return true;
            }
        }

        return false;
    }
}
