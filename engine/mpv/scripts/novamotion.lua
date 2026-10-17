-- SPDX-License-Identifier: GPL-3.0-or-later
-- Copyright (C) 2026 Novabit
-- NOVA Motion mpv integration
--  * applies the VapourSynth interpolation filter to every file
--  * hotkeys that edit engine.json and re-apply the filter live
--  * "script-message novamotion-reload" lets the NOVA Motion app push new settings

local mp    = require "mp"
local utils = require "mp.utils"
local opts  = require "mp.options"
local msg   = require "mp.msg"

local o = {
    vpy = "",                 -- path to novamotion.vpy (default: <mpv dir>\..\novamotion.vpy)
    config = "",              -- path to engine.json   (default: env / %APPDATA%)
    buffered_frames = 12,      -- slack so RIFE bursts do not starve the display
    concurrent_frames = "auto",
}
opts.read_options(o, "novamotion")

local FILTER = "@novamotion"
local VSR    = "@novamotion-vsr"

local function config_path()
    if o.config ~= "" then return o.config end
    local env = os.getenv("NOVAMOTION_ENGINE_CONFIG")
    if env and env ~= "" then return env end
    local appdata = os.getenv("APPDATA") or "."
    return utils.join_path(utils.join_path(appdata, "NovaMotion"), "engine.json")
end

local function vpy_path()
    if o.vpy ~= "" then return o.vpy end
    return mp.command_native({"expand-path", "~~exe_dir/../novamotion.vpy"})
end

local DEFAULTS = {
    enabled = true, engine = "rife", target = "display", max_source_fps = 31.0,
    rife_model = "rife-v4.6_ensembleFalse", rife_gpu = 0, rife_threads = 2, scene_detect = true,
    mv_quality = "balanced", demo = false, rtx_vsr = false,
}

local function load()
    local cfg = {}
    for k, v in pairs(DEFAULTS) do cfg[k] = v end
    local f = io.open(config_path(), "r")
    if f then
        local parsed = utils.parse_json(f:read("*a"))
        f:close()
        if type(parsed) == "table" then
            for k, v in pairs(parsed) do cfg[k] = v end
        end
    end
    return cfg
end

local function save(cfg)
    local path = config_path()
    local dir = utils.split_path(path)
    if not utils.file_info(dir) then
        mp.command_native({name = "subprocess", playback_only = false,
                           args = {"cmd", "/c", "mkdir", dir}})
    end
    local f = io.open(path, "w")
    if not f then
        mp.osd_message("NOVA Motion: cannot write " .. path)
        return
    end
    f:write(utils.format_json(cfg))
    f:close()
end

local function has_filter(label)
    for _, f in ipairs(mp.get_property_native("vf") or {}) do
        -- mpv keeps a failed filter in the list but marks it disabled
        if f.label == label:sub(2) and f.enabled ~= false then return true end
    end
    return false
end

local function describe(cfg)
    if not cfg.enabled then return "NOVA Motion: off" end
    local s = string.format("NOVA Motion: %s -> %s", ({rife_trt = "RIFE TensorRT", rife = "RIFE", mvtools = "MVTools"})[cfg.engine] or cfg.engine,
                            tostring(cfg.target))
    if cfg.demo then s = s .. "  [demo]" end
    if cfg.rtx_vsr then s = s .. "  [RTX VSR]" end
    return s
end

