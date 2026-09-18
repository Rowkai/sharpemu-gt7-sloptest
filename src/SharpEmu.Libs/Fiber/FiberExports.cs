// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Fiber;

public static class FiberExports
{
    private const int MaxNameLength = 31;
    private const int FiberInfoSize = 128;
    private const int FiberContextMinimumSize = 512;
    private const uint FiberSignature0 = 0xDEF1649C;
    private const uint FiberSignature1 = 0xB37592A0;
    private const uint FiberOptSignature = 0xBB40E64D;
    private const ulong FiberStackSignature = 0x7149F2CA7149F2CAUL;
    private const ulong FiberStackSizeCheck = 0xDEADBEEFDEADBEEFUL;
    private const uint FiberStateRun = 1;
    private const uint FiberStateIdle = 2;
    private const uint FiberStateTerminated = 3;
    private const uint FiberFlagContextSizeCheck = 0x10;
    private const uint FiberFlagSetFpuRegs = 0x100;
    private const uint Firmware350BuildVersion = 0x03500000;
    private const ushort InitialFpuControlWord = 0x037F;
    private const uint InitialMxcsr = 0x9FC0;

    private const int FiberErrorNull = unchecked((int)0x80590001);
    private const int FiberErrorAlignment = unchecked((int)0x80590002);
    private const int FiberErrorRange = unchecked((int)0x80590003);
    private const int FiberErrorInvalid = unchecked((int)0x80590004);
    private const int FiberErrorPermission = unchecked((int)0x80590005);
    private const int FiberErrorState = unchecked((int)0x80590006);

    private const int FiberMagicStartOffset = 0;
    private const int FiberStateOffset = 4;
    private const int FiberEntryOffset = 8;
    private const int FiberArgOnInitializeOffset = 16;
    private const int FiberContextAddressOffset = 24;
    private const int FiberContextSizeOffset = 32;
    private const int FiberNameOffset = 40;
    private const int FiberContextPointerOffset = 72;
    private const int FiberFlagsOffset = 80;
    private const int FiberContextStartOffset = 88;
    private const int FiberContextEndOffset = 96;
    private const int FiberMagicEndOffset = 104;

    private static int _contextSizeCheck;

    private static readonly object _fiberGate = new();
    private static readonly ConcurrentDictionary<ulong, FiberContinuation> _continuations = new();
    private static readonly ConcurrentDictionary<ulong, FiberStackRange> _stackRanges = new();
    private static readonly ConcurrentDictionary<ulong, FiberThreadState> _threadStates = new();

    // SHARPEMU_LOG_FIBER=1 traces every transition and checks sceFiberGetSelf;
    // =getself only checks sceFiberGetSelf, which keeps a fiber-heavy title fast
    // enough to reach the code under investigation. Read once: the transition
    // path runs per fiber switch.
    private static readonly string? _fiberTraceMode = Environment.GetEnvironmentVariable("SHARPEMU_LOG_FIBER");
    private static readonly bool _traceFiberTransitions = string.Equals(_fiberTraceMode, "1", StringComparison.Ordinal);
    private static readonly bool _checkFiberGetSelf =
        _traceFiberTransitions || string.Equals(_fiberTraceMode, "getself", StringComparison.Ordinal);

    // The fiber each guest thread last entered, recorded only at the transitions
    // themselves. It is deliberately not derived from _threadStates or the
    // per-host-thread current fiber, so sceFiberGetSelf can be checked against it
    // rather than against its own inputs.
    private static readonly ConcurrentDictionary<ulong, FiberEntryRecord> _enteredFibers = new();
    private static readonly ConcurrentDictionary<ulong, long> _fiberEntryCounts = new();
    private static long _fiberEntries;
    private static long _getSelfOutsideFiber;
    private static long _getSelfMismatches;
    private static long _getSelfWrongFiber;

    static FiberExports()
    {
        RunFiberSelfChecks();
    }

    public static void ResetRuntimeState()
    {
        lock (_fiberGate)
        {
            _continuations.Clear();
            _stackRanges.Clear();
            _threadStates.Clear();
            _enteredFibers.Clear();
            _fiberEntryCounts.Clear();
            Volatile.Write(ref _contextSizeCheck, 0);
        }
    }

    [SysAbiExport(
        Nid = "hVYD7Ou2pCQ",
        ExportName = "_sceFiberInitializeImpl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberInitialize(CpuContext ctx)
    {
        var optParam = ReadStackArg64(ctx, 0);
        var buildVersion = unchecked((uint)ReadStackArg64(ctx, 1));
        return FiberInitializeCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9],
            optParam,
            flags: 0,
            buildVersion);
    }

