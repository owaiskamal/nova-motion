local U = {}
function U.join_path(a,b) return a.."/"..b end
function U.split_path(p) return p:match("^(.*)/[^/]*$") or ".", p end
function U.file_info(p) return {} end
-- minimal json via python-free approach: store table serialized with load()
function U.format_json(t) local parts={} for k,v in pairs(t) do local val = type(v)=="string" and string.format("%q",v) or tostring(v); parts[#parts+1]=string.format("[%q]=%s",k,val) end return "return {"..table.concat(parts,",").."}" end
function U.parse_json(s) local f=load(s); return f and f() or nil end
return U
