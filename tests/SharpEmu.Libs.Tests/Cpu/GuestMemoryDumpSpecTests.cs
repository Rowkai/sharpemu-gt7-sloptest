// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// The stall snapshot's <c>SHARPEMU_DUMP_GUEST_MEMORY</c> specs. Guest heap
/// objects land at a different address on every boot, so a register-relative
/// spec is the only one that can be set before the run that needs it.
/// </summary>
public sealed class GuestMemoryDumpSpecTests
{
    [Fact]
    public void AbsoluteAddress_ParsesWithAndWithoutPrefix()
    {
        Assert.True(DirectExecutionBackend.TryResolveDumpAddress(null, "0x8054E6278", out var prefixed));
        Assert.Equal(0x8054E6278UL, prefixed);

        Assert.True(DirectExecutionBackend.TryResolveDumpAddress(null, "8054E6278", out var bare));
        Assert.Equal(0x8054E6278UL, bare);
    }

    [Fact]
    public void AbsoluteAddress_AcceptsAnOffset()
    {
        Assert.True(DirectExecutionBackend.TryResolveDumpAddress(null, "0x8054E6278-0x8", out var below));
        Assert.Equal(0x8054E6270UL, below);

        Assert.True(DirectExecutionBackend.TryResolveDumpAddress(null, "0x8054E6278+0x2A8", out var above));
        Assert.Equal(0x8054E6520UL, above);
    }

    [Fact]
    public void RegisterRelativeSpec_NeedsALiveContext()
    {
        Assert.False(DirectExecutionBackend.TryResolveDumpAddress(null, "rbx-0x28", out _));
    }

    [Fact]
    public void DerefSpec_NeedsALiveContext()
    {
        Assert.False(DirectExecutionBackend.TryResolveDumpAddress(null, "[0x8064E5270]+0x90", out _));
    }

    [Fact]
    public void UnparsedSpec_IsRejectedRatherThanReadAsZero()
    {
        Assert.False(DirectExecutionBackend.TryResolveDumpAddress(null, "notanaddress", out _));
        Assert.False(DirectExecutionBackend.TryResolveDumpAddress(null, "[0x8064E5270", out _));
        Assert.False(DirectExecutionBackend.TryResolveDumpAddress(null, string.Empty, out _));
    }
}
