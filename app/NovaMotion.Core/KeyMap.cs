// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
namespace NovaMotion.Core;

public enum PlayerAction
{
    None, TogglePause, SeekBack, SeekForward, SeekBackLong, SeekForwardLong, VolumeUp, VolumeDown, ToggleMute,
    ToggleFullscreen, ExitFullscreen, Next, Previous, FrameStep, FrameBackStep, SpeedUp, SpeedDown, SpeedReset,
    ToggleSmoothing, CycleEngine, CycleTarget, ToggleDemo, ToggleVsr, ToggleMotionPanel, CycleSubtitles, CycleAudio,
    Screenshot, Stop, SeekPercent,
}

/// <summary>
/// Keyboard shortcuts in the player. Keys arrive as WPF key names ("Space", "Left", "OemPeriod")
/// plus modifier flags, so this stays testable without WPF.
/// </summary>
public static class KeyMap
{
    public sealed record Shortcut(string Keys, string Description);

    public static (PlayerAction Action, int Arg) Resolve(string key, bool ctrl, bool shift, bool alt)
    {
        if (alt) return key == "Return" || key == "Enter" ? (PlayerAction.ToggleFullscreen, 0) : (PlayerAction.None, 0);
        if (ctrl)
        {
            return key switch
            {
                "S" => (PlayerAction.ToggleSmoothing, 0),
                "E" => (PlayerAction.CycleEngine, 0),
                "T" => (PlayerAction.CycleTarget, 0),
                "D" => (PlayerAction.ToggleDemo, 0),
                "R" => (PlayerAction.ToggleVsr, 0),
                "Right" => (PlayerAction.Next, 0),
                "Left" => (PlayerAction.Previous, 0),
                _ => (PlayerAction.None, 0),
            };
        }
        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) && !shift)
            return (PlayerAction.SeekPercent, (key[1] - '0') * 10);       // 0-9: jump to 0%-90%
        if (key.StartsWith("NumPad", StringComparison.Ordinal) && key.Length == 7 && char.IsDigit(key[6]))
            return (PlayerAction.SeekPercent, (key[6] - '0') * 10);
        return key switch
        {
            "Space" or "K" or "MediaPlayPause" or "Play" or "Pause" => (PlayerAction.TogglePause, 0),
            "Left" => (shift ? PlayerAction.SeekBackLong : PlayerAction.SeekBack, 0),
            "Right" => (shift ? PlayerAction.SeekForwardLong : PlayerAction.SeekForward, 0),
            "J" => (PlayerAction.SeekBackLong, 0),
            "L" => (PlayerAction.SeekForwardLong, 0),
            "Up" => (PlayerAction.VolumeUp, 0),
            "Down" => (PlayerAction.VolumeDown, 0),
            "VolumeUp" => (PlayerAction.VolumeUp, 0),
            "VolumeDown" => (PlayerAction.VolumeDown, 0),
            "M" or "VolumeMute" => (PlayerAction.ToggleMute, 0),
            "F" or "F11" => (PlayerAction.ToggleFullscreen, 0),
            "Escape" => (PlayerAction.ExitFullscreen, 0),
            "N" or "MediaNextTrack" => (PlayerAction.Next, 0),
            "P" or "MediaPreviousTrack" => (PlayerAction.Previous, 0),
            "OemPeriod" => (PlayerAction.FrameStep, 0),
            "OemComma" => (PlayerAction.FrameBackStep, 0),
            "OemCloseBrackets" => (PlayerAction.SpeedUp, 0),
            "OemOpenBrackets" => (PlayerAction.SpeedDown, 0),
            "Back" => (PlayerAction.SpeedReset, 0),
            "Tab" => (PlayerAction.ToggleMotionPanel, 0),
            "V" => (PlayerAction.CycleSubtitles, 0),
            "A" => (PlayerAction.CycleAudio, 0),
            "S" => (PlayerAction.Screenshot, 0),
            "MediaStop" => (PlayerAction.Stop, 0),
            _ => (PlayerAction.None, 0),
        };
    }

    /// <summary>For the shortcuts list in Settings and the "?" sheet.</summary>
    public static readonly IReadOnlyList<Shortcut> All = new Shortcut[]
    {
        new("Space / K", "Play or pause"),
        new("← / →", "Back or forward 5 s"),
        new("Shift + ← / → · J / L", "Back or forward 30 s"),
        new("↑ / ↓", "Volume"),
        new("M", "Mute"),
        new("F · double-click", "Full screen"),
        new("0 – 9", "Jump to 0 % – 90 %"),
        new(", / .", "Previous or next frame (paused)"),
        new("[ / ]", "Slower or faster · Backspace resets"),
        new("N / P · Ctrl + → / ←", "Next or previous in queue"),
        new("V / A", "Next subtitle or audio track"),
        new("S", "Screenshot to Pictures"),
        new("Tab", "Motion panel"),
        new("Ctrl + S", "Smoothing on or off"),
        new("Ctrl + E", "Next engine"),
        new("Ctrl + T", "Next frame-rate target"),
        new("Ctrl + D", "Before / after split screen"),
        new("Ctrl + R", "RTX Video Super Resolution"),
    };
}
