// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Libs.Pad;

/// <summary>Cross-platform input state supplied by the SDL game window.</summary>
public static class HostWindowInput
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> PressedKeys = new();
    private static readonly Dictionary<int, long> ReleaseHoldUntilMs = new();
    private static bool _focused;
    private static bool _gamepadConnected;
    private static string? _gamepadName;
    private static HostGamepadState _gamepadState;
    private static IHostGamepadOutput? _gamepadOutput;
    private static readonly WindowInputSource Source = new();

    public static void Connect(IHostGamepadOutput? gamepadOutput = null)
    {
        lock (Gate)
        {
            _focused = true;
            _gamepadOutput = gamepadOutput;
            PressedKeys.Clear();
            ReleaseHoldUntilMs.Clear();
        }

        HostWindowInputSource.Set(Source);
    }

    public static void Disconnect()
    {
        lock (Gate)
        {
            _focused = false;
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
            _gamepadOutput = null;
            PressedKeys.Clear();
            ReleaseHoldUntilMs.Clear();
        }

        HostWindowInputSource.Clear(Source);
    }

    public static void SetFocused(bool focused)
    {
        lock (Gate)
        {
            _focused = focused;
            if (!focused)
            {
                PressedKeys.Clear();
                ReleaseHoldUntilMs.Clear();
            }
        }
    }

    // SDL events are drained in one loop before each frame, so a key pressed
    // and released between two drains is added and removed here back to back
    // and the guest, which samples the pad once a frame, never sees it. Real
    // hardware cannot lose a tap that way. Keep a released key visible for a
    // short window measured from its press so at least one guest sample
    // observes it.
    // ponytail: fixed window; latch until a sample has consumed the key if a
    // title ever polls slower than MinimumVisiblePressMs.
    private const long MinimumVisiblePressMs = 100;

    public static void SetKey(int virtualKey, bool down)
    {
        lock (Gate)
        {
            if (down)
            {
                PressedKeys.Add(virtualKey);
                ReleaseHoldUntilMs.Remove(virtualKey);
            }
            else if (PressedKeys.Remove(virtualKey))
            {
                ReleaseHoldUntilMs[virtualKey] =
                    Environment.TickCount64 + MinimumVisiblePressMs;
            }
        }
    }

    private static bool IsKeyDownLocked(int virtualKey)
    {
        if (PressedKeys.Contains(virtualKey))
        {
            return true;
        }

        if (!ReleaseHoldUntilMs.TryGetValue(virtualKey, out var holdUntil))
        {
            return false;
        }

        if (Environment.TickCount64 < holdUntil)
        {
            return true;
        }

        ReleaseHoldUntilMs.Remove(virtualKey);
        return false;
    }

    public static void SetGamepad(string? name, HostGamepadState state)
    {
        lock (Gate)
        {
            _gamepadConnected = state.Connected;
            _gamepadName = name;
            _gamepadState = state;
        }
    }

    public static void ClearGamepad()
    {
        lock (Gate)
        {
            _gamepadConnected = false;
            _gamepadName = null;
            _gamepadState = default;
        }
    }

    internal static byte ToStickByte(short value)
    {
        var normalized = value + 32768;
        return (byte)Math.Clamp((normalized * 255 + 32767) / 65535, 0, 255);
    }

    internal static byte ToTriggerByte(short value) =>
        (byte)Math.Clamp(value * 255 / 32767, 0, 255);

    private sealed class WindowInputSource : IHostWindowInputSource
    {
        public bool HasKeyboardFocus
        {
            get
            {
                lock (Gate)
                {
                    return _focused;
                }
            }
        }

        public bool IsKeyDown(int virtualKey)
        {
            lock (Gate)
            {
                return IsKeyDownLocked(virtualKey);
            }
        }

        public int GetGamepadStates(Span<HostGamepadState> destination)
        {
            lock (Gate)
            {
                if (!_gamepadConnected || destination.IsEmpty)
                {
                    return 0;
                }

                destination[0] = _gamepadState;
                return 1;
            }
        }

        public string? DescribeConnectedGamepad()
        {
            lock (Gate)
            {
                return _gamepadConnected ? _gamepadName ?? "SDL gamepad" : null;
            }
        }

        public void SetRumble(byte largeMotor, byte smallMotor)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetRumble(largeMotor, smallMotor);
        }

        public void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetTriggerRumble(leftTrigger, rightTrigger);
        }

        public void SetAdaptiveTriggerEffect(
            HostAdaptiveTriggerEffect? leftTrigger,
            HostAdaptiveTriggerEffect? rightTrigger)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetAdaptiveTriggerEffect(leftTrigger, rightTrigger);
        }

        public void SetLightbar(byte red, byte green, byte blue)
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.SetLightbar(red, green, blue);
        }

        public void ResetLightbar()
        {
            IHostGamepadOutput? output;
            lock (Gate)
            {
                output = _gamepadOutput;
            }

            output?.ResetLightbar();
        }
    }
}

public interface IHostGamepadOutput
{
    void SetRumble(byte largeMotor, byte smallMotor);

    void SetTriggerRumble(byte? leftTrigger, byte? rightTrigger);

    void SetAdaptiveTriggerEffect(
        HostAdaptiveTriggerEffect? leftTrigger,
        HostAdaptiveTriggerEffect? rightTrigger);

    void SetLightbar(byte red, byte green, byte blue);

    void ResetLightbar();
}
