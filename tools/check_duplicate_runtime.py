"""用 Lua 5.1 和战役接口桩验证随包的重复兵结算脚本。

用法：python check_duplicate_runtime.py [lupa 所在目录]
此脚本只验证记账；原生换兵仍需游戏验收。
"""
from pathlib import Path
import copy
import sys
if len(sys.argv) > 1:
    sys.path.insert(0, sys.argv[1])
from lupa.lua51 import LuaRuntime

SOURCE = (Path(__file__).parents[1] / 'src/WarbandStudio.Pack/Runtime/duplicate_upgrades.lua').read_text(encoding='utf-8-sig')
CONFIG = {
    'namespace': 'test', 'fingerprint': 'fixture', 'scale': 10000, 'budget_cap': 10000000,
    'sync_factor': 'sync', 'pay_factor': 'pay',
    'policies': [{
        'id': 'p', 'native_gold': 100, 'decisions': {},
        'profiles': [
            {'id': 'a', 'gold': 500, 'pools': [], 'scopes': [{'factions': ['gold'], 'races': [], 'excluded': False}]},
            {'id': 'b', 'gold': 100, 'pools': [
                {'key': 'power', 'scope': 'FACTION', 'amount': 200, 'factor': 'other', 'ai_ignored': False},
                {'key': 'corpses', 'scope': 'FACTION_PROVINCE_PERSISTENT', 'amount': 300, 'factor': 'recruitment', 'ai_ignored': True}],
             'scopes': [{'factions': ['counts'], 'races': [], 'excluded': False}]},
            {'id': 'c', 'gold': 200, 'pools': [{'key': 'infamy', 'scope': 'FACTION', 'amount': 70, 'factor': 'other', 'ai_ignored': True}],
             'scopes': [{'factions': ['coast'], 'races': [], 'excluded': False}]}],
        'slots': [{'key': key, 'id': 's'+str(i), 'resource': 'proxy'+str(i)} for i, key in enumerate(['__gold', 'power', 'corpses', 'infamy', '__gate'])]}]}

