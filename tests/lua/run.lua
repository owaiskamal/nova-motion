-- SPDX-License-Identifier: GPL-3.0-or-later
-- Copyright (C) 2026 Novabit
-- Tests for engine/mpv/scripts/novamotion.lua against a mock mpv API (mp.lua, mp/*.lua).
-- Run from tests/lua:  TESTCFG=/tmp/nm-cfg.json lua5.4 run.lua ../../engine/mpv/scripts/novamotion.lua
package.path = "./?.lua;" .. package.path
dofile(arg[1])
local M = MP
local function has(l) for _,f in ipairs(M.vf) do if f.label==l then return true end end return false end
local fails=0
local function check(c,w) print((c and "PASS " or "FAIL ")..w); if not c then fails=fails+1 end end
M.events["file-loaded"]()
check(has("novamotion") and #M.vf==1, "file-loaded applies filter")
check(M.cmds[1]:find("vapoursynth=file=%%%d+%%C:/FF/runtime/mpv/../novamotion.vpy:buffered%-frames=12") ~= nil, "vpy path length-quoted: "..M.cmds[1])
M.bindings["toggle"]()
check(not has("novamotion"), "toggle off removes filter")
M.bindings["toggle"](); check(has("novamotion"), "toggle on re-adds")
M.bindings["toggle-vsr"](); check(has("novamotion") and has("novamotion-vsr"), "VSR added after engine")
M.bindings["cycle-target"](); check(M.osd[#M.osd]:find("-> display/2") ~= nil, "cycle target -> display/2: "..M.osd[#M.osd])
M.bindings["cycle-engine"](); check(M.osd[#M.osd]:find("MVTools") ~= nil, "cycle engine -> MVTools")
M.msgs["novamotion-reload"](); check(#M.vf==2, "reload keeps exactly one of each filter")
M.timers[1](); M.timers[1]()
check(LASTLOG and LASTLOG:find("ff%-stats") ~= nil and LASTLOG:find("res=1920x1080") ~= nil, "perf log line: "..tostring(LASTLOG):sub(1,60))
M.bindings["cycle-engine"](); check(M.osd[#M.osd]:find("RIFE TensorRT") ~= nil, "cycle engine mvtools -> RIFE TensorRT")
M.bindings["cycle-engine"](); check(M.osd[#M.osd]:find("RIFE ") ~= nil and not M.osd[#M.osd]:find("TensorRT"), "cycle engine -> RIFE")
local vsr; for _,c in ipairs(M.cmds) do if c:find("d3d11vpp") then vsr = c end end
check(vsr and vsr:find("scale=2.000"), "VSR scale fits 1080p into 2160p screen: "..tostring(vsr))
print(fails==0 and "ALL PASSED" or fails.." FAILED")
os.exit(fails == 0 and 0 or 1)
