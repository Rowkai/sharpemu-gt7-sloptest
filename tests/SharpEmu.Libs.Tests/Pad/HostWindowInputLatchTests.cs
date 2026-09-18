// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

[Collection(HostWindowInputStateCollection.Name)]
public sealed class HostWindowInputLatchTests
{
    private const int VkReturn = 0x0D;

    // SDL drains every queued event before a frame, so a press and its release
    // can both land between two guest pad samples. The guest must still see it.
    [Fact]
    public void KeyPressedAndReleasedBetweenSamples_IsStillReportedDown()
    {
        HostWindowInput.Connect();
        try
        {
            var input = HostWindowInputSource.Current;
            Assert.NotNull(input);

            HostWindowInput.SetKey(VkReturn, down: true);
            HostWindowInput.SetKey(VkReturn, down: false);

            Assert.True(input!.IsKeyDown(VkReturn));
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }

    [Fact]
    public void AKeyStillHeld_StaysDown()
    {
        HostWindowInput.Connect();
        try
        {
            var input = HostWindowInputSource.Current;
            HostWindowInput.SetKey(VkReturn, down: true);

            Assert.True(input!.IsKeyDown(VkReturn));
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }

    [Fact]
    public void LosingFocus_DropsTheLatch()
    {
        HostWindowInput.Connect();
        try
        {
            var input = HostWindowInputSource.Current;
            HostWindowInput.SetKey(VkReturn, down: true);
            HostWindowInput.SetKey(VkReturn, down: false);
            HostWindowInput.SetFocused(false);

            Assert.False(input!.IsKeyDown(VkReturn));
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }

    [Fact]
    public void AKeyNeverPressed_IsNotDown()
    {
        HostWindowInput.Connect();
        try
        {
            var input = HostWindowInputSource.Current;

            Assert.False(input!.IsKeyDown(VkReturn));
        }
        finally
        {
            HostWindowInput.Disconnect();
        }
    }
}