    [SysAbiExport(
        Nid = "7+OJIpko9RY",
        ExportName = "_sceFiberInitializeWithInternalOptionImpl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberInitializeWithInternalOption(CpuContext ctx)
    {
        var optParam = ReadStackArg64(ctx, 0);
        var flags = unchecked((uint)ReadStackArg64(ctx, 1));
        var buildVersion = unchecked((uint)ReadStackArg64(ctx, 2));
        return FiberInitializeCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9],
            optParam,
            flags,
            buildVersion);
    }

    [SysAbiExport(
        Nid = "asjUJJ+aa8s",
        ExportName = "sceFiberOptParamInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberOptParamInitialize(CpuContext ctx)
    {
        var optParam = ctx[CpuRegister.Rdi];
        if (optParam == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if ((optParam & 7) != 0)
        {
            return SetReturn(ctx, FiberErrorAlignment);
        }

        return TryWriteUInt32(ctx, optParam, FiberOptSignature)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    [SysAbiExport(
        Nid = "JeNX5F-NzQU",
        ExportName = "sceFiberFinalize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberFinalize(CpuContext ctx)
    {
        var fiber = ctx[CpuRegister.Rdi];
        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (!TryReadUInt32(ctx, fiber + FiberStateOffset, out var state))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (state != FiberStateIdle)
        {
            return SetReturn(ctx, FiberErrorState);
        }

        _continuations.TryRemove(fiber, out _);
        _stackRanges.TryRemove(fiber, out _);
        _ = TryWriteUInt32(ctx, fiber + FiberStateOffset, FiberStateTerminated);
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "a0LLrZWac0M",
        ExportName = "sceFiberRun",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberRun(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            attachContextAddress: 0,
            attachContextSize: 0,
            reason: "sceFiberRun",
            isSwitch: false);
    }

    [SysAbiExport(
        Nid = "avfGJ94g36Q",
        ExportName = "_sceFiberAttachContextAndRun",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberAttachContextAndRun(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            attachContextAddress: ctx[CpuRegister.Rsi],
            attachContextSize: ctx[CpuRegister.Rdx],
            reason: "_sceFiberAttachContextAndRun",
            isSwitch: false);
    }

    [SysAbiExport(
        Nid = "PFT2S-tJ7Uk",
        ExportName = "sceFiberSwitch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberSwitch(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            attachContextAddress: 0,
            attachContextSize: 0,
            reason: "sceFiberSwitch",
            isSwitch: true);
    }

    [SysAbiExport(
        Nid = "ZqhZFuzKT6U",
        ExportName = "_sceFiberAttachContextAndSwitch",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberAttachContextAndSwitch(CpuContext ctx)
    {
        return FiberRunCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.R8],
            attachContextAddress: ctx[CpuRegister.Rsi],
            attachContextSize: ctx[CpuRegister.Rdx],
            reason: "_sceFiberAttachContextAndSwitch",
            isSwitch: true);
    }

    [SysAbiExport(
        Nid = "B0ZX2hx9DMw",
        ExportName = "sceFiberReturnToThread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberReturnToThread(CpuContext ctx)
    {
        var fiberAddress = ResolveCurrentFiberAddress(ctx);
        if (fiberAddress == 0)
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        if (GuestThreadExecution.Scheduler is not { SupportsGuestContextTransfer: true } ||
            !GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        var returnArgument = ctx[CpuRegister.Rdi];
        var argOnRunAddress = ctx[CpuRegister.Rsi];
        GuestCpuContinuation transferTarget;
        lock (_fiberGate)
        {
            _continuations[fiberAddress] = new FiberContinuation(
                CaptureContinuation(ctx, frame.ReturnRip, frame.ResumeRsp, frame.ReturnSlotAddress),
                argOnRunAddress);

            var threadKey = GetThreadKey(ctx);
            if (!_threadStates.TryGetValue(threadKey, out var threadState) ||
                !TryWriteResumeArgument(ctx, threadState.RootContinuation, returnArgument))
            {
                _continuations.TryRemove(fiberAddress, out _);
                return SetReturn(ctx, FiberErrorPermission);
            }

            if (!TryWriteUInt32(ctx, fiberAddress + FiberStateOffset, FiberStateIdle))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }

            transferTarget = threadState.RootContinuation.Context with { Rax = 0 };
            _threadStates.TryRemove(threadKey, out _);
        }

        _ = GuestThreadExecution.EnterFiber(0);
        NoteFiberEntered(ctx, 0);
        GuestThreadExecution.RequestCurrentContextTransfer(transferTarget);
        TraceFiber(
            $"return-to-thread fiber=0x{fiberAddress:X16} " +
            $"resume=0x{transferTarget.Rip:X16} rsp=0x{transferTarget.Rsp:X16} arg=0x{returnArgument:X16}");
        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "p+zLIOg27zU",
        ExportName = "sceFiberGetSelf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberGetSelf(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        var fiberAddress = ResolveCurrentFiberAddress(ctx);
        if (_checkFiberGetSelf)
        {
            CheckGetSelfAgainstTransitions(ctx, fiberAddress);
        }

        if (fiberAddress == 0)
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        return TryWriteUInt64(ctx, outAddress, fiberAddress)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    [SysAbiExport(
        Nid = "uq2Y5BFz0PE",
        ExportName = "sceFiberGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberGetInfo(CpuContext ctx)
    {
        var fiber = ctx[CpuRegister.Rdi];
        var info = ctx[CpuRegister.Rsi];
        if (info == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (!TryReadUInt64(ctx, info, out var size) || size != FiberInfoSize)
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (!TryReadFiberFields(ctx, fiber, out var fields))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (!TryWriteUInt64(ctx, info + 8, fields.Entry) ||
            !TryWriteUInt64(ctx, info + 16, fields.ArgOnInitialize) ||
            !TryWriteUInt64(ctx, info + 24, fields.ContextAddress) ||
            !TryWriteUInt64(ctx, info + 32, fields.ContextSize) ||
            !TryWriteName(ctx, info + 40, fields.Name) ||
            !TryWriteUInt64(ctx, info + 72, ulong.MaxValue))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "JzyT91ucGDc",
        ExportName = "sceFiberRename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberRename(CpuContext ctx)
    {
        var fiber = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (nameAddress == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxNameLength + 1, out var name))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        return TryWriteName(ctx, fiber + FiberNameOffset, name)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    [SysAbiExport(
        Nid = "Lcqty+QNWFc",
        ExportName = "sceFiberStartContextSizeCheck",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberStartContextSizeCheck(CpuContext ctx)
    {
        if (ctx[CpuRegister.Rdi] != 0)
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        return Interlocked.Exchange(ref _contextSizeCheck, 1) == 0
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorState);
    }

    [SysAbiExport(
        Nid = "Kj4nXMpnM8Y",
        ExportName = "sceFiberStopContextSizeCheck",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberStopContextSizeCheck(CpuContext ctx)
    {
        return Interlocked.Exchange(ref _contextSizeCheck, 0) == 1
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorState);
    }

    [SysAbiExport(
        Nid = "0dy4JtMUcMQ",
        ExportName = "_sceFiberGetThreadFramePointerAddress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceFiber")]
    public static int FiberGetThreadFramePointerAddress(CpuContext ctx)
    {
        var outAddress = ctx[CpuRegister.Rdi];
        if (outAddress == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if (ResolveCurrentFiberAddress(ctx) == 0 ||
            !_threadStates.TryGetValue(GetThreadKey(ctx), out var threadState))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        return TryWriteUInt64(ctx, outAddress, threadState.RootContinuation.Context.Rbp)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, FiberErrorInvalid);
    }

    private static int FiberInitializeCore(
        CpuContext ctx,
        ulong fiber,
        ulong nameAddress,
        ulong entry,
        ulong argOnInitialize,
        ulong contextAddress,
        ulong contextSize,
        ulong optParam,
        uint flags,
        uint buildVersion)
    {
        if (fiber == 0 || nameAddress == 0 || entry == 0)
        {
            return SetReturn(ctx, FiberErrorNull);
        }

        if ((fiber & 7) != 0 ||
            (contextAddress & 15) != 0 ||
            (optParam & 7) != 0)
        {
            return SetReturn(ctx, FiberErrorAlignment);
        }

        if (contextSize != 0 && contextSize < FiberContextMinimumSize)
        {
            return SetReturn(ctx, FiberErrorRange);
        }

        if ((contextSize & 15) != 0 ||
            (contextAddress == 0 && contextSize != 0) ||
            (contextAddress != 0 && contextSize == 0))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (optParam != 0 &&
            (!TryReadUInt32(ctx, optParam, out var optMagic) || optMagic != FiberOptSignature))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxNameLength + 1, out var name))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        flags = ApplyInitializationFlags(
            flags,
            buildVersion,
            Volatile.Read(ref _contextSizeCheck) != 0);

        if (!TryWriteUInt32(ctx, fiber + FiberMagicStartOffset, FiberSignature0) ||
            !TryWriteUInt32(ctx, fiber + FiberStateOffset, FiberStateIdle) ||
            !TryWriteUInt64(ctx, fiber + FiberEntryOffset, entry) ||
            !TryWriteUInt64(ctx, fiber + FiberArgOnInitializeOffset, argOnInitialize) ||
            !TryWriteUInt64(ctx, fiber + FiberContextAddressOffset, contextAddress) ||
            !TryWriteUInt64(ctx, fiber + FiberContextSizeOffset, contextSize) ||
            !TryWriteName(ctx, fiber + FiberNameOffset, name) ||
            !TryWriteUInt64(ctx, fiber + FiberContextPointerOffset, 0) ||
            !TryWriteUInt32(ctx, fiber + FiberFlagsOffset, flags) ||
            !TryWriteUInt64(ctx, fiber + FiberContextStartOffset, contextAddress) ||
            !TryWriteUInt64(ctx, fiber + FiberContextEndOffset, contextAddress == 0 ? 0 : contextAddress + contextSize) ||
            !TryWriteUInt32(ctx, fiber + FiberMagicEndOffset, FiberSignature1))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (contextAddress != 0)
        {
            if (!TryWriteUInt64(ctx, contextAddress, FiberStackSignature))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }

            if ((flags & FiberFlagContextSizeCheck) != 0)
            {
                FillContextSizeCheck(ctx, contextAddress + sizeof(ulong), contextSize - sizeof(ulong));
            }
        }

        if (contextAddress != 0 && contextSize != 0)
        {
            _stackRanges[fiber] = new FiberStackRange(contextAddress, contextSize);
        }

        TraceFiber(
            $"init fiber=0x{fiber:X16} entry=0x{entry:X16} ctx=0x{contextAddress:X16} " +
            $"size=0x{contextSize:X} flags=0x{flags:X} build=0x{buildVersion:X8} name='{name}'");
        return SetReturn(ctx, 0);
    }

    private static int FiberRunCore(
        CpuContext ctx,
        ulong fiber,
        ulong argOnRun,
        ulong outArgumentAddress,
        ulong attachContextAddress,
        ulong attachContextSize,
        string reason,
        bool isSwitch)
    {
        if (!TryValidateFiber(ctx, fiber, out var error))
        {
            return SetReturn(ctx, error);
        }

        if (!TryReadFiberFields(ctx, fiber, out var fields))
        {
            return SetReturn(ctx, FiberErrorInvalid);
        }

        if (attachContextAddress != 0 || attachContextSize != 0)
        {
            var attachResult = AttachContext(ctx, fiber, attachContextAddress, attachContextSize, ref fields);
            if (attachResult != 0)
            {
                return SetReturn(ctx, attachResult);
            }
        }

        var previousFiber = ResolveCurrentFiberAddress(ctx);
        if ((isSwitch && previousFiber == 0) ||
            (!isSwitch && previousFiber != 0))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }
        if (previousFiber == fiber)
        {
            return SetReturn(ctx, FiberErrorState);
        }
        if (GuestThreadExecution.Scheduler is not { SupportsGuestContextTransfer: true } ||
            !GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame))
        {
            return SetReturn(ctx, FiberErrorPermission);
        }

        GuestCpuContinuation transferTarget;
        var resumed = false;
        lock (_fiberGate)
        {
            var threadKey = GetThreadKey(ctx);
            if (!TryReadFiberFields(ctx, fiber, out fields))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }
            if (fields.State != FiberStateIdle)
            {
                TraceFiber($"run-state-error reason={reason} fiber=0x{fiber:X16} state=0x{fields.State:X8}");
                return SetReturn(ctx, FiberErrorState);
            }

            FiberContinuation targetContinuation = default;
            if (_continuations.TryGetValue(fiber, out var savedContinuation))
            {
                targetContinuation = savedContinuation;
                resumed = true;
            }
            var callerContinuation = new FiberContinuation(
                CaptureContinuation(ctx, frame.ReturnRip, frame.ResumeRsp, frame.ReturnSlotAddress),
                outArgumentAddress);

            if (!resumed)
            {
                var rootStackTop = _threadStates.TryGetValue(threadKey, out var existingThreadState)
                    ? existingThreadState.RootContinuation.Context.Rsp
                    : callerContinuation.Context.Rsp;
                if (!TryCreateInitialContinuation(
                        ctx,
                        fields,
                        argOnRun,
                        rootStackTop,
                        out targetContinuation))
                {
                    return SetReturn(ctx, FiberErrorInvalid);
                }
            }
            else if (!TryWriteResumeArgument(ctx, targetContinuation, argOnRun))
            {
                return SetReturn(ctx, FiberErrorInvalid);
            }

            if (previousFiber != 0)
            {
                if (!TryReadUInt32(ctx, previousFiber + FiberStateOffset, out var previousState) ||
                    previousState != FiberStateRun ||
                    !TryWriteUInt32(ctx, previousFiber + FiberStateOffset, FiberStateIdle))
                {
                    return SetReturn(ctx, FiberErrorState);
                }

                _continuations[previousFiber] = callerContinuation;
            }
            else
            {
                if (_threadStates.ContainsKey(threadKey))
                {
                    return SetReturn(ctx, FiberErrorPermission);
                }

                _threadStates[threadKey] = new FiberThreadState(callerContinuation, fiber, previousFiber);
            }

            if (!TryWriteUInt32(ctx, fiber + FiberStateOffset, FiberStateRun))
            {
                if (previousFiber != 0)
                {
                    _continuations.TryRemove(previousFiber, out _);
                    _ = TryWriteUInt32(ctx, previousFiber + FiberStateOffset, FiberStateRun);
                }
                else
                {
                    _threadStates.TryRemove(threadKey, out _);
                }
                return SetReturn(ctx, FiberErrorInvalid);
            }

            if (resumed)
            {
                _continuations.TryRemove(fiber, out _);
            }

            transferTarget = targetContinuation.Context with { Rax = 0 };
            if (_threadStates.TryGetValue(threadKey, out var activeState))
            {
                _threadStates[threadKey] = activeState with
                {
                    CurrentFiber = fiber,
                    PreviousFiber = previousFiber,
                };
            }
        }

        _ = GuestThreadExecution.EnterFiber(fiber);
        NoteFiberEntered(ctx, fiber);
        GuestThreadExecution.RequestCurrentContextTransfer(transferTarget);
        TraceFiber(
            $"transfer reason={reason} from=0x{previousFiber:X16} to=0x{fiber:X16} resume={resumed} " +
            $"rip=0x{transferTarget.Rip:X16} rsp=0x{transferTarget.Rsp:X16} arg=0x{argOnRun:X16}");
        return SetReturn(ctx, 0);
    }

    private static bool TryCreateInitialContinuation(
        CpuContext ctx,
        FiberFields fields,
        ulong argOnRun,
        ulong rootStackTop,
        out FiberContinuation continuation)
    {
        continuation = default;
        ulong stackEnd;
        if (fields.ContextAddress == 0)
        {
            if (rootStackTop < 32)
            {
                return false;
            }

            // Contextless fibers are specified to execute on the root
            // sceFiberRun stack.  Keep the suspended import return record
            // above the entry stack and use a synthetic transfer slot below it.
            stackEnd = rootStackTop & ~15UL;
        }
        else
        {
            if (fields.ContextSize < FiberContextMinimumSize)
            {
                return false;
            }

            stackEnd = fields.ContextAddress + fields.ContextSize;
        }

        if (!TryCalculateInitialStackLayout(stackEnd, out var entryRsp, out var transferSlot))
        {
            return false;
        }
        if (!TryWriteUInt64(ctx, transferSlot, fields.Entry) ||
            !TryWriteUInt64(ctx, entryRsp, 0))
        {
            return false;
        }

        var setFpuRegisters = (fields.Flags & FiberFlagSetFpuRegs) != 0;

        continuation = new FiberContinuation(
            new GuestCpuContinuation(
                fields.Entry,
                entryRsp,
                entryRsp,
                ctx.Rflags == 0 ? 0x202UL : ctx.Rflags,
                // No thread pointer: see CaptureContinuation.
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                argOnRun,
                fields.ArgOnInitialize,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                setFpuRegisters ? InitialFpuControlWord : ctx.FpuControlWord,
                setFpuRegisters ? InitialMxcsr : ctx.Mxcsr,
                RestoreFullFpuState: setFpuRegisters),
            0);
        return true;
    }

    private static bool TryWriteResumeArgument(
        CpuContext ctx,
        FiberContinuation continuation,
        ulong argument) =>
        continuation.ArgOnRunAddress == 0 ||
        TryWriteUInt64(ctx, continuation.ArgOnRunAddress, argument);

    /// <summary>
    /// Captures a fiber's resume point. A fiber switch saves the callee-saved
    /// registers, stack and FPU state — never the thread pointer: fs belongs to the
    /// kernel thread, and a fiber suspended on one thread runs with the TLS of
    /// whichever thread resumes it (ShadPS4's switch saves rsp/rbp/rbx/r12–r15 and
    /// MXCSR/FPUCW only, and keeps the current fiber in the per-thread TCB). Titles
    /// rely on this: a job system that migrates fibers between workers detaches and
    /// reattaches its own TLS job state around the switch. Carrying fs here made a
    /// resumed fiber run on the suspending worker's TLS block, so two workers shared
    /// job state (GT7, docs/gt7 blocker E). Zero leaves the resuming thread's fs in
    /// place — ApplyGuestContinuation only overwrites a non-zero value.
    /// </summary>
    private static GuestCpuContinuation CaptureContinuation(
        CpuContext ctx,
        ulong resumeRip,
        ulong resumeRsp,
        ulong returnSlotAddress) =>
        new(
            resumeRip,
            resumeRsp,
            returnSlotAddress,
            ctx.Rflags == 0 ? 0x202UL : ctx.Rflags,
            0,
            0,
            0,
            ctx[CpuRegister.Rcx],
            ctx[CpuRegister.Rdx],
            ctx[CpuRegister.Rbx],
            ctx[CpuRegister.Rbp],
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.R8],
            ctx[CpuRegister.R9],
            ctx[CpuRegister.R10],
            ctx[CpuRegister.R11],
            ctx[CpuRegister.R12],
            ctx[CpuRegister.R13],
            ctx[CpuRegister.R14],
            ctx[CpuRegister.R15],
            ctx.FpuControlWord,
            ctx.Mxcsr,
            RestoreFullFpuState: false);

    private static ulong ResolveCurrentFiberAddress(CpuContext ctx)
    {
        if (GuestThreadExecution.CurrentFiberAddress != 0)
        {
            return GuestThreadExecution.CurrentFiberAddress;
        }

        if (_threadStates.TryGetValue(GetThreadKey(ctx), out var threadState) &&
            threadState.CurrentFiber != 0)
        {
            return threadState.CurrentFiber;
        }

        return TryFindFiberByStack(ctx, out var fiberAddress) ? fiberAddress : 0;
    }

    private static ulong GetThreadKey(CpuContext ctx)
    {
        var handle = GuestThreadExecution.CurrentGuestThreadHandle;
        if (handle != 0)
        {
            return handle;
        }

        // The main guest thread does not always have a scheduler handle.  Its
        // ABI thread pointer is stable across native/managed transitions.
        return ctx.FsBase != 0 ? ctx.FsBase : 1;
    }

    private static uint ApplyInitializationFlags(
        uint flags,
        uint buildVersion,
        bool contextSizeCheck)
    {
        if (buildVersion >= Firmware350BuildVersion)
        {
            flags |= FiberFlagSetFpuRegs;
        }
        if (contextSizeCheck)
        {
            flags |= FiberFlagContextSizeCheck;
        }

        return flags;
    }

    private static bool TryCalculateInitialStackLayout(
        ulong stackEnd,
        out ulong entryRsp,
        out ulong transferSlot)
    {
        var alignedEnd = stackEnd & ~15UL;
        if (alignedEnd < 2 * sizeof(ulong))
        {
            entryRsp = 0;
            transferSlot = 0;
            return false;
        }

        entryRsp = alignedEnd - sizeof(ulong);
        transferSlot = entryRsp - sizeof(ulong);
        return true;
    }

    [Conditional("DEBUG")]
    private static void RunFiberSelfChecks()
    {
        Debug.Assert(
            ApplyInitializationFlags(0, Firmware350BuildVersion - 1, false) == 0,
            "Pre-3.50 fibers unexpectedly enable SetFpuRegs.");
        Debug.Assert(
            ApplyInitializationFlags(0, Firmware350BuildVersion, false) == FiberFlagSetFpuRegs,
            "3.50+ fibers must initialize x87/MXCSR control state.");
        Debug.Assert(
            ApplyInitializationFlags(1, Firmware350BuildVersion, true) ==
                (1u | FiberFlagSetFpuRegs | FiberFlagContextSizeCheck),
            "Fiber initialization discarded caller/internal flags.");
        Debug.Assert(
            TryCalculateInitialStackLayout(0x1017, out var rsp, out var slot) &&
            rsp == 0x1008 && slot == 0x1000 && (rsp & 15) == 8,
            "Initial fiber transfer slot does not produce SysV entry alignment.");
    }

    internal static ulong GetCurrentFiberAddressForDiagnostics(CpuContext ctx) =>
        ResolveCurrentFiberAddress(ctx);

    private static int AttachContext(
        CpuContext ctx,
        ulong fiber,
        ulong contextAddress,
        ulong contextSize,
        ref FiberFields fields)
    {
        if ((contextAddress & 15) != 0)
        {
            return FiberErrorAlignment;
        }

        if (contextSize != 0 && contextSize < FiberContextMinimumSize)
        {
            return FiberErrorRange;
        }

        if ((contextSize & 15) != 0 ||
            contextAddress == 0 ||
            contextSize == 0 ||
            fields.ContextAddress != 0)
        {
            return FiberErrorInvalid;
        }

        if (!TryWriteUInt64(ctx, fiber + FiberContextAddressOffset, contextAddress) ||
            !TryWriteUInt64(ctx, fiber + FiberContextSizeOffset, contextSize) ||
            !TryWriteUInt64(ctx, fiber + FiberContextStartOffset, contextAddress) ||
            !TryWriteUInt64(ctx, fiber + FiberContextEndOffset, contextAddress + contextSize) ||
            !TryWriteUInt64(ctx, contextAddress, FiberStackSignature))
        {
            return FiberErrorInvalid;
        }

        fields = fields with
        {
            ContextAddress = contextAddress,
            ContextSize = contextSize,
        };
        _stackRanges[fiber] = new FiberStackRange(contextAddress, contextSize);
        return 0;
    }

    private static bool TryFindFiberByStack(CpuContext ctx, out ulong fiber)
    {
        if (TryFindFiberByStackAddress(ctx[CpuRegister.Rsp], out fiber))
        {
            return true;
        }

        return TryFindFiberByStackAddress(ctx[CpuRegister.Rbp], out fiber);
    }

    private static bool TryFindFiberByStackAddress(ulong address, out ulong fiber)
    {
        if (address != 0)
        {
            foreach (var (candidate, range) in _stackRanges)
            {
                if (range.Contains(address))
                {
                    fiber = candidate;
                    return true;
                }
            }
        }

        fiber = 0;
        return false;
    }

    private static bool TryValidateFiber(CpuContext ctx, ulong fiber, out int error)
    {
        if (fiber == 0)
        {
            error = FiberErrorNull;
            return false;
        }

        if ((fiber & 7) != 0)
        {
            error = FiberErrorAlignment;
            return false;
        }

        if (!TryReadUInt32(ctx, fiber + FiberMagicStartOffset, out var magicStart) ||
            !TryReadUInt32(ctx, fiber + FiberMagicEndOffset, out var magicEnd) ||
            magicStart != FiberSignature0 ||
            magicEnd != FiberSignature1)
        {
            error = FiberErrorInvalid;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryReadFiberFields(CpuContext ctx, ulong fiber, out FiberFields fields)
    {
        fields = default;
        if (!TryReadUInt32(ctx, fiber + FiberStateOffset, out var state) ||
            !TryReadUInt64(ctx, fiber + FiberEntryOffset, out var entry) ||
            !TryReadUInt64(ctx, fiber + FiberArgOnInitializeOffset, out var argOnInitialize) ||
            !TryReadUInt64(ctx, fiber + FiberContextAddressOffset, out var contextAddress) ||
            !TryReadUInt64(ctx, fiber + FiberContextSizeOffset, out var contextSize) ||
            !TryReadUInt32(ctx, fiber + FiberFlagsOffset, out var flags) ||
            !TryReadInlineName(ctx, fiber + FiberNameOffset, out var name))
        {
            return false;
        }

        fields = new FiberFields(
            state,
            entry,
            argOnInitialize,
            contextAddress,
            contextSize,
            flags,
            name);
        return true;
    }

    private static void FillContextSizeCheck(CpuContext ctx, ulong address, ulong size)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(value, FiberStackSizeCheck);
        var end = address + size;
        for (var current = address; current + sizeof(ulong) <= end; current += sizeof(ulong))
        {
            _ = ctx.Memory.TryWrite(current, value);
        }
    }

    private static ulong ReadStackArg64(CpuContext ctx, int index)
    {
        if (ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong) + ((ulong)index * sizeof(ulong)), out var value))
        {
            return value;
        }

        return 0;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadUInt64(CpuContext ctx, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt64(CpuContext ctx, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryWriteName(CpuContext ctx, ulong address, string name)
    {
        Span<byte> buffer = stackalloc byte[MaxNameLength + 1];
        var bytes = Encoding.UTF8.GetBytes(name);
        bytes.AsSpan(0, Math.Min(bytes.Length, MaxNameLength)).CopyTo(buffer);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadInlineName(CpuContext ctx, ulong address, out string value)
    {
        Span<byte> buffer = stackalloc byte[MaxNameLength + 1];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = string.Empty;
            return false;
        }

        var length = buffer.IndexOf((byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        value = Encoding.UTF8.GetString(buffer[..length]);
        return true;
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int capacity, out string value)
    {
        var bytes = new byte[capacity];
        Span<byte> current = stackalloc byte[1];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)index, current))
            {
                value = string.Empty;
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, index);
                return true;
            }

            bytes[index] = current[0];
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }

    private static void TraceFiber(string message)
    {
        if (_traceFiberTransitions)
        {
            WriteFiberTrace(message);
        }
    }

    // Every fiber line names the guest thread and the host thread it ran on, so a
    // transition and a later sceFiberGetSelf can be tied to the same guest thread.
    private static void WriteFiberTrace(string message) =>
        Console.Error.WriteLine(
            $"[LOADER][TRACE] fiber.{message} thread=0x{GuestThreadExecution.CurrentGuestThreadHandle:X} " +
            $"host={Environment.CurrentManagedThreadId}");

    private static void NoteFiberEntered(CpuContext ctx, ulong fiber)
    {
        if (!_checkFiberGetSelf)
        {
            return;
        }

        var key = GetThreadKey(ctx);
        _enteredFibers[key] = new FiberEntryRecord(fiber, Environment.CurrentManagedThreadId);
        if (fiber == 0)
        {
            return;
        }

        // Entry counts make "not in a fiber" interpretable: zero mismatches means
        // nothing unless the threads asking are shown to run fibers at all.
        var perThread = _fiberEntryCounts.AddOrUpdate(key, 1, static (_, value) => value + 1);
        var total = Interlocked.Increment(ref _fiberEntries);
        if (perThread == 1 || (total & 0xFFF) == 0)
        {
            WriteFiberTrace(
                $"enter-summary total={total} key=0x{key:X} thread_entries={perThread} fiber=0x{fiber:X16} " +
                $"threads_with_fibers={_fiberEntryCounts.Count}");
        }
    }

    /// <summary>
    /// Compares sceFiberGetSelf's answer with the fiber this guest thread last
    /// entered through a transition. "Not in a fiber" while the transitions say the
    /// thread is running one is the case under investigation (docs/gt7 blocker F):
    /// GT7's wait primitive picks a thread wait instead of a fiber wait on that
    /// answer. Benign results are only counted and sampled.
    /// </summary>
    private static void CheckGetSelfAgainstTransitions(CpuContext ctx, ulong resolved)
    {
        var key = GetThreadKey(ctx);
        var caller = GuestThreadExecution.TryGetCurrentImportCallFrame(out var frame) ? frame.ReturnRip : 0;
        _enteredFibers.TryGetValue(key, out var entered);
        if (resolved == 0 && entered.Fiber != 0)
        {
            var count = Interlocked.Increment(ref _getSelfMismatches);
            WriteFiberTrace(
                $"getself-mismatch#{count} key=0x{key:X} entered=0x{entered.Fiber:X16} " +
                $"entered_host={entered.HostThreadId} tls_fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} " +
                $"thread_state={_threadStates.ContainsKey(key)} rsp=0x{ctx[CpuRegister.Rsp]:X16} caller=0x{caller:X16}");
            return;
        }

        if (resolved != 0 && resolved != entered.Fiber)
        {
            var count = Interlocked.Increment(ref _getSelfWrongFiber);
            WriteFiberTrace(
                $"getself-wrong-fiber#{count} key=0x{key:X} resolved=0x{resolved:X16} entered=0x{entered.Fiber:X16} " +
                $"entered_host={entered.HostThreadId} tls_fiber=0x{GuestThreadExecution.CurrentFiberAddress:X16} " +
                $"caller=0x{caller:X16}");
            return;
        }

        if (resolved == 0)
        {
            var count = Interlocked.Increment(ref _getSelfOutsideFiber);
            if (count <= 8 || (count & 0xFFF) == 0)
            {
                _fiberEntryCounts.TryGetValue(key, out var threadEntries);
                WriteFiberTrace(
                    $"getself-outside#{count} key=0x{key:X} thread_entries={threadEntries} caller=0x{caller:X16} " +
                    $"mismatches={Interlocked.Read(ref _getSelfMismatches)} wrong={Interlocked.Read(ref _getSelfWrongFiber)}");
            }
        }
    }

    private readonly record struct FiberEntryRecord(ulong Fiber, int HostThreadId);

    private readonly record struct FiberFields(
        uint State,
        ulong Entry,
        ulong ArgOnInitialize,
        ulong ContextAddress,
        ulong ContextSize,
        uint Flags,
        string Name);

    private readonly record struct FiberContinuation(
        GuestCpuContinuation Context,
        ulong ArgOnRunAddress);

    private sealed record FiberThreadState(
        FiberContinuation RootContinuation,
        ulong CurrentFiber,
        ulong PreviousFiber);

    private readonly record struct FiberStackRange(ulong Start, ulong Size)
    {
        public bool Contains(ulong address) =>
            Size != 0 && address >= Start && address < Start + Size;
    }
}
