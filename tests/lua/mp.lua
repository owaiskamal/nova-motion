local M = {vf = {}, cmds = {}, bindings = {}, msgs = {}, events = {}, osd = {}}
_G.MP = M
function M.get_property_native(n) return n == "vf" and M.vf or nil end
function M.commandv(...) local a = {...}; table.insert(M.cmds, table.concat(a, " "))
  if a[1]=="vf" and a[2]=="add" then local label=a[3]:match("^@([^:]+):"); table.insert(M.vf,{label=label})
  elseif a[1]=="vf" and a[2]=="remove" then for i,f in ipairs(M.vf) do if "@"..f.label==a[3] then table.remove(M.vf,i) break end end end end
function M.command_native(t) if t[1]=="expand-path" then return "C:/FF/runtime/mpv/../novamotion.vpy" end end
function M.add_key_binding(_, name, fn) M.bindings[name]=fn end
function M.register_script_message(n, fn) M.msgs[n]=fn end
function M.register_event(n, fn) M.events[n]=fn end
function M.osd_message(s) table.insert(M.osd, s) end
function M.add_timeout() end
M.timers = {}
function M.add_periodic_timer(s, fn) table.insert(M.timers, fn) end
function M.get_property_bool(n) return false end
function M.get_property_number(n, d) return ({width=1920,height=1080,["display-height"]=2160})[n] or d end
function M.get_property(n, d) return n == "path" and "x.mkv" or d end
return M
