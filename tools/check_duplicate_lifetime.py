"""Lua 5.1 regressions for engine handles invalidated between callbacks.

These leases model interface lifetime, not a real native crash. Run alongside
check_duplicate_runtime.py; the actual game remains the acceptance boundary.
Usage: python check_duplicate_lifetime.py <lupa-dir> [runtime.lua]
"""
from pathlib import Path
import ast
import copy
import json
import re
import sys

if len(sys.argv)>1: sys.path.insert(0,sys.argv[1])
from lupa.lua51 import LuaRuntime

APP=Path(__file__).resolve().parents[1]
tree=ast.parse((APP/'tools/check_duplicate_runtime.py').read_text(encoding='utf-8-sig'))
def constant(name):
    node=next(n for n in tree.body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id==name for t in n.targets))
    if name=='SETUP':return ast.literal_eval(node.value)
    # CONFIG uses one comprehension solely for its five slots.
    return eval(compile(ast.Expression(node.value),'fixture','eval'),{'__builtins__':{},'enumerate':enumerate,'str':str})
SETUP,CONFIG=constant('SETUP'),constant('CONFIG')
SOURCE=Path(sys.argv[2]).read_text(encoding='utf-8-sig') if len(sys.argv)>2 else (APP/'src/WarbandStudio.Pack/Runtime/duplicate_upgrades.lua').read_text(encoding='utf-8-sig')
MODULE=re.search(r"local config = require\('([^']+)'\)",SOURCE).group(1)

LEASES=r'''
state.epoch=1;state.native_calls=0;state.handles=setmetatable({},{__mode='k'})
local targets=setmetatable({},{__mode='k'})
function lease(value)
 if type(value)~='table' then return value end
 if targets[value] then value=targets[value] end
 local epoch=state.epoch
 local proxy={__lease=true}
 targets[proxy]=value;state.handles[proxy]=true
 local function check()
  state.native_calls=state.native_calls+1
  assert(epoch==state.epoch,'stale engine interface')
 end
 return setmetatable(proxy,{
  __index=function(_,key)
   check();local v=value[key]
   if type(v)=='function' then return function(_,...)check();return lease(v(value,...))end end
   return v
  end,
  __newindex=function(_,key,v)check();value[key]=v end
 })
end
for _,name in ipairs({'get_faction','get_region','get_character_by_cqi','model','whose_turn_is_it'})do
 local original=cm[name]
 cm[name]=function(self,...)state.native_calls=state.native_calls+1;return lease(original(self,...))end
end
local original_event=event
function event(...)return lease(original_event(...))end
local original_fire=fire
-- Native event contexts are freshly issued at dispatch, even for queued receipts.
function fire(name,context)return original_fire(name,lease(context))end
function flush()
 local q=state.queue;state.queue={}
 for _,e in ipairs(q)do fire('PooledResourceChanged',lease(e))end
end
for _,name in ipairs({'faction_add_pooled_resource','pooled_resource_factor_transaction','treasury_mod'})do
 local original=cm[name]
 cm[name]=function(self,...)
  original(self,...)
  if state.invalidate_after_write then state.epoch=state.epoch+1 end
 end
end
function no_retained_handles()
 collectgarbage('collect');collectgarbage('collect')
 assert(next(state.handles)==nil,'engine interfaces retained after callback')
end
function saved_is_plain(value,seen)
 if type(value)~='table'then return end
 assert(not rawget(value,'__lease'),'native handle persisted')
 seen=seen or {};if seen[value]then return end;seen[value]=true
 for k,v in pairs(value)do saved_is_plain(k,seen);saved_is_plain(v,seen)end
end
'''

def lua_table(l,value):
    if isinstance(value,dict):return l.table_from({k:lua_table(l,v) for k,v in value.items()})
    if isinstance(value,list):return l.table_from([lua_table(l,v) for v in value])
    return value
def fixture(before=''):
    l=LuaRuntime();l.globals().cfg=lua_table(l,copy.deepcopy(CONFIG))
    l.execute("package.preload[...]=function()return cfg end",MODULE)
    l.execute(SETUP);l.execute(LEASES);l.execute(before);l.execute(SOURCE);l.execute('state.first();no_failure()')
    return l

