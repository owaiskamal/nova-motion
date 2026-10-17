// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// Smoke tests for NovaMotion.Core that run without mpv: a fake mpv answers on the IPC pipe.
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovaMotion.Core;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

// --- links -------------------------------------------------------------
var yt = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=x";
var link = NovaMotionLink.Create(yt, 93);
var parsed = NovaMotionLink.Parse(link);
Check(parsed?.Target == yt && parsed?.Start == 93, $"link round-trip ({link})");
Check(NovaMotionLink.Parse("novamotion://play?url=" + Uri.EscapeDataString(@"C:\Windows\win.ini")) is null, "link rejects local file paths");
Check(NovaMotionLink.Parse("novamotion://play?url=" + Uri.EscapeDataString("file:///C:/x.mkv")) is null, "link rejects file://");
Check(NovaMotionLink.Parse("novamotion://play?url=memory%3A%2F%2Fx") is null, "link rejects mpv protocols");
Check(NovaMotionLink.Parse("https://youtube.com") is null, "non-novamotion link ignored");

// --- settings ----------------------------------------------------------
var dir = Path.Combine(Path.GetTempPath(), "ff-test-" + Guid.NewGuid().ToString("N")[..6]);
var store = new SettingsStore(dir);
var s = store.Load(new GpuInfo(new[] { "NVIDIA GeForce RTX 4070" }));
Check(s.Profiles.Count == 4 && s.Active.Name == "Balanced", "defaults for RTX GPU");
Check(s.Profiles.First(p => p.Name == "Max quality").Engine.RtxVsr, "Max quality enables RTX VSR on RTX");
var light = store.Load(new GpuInfo(new[] { "Intel(R) UHD Graphics 620" }));
Check(light.Active.Name == "Light (any GPU)", "integrated GPU defaults to light profile");
s.Active.Engine.TargetText = "120";
store.Save(s);
var engineJson = JsonNode.Parse(File.ReadAllText(store.EngineConfigPath))!;
Check(engineJson["target"]!.GetValue<double>() == 120, "engine.json target is numeric 120");
Check(engineJson["engine"]!.GetValue<string>() == "rife_trt" && engineJson["max_source_fps"] != null, "engine.json uses vpy field names; RTX gets TensorRT engine");
s.Active.Engine.TargetText = "x2"; store.Save(s);
Check(JsonNode.Parse(File.ReadAllText(store.EngineConfigPath))!["target"]!.GetValue<string>() == "x2", "engine.json target x2 string");
var reloaded = new SettingsStore(dir).Load();
Check(reloaded.Active.Engine.TargetText == "x2", "settings reload");
File.WriteAllText(store.SettingsPath, "{ broken");
Check(new SettingsStore(dir).Load().Profiles.Count == 4 && File.Exists(store.SettingsPath + ".bad"), "corrupt settings recovered");

// --- runtime layout ----------------------------------------------------
var rt = Path.Combine(dir, "pkg", "runtime");
Directory.CreateDirectory(Path.Combine(rt, "models", "rife-v4.6"));
File.WriteAllText(Path.Combine(rt, "novamotion.vpy"), "");
File.WriteAllText(Path.Combine(rt, "models", "rife-v4.6", "flownet.param"), "");
Directory.CreateDirectory(Path.Combine(dir, "pkg", "app", "bin"));
var layout = RuntimeLayout.Find(Path.Combine(dir, "pkg", "app", "bin"));
Check(layout?.Root == rt, "runtime found from nested app folder");
Check(layout!.RifeModels().SequenceEqual(new[] { "rife-v4.6" }), "RIFE models listed");

// --- mpv IPC against a fake mpv ---------------------------------------
var pipeName = "ff-fake-" + Guid.NewGuid().ToString("N")[..6];
var received = new List<JsonNode>();
var server = Task.Run(async () =>
{
    await using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    await srv.WaitForConnectionAsync();
    using var r = new StreamReader(srv, Encoding.UTF8, false, 4096, true);
    await using var w = new StreamWriter(srv, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    await w.WriteLineAsync("{\"event\":\"file-loaded\"}");
    while (await r.ReadLineAsync() is { } line)
    {
        var msg = JsonNode.Parse(line)!; received.Add(msg);
        var id = msg["request_id"]!.GetValue<int>();
        var cmd = msg["command"]!.AsArray().Select(n => n!.ToString()).ToArray();
        string reply = cmd[0] switch
        {
            "get_property" when cmd[1] == "container-fps" => $"{{\"request_id\":{id},\"error\":\"success\",\"data\":23.976}}",
            "get_property" => $"{{\"request_id\":{id},\"error\":\"property unavailable\"}}",
            _ => $"{{\"request_id\":{id},\"error\":\"success\",\"data\":null}}",
        };
        await w.WriteLineAsync(reply);
        if (cmd[0] == "quit") break;
    }
});

var player = new MpvPlayer(layout, store);
var gotEvent = new TaskCompletionSource<string>();
player.EventReceived += (ev, _) => gotEvent.TrySetResult(ev);
await player.ConnectAsync(TimeSpan.FromSeconds(5), pipeName: pipeName);
Check(await Task.WhenAny(gotEvent.Task, Task.Delay(3000)) == gotEvent.Task && gotEvent.Task.Result == "file-loaded", "IPC event delivered");
var fps = await player.CommandAsync("get_property", "container-fps");
Check(Math.Abs(fps!.GetValue<double>() - 23.976) < 1e-6, "IPC get_property result");
try { await player.CommandAsync("get_property", "nope"); Check(false, "IPC error surfaces"); }
catch (MpvException) { Check(true, "IPC error surfaces as MpvException"); }
await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => player.CommandAsync("script-message", "novamotion-reload")));
Check(received.Count(m => m["command"]![0]!.ToString() == "script-message") == 20, "20 concurrent commands, each answered");
await player.CommandAsync("quit");
await server;

var mpvArgs = player.BuildArguments("https://youtu.be/x", s);
Check(mpvArgs[^1] == "https://youtu.be/x" && mpvArgs[^2] == "--", "target passed after -- (no option injection)");
Check(mpvArgs.Any(a => a.StartsWith("--ytdl-format=bestvideo[height<=?1080]")), "ytdl format honours max height");

Directory.Delete(dir, true);
Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;
