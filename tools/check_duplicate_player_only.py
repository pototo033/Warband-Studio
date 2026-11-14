"""Lua 5.1 regressions: no AI budget work, including old-save and event paths.

Counts interface calls; this is not a native game performance benchmark.
Usage: python check_duplicate_player_only.py <lupa-dir> [runtime.lua]
"""
from pathlib import Path
import ast
import copy
import json
import re
import sys

if len(sys.argv)>1:
    sys.path.insert(0,sys.argv[1])
from lupa.lua51 import LuaRuntime

APP=Path(__file__).resolve().parents[1]
tree=ast.parse((APP/'tools/check_duplicate_runtime.py').read_text(encoding='utf-8-sig'))
def constant(name):
    node=next(n for n in tree.body if isinstance(n,ast.Assign) and any(isinstance(t,ast.Name) and t.id==name for t in n.targets))
    return ast.literal_eval(node.value) if name=='SETUP' else eval(compile(ast.Expression(node.value),'fixture','eval'),{'__builtins__':{},'enumerate':enumerate,'str':str})
SETUP,CONFIG=constant('SETUP'),constant('CONFIG')
SOURCE=Path(sys.argv[2]).read_text(encoding='utf-8-sig') if len(sys.argv)>2 else (APP/'src/WarbandStudio.Pack/Runtime/duplicate_upgrades.lua').read_text(encoding='utf-8-sig')
MODULE=re.search(r"local config = require\('([^']+)'\)",SOURCE).group(1)
def table(l,v):
    if isinstance(v,dict):return l.table_from({k:table(l,x) for k,x in v.items()})
    if isinstance(v,list):return l.table_from([table(l,x) for x in v])
    return v

SPIES=r'''
spy={queries={},writes={},lookups={}}
local get=cm.get_faction
function cm:get_faction(key)spy.lookups[key]=(spy.lookups[key]or 0)+1;return get(self,key)end
local write=cm.faction_add_pooled_resource
function cm:faction_add_pooled_resource(owner,...)spy.writes[owner]=(spy.writes[owner]or 0)+1;return write(self,owner,...)end
for name,f in pairs(state.factions)do
 local old=f.pooled_resource_manager
 function f:pooled_resource_manager()
  spy.queries[self:name()]=(spy.queries[self:name()]or 0)+1
  return old(self)
 end
end
function cm:model()error('must not enumerate world factions')end
function cm:whose_turn_is_it()error('must not enumerate active AI factions')end
function no_ai_work()
 for key in pairs(state.factions)do if key~=state.local_key then
  assert(not spy.queries[key]and not spy.writes[key]and not spy.lookups[key],'AI budget work: '..key)
 end end
end
function reset_spy()spy={queries={},writes={},lookups={}}end
'''
def fixture(before=''):
    l=LuaRuntime();l.globals().cfg=table(l,copy.deepcopy(CONFIG))
    l.execute('package.preload[...]=function()return cfg end',MODULE)
    l.execute(SETUP);l.execute(SPIES);l.execute(before);l.execute(SOURCE);l.execute('state.first();no_failure();no_ai_work()')
    return l

tests=[
 ('startup never queries or initializes AI pools','',"assert(spy.queries.gold>0);assert(state.saved.factions.gold and not state.saved.factions.counts);no_ai_work()"),
 ('idle timer does not inspect the active AI faction','',"for i=1,8 do state.timer()end;no_ai_work();assert(spy.queries.gold>0)"),
 ('AI turn start does no resource work','',"reset_spy();fire('FactionTurnStart',{faction=function()return counts end});assert(next(spy.queries)==nil and next(spy.writes)==nil and next(spy.lookups)==nil)"),
 ('AI resource events are rejected before resource access','',"reset_spy();fire('PooledResourceChanged',{has_faction=function()return true end,faction=function()return counts end,resource=function()error('AI resource read')end});assert(next(spy.queries)==nil and next(spy.lookups)==nil)"),
 ('AI payment receipts do not bill or refresh','',"reset_spy();local s=cfg.policies[1].slots[1];counts.pools[s.resource].pay=-10000;fire('PooledResourceChanged',event(counts,counts.pools[s.resource],-10000,cfg.pay_factor));assert(counts.cash==5000 and counts.pools.power.balance==1000 and next(spy.queries)==nil and next(spy.writes)==nil)"),
 ('unrelated player resource changes do not refresh','',"reset_spy();fire('PooledResourceChanged',event(gold,resource('unrelated',1),1,'other'));assert(next(spy.queries)==nil and next(spy.lookups)==nil)"),
 ('old AI ledger is discarded without replay','state.saved={fingerprint=cfg.fingerprint,factions={counts={paid={s0=10000},turn=0}}}',"assert(not state.saved.factions.counts);assert(counts.cash==5000 and area.pool.balance==1000);no_ai_work();assert(native_pay(gold)and gold.cash==4500)"),
 ('old AI pool balances are left untouched',"for _,s in ipairs(cfg.policies[1].slots)do counts.pools[s.resource].balance=1234 end", "state.timer();fire('FactionTurnStart',{faction=function()return counts end});for _,s in ipairs(cfg.policies[1].slots)do assert(counts.pools[s.resource].balance==1234)end;no_ai_work()"),
 ('each single-player faction retains its own original fees',"state.local_key='counts';state.ui_selected=1", "assert(native_pay(counts));assert(counts.cash==4900 and counts.pools.power.balance==800 and area.pool.balance==700);no_ai_work()"),
 ('coast player still pays native gold plus extra gold and infamy',"state.local_key='coast'", "assert(native_pay(coast));assert(coast.cash==4800 and coast.pools.infamy.balance==930);no_ai_work()"),
 ('local player turn reset and duplicate receipts stay correct','',"assert(native_pay(gold));state.turn=2;fire('FactionTurnStart',{faction=function()return gold end});for _,s in ipairs(cfg.policies[1].slots)do gold.pools[s.resource].pay=0 end;state.timer();assert(native_pay(gold));assert(gold.cash==4000);local s=cfg.policies[1].slots[1];fire('PooledResourceChanged',event(gold,gold.pools[s.resource],-10000,cfg.pay_factor));assert(gold.cash==4000);no_ai_work()"),
]
for name,before,body in tests:
    l=fixture(before);l.execute(body);l.execute('no_failure();no_ai_work()');print('PASS:',name)
print(f'PASS: {len(tests)} player-only checks (Lua 5.1); native game performance still needs user testing')
