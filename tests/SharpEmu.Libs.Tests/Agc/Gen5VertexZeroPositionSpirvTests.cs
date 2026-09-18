// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5VertexZeroPositionSpirvTests
{
    [Fact]
    public void AnAllZeroPositionExportIsCulledThroughAClipDistance()
    {
        // The PS5 drops a vertex exported as (0,0,0,0) before the perspective
        // divide. Without a clip distance Vulkan divides by zero and rasterises
        // the resulting NaN, which is how a dead vertex reaches the screen.
        var module = CompileVertexShader();

        var builtIn = Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Decorate &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[1] == (uint)SpirvDecoration.BuiltIn &&
                candidate.Operands[2] == (uint)SpirvBuiltIn.ClipDistance);

        Assert.Contains(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Variable &&
                candidate.Operands.Length >= 3 &&
                candidate.Operands[1] == builtIn.Operands[0] &&
                candidate.Operands[2] == (uint)SpirvStorageClass.Output);

        var all = Assert.Single(module, candidate => candidate.Opcode == SpirvOp.All);
        var select = Assert.Single(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Select &&
                candidate.Operands.Length >= 5 &&
                candidate.Operands[2] == all.Operands[1]);

        Assert.Equal(-1f, ConstantFloat(module, select.Operands[3]));
        Assert.Equal(0f, ConstantFloat(module, select.Operands[4]));

        Assert.Contains(
            module,
            candidate =>
                candidate.Opcode == SpirvOp.Capability &&
                candidate.Operands[0] == (uint)SpirvCapability.ClipDistance);
    }

    private static float ConstantFloat(IReadOnlyList<ParsedInstruction> module, uint id) =>
        BitConverter.UInt32BitsToSingle(
            Assert.Single(
                module,
                candidate =>
                    candidate.Opcode == SpirvOp.Constant &&
                    candidate.Operands.Length >= 3 &&
                    candidate.Operands[1] == id).Operands[2]);

    private static IReadOnlyList<ParsedInstruction> CompileVertexShader()
    {
        var export = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Exp,
            "Exp",
            [],
            [
                Gen5Operand.Vector(0),
                Gen5Operand.Vector(1),
                Gen5Operand.Vector(2),
                Gen5Operand.Vector(3),
            ],
            [],
            new Gen5ExportControl(12, 0xF, Compressed: false, Done: true, ValidMask: false));
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(0, [export, end]),
            [],
            null);
        var registers = new uint[256];
        var evaluation = new Gen5ShaderEvaluation(registers, registers, [], []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileVertexShader(
                state,
                evaluation,
                out var shader,
                out var error),
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
