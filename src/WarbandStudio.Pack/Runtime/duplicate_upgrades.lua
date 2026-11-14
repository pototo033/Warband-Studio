-- 原生接口负责换兵；本脚本仅为本地玩家镜像预算、结算原生已确认的资源交易。
local config = require('__DATA_MODULE__')
local namespace = 'studio_duplicate_' .. config.namespace
local save_key, timer = namespace .. '_accounting', namespace .. '_budget'
-- 模块重载时立即废弃旧闭包；注册清理在 first tick 最先执行。
local instance_key, instance = namespace .. '_instance', {}
rawset(_G, instance_key, instance)
local function active()
    return rawget(_G, instance_key)==instance and core:is_campaign() and cm:is_game_running()
end
local states, by_resource, real_resources = {}, {}, {}
local ready, started, writing, billing = false, false, false, false
local local_key, selected_cqi, saved
local scale, cap = config.scale, config.budget_cap
local function live(value)
    if value == nil or value == false then return false end
    local ok, null = pcall(function() return value:is_null_interface() end)
    return ok and not null
end
-- 原生接口只在当前操作内持有。显式保留中间管理器，避免绑定方法调用期间
-- 临时父接口被 GC；跨回调的 state/funding 中仅存 key、范围和账本数值。
local function hold(frame,value)
    if value~=nil then frame.handles[#frame.handles+1]=value end
    return value
end
local function faction_for(frame,state)
    if state.name~=local_key then return nil end
    local faction=hold(frame,cm:get_faction(state.name))
    if live(faction) and faction:is_human() then return faction end
end
local function pool_from(frame,manager,key)
    hold(frame,manager)
    if not live(manager) then return nil end
    local resource=hold(frame,manager:resource(key))
    if live(resource) then return resource end
end
local function faction_pool(frame,state,key)
    local faction=faction_for(frame,state)
    if not faction then return nil end
    local manager=hold(frame,faction:pooled_resource_manager())
    return pool_from(frame,manager,key)
end
local function treasury(frame,state)
    local faction=faction_for(frame,state)
    if not faction then error('faction unavailable: ' .. state.name) end
    return faction:treasury()
end
local function source_resource(frame,state,source)
    if not source then return nil end
    if source.scope=='faction' then return faction_pool(frame,state,source.key) end
    local faction=faction_for(frame,state)
    local region=hold(frame,cm:get_region(source.region))
    if not faction or not live(region) then return nil end
    local province=hold(frame,region:province())
    if not live(province) or province:key()~=source.province or
        not province:has_persistent_pooled_resource_manager_for_faction(faction) then return nil end
    local manager=hold(frame,province:persistent_pooled_resource_manager_for_faction(faction))
    return pool_from(frame,manager,source.key)
end
local function log(message)
    local line = '[STUDIO_DUPLICATE] ' .. config.namespace .. ' ' .. message
    out(line)
    if io and io.open then pcall(function()
        local file = io.open('warband_studio_duplicate_upgrades.log', 'a')
        if file then file:write(line .. '\n'); file:close() end
    end) end
end
local function round(value) return math.floor(value + 0.5) end
local function contains(list, key) for _, value in ipairs(list) do if value == key then return true end end return false end
local function matches(scope, faction, race)
    local f, r = contains(scope.factions, faction), contains(scope.races, race)
    if scope.excluded then
        if #scope.factions > 0 and #scope.races > 0 then return not (f and r) end
        return not (f or r)
    end
    return #scope.factions == 0 and #scope.races == 0 or f or r
end
local function profile_for(state, policy)
    local active, found = {}, {}
    for _, profile in ipairs(policy.profiles) do
        for _, scope in ipairs(profile.scopes) do if matches(scope, state.name, state.race) then
            active[#active + 1] = profile.id; found[profile.id] = profile; break
        end end
    end
    if #active == 0 then return nil end
    if #active == 1 then return found[active[1]] end
    table.sort(active)
    local winner = policy.decisions[table.concat(active, '|')]
    if not winner or not found[winner] then error('unresolved faction rule overlap: ' .. policy.id) end
    return found[winner]
end
local function spec_for(state, entry)
    local profile = profile_for(state, entry.policy)
    if not profile then return nil end
    if entry.slot.key == '__gate' then return {amount=0}, profile end
    if entry.slot.key == '__gold' then return {amount=profile.gold-entry.policy.native_gold, gold=true}, profile end
    for _, fee in ipairs(profile.pools) do if fee.key == entry.slot.key then
        if not state.human and fee.ai_ignored then return {amount=0}, profile end
        return fee, profile
    end end
    return {amount=0}, profile
end
for _, policy in ipairs(config.policies) do for _, slot in ipairs(policy.slots) do
    by_resource[slot.resource] = {policy=policy,slot=slot,id=slot.id}
end end
for _,policy in ipairs(config.policies)do for _,profile in ipairs(policy.profiles)do for _,fee in ipairs(profile.pools)do real_resources[fee.key]=true end end end
local function total(frame,resource)
    local factors = hold(frame,resource:factors())
    for i=0,factors:num_items()-1 do local f=hold(frame,factors:item_at(i))
        if f:key()==config.pay_factor or f:key()==resource:key() .. '_pay' then return f:value() end
    end
    return 0
end
local function persist(state)
    local regions={}
    for id, source in pairs(state.funding) do if source.region then regions[id]=source.region end end
    saved.factions[state.name]={paid=state.paid,regions=regions,turn=state.turn,reset=state.reset}
    cm:set_saved_value(save_key,saved)
    state.ledger_dirty=false
end
local function shadow(frame,state, entry, amount)
    local resource=faction_pool(frame,state,entry.slot.resource)
    if not live(resource) then error('missing private budget: ' .. entry.slot.resource) end
    local before=resource:value()
    if before~=amount then
        writing=true
        local ok,err=pcall(function() cm:faction_add_pooled_resource(state.name,entry.slot.resource,config.sync_factor,amount-before) end)
        writing=false
        if not ok then error(err) end
        -- 交易会调用其他 MOD 的回调；写后重新查询，不能沿用写前的资源接口。
        resource=faction_pool(frame,state,entry.slot.resource)
        if not resource or resource:value()~=amount then error('budget write mismatch') end
    end
    return resource
end
local function stop(state, reason)
    if state.disabled then return end
    state.disabled=true
    log('DISABLED faction=' .. state.name .. ' reason=' .. tostring(reason))
    if active() then
        for _,entry in pairs(by_resource) do pcall(function() shadow({handles={}},state,entry,0) end) end
    end
end
local function protected(state,label,fn)
    if state.disabled or not active() then return end
    local frame={handles={}}
    local ok,err=pcall(function()
        if not faction_for(frame,state) then return end
        fn(frame)
    end)
    if not ok then stop(state,label .. ':' .. tostring(err)) end
    frame.handles={} -- 明确将接口生命周期限制在这一操作，不写入 state 或存档。
end
local function source_for(frame,state, spec, region_key)
    if not spec or spec.amount==0 or spec.gold then return nil end
    if spec.scope=='FACTION' then
        local resource=faction_pool(frame,state,spec.key)
        if resource then return {key=spec.key,signature=state.name .. ':' .. spec.key,scope='faction'} end
        return nil
    end
    if spec.scope~='FACTION_PROVINCE_PERSISTENT' then error('unsupported resource scope') end
    local region
    if region_key then region=hold(frame,cm:get_region(region_key))
    elseif state.name==local_key and selected_cqi then
        local character=hold(frame,cm:get_character_by_cqi(selected_cqi))
        if live(character) then
            local owner=hold(frame,character:faction())
            if live(owner) and owner:name()==state.name and character:has_military_force() and character:has_region() then region=hold(frame,character:region()) end
        end
    end
    if not live(region) then return nil end
    local province=hold(frame,region:province())
    if not live(province) then return nil end
    local source={key=spec.key,region=region:name(),province=province:key(),signature=state.name .. '@' .. province:key() .. ':' .. spec.key,scope='province'}
    if source_resource(frame,state,source) then return source end
end
local function outstanding(frame,state,entry,resource)
    local current,paid=total(frame,resource),state.paid[entry.id]
    if paid==nil then paid=current;state.paid[entry.id]=current;state.ledger_dirty=true end
    if state.reset[entry.id] and current==0 then paid=0;state.paid[entry.id]=0;state.reset[entry.id]=nil;state.ledger_dirty=true end
    if current>paid then paid=0;state.paid[entry.id]=0;state.reset[entry.id]=nil;state.ledger_dirty=true end
    return math.max(0,paid-current),current
end
local function quote(state,entry,tokens)
    local spec,profile=spec_for(state,entry)
    if not spec then return nil end
    if spec.gold then return math.max(0,round(profile.gold*tokens/scale)-round(entry.policy.native_gold*tokens/scale)),spec end
    return round(spec.amount*tokens/scale),spec
end
local function settle(frame,state,entry,tokens,reason)
    local amount,spec=quote(state,entry,tokens)
    if not spec then error('native payment outside allowed page scope') end
    local source=state.funding[entry.id]
    if amount>0 then
        local resource=not spec.gold and source_resource(frame,state,source) or nil
        if spec.gold and treasury(frame,state)<amount then error('treasury below committed extra fee') end
        if not spec.gold and (not resource or resource:value()<amount) then error('resource below committed fee: ' .. tostring(spec.key)) end
        billing=true
        local ok,err=pcall(function()
            local before=spec.gold and treasury(frame,state) or resource:value()
            if spec.gold then cm:treasury_mod(state.name,-amount)
            else cm:pooled_resource_factor_transaction(resource,spec.factor,-amount) end
            local fresh=not spec.gold and source_resource(frame,state,source) or nil
            if not spec.gold and not fresh then error('settled resource unavailable') end
            local after=spec.gold and treasury(frame,state) or fresh:value()
            if after~=before-amount then error('settlement mismatch') end
            log('DEBIT faction=' .. state.name .. ' slot=' .. entry.id .. ' resource=' .. (spec.gold and 'treasury' or spec.key) .. ' amount=' .. amount .. ' before=' .. before .. ' after=' .. after .. ' province=' .. tostring(source and source.province))
        end)
        billing=false
        if not ok then error(err) end
    end
    state.paid[entry.id]=state.paid[entry.id]-tokens
    persist(state)
    log('SETTLED faction=' .. state.name .. ' slot=' .. entry.id .. ' tokens=' .. tokens .. ' amount=' .. amount .. ' reason=' .. reason .. ' accounted=' .. state.paid[entry.id])
end
local function mirror(frame,state,reason)
    local pending,reserved_gold,reserved_pool={},0,{}
    for _,entry in pairs(by_resource) do
        local resource=faction_pool(frame,state,entry.slot.resource)
        if not live(resource) then error('configured pool missing') end
        local debt=outstanding(frame,state,entry,resource);pending[entry.id]=debt
        local spec=spec_for(state,entry)
        if debt>0 and spec and spec.amount>0 then
            local fee=quote(state,entry,debt)*2+2
            if spec.gold then reserved_gold=reserved_gold+fee
            else local source=state.funding[entry.id];if not source then error('pending payment has no funded source') end
                reserved_pool[source.signature]=(reserved_pool[source.signature] or 0)+fee
            end
        end
    end
    local cash=treasury(frame,state)
    for _,entry in pairs(by_resource) do
        local spec,profile=spec_for(state,entry)
        local budget=profile and cap or 0
        local source
        if spec and spec.amount>0 then
            if spec.gold then budget=math.min(budget,math.floor(math.max(0,cash-reserved_gold)*scale/profile.gold))
            else
                local desired=source_for(frame,state,spec)
                source=pending[entry.id]>0 and state.funding[entry.id] or desired
                if pending[entry.id]>0 and spec.scope=='FACTION_PROVINCE_PERSISTENT' and (not desired or not source or desired.signature~=source.signature) then budget=0 end
                local resource=source_resource(frame,state,source)
                if not resource then budget=0
                else budget=math.min(budget,math.floor(math.max(0,resource:value()-(reserved_pool[source.signature] or 0))*scale/spec.amount)) end
            end
        end
        if pending[entry.id]==0 then
            local previous=state.funding[entry.id]
            state.funding[entry.id]=source
            if (previous and previous.region)~=(source and source.region) then persist(state) end
        end
        shadow(frame,state,entry,budget)
    end
    state.last_cash=cash
    if state.ledger_dirty then persist(state) end
end
local function create_state(faction,initialise)
    if not active() or not live(faction) then return nil end
    local name=faction:name()
    if name~=local_key or not faction:is_human() then return nil end
    if states[name] then return states[name] end
    local _,first=next(by_resource)
    if not first then return nil end
    local frame={handles={faction}}
    local resource=faction_pool(frame,{name=name},first.slot.resource)
    if not live(resource) then
        if not initialise then return nil end
        cm:faction_add_pooled_resource(name,first.slot.resource,config.sync_factor,0)
        resource=faction_pool(frame,{name=name},first.slot.resource)
        if not live(resource) then log('INACTIVE faction=' .. name .. ' reason=no_budget_pools');return nil end
    end
    faction=faction_for(frame,{name=name})
    if not faction then return nil end
    local state={name=name,race=faction:subculture(),human=faction:is_human(),paid={},funding={},reset={},turn=cm:turn_number()}
    states[name]=state
    local previous=saved.factions[name]
    protected(state,'startup',function(operation)
        for _,entry in pairs(by_resource) do
            local r=faction_pool(operation,state,entry.slot.resource)
            if not live(r) then cm:faction_add_pooled_resource(name,entry.slot.resource,config.sync_factor,0);r=faction_pool(operation,state,entry.slot.resource) end
            if not live(r) then error('cannot initialise cost budget') end
            state.paid[entry.id]=previous and previous.paid and previous.paid[entry.id] or total(operation,r)
            if previous and (previous.turn~=state.turn or previous.reset and previous.reset[entry.id]) then state.reset[entry.id]=true end
            state.funding[entry.id]=source_for(operation,state,spec_for(state,entry),previous and previous.regions and previous.regions[entry.id])
            local debt=outstanding(operation,state,entry,r)
            if debt>0 then settle(operation,state,entry,debt,'load_recovery') end
        end
        persist(state);mirror(operation,state,'startup')
        log('READY faction=' .. name .. ' policies=' .. #config.policies .. ' fingerprint=' .. config.fingerprint .. ' runtime=interface-lifetime-1 refresh=local_player_only')
    end)
    return state
end
local function refresh_local(reason)
    if not active() then return end
    local state=states[local_key] or create_state(cm:get_faction(local_key),true)
    if state and not selected_cqi and core:is_ui_created() then
        local ok,cqi=pcall(function() return common.get_context_value('SelectedCharacter.CQI') end)
        if ok and type(cqi)=='number' and cqi>0 then selected_cqi=cqi end
    end
    if state then protected(state,reason,function(frame) mirror(frame,state,reason) end) end
end
cm:add_first_tick_callback(function()
    if started or not active() then return end;started=true
    cm:remove_real_callback(timer)
    for _,suffix in ipairs({'_changed','_character','_turn','_deselected','_moved'})do core:remove_listener(namespace .. suffix)end
    if cm:is_multiplayer() then log('INACTIVE reason=multiplayer_not_validated');return end
    local_key=cm:get_local_faction_name(true)
    saved=cm:get_saved_value(save_key)
    if type(saved)~='table' or saved.fingerprint~=config.fingerprint or type(saved.factions)~='table' then saved={fingerprint=config.fingerprint,factions={}} end
    -- 不遍历世界派系，也不读取/清零旧AI预算；只迁移本地玩家自己的未结账本。
    saved.factions={[local_key]=saved.factions[local_key]}
    create_state(cm:get_faction(local_key),true)
    core:add_listener(namespace .. '_changed','PooledResourceChanged',true,function(context)
        if not active() or not ready or writing or billing then return end
        -- 行省资源变化可能不携带派系上下文，也要立即刷新当前军队预算。
        if not context:has_faction() then
            local resource=context:resource();if not live(resource) then return end
            if real_resources[resource:key()] then refresh_local('province_resource') end
            return
        end
        local faction=context:faction();if not live(faction) then return end
        if faction:name()~=local_key or not faction:is_human() then return end
        local state=states[local_key]
        if not state then return end
        local resource=context:resource();if not live(resource) then return end
        local key=resource:key()
        local entry=by_resource[key]
        if not entry and not real_resources[key] then return end
        protected(state,'resource',function(frame)
            hold(frame,context);hold(frame,resource);hold(frame,faction)
            if entry then
                local factor=hold(frame,context:factor())
                if not live(factor) or factor:key()~=config.pay_factor and factor:key()~=entry.slot.resource .. '_pay' then return end
                local amount=context:amount()
                if amount>0 then error('unexpected positive native payment') end
                if amount==0 then return end
                if state.reset[entry.id] then state.paid[entry.id]=0;state.reset[entry.id]=nil end
                local debt=outstanding(frame,state,entry,resource)
                local due=math.min(-amount,debt)
                if due>0 then settle(frame,state,entry,due,'native_payment') end
            end
            mirror(frame,state,'resource')
        end)
    end,true)
    core:add_listener(namespace .. '_character','CharacterSelected',true,function(context)
        if not active() then return end
        local character=context:character()
        local owner=live(character) and character:faction() or nil
        if live(owner) and owner:name()==local_key then
            selected_cqi=character:command_queue_index();refresh_local('character')
        end
    end,true)
    core:add_listener(namespace .. '_turn','FactionTurnStart',true,function(context)
        if not active() then return end
        local faction=context:faction()
        if not live(faction) or faction:name()~=local_key or not faction:is_human() then return end
        local state=states[local_key] or create_state(faction,true)
        if not state then return end
        protected(state,'turn',function(frame)
            state.turn=cm:turn_number();for _,entry in pairs(by_resource) do state.reset[entry.id]=true end
            mirror(frame,state,'turn')
            persist(state)
        end)
    end,true)
    core:add_listener(namespace .. '_deselected','CharacterDeselected',true,function() if active() then selected_cqi=nil;refresh_local('deselected') end end,true)
    core:add_listener(namespace .. '_moved','CharacterFinishedMovingEvent',true,function() refresh_local('moved') end,true)
    ready=true
    cm:repeat_real_callback(function()
        if not active() then return end
        refresh_local('timer')
    end,200,timer)
    refresh_local('ready')
end)
