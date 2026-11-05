// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// The player's modern features: Skip intro, timing, night mode, picture, loop, sleep timer, Up next, PiP.
using System.Text.Json.Nodes;
using NovaMotion.Core;
using NovaMotion.Core.ViewModels;

internal static class PlayerTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nm-player-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var fake = new FakeBackend();
        var now = new DateTime(2026, 11, 6, 3, 0, 0, DateTimeKind.Utc);
        var vm = new MainViewModel(new FakeHost(), new SettingsStore(Path.Combine(dir, "settings")), null, () => fake, () => now);
        var ep1 = Path.Combine(dir, "Ep 1.mkv");
        var ep2 = Path.Combine(dir, "Ep 2.mkv");
        File.WriteAllText(ep1, ""); File.WriteAllText(ep2, "");
        await vm.OpenAsync(new[] { ep1, ep2 });
        fake.Prop("idle-active", false);
        fake.Event("file-loaded");
        fake.Prop("duration", 1320);
        fake.Prop("chapter-list", JsonNode.Parse("""[{"time":0,"title":"Recap"},{"time":45,"title":"Opening"},{"time":135,"title":"Part A"},{"time":1250,"title":"Ending"}]"""));
        fake.Prop("time-pos", 60);
        vm.Tick();
        check(vm.CurrentChapter == "Opening" && vm.ShowSkipIntro && vm.SkipIntroText == "Skip intro", $"player: Skip intro during the opening ({vm.CurrentChapter})");
        fake.Commands.Clear();
        vm.SkipIntroCommand.Execute(null);
        await Task.Delay(10);
        check(fake.Commands.Any(c => c[0] as string == "seek" && Convert.ToDouble(c[1]) == 135), "player: Skip intro jumps to the next chapter");
        fake.Prop("time-pos", 10);
        vm.Tick();
        check(vm.ShowSkipIntro && vm.SkipIntroText == "Skip recap", "player: and Skip recap");
        fake.Prop("time-pos", 300);
        vm.Tick();
        check(!vm.ShowSkipIntro, "player: nothing to skip in the episode itself");

        fake.Commands.Clear();
        vm.SubLaterCommand.Execute(null); vm.SubLaterCommand.Execute(null); vm.SubLaterCommand.Execute(null);
        vm.AudioEarlierCommand.Execute(null);
        await Task.Delay(10);
        check(vm.SubDelayText == "+0.3 s" && vm.AudioDelayText == "−0.05 s"
              && fake.Commands.Any(c => c[1] as string == "sub-delay" && Math.Abs(Convert.ToDouble(c[2]) - 0.3) < 1e-9), $"player: subtitle and audio timing ({vm.SubDelayText}, {vm.AudioDelayText})");
        vm.NightMode = true;
        check(fake.Commands.Any(c => c[0] as string == "af" && c[1] as string == "add" && (c[2] as string)!.StartsWith("@night:")), "player: night mode evens out the sound");
        vm.Volume = 200;
        check(vm.Volume == 150, "player: volume boost up to 150%");

        vm.Brightness = 20;
        vm.SelectAspectCommand.Execute(vm.AspectOptions.Single(a => a.Value == "21:9"));
        vm.RotateCommand.Execute(null);
        check(vm.PictureChanged && fake.Commands.Any(c => c[1] as string == "video-aspect-override" && c[2] as string == "21:9")
              && fake.Commands.Any(c => c[1] as string == "video-rotate" && Convert.ToInt32(c[2]) == 90), "player: picture, aspect and rotation");
        vm.ResetPictureCommand.Execute(null);
        check(!vm.PictureChanged && vm.Brightness == 0, "player: picture reset");

        vm.ABLoopCommand.Execute(null); vm.ABLoopCommand.Execute(null);
        check(vm.ABLoopOn && vm.ABLoopText == "Stop the loop", "player: A-B loop");
        vm.ABLoopCommand.Execute(null);
        check(!vm.ABLoopOn, "player: loop off");

        vm.SelectSleepCommand.Execute(vm.SleepOptions.Single(o => o.Value == "15"));
        fake.Commands.Clear();
        now = now.AddMinutes(16);
        vm.Tick();
        check(fake.Commands.Any(c => c[1] as string == "pause" && c[2] is true) && vm.SleepOptions.Single(o => o.IsSelected).Value == "0", "player: sleep timer pauses");

        fake.Prop("pause", false);
        fake.Prop("time-pos", 1312);
        vm.Tick();
        check(vm.HasNext && vm.ShowUpNext && vm.UpNextText == "Plays in 8 s", $"player: Up next counts down at the end ({vm.UpNextText})");
        vm.UpNextCancelCommand.Execute(null);
        check(!vm.ShowUpNext, "player: Up next cancelled");

        vm.IsFullscreen = true;
        vm.IsPip = true;
        check(vm.IsPip && !vm.IsFullscreen, "player: picture in picture leaves full screen");
        await vm.HandleAsync(PlayerAction.ExitFullscreen, 0);
        check(!vm.IsPip, "player: Esc leaves picture in picture");
        check(KeyMap.Resolve("L", false, true, false).Action == PlayerAction.ABLoop && KeyMap.Resolve("L", false, false, false).Action == PlayerAction.SeekForwardLong
              && KeyMap.Resolve("PageDown", false, false, false).Action == PlayerAction.NextChapter && vm.ShortcutList.Any(s => s.Keys.Contains("F1")),
              "player: new shortcuts, and the list shows them");
        check(Thumbnails.SlotOf(600, 1320) == 90 && Thumbnails.SlotLength(60) == 2 && Thumbnails.SlotOf(5, 0) == 0, "player: seek previews by time slot");
    }
}