SETUP = r'''
state={logs={},listeners={},factions={},regions={},characters={},queue={},turn=1,local_key='gold',mp=false}
io=nil
function out(line)state.logs[#state.logs+1]=line end
local function forbidden()error('unsafe unit/UI access')end
UIComponent=forbidden;find_uicomponent=forbidden
common={get_context_value=function(key)assert(key=='SelectedCharacter.CQI');return state.ui_selected end}
function collection(items)return {num_items=function()return #items end,item_at=function(_,i)return items[i+1]end}end
local null={is_null_interface=function()return true end}
function resource(key,balance)
 local r={balance=balance or 0,pay=0}
 function r:is_null_interface()return false end
 function r:key()return key end
 function r:value()return self.balance end
 function r:factors()return collection({{key=function()return state.binding_keys and key..'_pay' or cfg.pay_factor end,value=function()return r.pay end}})end
 return r
end
function faction(name)
 local f={cash=5000,pools={power=resource('power',1000),infamy=resource('infamy',1000)}}
 function f:is_null_interface()return false end
 function f:name()return name end
 function f:subculture()return name=='counts' and 'vmp' or 'other' end
 function f:is_human()return name==state.local_key end
 function f:treasury()return self.cash end
 function f:pooled_resource_manager()return {is_null_interface=function()return false end,resource=function(_,key)return f.pools[key] or null end}end
 for _,p in ipairs(cfg.policies)do for _,slot in ipairs(p.slots)do f.pools[slot.resource]=resource(slot.resource)end end
 state.factions[name]=f;return f
end
gold=faction('gold');counts=faction('counts');coast=faction('coast');outsider=faction('outside')
function region(key,balance)
 local r={pool=resource('corpses',balance)}
 function r:name()return key end
 function r:is_null_interface()return false end
 function r:province()return {is_null_interface=function()return false end,key=function()return key end,has_persistent_pooled_resource_manager_for_faction=function()return true end,
 persistent_pooled_resource_manager_for_faction=function()return {is_null_interface=function()return false end,resource=function(_,k)assert(k=='corpses');return r.pool end}end}end
 state.regions[key]=r;return r
end
area=region('area',1000);other_area=region('other_area',50)
for i,key in ipairs({'area','other_area'})do state.characters[i]={is_null_interface=function()return false end,has_military_force=function()return true end,has_region=function()return true end,
 faction=function()return counts end,region=function()return state.regions[key]end,command_queue_index=function()return i end}end
core={is_ui_created=function()return true end,is_campaign=function()return not state.frontend end,get_ui_root=forbidden}
function core:add_listener(name,event,condition,callback)state.listeners[name]={event=event,condition=condition,callback=callback}end
function core:remove_listener(name)state.listeners[name]=nil end
function fire(event,context)for _,v in pairs(state.listeners)do if v.event==event and (v.condition==true or v.condition(context))then v.callback(context)end end end
function event(f,r,amount,factor)return {has_faction=function()return true end,faction=function()return f end,resource=function()return r end,amount=function()return amount end,
 factor=function()return {is_null_interface=function()return false end,key=function()return factor end}end}end
cm={modify_unit=forbidden,get_unit_by_cqi=forbidden}
function cm:add_first_tick_callback(fn)state.first=fn end
function cm:is_multiplayer()return state.mp end
function cm:is_game_running()return not state.stopped end
function cm:get_local_faction_name()return state.local_key end
function cm:get_faction(key)return state.factions[key]or null end
function cm:get_region(key)return state.regions[key]or null end
function cm:get_character_by_cqi(key)return state.characters[key]or null end
function cm:whose_turn_is_it()return collection({state.factions[state.local_key]})end
function cm:turn_number()return state.turn end
function cm:model()return {world=function()return {faction_list=function()return collection({gold,counts,coast,outsider})end}end}end
local function deep(value)if type(value)~='table'then return value end;local t={};for k,v in pairs(value)do t[k]=deep(v)end;return t end
function cm:get_saved_value()return deep(state.saved)end
function cm:set_saved_value(_,value)state.saved=deep(value)end
function cm:remove_real_callback()end
function cm:repeat_real_callback(fn,ms)assert(ms==200);state.timer=fn end
function cm:faction_add_pooled_resource(owner,key,factor,amount)
 assert(factor==cfg.sync_factor);local f=state.factions[owner];local r=f.pools[key];if not r then return end
 r.balance=r.balance+amount;assert(r.balance>=0 and r.balance<=2147483647);fire('PooledResourceChanged',event(f,r,amount,factor))
end
function cm:treasury_mod(key,amount)local f=state.factions[key];f.cash=f.cash+amount;assert(f.cash>=0)end
function cm:pooled_resource_factor_transaction(r,factor,amount)
 assert(factor==(r:key()=='corpses'and'recruitment'or'other'));r.balance=r.balance+amount;assert(r.balance>=0)
 fire('PooledResourceChanged',event(state.factions[state.local_key],r,amount,factor))
end
function select_army(cqi)fire('CharacterSelected',{character=function()return state.characters[cqi]end})end
function no_failure()local text=table.concat(state.logs,'\n');assert(not text:find('DISABLED',1,true),text)end
function native_pay(f,rate,queued,policy)
 local p=policy or cfg.policies[1];rate=rate or 1
 local tokens=math.floor(cfg.scale*rate+0.5);local common=math.floor(p.native_gold*rate+0.5)
 for _,slot in ipairs(p.slots)do if f.pools[slot.resource].balance<tokens then return false end end
 if f.cash<common then return false end
 f.cash=f.cash-common
 local events={}
 for _,slot in ipairs(p.slots)do local r=f.pools[slot.resource];r.balance=r.balance-tokens;r.pay=r.pay-tokens
  events[#events+1]=event(f,r,-tokens,state.binding_keys and slot.resource..'_pay' or cfg.pay_factor)
 end
 for _,e in ipairs(events)do if queued then state.queue[#state.queue+1]=e else fire('PooledResourceChanged',e)end end
 return true
end
function flush()local q=state.queue;state.queue={};for _,e in ipairs(q)do fire('PooledResourceChanged',e)end end
'''

def table(lua, value):
    if isinstance(value, dict):
        return lua.table_from({k: table(lua, v) for k, v in value.items()})
    if isinstance(value, list):
        return lua.table_from([table(lua, v) for v in value])
    return value

def fixture(before='', config=None):
    lua = LuaRuntime()
    lua.globals().cfg = table(lua, copy.deepcopy(config or CONFIG))
    lua.execute("package.preload['__DATA_MODULE__']=function()return cfg end")
    lua.execute(SETUP)
    lua.execute(before)
    lua.execute(SOURCE)
    lua.execute('assert(next(state.listeners)==nil);state.first()')
    return lua

