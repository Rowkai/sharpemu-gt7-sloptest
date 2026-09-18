// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace SharpEmu.HLE;

/// <summary>
/// A bounded in-memory trace of one condition variable and one semaphore, so a stall
/// can be read back afterwards without streaming synchronisation logging that would
/// change the timing being investigated.
/// </summary>
/// <remarks>
/// Only the objects named by <c>SHARPEMU_TRACE_SYNC_COND</c> (hex guest addresses) and
/// <c>SHARPEMU_TRACE_SYNC_SEMA</c> (handles) are recorded, so every other lock keeps its
/// untouched hot path. Nothing is emitted until <see cref="Dump"/> runs from a failure
/// path (native exception, stall watchdog, periodic snapshot).
/// </remarks>
public static class SyncTraceRing
{
    public enum SyncEvent : byte
    {
        CondWaitEnqueue,
        CondWaitExit,
        CondSignal,
        CondComplete,
        CondWake,
        SemaWaitFast,
        SemaWaitBlock,
        SemaPredicate,
        SemaSignal,
        SemaResume,
    }

    private const int Capacity = 8192;

    private static readonly Entry[] _entries = new Entry[Capacity];
    private static long _sequence = -1;
    private static long _dumpedThrough = -1;

    private static readonly ulong[] _condAddresses =
        Parse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SYNC_COND"));

    private static readonly ulong[] _semaphoreHandles =
        Parse(Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SYNC_SEMA"));

    public static bool Enabled => _condAddresses.Length != 0 || _semaphoreHandles.Length != 0;

    public static bool TracesCond(ulong condAddress) => Contains(_condAddresses, condAddress);

    public static bool TracesSemaphore(ulong handle) => Contains(_semaphoreHandles, handle);

    public static void Record(SyncEvent kind, ulong traced, ulong argA, ulong argB)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        _entries[(int)((ulong)sequence % Capacity)] = new Entry(
            sequence,
            Stopwatch.GetTimestamp(),
            kind,
            traced,
            Environment.CurrentManagedThreadId,
            GuestThreadExecution.CurrentGuestThreadHandle,
            argA,
            argB);
    }

    /// <summary>Prints the entries still held in the ring, oldest first.</summary>
    public static void Dump(string reason)
    {
        if (!Enabled)
        {
            return;
        }

        var last = Interlocked.Read(ref _sequence);
        if (last < 0)
        {
            Console.Error.WriteLine($"[LOADER][INFO] sync-trace ({reason}): empty");
            return;
        }

        // The stall watchdog dumps repeatedly; only entries not already printed are
        // emitted so a periodic snapshot does not reprint the whole ring each time.
        var first = Math.Max(Math.Max(0, last - Capacity + 1), _dumpedThrough + 1);
        _dumpedThrough = last;
        if (first > last)
        {
            Console.Error.WriteLine($"[LOADER][INFO] sync-trace ({reason}): no new entries since {last}");
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] sync-trace ({reason}): entries {first}..{last} " +
            $"cond=[{Describe(_condAddresses)}] sema=[{Describe(_semaphoreHandles)}]");

        var frequency = (double)Stopwatch.Frequency;
        for (var sequence = first; sequence <= last; sequence++)
        {
            var entry = _entries[(int)((ulong)sequence % Capacity)];
            if (entry.Sequence != sequence)
            {
                // Overwritten while dumping: the ring is deliberately lock-free.
                continue;
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] sync-trace #{entry.Sequence} t={entry.Timestamp / frequency:F6} " +
                $"{entry.Kind} obj=0x{entry.Object:X} host={entry.HostThreadId} " +
                $"guest=0x{entry.GuestThread:X} a=0x{entry.ArgA:X} b=0x{entry.ArgB:X}");
        }
    }

    private static bool Contains(ulong[] values, ulong value)
    {
        foreach (var candidate in values)
        {
            if (candidate == value)
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(ulong[] values)
    {
        if (values.Length == 0)
        {
            return string.Empty;
        }

        var parts = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            parts[index] = $"0x{values[index]:X}";
        }

        return string.Join(",", parts);
    }

    private static ulong[] Parse(string? specification)
    {
        if (string.IsNullOrWhiteSpace(specification))
        {
            return [];
        }

        var parsed = new List<ulong>();
        foreach (var part in specification.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                parsed.Add(value);
            }
        }

        return parsed.ToArray();
    }

    private readonly record struct Entry(
        long Sequence,
        long Timestamp,
        SyncEvent Kind,
        ulong Object,
        int HostThreadId,
        ulong GuestThread,
        ulong ArgA,
        ulong ArgB);
}
