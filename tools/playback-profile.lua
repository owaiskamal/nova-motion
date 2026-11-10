-- Part of tools\playback-profile.ps1: once a second, writes what the player is doing to a file.
local out = mp.get_opt("profile-out")
if not out or out == "" then return end
local f = io.open(out, "w")
local props = { "time-pos", "estimated-vf-fps", "container-fps", "display-fps", "estimated-display-fps",
                "frame-drop-count", "decoder-frame-drop-count", "mistimed-frame-count", "vo-delayed-frame-count",
                "vsync-ratio", "vsync-jitter", "avsync", "video-params/w", "video-params/h", "dwidth", "dheight",
                "hwdec-current", "video-out-params/w", "video-out-params/h", "demuxer-cache-duration" }
f:write("wall," .. table.concat(props, ",") .. "\n")
local t0 = mp.get_time()
mp.add_periodic_timer(1, function()
    local row = { string.format("%.1f", mp.get_time() - t0) }
    for _, p in ipairs(props) do
        local v = mp.get_property(p)
        row[#row + 1] = v and v:gsub(",", ";") or ""
    end
    f:write(table.concat(row, ",") .. "\n"); f:flush()
end)
mp.register_event("file-loaded", function()
    mp.add_timeout(4, function() f:write("#vf " .. (mp.get_property("vf") or "") .. "\n"); f:flush() end)
end)
mp.register_event("shutdown", function() f:close() end)
