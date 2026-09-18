// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Hardware write watch over guest memory. Guest code executes natively, so a
/// store into a guest object is an ordinary CPU write with nothing for the HLE
/// layer to observe: the managed <c>SHARPEMU_WATCH_WRITE</c> only sees writes
/// an export performed, and the page tracker disarms a whole page on its first
/// fault. Programming a debug register on every guest thread instead names the
/// exact guest instruction that wrote a field, which is the one question a
/// periodic memory dump cannot answer for a field its owner resets each pass.
/// </summary>
public sealed partial class DirectExecutionBackend
{
	// SHARPEMU_WATCH_GUEST_WRITE=<hex address>[:<1|2|4|8>] breaks on a store,
	// SHARPEMU_WATCH_GUEST_EXEC=<hex address> breaks when guest code reaches an
	// instruction. The execute form answers "does this function ever run, and
	// who calls it" for a function reached only through a runtime dispatch
	// table, which no static cross-reference can decide.
	private static readonly string? _watchGuestWriteSpec =
		Environment.GetEnvironmentVariable("SHARPEMU_WATCH_GUEST_WRITE");

	private static readonly string? _watchGuestExecSpec =
		Environment.GetEnvironmentVariable("SHARPEMU_WATCH_GUEST_EXEC");

	private static bool _watchGuestExec;

	private const int Win64ContextDr0Offset = 0x48;
	private const int Win64ContextDr6Offset = 0x68;
	private const int Win64ContextDr7Offset = 0x70;
	private const uint ContextAmd64DebugRegisters = 0x00100010u;
	private const uint ThreadSetContext = 0x0010u;
	private const uint ExceptionSingleStep = 0x80000004u;

	private static string _watchGuestWriteAddressText = string.Empty;
	private static ulong _watchGuestWriteAddress;
	private static ulong _watchGuestWriteDr7;
	private static int _watchGuestWriteLength;
	private static long _watchGuestWriteHits;
	private static readonly ConcurrentDictionary<ulong, long> _watchGuestWriteSites = new();

	private int _guestWriteWatchStarted;
	// The title's main thread is not a registered guest thread, so the arm loop's
	// thread snapshot never includes it and its stores never trapped — early-boot
	// initialisation, which is where heap arenas get laid out, ran unwatched.
	private int _guestWriteWatchPrimaryHostThreadId;
	private readonly ConcurrentDictionary<int, byte> _guestWriteWatchArmed = new();

	private void EnsureGuestWriteWatch()
	{
		var spec = string.IsNullOrWhiteSpace(_watchGuestWriteSpec)
			? _watchGuestExecSpec
			: _watchGuestWriteSpec;
		if (string.IsNullOrWhiteSpace(spec) || !OperatingSystem.IsWindows())
		{
			return;
		}

		// Every import dispatch passes here on the thread making the call; the first
		// one that is not a registered guest thread is the title's main thread.
		if (_guestWriteWatchPrimaryHostThreadId == 0 && !SharpEmu.HLE.GuestThreadExecution.IsGuestThread)
		{
			_ = Interlocked.CompareExchange(
				ref _guestWriteWatchPrimaryHostThreadId, unchecked((int)GetCurrentThreadId()), 0);
		}

		if (Interlocked.Exchange(ref _guestWriteWatchStarted, 1) != 0)
		{
			return;
		}

		_watchGuestExec = string.IsNullOrWhiteSpace(_watchGuestWriteSpec);
		if (!TryConfigureGuestWriteWatch(spec!))
		{
			Console.Error.WriteLine(
				$"[LOADER][ERROR] guest-write-watch: cannot parse '{spec}' " +
				"(expected <hex address>[:1|2|4|8], address aligned to the length)");
			return;
		}

		var watcher = new Thread(GuestWriteWatchArmLoop)
		{
			IsBackground = true,
			Name = "SharpEmu guest write watch",
			Priority = ThreadPriority.BelowNormal,
		};
		watcher.Start();
		Console.Error.WriteLine(
			$"[LOADER][INFO] guest-write-watch arming: spec='{_watchGuestWriteAddressText}' " +
			$"len={_watchGuestWriteLength} exec={_watchGuestExec}");
	}