tests=[
 ('no engine handles retained or persisted', '', 'no_retained_handles();saved_is_plain(state.saved)'),
 ('faction handles refreshed after model generation changes', '', "state.epoch=state.epoch+1;state.timer();assert(native_pay(gold));assert(gold.cash==4500);no_failure();no_retained_handles()"),
 ('province and blood handles refreshed between callbacks', "state.local_key='counts';state.ui_selected=1", "state.epoch=state.epoch+1;state.timer();assert(native_pay(counts));assert(counts.cash==4900 and counts.pools.power.balance==800 and area.pool.balance==700);no_failure();no_retained_handles()"),
 ('queued payment reacquires its original province after model rebuild', "state.local_key='counts';state.ui_selected=1", "assert(native_pay(counts,1,true));state.epoch=state.epoch+1;select_army(2);flush();assert(area.pool.balance==700 and other_area.pool.balance==50 and counts.pools.power.balance==800);no_failure();no_retained_handles()"),
 ('resource writes may invalidate all previous handles', "state.local_key='counts';state.ui_selected=1;state.invalidate_after_write=true", "assert(native_pay(counts));assert(counts.cash==4900 and area.pool.balance==700 and counts.pools.power.balance==800);state.timer();no_failure();no_retained_handles()"),
 ('replacement province resource uses its new live balance', "state.local_key='counts';state.ui_selected=1", "local old=area.pool;area.pool=resource('corpses',600);state.epoch=state.epoch+1;state.timer();assert(native_pay(counts));assert(old.balance==1000 and area.pool.balance==300);no_failure();no_retained_handles()"),
 ('temporarily unavailable faction is not queried through an old handle', '', "state.factions.gold=nil;state.epoch=state.epoch+1;state.timer();state.factions.gold=gold;state.timer();assert(native_pay(gold));assert(gold.cash==4500);no_failure();no_retained_handles()"),
 ('frontend and stopped campaign callbacks do no native work', '', "state.frontend=true;local n=state.native_calls;state.timer();fire('CharacterSelected',{});fire('FactionTurnStart',{});fire('PooledResourceChanged',{});assert(state.native_calls==n);state.frontend=false;state.stopped=true;state.timer();assert(state.native_calls==n);no_failure();no_retained_handles()"),
 ('collection pressure between repeated idle refreshes', '', "for i=1,80 do state.epoch=state.epoch+1;collectgarbage('collect');state.timer();no_retained_handles()end;assert(native_pay(gold));assert(gold.cash==4500);no_failure();saved_is_plain(state.saved)"),
]
for name,before,body in tests:
    fixture(before).execute(body)
    print('PASS:',name)

l=fixture();l.execute("assert(native_pay(gold));old_timer=state.timer;old_listener=state.listeners['studio_duplicate_test_changed'].callback")
l.execute(SOURCE)
l.execute("local n=state.native_calls;old_timer();old_listener({});assert(state.native_calls==n);state.first();assert(native_pay(gold));assert(gold.cash==4000);n=state.native_calls;old_timer();old_listener({});assert(state.native_calls==n);old_timer=nil;old_listener=nil;no_failure();no_retained_handles()")
print('PASS: old module callbacks cannot access a replacement campaign')

l=fixture();l.execute("state.first();assert(native_pay(gold));assert(gold.cash==4500);no_failure();no_retained_handles()")
print('PASS: repeated first tick cannot double-register or double-charge')

l=fixture()
l.execute(r'''
local original=gold.pooled_resource_manager
function gold:pooled_resource_manager()return {is_null_interface=function()return true end,resource=function()state.null_queried=true;error('null manager queried')end}end
state.timer();assert(not state.null_queried and gold.cash==5000)
assert(table.concat(state.logs,'\n'):find('DISABLED',1,true));no_retained_handles()
''')
print('PASS: null manager fails closed before any resource method is called')
print(f'PASS: {len(tests)+3} lifetime checks (Lua 5.1); native game crash reproduction remains required')
