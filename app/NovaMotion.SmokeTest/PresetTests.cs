// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// Enhance presets (built in and saved) and time estimates before an export starts.
using NovaMotion.Core;

internal static class PresetTests
{
    public static void Run(Action<bool, string> check)
    {
        var s = AppSettings.CreateDefaults();
        check(Presets.Video.Count == 6 && Presets.Photo.Count == 5 && Presets.Video.All(p => p.BuiltIn)
              && Presets.Video.Select(p => p.Id).Distinct().Count() == 6, "presets: built-in video and photo presets, unique ids");
        var anime = Presets.FindVideo(s, "anime")!;
        check(!Presets.IsEdited(anime, anime.Recipe) && !Presets.IsEdited(anime, anime.Recipe with { Enhance = anime.Recipe.Enhance with { Tuning = 0 } }),
              "presets: the preset's own choices aren't 'edited' (bookkeeping ignored)");
        var edited = anime.Recipe with { Enhance = anime.Recipe.Enhance with { Height = 1440 } };
        check(Presets.IsEdited(anime, edited), "presets: a changed size shows as edited");

        var mine = Presets.SaveVideo(s, "Anime episode", "1440p anime", edited);
        check(mine.Id == "my-video-1" && mine.Name == "Anime episode 2" && !mine.BuiltIn && Presets.FindVideo(s, "my-video-1") == mine,
              $"presets: saved as the person's own, a taken name numbered ({mine.Name})");
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), "nm-presets-" + Guid.NewGuid().ToString("N")[..6]));
        store.Save(s);
        var back = store.Load();
        check(back.MyVideoPresets.Count == 1 && back.MyVideoPresets[0].Recipe.SameAs(edited) && back.MyVideoPresets[0].Name == "Anime episode 2",
              "presets: saved presets survive a restart");
        var photo = Presets.SavePhoto(s, "Tour photos", "", new PhotoOptions("film", 2, "light", true));
        check(Presets.Rename(s, photo.Id, "  Skardu trip ") && Presets.FindPhoto(s, photo.Id)!.Name == "Skardu trip"
              && !Presets.Rename(s, "anime", "x") && !Presets.Remove(s, "anime") && Presets.Remove(s, photo.Id) && Presets.FindPhoto(s, photo.Id) is null,
              "presets: own presets rename and delete; built-in ones stay");

        // ---- estimates, counted as the engine works
        check(Estimate.Tiles(1920, 1080, Estimate.Tile) == 12 && Estimate.Tiles(960, 540, Estimate.Tile) == 4 && Estimate.Tiles(480, 360, Estimate.Tile) == 1,
              $"estimate: tiles as the engine cuts them ({Estimate.Tiles(1920, 1080, Estimate.Tile)})");
        check(Estimate.AiInputSize(1920, 1080, 1920, 1080) == (960, 540) && Estimate.AiInputSize(1920, 1080, 3840, 2160) == (1920, 1080)
              && Estimate.OutputSize(1920, 1080, 0) == (1920, 1080) && Estimate.OutputSize(1920, 1080, 2160) == (3840, 2160),
              "estimate: sizes as the engine picks them");
        var heavy = new VideoRecipe { Enhance = new EnhanceSettings { Preset = "anime", Height = 0, Clean = "light", FixCompression = true } };
        var episode = Estimate.Video(1920, 1080, 23.976, 22 * 60, heavy, "entry");
        check(episode.TotalHours is > 25 and < 70, $"estimate: a 22-minute anime episode, heaviest, GTX 1650: tens of hours ({Estimate.Words(episode)})");
        var light = Estimate.Video(1920, 1080, 23.976, 22 * 60, heavy with { Enhance = heavy.Enhance with { FixCompression = false } }, "entry");
        var rtx = Estimate.Video(1920, 1080, 23.976, 22 * 60, heavy, "");
        var smooth = Estimate.Video(1920, 1080, 23.976, 22 * 60, Presets.FindVideo(s, "smooth")!.Recipe, "entry");
        check(light < episode && rtx.TotalHours < episode.TotalHours / 3 && smooth.TotalMinutes < 120,
              $"estimate: Fix compression off, RTX and Just smooth are all faster ({Estimate.Words(light)}, {Estimate.Words(rtx)}, {Estimate.Words(smooth)})");
        var p = Estimate.Photo(1500, 2000, new PhotoOptions("film", 1, "light", true), "entry");
        check(p.TotalSeconds is > 20 and < 45, $"estimate: the 1500 x 2000 Max detail photo took 30 s ({p.TotalSeconds:0} s)");

        // ---- calibration: a slower PC than guessed corrects the estimate, gently
        var cal = new Dictionary<string, double>();
        var work = Estimate.VideoWork(1920, 1080, 23.976, 600, heavy, "entry");
        var guessed = TimeSpan.FromSeconds(work.Values.Sum());
        Estimate.Calibrate(cal, work, guessed * 2);
        var next = Estimate.Video(1920, 1080, 23.976, 600, heavy, "entry", cal);
        check(next > guessed * 1.1 && next < guessed * 2, $"estimate: learns from a slow export, a third of the way ({next.TotalMinutes:0} vs {guessed.TotalMinutes:0} min)");
        for (var i = 0; i < 12; i++) Estimate.Calibrate(cal, Estimate.VideoWork(1920, 1080, 23.976, 600, heavy, "entry", cal), guessed * 2);
        var settled = Estimate.Video(1920, 1080, 23.976, 600, heavy, "entry", cal);
        check(Math.Abs(settled.TotalSeconds / (guessed.TotalSeconds * 2) - 1) < 0.1, "estimate: settles on this PC's real speed");
        check(Estimate.Words(TimeSpan.FromMinutes(70)) == "about 1 h 10 min" && Estimate.Words(TimeSpan.FromMinutes(40)) == "about 40 min"
              && Estimate.Words(TimeSpan.FromHours(60)) == "about 3 days" && Estimate.Words(TimeSpan.FromSeconds(20)) == "under a minute",
              "estimate: in words");
    }
}