	private static bool TryConfigureGuestWriteWatch(string spec)
	{
		var text = spec.Trim();
		var length = 4;
		var colon = text.IndexOf(':');
		if (colon >= 0)
		{
			if (!int.TryParse(text[(colon + 1)..], out length))
			{
				return false;
			}

			text = text[..colon];
		}

		// DR7: L0 enables breakpoint 0, LE keeps the report precise, RW0=01 is
		// "break on data write", and LEN0 encodes the width (4 bytes is 0b11).
		var lengthBits = length switch
		{
			1 => 0b00UL,
			2 => 0b01UL,
			4 => 0b11UL,
			8 => 0b10UL,
			_ => ulong.MaxValue,
		};
		if (lengthBits == ulong.MaxValue)
		{
			return false;
		}

		// The address may name a heap object that lands somewhere new each boot,
		// so it takes the same pointer-deref form as SHARPEMU_DUMP_GUEST_MEMORY
		// and is resolved in the arm loop once the object exists.
		_watchGuestWriteAddressText = text;
		_watchGuestWriteLength = _watchGuestExec ? 1 : length;
		// An execute breakpoint is RW=00 with LEN=00; anything else is rejected
		// by the CPU.
		_watchGuestWriteDr7 = _watchGuestExec
			? 1UL | (1UL << 8)
			: 1UL | (1UL << 8) | (0b01UL << 16) | (lengthBits << 18);
		return true;
	}

	private bool TryResolveGuestWriteWatchAddress()
	{
		if (_watchGuestWriteAddress != 0)
		{
			return true;
		}

		// A deref spec resolves to a small offset while the object it names is
		// still null, so keep retrying until it lands in mapped guest memory
		// rather than arming on the low page.
		if (!TryResolveDumpAddress(_cpuContext, _watchGuestWriteAddressText, out var address) ||
			address < 0x10000UL ||
			(!_watchGuestExec && (address & (ulong)(_watchGuestWriteLength - 1)) != 0))
		{
			return false;
		}

		_watchGuestWriteAddress = address;
		_watchGuestExecContext = _cpuContext;
		Console.Error.WriteLine(
			$"[LOADER][INFO] guest-write-watch resolved '{_watchGuestWriteAddressText}' " +
			$"to 0x{address:X16} len={_watchGuestWriteLength} exec={_watchGuestExec}");
		return true;
	}

	private void GuestWriteWatchArmLoop()
	{
		while (true)
		{
			try
			{
				if (!TryResolveGuestWriteWatchAddress())
				{
					Thread.Sleep(50);
					continue;
				}

				foreach (var thread in SnapshotGuestThreads())
				{
					var hostThreadId = Volatile.Read(ref thread.HostThreadId);
					if (hostThreadId == 0 || _guestWriteWatchArmed.ContainsKey(hostThreadId))
					{
						continue;
					}

					if (TryArmGuestWriteWatch(hostThreadId))
					{
						_guestWriteWatchArmed[hostThreadId] = 1;
					}
				}

				var primaryHostThreadId = Volatile.Read(ref _guestWriteWatchPrimaryHostThreadId);
				if (primaryHostThreadId != 0 &&
					!_guestWriteWatchArmed.ContainsKey(primaryHostThreadId) &&
					TryArmGuestWriteWatch(primaryHostThreadId))
				{
					_guestWriteWatchArmed[primaryHostThreadId] = 1;
					Console.Error.WriteLine(
						$"[LOADER][INFO] guest-write-watch armed title main thread host_tid={primaryHostThreadId}");
				}
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(
					$"[LOADER][ERROR] guest-write-watch arm loop: {ex.GetType().Name}: {ex.Message}");
			}

			Thread.Sleep(50);
		}
	}