tests = [
    ('no real resources minted', '', 'assert(gold.cash==5000 and counts.pools.power.balance==1000 and area.pool.balance==1000);no_failure()'),
    ('gold 500, no original resource', '', 'assert(native_pay(gold));assert(gold.cash==4500 and gold.pools.power.balance==1000);no_failure()'),
    ('multiple original resources and native gold', "state.local_key='counts';state.ui_selected=1", 'assert(native_pay(counts));assert(counts.cash==4900 and counts.pools.power.balance==800 and area.pool.balance==700);no_failure()'),
    ('coast native gold plus script delta and infamy', "state.local_key='coast'", 'assert(native_pay(coast));assert(coast.cash==4800 and coast.pools.infamy.balance==930);no_failure()'),
    ('insufficient province blocks before spending', "state.local_key='counts';state.ui_selected=2", 'assert(not native_pay(counts));assert(counts.cash==5000 and counts.pools.power.balance==1000);no_failure()'),
    ('no selected army blocks province costs', "state.local_key='counts'", 'assert(not native_pay(counts));select_army(1);assert(native_pay(counts));no_failure()'),
    ('province events without faction immediately revoke stale credit', "state.local_key='counts';state.ui_selected=1", "area.pool.balance=50;fire('PooledResourceChanged',{has_faction=function()return false end,resource=function()return area.pool end});assert(not native_pay(counts));no_failure()"),
    ('original price applies only to the current player', "state.local_key='counts';state.ui_selected=1", 'assert(native_pay(counts));assert(area.pool.balance==700 and counts.pools.power.balance==800);assert(not native_pay(gold));assert(gold.cash==5000);no_failure()'),
    ('duplicate native callbacks do not charge again', '', "assert(native_pay(gold));local s=cfg.policies[1].slots[1];fire('PooledResourceChanged',event(gold,gold.pools[s.resource],-cfg.scale,cfg.pay_factor));assert(gold.cash==4500);no_failure()"),
    ('binding IDs accepted', 'state.binding_keys=true', 'assert(native_pay(gold));assert(gold.cash==4500);no_failure()'),
    ('queued batch charged once', '', 'assert(native_pay(gold,1,true));assert(native_pay(gold,1,true));state.timer();flush();assert(gold.cash==4000);no_failure()'),
    ('pending province stays with original army', "state.local_key='counts';state.ui_selected=1", 'assert(native_pay(counts,1,true));select_army(2);assert(not native_pay(counts));flush();assert(area.pool.balance==700 and other_area.pool.balance==50);no_failure()'),
    ('outside visibility has no credit', '', 'assert(not native_pay(outsider));no_failure()'),
    ('treasury below full price blocks', 'gold.cash=400', 'assert(not native_pay(gold));assert(gold.cash==400);no_failure()'),
    ('discount total rounded once', '', 'assert(native_pay(gold,0.955));assert(gold.cash==5000-math.floor(500*0.955+0.5));no_failure()'),
    ('next turn epoch does not drop first payment', '', "assert(native_pay(gold));state.turn=2;fire('FactionTurnStart',{faction=function()return gold end});for _,s in ipairs(cfg.policies[1].slots)do gold.pools[s.resource].pay=0 end;assert(native_pay(gold));assert(gold.cash==4000);no_failure()"),
    ('AI receives no compatibility budget', '', 'assert(not native_pay(coast));assert(coast.cash==5000 and coast.pools.infamy.balance==1000);no_failure()'),
    ('multiplayer fails closed', 'state.mp=true', 'assert(next(state.listeners)==nil and not native_pay(gold));assert(gold.cash==5000)'),
]
for name, before, code in tests:
    fixture(before).execute(code)
    print('PASS:', name)

for queued in (False, True):
    lua = fixture("state.local_key='counts';state.ui_selected=1")
    lua.execute(f"assert(native_pay(counts,1,{str(queued).lower()}));state.listeners={{}};state.queue={{}};state.ui_selected=nil")
    lua.execute(SOURCE)
    lua.execute('state.first();assert(counts.cash==4900 and counts.pools.power.balance==800 and area.pool.balance==700);no_failure()')
    print('PASS: reload', 'pending' if queued else 'settled', 'multiple currencies')

config = copy.deepcopy(CONFIG)
config['policies'][0]['native_gold'] = 1
config['policies'][0]['profiles'][0]['gold'] = 2
fixture(config=config).execute('assert(native_pay(gold,0.5));assert(gold.cash==4999);no_failure()')
print('PASS: half-price common gold plus delta does not round twice')
config = copy.deepcopy(CONFIG)
config['policies'][0]['profiles'][0]['scopes'] = [{'factions': [], 'races': ['vmp'], 'excluded': False}]
config['policies'][0]['decisions'] = {'a|b': 'b'}
fixture("state.local_key='counts';state.ui_selected=1", config).execute('assert(native_pay(counts));assert(counts.cash==4900 and area.pool.balance==700);no_failure()')
print('PASS: race/faction overlap uses explicit user decision')
lua=fixture()
lua.execute(SOURCE)
lua.execute('state.first();assert(native_pay(gold));assert(gold.cash==4500);no_failure()')
print('PASS: duplicate runtime startup replaces old listeners')
lua=fixture()
lua.execute("assert(native_pay(gold));state.turn=2;fire('FactionTurnStart',{faction=function()return gold end});for _,s in ipairs(cfg.policies[1].slots)do gold.pools[s.resource].pay=0 end;state.timer();assert(native_pay(gold));assert(gold.cash==4000)")
lua.execute(SOURCE)
lua.execute('state.first();assert(gold.cash==4000);assert(native_pay(gold));assert(gold.cash==3500);no_failure()')
print('PASS: settled new-turn payment stays settled after save/reload')
print(f'PASS: {len(tests)+6} accounting checks (Lua 5.1); native replacement still needs game acceptance')
