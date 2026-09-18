// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using Silk.NET.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanScaledTextureFormatTests
{
    [Theory]
    [InlineData(1u, 2u, Format.R16Sfloat, 8)]
    [InlineData(1u, 3u, Format.R16Sfloat, 8)]
    [InlineData(3u, 2u, Format.R16G16Sfloat, 8)]
    [InlineData(10u, 3u, Format.R16G16B16A16Sfloat, 8)]
    [InlineData(2u, 2u, Format.R32Sfloat, 16)]
    [InlineData(5u, 3u, Format.R32G32Sfloat, 16)]
    [InlineData(12u, 2u, Format.R32G32B32A32Sfloat, 16)]
    public void ScaledTextureFormats_WidenToAFloatFormat(
        uint format,
        uint numberType,
        Format expected,
        int expectedSourceBits)
    {
        Assert.True(VulkanVideoPresenter.TryGetScaledTextureWidening(
            format,
            numberType,
            out var hostFormat,
            out var sourceBits));
        Assert.Equal(expected, hostFormat);
        Assert.Equal(expectedSourceBits, sourceBits);
    }

    [Theory]
    [InlineData(1u, 0u)] // R8_UNORM
    [InlineData(1u, 4u)] // R8_UINT
    [InlineData(10u, 7u)] // float
    [InlineData(169u, 2u)] // BC1 has no scaled variant
    public void UnscaledFormats_AreLeftAlone(uint format, uint numberType)
    {
        Assert.False(VulkanVideoPresenter.TryGetScaledTextureWidening(
            format,
            numberType,
            out _,
            out _));
    }

    // USCALED delivers the stored integer unnormalised, so a Y plane byte of
    // 200 must still read as 200.0 after widening - not 200/255.
    [Fact]
    public void WidenScaledTexels_KeepsTheUnnormalisedValueOfAnEightBitPlane()
    {
        byte[] plane = [0, 1, 16, 128, 200, 255];

        var widened = VulkanVideoPresenter.WidenScaledTexels(1, 2, plane);

        Assert.Equal(plane.Length * 2, widened.Length);
        for (var i = 0; i < plane.Length; i++)
        {
            var value = BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(widened.AsSpan(i * 2, 2)));
            Assert.Equal((float)plane[i], (float)value);
        }
    }

    [Fact]
    public void WidenScaledTexels_SignExtendsSscaledBytes()
    {
        byte[] plane = [0xFF, 0x80, 0x7F];

        var widened = VulkanVideoPresenter.WidenScaledTexels(1, 3, plane);

        var values = new float[plane.Length];
        for (var i = 0; i < plane.Length; i++)
        {
            values[i] = (float)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(widened.AsSpan(i * 2, 2)));
        }

        Assert.Equal([-1f, -128f, 127f], values);
    }

    [Fact]
    public void WidenScaledTexels_KeepsSixteenBitValuesExactly()
    {
        var plane = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(plane.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(plane.AsSpan(2, 2), 65535);

        var widened = VulkanVideoPresenter.WidenScaledTexels(2, 2, plane);

        Assert.Equal(8, widened.Length);
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(widened.AsSpan(0, 4)));
        Assert.Equal(65535f, BinaryPrimitives.ReadSingleLittleEndian(widened.AsSpan(4, 4)));
    }

    // GT7's movie composite binds an R16G16B16A16 SNORM storage image
    // (guest 12/num 1) to a compute shader declared Rgba16Snorm. The texture
    // table had no SNORM entry for 16_16_16_16, so it fell to the RGBA8
    // default and the matching pair was rejected as a format mismatch.
    [Fact]
    public void SnormStorageImage_MatchesItsTypedShaderContract()
    {
        var contract = new VulkanVideoPresenter.SpirvStorageImageContract(
            SpirvImageFormat.Rgba16Snorm,
            VulkanVideoPresenter.StorageImageComponentKind.Float);

        Assert.True(VulkanVideoPresenter.TryValidateStorageImageContract(
            contract,
            guestFormat: 12,
            guestNumberType: 1,
            supportsStorage: true,
            out var vulkanFormat,
            out var error), error);
        Assert.Equal(Format.R16G16B16A16SNorm, vulkanFormat);
    }

    [Theory]
    [InlineData(5u, SpirvImageFormat.Rg16Snorm)]
    [InlineData(10u, SpirvImageFormat.Rgba8Snorm)]
    [InlineData(12u, SpirvImageFormat.Rgba16Snorm)]
    public void SnormTextureFormats_AreNotTheRgba8Fallback(uint guestFormat, SpirvImageFormat shaderFormat)
    {
        var contract = new VulkanVideoPresenter.SpirvStorageImageContract(
            shaderFormat,
            VulkanVideoPresenter.StorageImageComponentKind.Float);

        Assert.True(VulkanVideoPresenter.TryValidateStorageImageContract(
            contract,
            guestFormat,
            guestNumberType: 1,
            supportsStorage: true,
            out _,
            out var error), error);
    }

    [Fact]
    public void WidenScaledTexels_ReturnsUnscaledPixelsUntouched()
    {
        byte[] pixels = [1, 2, 3, 4];

        Assert.Same(pixels, VulkanVideoPresenter.WidenScaledTexels(1, 0, pixels));
    }
}