	private unsafe static bool TryArmGuestWriteWatch(int hostThreadId)
	{
		if (hostThreadId == 0 || unchecked((uint)hostThreadId) == GetCurrentThreadId())
		{
			return false;
		}

		var threadHandle = OpenThread(
			ThreadGetContext | ThreadSetContext | ThreadSuspendResume,
			false,
			unchecked((uint)hostThreadId));
		if (threadHandle == 0)
		{
			return false;
		}

		void* contextRecord = null;
		var suspended = false;
		try
		{
			if (SuspendThread(threadHandle) == uint.MaxValue)
			{
				return false;
			}

			suspended = true;
			contextRecord = NativeMemory.AllocZeroed((nuint)Win64ContextSize);
			WriteCtxU32(contextRecord, Win64ContextFlagsOffset, ContextAmd64DebugRegisters);
			if (!GetThreadContext(threadHandle, contextRecord))
			{
				return false;
			}

			WriteCtxU64(contextRecord, Win64ContextDr0Offset, _watchGuestWriteAddress);
			WriteCtxU64(contextRecord, Win64ContextDr6Offset, 0uL);
			WriteCtxU64(contextRecord, Win64ContextDr7Offset, _watchGuestWriteDr7);
			WriteCtxU32(contextRecord, Win64ContextFlagsOffset, ContextAmd64DebugRegisters);
			return SetThreadContext(threadHandle, contextRecord);
		}
		finally
		{
			if (contextRecord != null)
			{
				NativeMemory.Free(contextRecord);
			}

			if (suspended)
			{
				_ = ResumeThread(threadHandle);
			}

			_ = CloseHandle(threadHandle);
		}
	}