local function apply(show)
    local cfg = load()
    if has_filter(FILTER) then mp.commandv("vf", "remove", FILTER) end
    if has_filter(VSR) then mp.commandv("vf", "remove", VSR) end

    if cfg.enabled then
        local spec = string.format("%s:vapoursynth=file=%%%d%%%s:buffered-frames=%d:concurrent-frames=%s",
            FILTER, #vpy_path(), vpy_path(), o.buffered_frames, o.concurrent_frames)
        mp.commandv("vf", "add", spec)
        mp.add_timeout(2.0, function()
            if load().enabled and not has_filter(FILTER) then
                mp.osd_message("NOVA Motion: engine failed to start (press ` for console log)", 4)
            end
        end)
    end

    if cfg.rtx_vsr then
        -- NVIDIA RTX Video Super Resolution via D3D11 video processor (RTX GPUs, recent drivers).
        -- Upscale only up to the screen height: scaling 1080p to 4K on a 1440p screen just burns GPU
        local vh = mp.get_property_number("height", 0)
        local dh = mp.get_property_number("display-height", 0)
        local scale = (vh > 0 and dh > 0) and math.min(2, dh / vh) or 2
        if scale > 1.05 then
            mp.commandv("vf", "add", string.format("%s:d3d11vpp=scaling-mode=nvidia:scale=%.3f", VSR, scale))
        elseif show then
            mp.osd_message("NOVA Motion: video already fills the screen, RTX VSR not needed", 3)
        end
        mp.add_timeout(1.5, function()
            if scale > 1.05 and not has_filter(VSR) then
                mp.osd_message("NOVA Motion: RTX VSR not available on this GPU/setup", 3)
            end
        end)
    end

    if show then mp.osd_message(describe(cfg)) end
end

local function update(fn)
    local cfg = load()
    fn(cfg)
    save(cfg)
    apply(true)
end

local TARGETS = {"display", "display/2", 60, 120, "x2"}

mp.add_key_binding(nil, "toggle", function() update(function(c) c.enabled = not c.enabled end) end)
mp.add_key_binding(nil, "toggle-demo", function() update(function(c) c.demo = not c.demo end) end)
mp.add_key_binding(nil, "toggle-vsr", function() update(function(c) c.rtx_vsr = not c.rtx_vsr end) end)
mp.add_key_binding(nil, "cycle-engine", function()
    local order = {rife_trt = "rife", rife = "mvtools", mvtools = "rife_trt"}
    update(function(c) c.engine = order[c.engine] or "rife" end)
end)
mp.add_key_binding(nil, "cycle-target", function()
    update(function(c)
        local idx = 1
        for i, t in ipairs(TARGETS) do if tostring(t) == tostring(c.target) then idx = i end end
        c.target = TARGETS[idx % #TARGETS + 1]
    end)
end)
mp.add_key_binding(nil, "info", function() mp.osd_message(describe(load()), 4) end)

mp.register_script_message("novamotion-reload", function() apply(true) end)

-- Re-apply per file: the script reads container_fps/display_fps fresh each time
mp.register_event("file-loaded", function() apply(false) end)

-- Performance log: one line every 5 s while playing, written to mpv.log (the app passes --log-file).
-- Lets us see whether the decoder, the interpolation engine or the renderer is the bottleneck.
local last = {}
local function num(name) return mp.get_property_number(name, -1) end
mp.add_periodic_timer(5, function()
    if mp.get_property_bool("pause") or mp.get_property("path") == nil then return end
    local cur = {
        drop = num("frame-drop-count"), dec = num("decoder-frame-drop-count"),
        late = num("vo-delayed-frame-count"), mist = num("mistimed-frame-count"),
    }
    local function d(k) return (last[k] and cur[k] >= 0) and (cur[k] - last[k]) or 0 end
    local cfg = load()
    msg.info(string.format(
        "ff-stats src=%.3f out=%.2f display=%.2f res=%dx%d hwdec=%s engine=%s target=%s model=%s vsr=%s demo=%s active=%s " ..
        "drops+%d decdrops+%d late+%d mistimed+%d cache=%.1fs buffering=%s",
        num("container-fps"), num("estimated-vf-fps"), num("display-fps"),
        num("width"), num("height"), mp.get_property("hwdec-current", "?"),
        tostring(cfg.engine), tostring(cfg.target), tostring(cfg.rife_model), tostring(cfg.rtx_vsr), tostring(cfg.demo),
        tostring(has_filter(FILTER)), d("drop"), d("dec"), d("late"), d("mist"),
        num("demuxer-cache-duration"), tostring(mp.get_property_bool("paused-for-cache"))))
    last = cur
end)
