// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// V_XAD_U32 (VOP3 opcode 0x345 on GFX10, LLVM VOP3_Real_gfx10<0x345>, sitting
// directly below V_LSHL_ADD_U32 0x346 and V_ADD_LSHL_U32 0x347 which this tree
// already decoded): D = (S0 ^ S1) + S2. Tiling and swizzle address math reaches
// for it, and GT7 ships a compute kernel that uses it between V_ADD_LSHL_U32 and
// V_OR3_B32. The decoder had no name for it, so the shader failed emission with
// "unsupported vector opcode" and the whole dispatch was dropped.
public sealed class Gen5XadSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    // VOP3: [31:26]=0b110101, [25:16]=op, [7:0]=vdst; second dword carries
    // src0 [8:0], src1 [17:9], src2 [26:18] (>= 256 selects a VGPR).
    private const uint Vop3 = 0xD400_0000u;
    private const uint XadOpcode = 0x345;

    private const uint EndPgm = 0xBF81_0000u;

    private static uint Vgpr(uint index) => 256u + index;

    [Fact]
    public void XadU32_LowersToXorThenAdd()
    {
        // v_xad_u32 v4, v0, v1, v2  ->  v4 = (v0 ^ v1) + v2
        var spirv = Compile(
        [
            Vop3 | (XadOpcode << 16) | 4u,
            Vgpr(0) | (Vgpr(1) << 9) | (Vgpr(2) << 18),
            EndPgm,
        ]);

        Assert.True(
            HasAddOfXor(spirv),
            "V_XAD_U32 must add its third source to the xor of the first two");
    }

    /// <summary>
    /// The failure this replaces was a dropped shader, not a wrong result, so
    /// the plain "it translates at all" assertion is worth keeping separately.
    /// </summary>
    [Fact]
    public void XadU32_DoesNotDropTheShader()
    {
        Assert.True(
            TryCompile(
                [
                    Vop3 | (XadOpcode << 16) | 4u,
                    Vgpr(0) | (Vgpr(1) << 9) | (Vgpr(2) << 18),
                    EndPgm,
                ],
                out _,
                out var error),
            error);
    }

    // True when some OpIAdd consumes the result of an OpBitwiseXor.
    private static bool HasAddOfXor(byte[] spirv)
    {
        const ushort OpIAdd = 128;
        const ushort OpBitwiseXor = 198;

        var xorResults = new HashSet<uint>();
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (op == OpBitwiseXor && wordCount >= 5)
            {
                xorResults.Add(ReadWord(spirv, offset + 8));
            }
        }

        Assert.NotEmpty(xorResults);

        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            if (op != OpIAdd || wordCount < 5)
            {
                continue;
            }

            if (xorResults.Contains(ReadWord(spirv, offset + 12)) ||
                xorResults.Contains(ReadWord(spirv, offset + 16)))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Op, int WordCount, int Offset)> EnumerateInstructions(
        byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = ReadWord(spirv, offset);
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0)
            {
                yield break;
            }

            yield return ((ushort)word, wordCount, offset);
            offset += wordCount * sizeof(uint);
        }
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));

    private static byte[] Compile(uint[] programWords)
    {
        Assert.True(TryCompile(programWords, out var spirv, out var error), error);
        return spirv;
    }

    private static bool TryCompile(uint[] programWords, out byte[] spirv, out string error)
    {
        spirv = [];
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };

        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out error) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error) ||
            !Gen5SpirvTranslator.TryCompileComputeShader(
                state, evaluation, 1, 1, 1, out var shader, out error))
        {
            return false;
        }

        spirv = shader.Spirv;
        return true;
    }
}