	/// <summary>
	/// Reports the guest instruction that tripped the watch. A data breakpoint
	/// traps after the store retires, so RIP is the instruction following it and
	/// the watched bytes already hold the new value.
	/// </summary>
	private unsafe static bool TryHandleGuestWriteWatch(void* exceptionInfo)
	{
		if (_watchGuestWriteAddress == 0)
		{
			return false;
		}

		var exceptionRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ExceptionRecord;
		if (exceptionRecord->ExceptionCode != ExceptionSingleStep)
		{
			return false;
		}

		var contextRecord = ((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord;
		var dr6 = ReadCtxU64(contextRecord, Win64ContextDr6Offset);
		if ((dr6 & 1UL) == 0)
		{
			return false;
		}

		WriteCtxU64(contextRecord, Win64ContextDr6Offset, 0uL);
		if (_watchGuestExec)
		{
			// An execute breakpoint traps before the instruction runs. Resuming
			// without the resume flag re-executes it with the breakpoint still
			// armed, so the thread traps on the same instruction forever instead
			// of running it: every "hit" after the first is that loop.
			WriteCtxU32(contextRecord, CTX_EFLAGS, ReadCtxU32(contextRecord, CTX_EFLAGS) | 0x10000u);
		}

		var hits = Interlocked.Increment(ref _watchGuestWriteHits);
		var rip = ReadCtxU64(contextRecord, CTX_RIP);
		if (_watchGuestExec && _watchGuestExecRingCapacity > 0)
		{
			RecordGuestExecWatchRingHit(contextRecord, rip);
			return true;
		}

		var siteHits = _watchGuestWriteSites.AddOrUpdate(rip, 1, static (_, value) => value + 1);
		// A narrow data watch fires rarely; sampling per site would hide the single
		// foreign store this is set up to catch. Cap only to survive a hot address.
		if (_watchGuestExec ? (siteHits <= 3 || hits % 256 == 0) : (hits <= 64 || hits % 64 == 0))
		{
			if (_watchGuestExec)
			{
				// The trap fires before the instruction runs, so rsp still holds
				// the return address a call pushed.
				var rsp = ReadCtxU64(contextRecord, CTX_RSP);
				var caller = rsp != 0 ? *(ulong*)rsp : 0uL;
				// All four volatile/callee-saved GPRs, not just the first two
				// arguments: watching a store names its object and value in
				// whichever registers that instruction happens to use.
				Console.Error.WriteLine(
					$"[LOADER][ERROR] guest-exec-watch hit#{hits} rip=0x{rip:X16} " +
					$"caller=0x{caller:X16} rdi=0x{ReadCtxU64(contextRecord, CTX_RDI):X16} " +
					$"rsi=0x{ReadCtxU64(contextRecord, CTX_RSI):X16} " +
					$"rax=0x{ReadCtxU64(contextRecord, CTX_RAX):X16} " +
					$"rbx=0x{ReadCtxU64(contextRecord, CTX_RBX):X16} " +
					$"rcx=0x{ReadCtxU64(contextRecord, CTX_RCX):X16} " +
					$"rdx=0x{ReadCtxU64(contextRecord, CTX_RDX):X16} tid={GetCurrentThreadId()}" +
					DescribeExecWatchProbes(contextRecord));
			}
			else
			{
				var value = *(ulong*)_watchGuestWriteAddress;
				var rsp = ReadCtxU64(contextRecord, CTX_RSP);
				var rbp = ReadCtxU64(contextRecord, CTX_RBP);
				Console.Error.WriteLine(
					$"[LOADER][ERROR] guest-write-watch hit#{hits} addr=0x{_watchGuestWriteAddress:X16} " +
					$"value=0x{value:X16} after_rip=0x{rip:X16} tid={GetCurrentThreadId()} site_hits={siteHits} " +
					$"guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} " +
					$"rsp=0x{rsp:X16} rbp=0x{rbp:X16} " +
					$"rax=0x{ReadCtxU64(contextRecord, CTX_RAX):X16} rbx=0x{ReadCtxU64(contextRecord, CTX_RBX):X16} " +
					$"rcx=0x{ReadCtxU64(contextRecord, CTX_RCX):X16} rdx=0x{ReadCtxU64(contextRecord, CTX_RDX):X16} " +
					$"rsi=0x{ReadCtxU64(contextRecord, CTX_RSI):X16} rdi=0x{ReadCtxU64(contextRecord, CTX_RDI):X16}" +
					DescribeGuestWriteWatchFrames(rsp, rbp));
			}
		}

		return true;
	}

	/// <summary>
	/// Return addresses above the trapping store, from the pushed return address at
	/// rsp and then an RBP walk. A write watch answers "who wrote this" only if the
	/// caller chain comes with it; without one, a store inside a shared helper
	/// (memset, a table initialiser) names nothing. Reads are bounds-checked and
	/// wrapped: this runs inside the vectored handler, where a fault would recurse.
	/// </summary>
	private unsafe static string DescribeGuestWriteWatchFrames(ulong rsp, ulong rbp)
	{
		var text = new System.Text.StringBuilder(" frames=");
		try
		{
			if (IsReadableGuestStackAddress(rsp))
			{
				text.Append($"0x{*(ulong*)rsp:X16}");
			}

			var frame = rbp;
			for (var depth = 0; depth < 4 && IsReadableGuestStackAddress(frame); depth++)
			{
				var returnAddress = *(ulong*)(frame + sizeof(ulong));
				if (returnAddress == 0)
				{
					break;
				}

				text.Append($",0x{returnAddress:X16}");
				var next = *(ulong*)frame;
				if (next <= frame)
				{
					break;
				}

				frame = next;
			}
		}
		catch
		{
			text.Append(",<unreadable>");
		}

		return text.ToString();
	}

	private unsafe static bool IsReadableGuestStackAddress(ulong address)
	{
		if (address == 0 || (address & 7) != 0 || address < 0x10000)
		{
			return false;
		}

		MemoryBasicInformationWatch info;
		if (VirtualQueryWatch((void*)address, out info, (nuint)sizeof(MemoryBasicInformationWatch)) == 0)
		{
			return false;
		}

		const uint memCommit = 0x1000;
		const uint pageNoAccess = 0x01;
		const uint pageGuard = 0x100;
		return info.State == memCommit &&
			(info.Protect & (pageNoAccess | pageGuard)) == 0 &&
			address + 2 * sizeof(ulong) <= info.BaseAddress + info.RegionSize;
	}

	[DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
	private unsafe static extern nuint VirtualQueryWatch(void* address, out MemoryBasicInformationWatch buffer, nuint length);

	private struct MemoryBasicInformationWatch
	{
		public ulong BaseAddress;
		public ulong AllocationBase;
		public uint AllocationProtect;
		public uint Alignment1;
		public ulong RegionSize;
		public uint State;
		public uint Protect;
		public uint Type;
		public uint Alignment2;
	}

	// SHARPEMU_WATCH_GUEST_EXEC_PROBE=<spec>[,<spec>...] reads guest memory at
	// each execute-watch hit. The registers alone answer "was this function
	// called"; a gate that rejects on a flag two hops off an argument needs the
	// flag itself, and resolving it between runs costs a boot per hop because the
	// objects move. Specs take the deref form "[rdi+8]+FC", so a probe can start
	// from a register in the trapped frame.
	private static readonly string[] _watchGuestExecProbes =
		(Environment.GetEnvironmentVariable("SHARPEMU_WATCH_GUEST_EXEC_PROBE") ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	private static CpuContext? _watchGuestExecContext;

	private unsafe static string DescribeExecWatchProbes(void* contextRecord)
	{
		if (_watchGuestExecProbes.Length == 0)
		{
			return string.Empty;
		}

		// The trapped thread's registers are in the context record, not in the
		// managed CpuContext, so publish them for the spec resolver first.
		var context = _watchGuestExecContext;
		if (context is null)
		{
			return string.Empty;
		}

		context[CpuRegister.Rdi] = ReadCtxU64(contextRecord, CTX_RDI);
		context[CpuRegister.Rsi] = ReadCtxU64(contextRecord, CTX_RSI);
		context[CpuRegister.Rax] = ReadCtxU64(contextRecord, CTX_RAX);
		context[CpuRegister.Rbx] = ReadCtxU64(contextRecord, CTX_RBX);
		context[CpuRegister.Rcx] = ReadCtxU64(contextRecord, CTX_RCX);
		context[CpuRegister.Rdx] = ReadCtxU64(contextRecord, CTX_RDX);

		var text = new System.Text.StringBuilder();
		foreach (var probe in _watchGuestExecProbes)
		{
			text.Append(' ').Append(probe).Append('=');
			if (!TryResolveDumpAddress(context, probe, out var address) || address < 0x10000UL)
			{
				text.Append("unresolved");
				continue;
			}

			Span<byte> word = stackalloc byte[8];
			text.Append(context.Memory.TryRead(address, word)
				? $"0x{BitConverter.ToUInt64(word):X16}@0x{address:X16}"
				: $"fault@0x{address:X16}");
		}

		return text.ToString();
	}

	// SHARPEMU_WATCH_GUEST_EXEC_RING=<entries> keeps every execute-watch hit in a
	// bounded in-memory ring instead of printing a sample, and prints the ring only
	// when the run fails. A hot site such as a job entry traps thousands of times:
	// printing each hit changes the timing under investigation, and sampling drops
	// the one hit that matters. Probe values are read at the hit, because the
	// memory they name has usually been recycled by the time anything is dumped.
	private static readonly int _watchGuestExecRingCapacity =
		int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_WATCH_GUEST_EXEC_RING"), out var ringEntries) &&
		ringEntries > 0
			? ringEntries
			: 0;

	private readonly record struct GuestExecWatchRingEntry(
		long Sequence,
		long Timestamp,
		uint HostThreadId,
		ulong GuestThread,
		ulong Rip,
		ulong Rdi,
		ulong Rbx,
		ulong R14,
		ulong Probe0,
		ulong Probe1,
		byte ProbeMask);

	private static readonly GuestExecWatchRingEntry[] _watchGuestExecRing =
		new GuestExecWatchRingEntry[Math.Max(1, _watchGuestExecRingCapacity)];

	private static long _watchGuestExecRingSequence = -1;
	private static long _watchGuestExecRingDumpedThrough = -1;

	// One resolver context per trapping thread: the shared one would have its
	// registers overwritten by another job thread's hit mid-resolve.
	[ThreadStatic]
	private static CpuContext? t_guestExecWatchProbeContext;

	private unsafe static void RecordGuestExecWatchRingHit(void* contextRecord, ulong rip)
	{
		ulong probe0 = 0;
		ulong probe1 = 0;
		byte probeMask = 0;
		if (_watchGuestExecProbes.Length != 0 && _watchGuestExecContext is { } shared)
		{
			var context = t_guestExecWatchProbeContext ??= new CpuContext(shared.Memory, shared.TargetGeneration);
			context[CpuRegister.Rdi] = ReadCtxU64(contextRecord, CTX_RDI);
			context[CpuRegister.Rsi] = ReadCtxU64(contextRecord, CTX_RSI);
			context[CpuRegister.Rax] = ReadCtxU64(contextRecord, CTX_RAX);
			context[CpuRegister.Rbx] = ReadCtxU64(contextRecord, CTX_RBX);
			context[CpuRegister.Rcx] = ReadCtxU64(contextRecord, CTX_RCX);
			context[CpuRegister.Rdx] = ReadCtxU64(contextRecord, CTX_RDX);
			context[CpuRegister.R14] = ReadCtxU64(contextRecord, CTX_R14);
			if (TryReadGuestExecWatchProbe(context, 0, out probe0))
			{
				probeMask |= 1;
			}

			if (TryReadGuestExecWatchProbe(context, 1, out probe1))
			{
				probeMask |= 2;
			}
		}

		var sequence = Interlocked.Increment(ref _watchGuestExecRingSequence);
		_watchGuestExecRing[(int)((ulong)sequence % (ulong)_watchGuestExecRingCapacity)] = new GuestExecWatchRingEntry(
			sequence,
			System.Diagnostics.Stopwatch.GetTimestamp(),
			GetCurrentThreadId(),
			GuestThreadExecution.CurrentGuestThreadHandle,
			rip,
			ReadCtxU64(contextRecord, CTX_RDI),
			ReadCtxU64(contextRecord, CTX_RBX),
			ReadCtxU64(contextRecord, CTX_R14),
			probe0,
			probe1,
			probeMask);
	}

	private static bool TryReadGuestExecWatchProbe(CpuContext context, int index, out ulong value)
	{
		value = 0;
		if (index >= _watchGuestExecProbes.Length ||
			!TryResolveDumpAddress(context, _watchGuestExecProbes[index], out var address) ||
			address < 0x10000UL)
		{
			return false;
		}

		Span<byte> word = stackalloc byte[8];
		if (!context.Memory.TryRead(address, word))
		{
			return false;
		}

		value = BitConverter.ToUInt64(word);
		return true;
	}

	/// <summary>
	/// Prints the execute-watch ring entries not already printed, oldest first.
	/// Called from failure paths only.
	/// </summary>
	internal static void DumpGuestExecWatchRing(string reason)
	{
		if (_watchGuestExecRingCapacity == 0 || !_watchGuestExec)
		{
			return;
		}

		var last = Interlocked.Read(ref _watchGuestExecRingSequence);
		var first = Math.Max(Math.Max(0, last - _watchGuestExecRingCapacity + 1), _watchGuestExecRingDumpedThrough + 1);
		_watchGuestExecRingDumpedThrough = last;
		Console.Error.WriteLine(
			$"[LOADER][INFO] guest-exec-ring ({reason}): entries {first}..{last} " +
			$"probes=[{string.Join(",", _watchGuestExecProbes)}]");
		var frequency = (double)System.Diagnostics.Stopwatch.Frequency;
		for (var sequence = first; sequence <= last; sequence++)
		{
			var entry = _watchGuestExecRing[(int)((ulong)sequence % (ulong)_watchGuestExecRingCapacity)];
			if (entry.Sequence != sequence)
			{
				continue;
			}

			Console.Error.WriteLine(
				$"[LOADER][INFO] guest-exec-ring #{entry.Sequence} t={entry.Timestamp / frequency:F6} " +
				$"tid={entry.HostThreadId} guest=0x{entry.GuestThread:X} rip=0x{entry.Rip:X} " +
				$"rdi=0x{entry.Rdi:X} rbx=0x{entry.Rbx:X} r14=0x{entry.R14:X} " +
				$"p0={((entry.ProbeMask & 1) != 0 ? $"0x{entry.Probe0:X}" : "unreadable")} " +
				$"p1={((entry.ProbeMask & 2) != 0 ? $"0x{entry.Probe1:X}" : "unreadable")}");
		}
	}

	private unsafe static bool SetThreadContext(nint hThread, void* lpContext) =>
		OperatingSystem.IsWindows() && Win32SetThreadContext(hThread, lpContext);

	[DllImport("kernel32.dll", EntryPoint = "SetThreadContext", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private unsafe static extern bool Win32SetThreadContext(nint hThread, void* lpContext);
}
