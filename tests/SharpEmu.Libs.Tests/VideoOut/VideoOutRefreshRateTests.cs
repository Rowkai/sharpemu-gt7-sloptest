// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// <c>sceVideoOutGetOutputStatus</c> reports the refresh rate as an
/// <c>SCE_VIDEO_OUT_REFRESH_RATE_*</c> ordinal, not as hertz. Titles branch on
/// the ordinal, so a raw hertz value there is not merely imprecise: it is a
/// value no hardware ever reports, and the branch falls through.
/// </summary>
public sealed class VideoOutRefreshRateTests
{
    [Theory]
    [InlineData(60u, 3u)]    // SCE_VIDEO_OUT_REFRESH_RATE_59_94HZ
    [InlineData(59u, 3u)]
    [InlineData(120u, 13u)]  // SCE_VIDEO_OUT_REFRESH_RATE_119_88HZ
    [InlineData(119u, 13u)]
    [InlineData(50u, 2u)]    // SCE_VIDEO_OUT_REFRESH_RATE_50HZ
    [InlineData(30u, 6u)]    // SCE_VIDEO_OUT_REFRESH_RATE_29_97HZ
    [InlineData(24u, 1u)]    // SCE_VIDEO_OUT_REFRESH_RATE_23_98HZ
    [InlineData(90u, 35u)]   // SCE_VIDEO_OUT_REFRESH_RATE_89_91HZ
    public void HostRefreshRate_IsReportedAsTheSceOrdinal(uint hertz, ulong expected)
    {
        Assert.Equal(expected, VideoOutExports.ToSceRefreshRate(hertz));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(144u)]
    [InlineData(75u)]
    public void RateWithNoConsoleOrdinal_IsReportedAsUnknown(uint hertz)
    {
        Assert.Equal(0u, VideoOutExports.ToSceRefreshRate(hertz));
    }

    [Fact]
    public void HertzIsNeverReportedVerbatim()
    {
        // The defect this guards: writing 60 where the ordinal for 60 Hz is 3.
        Assert.NotEqual(60u, VideoOutExports.ToSceRefreshRate(60u));
    }
}
