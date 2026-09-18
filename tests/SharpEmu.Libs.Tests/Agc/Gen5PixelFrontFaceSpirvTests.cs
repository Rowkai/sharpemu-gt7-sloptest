// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5PixelFrontFaceSpirvTests
{
    private const uint FrontFaceMask = 1u << 12;
    private const uint FrontFacingBits = 0x3F80_0000u;
    private const uint BackFacingBits = 0xBF80_0000u;

    [Fact]
    public void FrontFaceRegisterHoldsFloatOneBitsNotABooleanFlag()
    {
        var module = CompilePixelShader(FrontFaceMask, FrontFaceMask);

        var builtIn = Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Decorate &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                candidate.Operands[2] == (uint)SpirvBuiltIn.FrontFacing);

        var facing = Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Load &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[2] == builtIn.Operands[0]);

        // Hardware hands the shader +1.0f / -1.0f, so a select feeding the
        // register with 1 and 0 would make every facing comparison wrong.
        var select = Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Select &&
                candidate.Operands.Length >= 5 &&
                candidate.Operands[2] == facing.Operands[1]);

        Assert.Equal(FrontFacingBits, ConstantValue(module, select.Operands[3]));
        Assert.Equal(BackFacingBits, ConstantValue(module, select.Operands[4]));
    }

    [Fact]
    public void FrontFaceBuiltInIsAbsentWhenTheGuestDoesNotEnableIt()
    {
        var module = CompilePixelShader(0, 0);

        Assert.DoesNotContain(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Decorate &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                candidate.Operands[2] == (uint)SpirvBuiltIn.FrontFacing);
    }

    private static uint ConstantValue(IReadOnlyList<ParsedInstruction> module, uint id) =>
        Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Constant &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[1] == id).Operands[2];

    private static IReadOnlyList<ParsedInstruction> CompilePixelShader(
        uint pixelInputEnable,
        uint pixelInputAddress)
    {
        var end = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [end]),
            [],
            null);
        var registers = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(registers, registers, [], []);

        Assert.True(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out var shader,
                out var error,
                pixelInputEnable: pixelInputEnable,
                pixelInputAddress: pixelInputAddress),
            error);

        return ParseModule(shader.Spirv);
    }

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5; offset < spirv.Length / sizeof(uint);)
        {
            var header = BitConverter.ToUInt32(spirv, offset * sizeof(uint));
            var wordCount = (int)(header >> 16);
            Assert.True(wordCount > 0);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BitConverter.ToUInt32(
                    spirv,
                    (offset + index + 1) * sizeof(uint));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount;
        }

        return instructions;
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
