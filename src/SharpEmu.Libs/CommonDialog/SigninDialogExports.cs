// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.CommonDialog;

/// <summary>
/// libSceSigninDialog: the system prompt asking the user to sign in to PSN.
/// There is no PSN account to sign in to, so an opened dialog finishes at once
/// with the result a console gets when the user backs out of it. Leaving the
/// library unresolved is not equivalent: Initialize then fails with
/// ORBIS_GEN2_ERROR_NOT_FOUND, which no real console returns, and a title
/// branching on it takes a path the hardware never produces.
/// </summary>
public static class SigninDialogExports
{
    // Shared common-dialog error codes (SceCommonDialogError).
    private const int NotInitialized = unchecked((int)0x80B80003);
    private const int AlreadyInitialized = unchecked((int)0x80B80004);
    private const int NotFinished = unchecked((int)0x80B80005);
    private const int ArgNull = unchecked((int)0x80B8000D);

    // SceCommonDialogStatus.
    private const int StatusNone = 0;
    private const int StatusInitialized = 1;
    private const int StatusFinished = 3;

    // SceCommonDialogResult.
    private const int ResultUserCanceled = 1;

    private static int _initialized;
    private static int _status;

    [SysAbiExport(
        Nid = "mlYGfmqE3fQ",
        ExportName = "sceSigninDialogInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogInitialize(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return ctx.SetReturn(AlreadyInitialized);
        }

        Volatile.Write(ref _status, StatusInitialized);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "JlpJVoRWv7U",
        ExportName = "sceSigninDialogOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogOpen(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] == 0)
        {
            return ctx.SetReturn(ArgNull);
        }

        if (Volatile.Read(ref _initialized) == 0)
        {
            return ctx.SetReturn(NotInitialized);
        }

        Volatile.Write(ref _status, StatusFinished);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "2m077aeC+PA",
        ExportName = "sceSigninDialogGetStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogGetStatus(CpuContext ctx) =>
        ctx.SetReturn(Volatile.Read(ref _status));

    [SysAbiExport(
        Nid = "Bw31liTFT3A",
        ExportName = "sceSigninDialogUpdateStatus",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogUpdateStatus(CpuContext ctx) =>
        ctx.SetReturn(Volatile.Read(ref _status));

    // Writes only the leading result field; the rest of the structure is the
    // caller's and its layout is not established here.
    [SysAbiExport(
        Nid = "nqG7rqnYw1U",
        ExportName = "sceSigninDialogGetResult",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogGetResult(CpuContext ctx)
    {
        var resultAddress = ctx[CpuRegister.Rdi];
        if (resultAddress == 0)
        {
            return ctx.SetReturn(ArgNull);
        }

        if (Volatile.Read(ref _status) != StatusFinished)
        {
            return ctx.SetReturn(NotFinished);
        }

        Span<byte> result = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(result, ResultUserCanceled);
        return ctx.Memory.TryWrite(resultAddress, result)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "M3OkENHcyiU",
        ExportName = "sceSigninDialogClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogClose(CpuContext ctx)
    {
        Volatile.Write(ref _status, StatusFinished);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "LXlmS6PvJdU",
        ExportName = "sceSigninDialogTerminate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceSigninDialog")]
    public static int SigninDialogTerminate(CpuContext ctx)
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return ctx.SetReturn(NotInitialized);
        }

        Volatile.Write(ref _status, StatusNone);
        return ctx.SetReturn(0);
    }
}
