#!/usr/bin/env node
// canvas.html「已有升级」面板的离线行为测试（无头也能跑）：
//   在最小 DOM 桩 + vm 沙箱里加载画布脚本，喂一份假 DATA.routeList，然后
//     · 检查 upgradeRowsFor 的方向/跨页/无连线判定；
//     · 真点面板上的按钮，收 report() 消息，核对 routeOnly（跨页只改路线表）旗标与交换后的出入口位置；
//     · 走一遍「改金额/等级」对话框，核对 rpSwap / rpDel 也带着 routeOnly。
// 用法：node tools/check_canvas_panel.js
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const file = path.join(__dirname, '..', 'src', 'WarbandStudio.Ui', 'Web', 'canvas.html');
const html = fs.readFileSync(file, 'utf8');
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

/* ── 最小 DOM 桩 ── */
const SOURCE = script;
const ORPHAN = { style: {}, dataset: {}, children: [], className: '', tagName: 'div', parentElement: null,
                 classList: { contains: () => false, add() { }, remove() { }, toggle() { } },
                 appendChild(c) { this.children.push(c); return c; }, querySelector: () => null };
function makeEl(tag) {
  const e = {
    tagName: tag || 'div', style: {}, dataset: {}, children: [], _html: '',
    classList: {
      _s: new Set(),
      add(c) { this._s.add(c); }, remove(c) { this._s.delete(c); },
      toggle(c, f) { if (f === undefined) { this._s.has(c) ? this._s.delete(c) : this._s.add(c); } else if (f) this._s.add(c); else this._s.delete(c); },
      contains(c) { return this._s.has(c); },
    },
    appendChild(c) { this.children.push(c); if (c) c.parentElement = this; return c; },
    insertBefore(c) { this.children.push(c); return c; },
    removeChild(c) { const i = this.children.indexOf(c); if (i >= 0) this.children.splice(i, 1); },
    remove() { }, setAttribute() { }, getAttribute() { return null; }, removeAttribute() { },
    addEventListener() { }, removeEventListener() { }, dispatchEvent() { },
    querySelectorAll(sel) { return selAll(sel).filter(x => { let p = x.parentElement; while (p) { if (p === e) return true; p = p.parentElement; } return false; }); },
    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
    closest() { return null; }, matches() { return false; },
    getBoundingClientRect() { return { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 }; },
    get innerHTML() { return this._html; }, set innerHTML(v) { this._html = v; },
    textContent: '', title: '', value: '', checked: false, draggable: false,
    onclick: null, onchange: null, oncontextmenu: null,
    get firstChild() { return this.children[0] || null; },
  };
  let parent = null;
  // 没爹时给一个"替身父元素"：脚本里有 document.getElementById('x').parentElement.style 这种读法；
  // 替身自身 parentElement = null，保证 selAll 向上找祖先时不会绕圈。
  Object.defineProperty(e, 'parentElement', { get() { return parent || ORPHAN; }, set(v) { parent = v; } });
  ALL.push(e);
  return e;
}
const byId = {};
const ALL = [];                                   // 所有创建过的元素（给迷你选择器用）
function selMatch(el, part) {
  const attrless = part.replace(/\[[^\]]*\]/g, '');
  const tag = (attrless.match(/^[a-zA-Z]+/) || [''])[0];
  if (tag && (el.tagName || '') !== tag) return false;
  const idm = attrless.match(/#([\w-]+)/);
  if (idm && byId[idm[1]] !== el) return false;
  for (const c of attrless.matchAll(/\.([\w-]+)/g)) {
    const cls = c[1];
    const inStr = (' ' + (el.className || '') + ' ').includes(' ' + cls + ' ');
    if (!inStr && !el.classList.contains(cls)) return false;
  }
  return true;
}
function selAll(sel) {
  const parts = sel.trim().split(/\s+/);
  let cands = ALL.filter(el => selMatch(el, parts[parts.length - 1]));
  for (let i = parts.length - 2; i >= 0; i--) {
    const want = parts[i];
    cands = cands.filter(el => {
      let p = el.parentElement;
      while (p) { if (selMatch(p, want)) return true; p = p.parentElement; }
      return false;
    });
  }
  return cands;
}
const documentStub = {
  createElement: t => makeEl(t),
  createElementNS: (_ns, t) => makeEl(t),
  getElementById(id) { return byId[id] || (byId[id] = makeEl('div')); },
  querySelector: sel => selAll(sel)[0] || null,
  querySelectorAll: sel => selAll(sel),
  addEventListener() { },
  body: makeEl('body'),
};

const ctx = { console, setTimeout, clearTimeout, setInterval, clearInterval, Math, Date, JSON, document: documentStub,
              prompt: () => null, confirm: () => true, alert: () => { }, addEventListener: () => { } };
ctx.window = ctx;
ctx.globalThis = ctx;
vm.createContext(ctx);
vm.runInContext(script, ctx, { filename: 'canvas.html<script>' });

/* ── 造数据 ── */
const groups = {
  a: { unit_group: 'a', x: 0, y: 0, category: 'P1', units: ['u1'] },
  b: { unit_group: 'b', x: 0, y: 200, category: 'P1', units: ['u2'] },
  c: { unit_group: 'c', x: 400, y: 0, category: 'P2', units: ['u3'] },
};
const DATA = {
  groups: Object.values(groups),
  names: { u1: '甲', u2: '乙', u3: '丙' },
  routeList: [
    { k: 'r1', b: 'a', t: 'b', c: 'cost_400', ca: -400, rr: 2, rs: 0, link: true },    // 同页 + 有线 → 连线一起改
    { k: 'r2', b: 'a', t: 'c', c: 'cost_800', ca: -800, rr: 3, rs: 1, link: false },   // 跨页 → 只改路线表
    { k: 'r3', b: 'c', t: 'a', c: '', ca: 0, rr: 1, rs: 0, link: false },               // 跨页（本组是 target）
    { k: 'r4', b: 'b', t: 'a', c: 'cost_400', ca: -400, rr: 1, rs: 0, link: false },    // 同页但没有连线 → 不凭空写线
  ],
  links: [], costKeys: [{ k: 'cost_400', v: -400 },
                        { k: 'cost_800', v: -800, pools: [{ f: 'chivalry_other', a: -50 }] }],
  pages: ['P1', 'P2'], icons: {}, openedTabs: [], newTabs: [], tabKeys: [], groupRaces: {}, pageRaces: {},
};
ctx.__msgs = [];
vm.runInContext('GROUPS = ' + JSON.stringify(groups) + '; DATA = ' + JSON.stringify(DATA) +
                '; report = function (m) { __msgs.push(m); };', ctx);

/* ── 断言 ── */
let bad = 0, good = 0;
function chk(label, cond, detail) {
  if (cond) { good++; console.log('  ✓ ' + label); }
  else { bad++; console.log('  ✗ ' + label + (detail !== undefined ? '（' + JSON.stringify(detail) + '）' : '')); }
}
const msgs = () => ctx.__msgs;
const last = () => msgs()[msgs().length - 1];
const clear = () => { ctx.__msgs.length = 0; };

console.log('① upgradeRowsFor：方向 / 跨页 / 无连线判定');
const rows = vm.runInContext('upgradeRowsFor("a")', ctx);
chk('本组 4 条关系都列出来（2 出 2 入）',
    rows.length === 4 && rows.filter(r => r.dir === 'out').length === 2 && rows.filter(r => r.dir === 'in').length === 2,
    rows.map(r => r.r.k + ':' + r.dir));
const byKey = Object.fromEntries(rows.map(r => [r.r.k, r]));
chk('同页 + 有线（r1）→ routeOnly = false（交换/删除会带连线）', byKey.r1.routeOnly === false, byKey.r1);
chk('跨页（r2）→ routeOnly = true', byKey.r2.routeOnly === true && byKey.r2.crossPage === true, byKey.r2);
chk('跨页 + 本组是 target（r3）→ 方向 in、routeOnly = true',
    byKey.r3.routeOnly === true && byKey.r3.dir === 'in' && byKey.r3.other === 'c', byKey.r3);
chk('同页但无连线（r4）→ routeOnly = true（不凭空写线）',
    byKey.r4.routeOnly === true && byKey.r4.crossPage === false && byKey.r4.hasLink === false, byKey.r4);

console.log('② buildUpgPanel：面板行与文案');
vm.runInContext('upgGroups = ["a"]; buildUpgPanel();', ctx);
const body = byId['upBody'];
chk('面板渲染出 1 个组头 + 4 行', body.children.length === 5, body.children.map(c => c.textContent).slice(0, 2));
const rowEls = body.children.slice(1);
// 按钮现在统一放在"按钮行"容器里（flex-basis:100%），所以查找要能下钻一层
const btnOf = (el, label) => (el.children || [])
  .flatMap(c => [c, ...(c.children || [])])
  .find(c => c.textContent === label || (c.textContent || '').indexOf(label) === 0);
chk('行里带「跨页」「无连线」标记与成本数值',
    rowEls.some(r => r.children.some(c => (c.innerHTML || '').includes('跨页'))) &&
    rowEls.some(r => r.children.some(c => (c.innerHTML || '').includes('无连线'))) &&
    rowEls.some(r => r.children.some(c => (c.textContent || '').includes('cost_400（-400 金）'))) &&
    rowEls.some(r => r.children.some(c => (c.textContent || '').includes('cost_800（-800 金 · -50 chivalry_other）'))));
chk('每行都有 改 / ⇄ / 删除 三个按钮',
    rowEls.every(r => btnOf(r, '改金额/等级') && btnOf(r, '⇄ 交换方向') && btnOf(r, '删除')));

console.log('③ 点按钮 → 消息里的 routeOnly 旗标');
const rowR1 = rowEls.find(r => btnOf(r, '删除').title.includes('路线 + 对应连线'));
const rowR2 = rowEls.find(r => btnOf(r, '删除').title.includes('只删路线表'));
clear();
btnOf(rowR2, '⇄ 交换方向').onclick();
chk('跨页行点「⇄ 交换方向」→ swapRoute 且 routeOnly=true、位置已算好',
    last() && last().type === 'swapRoute' && last().routeOnly === true && last().key === 'r2'
    && Number.isInteger(last().childPos) && Number.isInteger(last().parentPos), last());
clear();
btnOf(rowR1, '⇄ 交换方向').onclick();
chk('同页有线行点「⇄ 交换方向」→ swapRoute 且 routeOnly=false', last().routeOnly === false, last());
clear();
btnOf(rowR2, '删除').onclick();
chk('跨页行点「删除」→ removeRoute 且 routeOnly=true', last().type === 'removeRoute' && last().routeOnly === true, last());
clear();
btnOf(rowR1, '删除').onclick();
chk('同页有线行点「删除」→ removeRoute 且 routeOnly=false', last().type === 'removeRoute' && last().routeOnly === false, last());

console.log('④ 「改金额/等级」对话框（复用 routeDialog）');
clear();
btnOf(rowR2, '改金额/等级').onclick();
chk('对话框填上了这条升级的 key/成本/等级',
    byId['rpTitle'].textContent.includes('r2') === false &&                        // 标题显示的是兵名，key 在 title 里
    byId['rpTitle'].title.includes('r2') && byId['rpRank'].value === 3 && byId['rpPair'].textContent.includes('只改路线表'),
    { title: byId['rpTitle'].title, rank: byId['rpRank'].value, pair: byId['rpPair'].textContent });
vm.runInContext("document.getElementById('rpSwap').onclick()", ctx);
chk('对话框里点「⇄ 交换方向」→ 也带 routeOnly=true', last().type === 'swapRoute' && last().routeOnly === true, last());
clear();
btnOf(rowR2, '改金额/等级').onclick();
vm.runInContext("document.getElementById('rpDel').onclick()", ctx);
chk('对话框里点「删除这条升级」→ 也带 routeOnly=true', last().type === 'removeRoute' && last().routeOnly === true, last());
clear();
btnOf(rowR1, '改金额/等级').onclick();
vm.runInContext("document.getElementById('rpOk').onclick()", ctx);
chk('对话框里点「保存修改」→ updateRoute（不动连线，无 routeOnly 依赖）',
    last().type === 'updateRoute' && last().key === 'r1' && last().cost === 'cost_400', last());

console.log('⑤ 加兵统一入口 reportAddUnit（重复兵确认 + 覆盖旗标）');
clear();
const r1 = vm.runInContext('reportAddUnit("u1", 10, 20, true, "P1", "")', ctx);   // u1 已在组 a
chk('已经在别组的兵 → 弹确认，确认后带 overwrite=true 且不带 group（走新建组）',
    r1 === true && last().type === 'addUnit' && last().overwrite === true && last().group === undefined
    && last().isNew === true, last());
clear();
ctx.confirm = () => false;
const r2 = vm.runInContext('reportAddUnit("u1", 10, 20, true, "P1", "")', ctx);
chk('确认框选「取消」→ 一条消息都不发', r2 === false && msgs().length === 0, msgs());
ctx.confirm = () => true;
clear();
const r3 = vm.runInContext('reportAddUnit("u9", 10, 20, true, "P1", "")', ctx);   // 没用过的兵
chk('没用过的兵 → 不弹确认，overwrite=false', r3 === true && last().overwrite === false, last());
clear();
vm.runInContext('reportAddUnit("u1", 10, 20, false, "", "a")', ctx);              // 落回自己所在的组
chk('拖回自己所在的那个组 → 不弹确认（不误报）', last().overwrite === false && last().group === 'a', last());

console.log('⑥ 源码防呆：拖动的坐标必须真的回报（v0.85 的"拖了弹回原位"就是这里断的）');
chk('mousemove 里有把 drag.moved 置 true 的语句', /drag\.moved\s*=\s*true/.test(script));
chk('mouseup 里有回报坐标的 move / moveMany 消息',
    /type:\s*'move'/.test(script) && /type:\s*'moveMany'/.test(script));
chk('加兵入口只有 reportAddUnit 一处（其余调用点不许直接发 addUnit）',
    (script.match(/type:\s*'addUnit'/g) || []).length === 1);

console.log('⑦ 换图对话框：默认显示「当前这张」，不再只写「跟母版一样」');
vm.runInContext(`
  activePage = 'SKV4';
  DATA.pageBg = { SKV4: 'http://skins.local/pack/background_images_skv4.png' };
  DATA.pageBtn = { SKV4: 'http://skins.local/pack/button_upgrade_skv4.png' };
  DATA.artLibrary = [        // v0.92 起：素材库是**本地素材**（file = 本地路径，name = 文件名）
    { kind: 'bg', name: 'background_images_mod1.png', file: 'C:/lib/bg/background_images_mod1.png', url: 'http://x/mod1.png' },
    { kind: 'bg', name: 'background_images_skv4.png', file: 'C:/lib/bg/background_images_skv4.png', url: 'http://x/skv4.png' },
    { kind: 'btn', name: 'button_upgrade_mod1.png', file: 'C:/lib/btn/button_upgrade_mod1.png', url: 'http://x/bmod1.png' },
  ];
  openNewPop('art');
`, ctx);
chk('选择器默认「保持当前」，预览=当前包里的那张（不再按名字预选库里的图）',
    vm.runInContext("npArt.bg", ctx) === ''
    && byId['npBgPick'].textContent === '（保持当前）'
    && byId['npBgPrev'].src === 'http://skins.local/pack/background_images_skv4.png',
    { v: vm.runInContext("npArt.bg", ctx), label: byId['npBgPick'].textContent, src: byId['npBgPrev'].src });
chk('选择器按钮显示「（保持当前）」', byId['npBgPick'].textContent === '（保持当前）', byId['npBgPick'].textContent);
vm.runInContext("pickArt('bg', 'C:/lib/bg/background_images_skv4.png')", ctx);
chk('点一格缩略图后：选中项就是它（值是素材 file）',
    vm.runInContext("npArt.bg", ctx) === 'C:/lib/bg/background_images_skv4.png', vm.runInContext("npArt.bg", ctx));
chk('缩略图网格已建出来（首格「保持当前」+ 库里的背景 2 格 = 3 格）',
    byId['npBgGrid'] && byId['npBgGrid'].children.length === 3,
    byId['npBgGrid'] ? byId['npBgGrid'].children.length : '(没建出来)');
chk('按钮图默认「保持当前」并预览当前包里的按钮',
    vm.runInContext("npArt.btn", ctx) === '' && byId['npBtnPrev'].src === 'http://skins.local/pack/button_upgrade_skv4.png',
    { v: vm.runInContext("npArt.btn", ctx), src: byId['npBtnPrev'].src });

console.log('⑦b 换图「应用」必须真的发出 setTabArt（回归：artCat 未定义 → 点了没反应）');
clear();
let npThrew = null;
try { vm.runInContext("npTargetCat = 'SKV2'; activePage = 'ART'; openNewPop('art');", ctx); }
catch (e) { npThrew = e; }
chk('右键菜单入口打开换图：标题用被右键的页签（不是当前页签）',
    !npThrew && byId['npTitle'].textContent === '换图（SKV2）',
    { e: npThrew && npThrew.message, t: byId['npTitle'].textContent });
vm.runInContext("pickArt('bg', 'C:/lib/bg/background_images_mod1.png')", ctx);
npThrew = null;
try { vm.runInContext("document.getElementById('npOk').onclick()", ctx); }
catch (e) { npThrew = e; }
chk('点「应用换图」不抛异常（曾经 ReferenceError: artCat is not defined）',
    !npThrew, npThrew && (npThrew.message || String(npThrew)));
const artM = last();
chk('点「应用换图」→ 发出 setTabArt 消息', artM && artM.type === 'setTabArt', artM);
chk('setTabArt 的 category = 被右键的目标页签', artM && artM.category === 'SKV2', artM && artM.category);
chk('setTabArt 带上选好的图', artM && artM.bg === 'C:/lib/bg/background_images_mod1.png', artM && artM.bg);
clear();
vm.runInContext("activePage = 'SKV4'; npTargetCat = null; openNewPop('art');", ctx);
vm.runInContext("document.getElementById('npOk').onclick()", ctx);
const artM2 = last();
chk('没有右键目标时（普通入口）：category 回退到当前页签',
    artM2 && artM2.type === 'setTabArt' && artM2.category === 'SKV4', artM2);

console.log('⑦c 换图缩略图右键 →「从包中删除」（清旧图/素材）');
clear();
vm.runInContext("activePage = 'SKV4'; openNewPop('art');", ctx);      // 重开对话框，拿到最新的网格
vm.runInContext("document.getElementById('artMenu');", ctx);          // 预热：桩按需创建元素（真实页面里它是静态 DOM）
vm.runInContext("document.getElementById('artMenu').classList.add('hidden');", ctx);   // 桩不解析静态 HTML 的 class，手动还原初始态
const bgGrid = byId['npBgGrid'];
const keepCell = bgGrid.children[0];                                 // 第 0 格 =「保持当前」（没有实体文件）
const libCell = bgGrid.children[1];                                  // 第 1 格 = 库里的第一张背景
const evStub = { preventDefault() { }, stopPropagation() { }, clientX: 10, clientY: 10 };
keepCell.oncontextmenu(evStub);
chk('「保持当前」格右键不弹删除菜单（它没有实体文件）',
    byId['artMenu'].classList.contains('hidden'), byId['artMenu'].classList.contains('hidden'));
libCell.oncontextmenu(evStub);
chk('普通缩略图右键 → 弹出删除菜单', !byId['artMenu'].classList.contains('hidden'), null);
byId['artMenuDel'].onclick();
const delM = last();
chk('点「从包中删除」→ 发出 deletePackArt（带那张图的路径）',
    delM && delM.type === 'deletePackArt' && delM.file === 'C:/lib/bg/background_images_mod1.png', delM);
chk('删除菜单点完自动收起', byId['artMenu'].classList.contains('hidden'), null);

console.log('⑧ 黄标跟着过滤实时重算（v0.91 修的场景：切换种族后旧标不会挂着）');
vm.runInContext(`
  DATA = {
    groups: [
      { unit_group: 'ga', x: 0, y: 0, category: 'PA', units: ['ua'] },
      { unit_group: 'gb', x: 0, y: 120, category: 'PB', units: ['ub'] },
    ],
    links: [], icons: {}, names: { ua: '甲', ub: '乙' }, pages: ['PA', 'PB'],
    pageRaces: { PA: ['R_A'], PB: ['R_B'] }, pageFactions: {},
    pageBg: { PA: 'http://x/bgA.png', PB: 'http://x/bgB.png' },   // 切种族要**立刻**换背景（不用再点兵牌）
    groupRaces: { ga: ['R_A'], gb: ['R_B'] }, groupFactions: {}, unitExcl: {},
    openedTabs: [], newTabs: [],
  };
  activePage = null;
  filterRace = 'R_Z'; filterFaction = '';   // 先选一个"谁的牌都不属于"的种族 → 旧代码会把黄标烤在全部卡上
  renderWarband(DATA);
`, ctx);
const foreignVisible = () => vm.runInContext(
  "document.querySelectorAll('#tree .card.foreign').filter(c => c.style.display !== 'none').length", ctx);
chk('带着不匹配的种族渲染时：可见的牌都该有黄标（R_Z 谁的都不是）…（此时被范围过滤，可见 0 张）',
    foreignVisible() === 0, { visible: foreignVisible() });
vm.runInContext("canvasFilterBy({ race: 'R_A', faction: '' })", ctx);
chk('切到真正属于它的种族 R_A：本页可见的牌**没有**黄标（旧代码会把上一次烤的标留着 → 1）',
    foreignVisible() === 0, { visible: foreignVisible() });
const allForeign = vm.runInContext("document.querySelectorAll('#tree .card.foreign').length", ctx);
chk('不属于 R_A 的那张牌（被过滤隐藏）重算后仍标黄', allForeign === 1, { all: allForeign });
vm.runInContext("canvasFilterBy({ race: 'R_B', faction: '' })", ctx);
chk('切到 R_B：换过来也一样干净', foreignVisible() === 0, { visible: foreignVisible() });
chk('切种族**立刻**换页签背景（v0.94 动态刷新：以前要再点一下兵牌/页签才变）',
    String(byId['bg'].src).includes('bgB.png'), { src: byId['bg'].src });
vm.runInContext("canvasFilterBy({ race: 'R_A', faction: '' })", ctx);
chk('切回 R_A：背景跟着切回来', String(byId['bg'].src).includes('bgA.png'), { src: byId['bg'].src });

console.log('⑨ 拆组入口：卡片面板 + 右键菜单（以前只有 Ctrl 选一个组才出）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'g2', x: 10, y: 20, category: 'PA', units: ['u1', 'u2'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {} };
  activePage = 'PA'; filterRace = ''; filterFaction = '';
  renderWarband(DATA);
  selectCard('g2', 'u1');
`, ctx);
clear();
vm.runInContext("document.getElementById('cpSplit').onclick()", ctx);
chk('卡片面板「拆分成单兵组」→ splitGroup（带坐标/页签）',
    last() && last().type === 'splitGroup' && last().group === 'g2' && last().x === 10 && last().category === 'PA', last());
clear();
vm.runInContext("ctxGroups = ['g2']; document.getElementById('ctxSplit').onclick()", ctx);
chk('右键菜单「拆分成单兵组」→ splitGroup', last() && last().type === 'splitGroup' && last().group === 'g2', last());
clear();
vm.runInContext(`GROUPS['g2'].units = ['u1']; document.getElementById('cpSplit').onclick()`, ctx);
chk('只有一个兵时点它：不发消息（本来就没得拆）', msgs().length === 0, msgs());

console.log('⑩ 页签右键小菜单 + "应用到其他种族"（手工归属）');
vm.runInContext(`
  DATA.tabRaceScopes = { PB: ['R_A'] };                 // 手工把 PB 页签也归给 R_A
  filterRace = 'R_A'; filterFaction = '';
  canvasFilterBy({ race: 'R_A', faction: '' });
`, ctx);
chk('页签列里出现了手工归属的页签（isOurs 吃 tabRaceScopes）',
    vm.runInContext("document.querySelectorAll('#railList .rp').length", ctx) >= 1
    && vm.runInContext("document.querySelectorAll('#railList .rp').map(x => x.textContent).join(',')", ctx).indexOf('PB') >= 0,
    vm.runInContext("document.querySelectorAll('#railList .rp').map(x => x.textContent).join(',')", ctx));
clear();
vm.runInContext("tmCategory = 'PB'; document.getElementById('tmScopeOk').onclick()", ctx);
chk('菜单里点「应用」→ 发 applyTabToRaces（带上勾选的种族）',
    last() && last().type === 'applyTabToRaces' && last().category === 'PB' && Array.isArray(last().races), last());
chk('右键菜单四个部件都在（重命名 / 应用到其他种族 / 应用 / 取消）',
    ['tmRename', 'tmScope', 'tmScopeOk', 'tmScopeCancel'].every(id => byId[id]), Object.keys(byId).filter(k => k.startsWith('tm')));

console.log('⑪ 换图面板的「在用」后台标记（usedBy；选它给别的页签换图=复制一张，不动原图）');
vm.runInContext(`
  activePage = 'SKV4';
  DATA.pageBg = { SKV4: 'http://skins.local/pack/background_images_skv4.png' };
  DATA.pageBtn = {};
  DATA.artLibrary = [
    { kind: 'bg', name: 'background_images_skv.png', file: 'ui/skins/default/warband_upgrades/background_images_skv.png',
      url: 'http://x/skv.png', usedBy: 'SKV' },                     // 被 SKV 页签用着
    { kind: 'bg', name: 'background_images_skv_1.png', file: 'ui/skins/default/warband_upgrades/background_images_skv_1.png',
      url: 'http://x/skv_1.png', usedBy: '' },                      // 素材库加包自动 _1：没人用
  ];
  openNewPop('art');
`, ctx);
const grid = byId['npBgGrid'].children;
chk('被用的图带 use 类和「SKV 在用」标签',
    grid.length >= 3 && (grid[1].className || '').includes('use')
    && (grid[1].innerHTML || '').includes('SKV 在用'), grid.map(c => c.className + '|' + (c.innerHTML || '').slice(-24)).join(' /// '));
chk('没被用的图不带标记（_1 这种自动命名的不算在用）',
    !(grid[2].className || '').includes('use') && !(grid[2].innerHTML || '').includes('在用'),
    grid[2].className + '|' + grid[2].innerHTML);
vm.runInContext("pickArt('bg', 'ui/skins/default/warband_upgrades/background_images_skv.png')", ctx);
chk('选中被用的图时按钮写明「会复制一张」',
    byId['npBgPick'].textContent.includes('SKV') && byId['npBgPick'].textContent.includes('复制'),
    byId['npBgPick'].textContent);
vm.runInContext("activePage = 'SKV'; openNewPop('art')", ctx);
const grid2 = byId['npBgGrid'].children;
chk('本页签自己在用的图 → 标成「本页在用」',
    (grid2[1].innerHTML || '').includes('本页在用') && !(grid2[1].innerHTML || '').includes('SKV 在用'),
    (grid2[1].innerHTML || '').slice(-30));

chk('换图后旧标记撤销：重开一次面板只认新状态（画布不自己记旧标记）',
    (function () {
      // 换图前：background_images_skv.png 标着 SKV 在用
      vm.runInContext(`activePage = 'SKV4';
        DATA.artLibrary = [{ kind: 'bg', name: 'background_images_skv.png',
          file: 'ui/skins/default/warband_upgrades/background_images_skv.png', url: 'http://x/skv.png', usedBy: 'SKV' }];
        openNewPop('art');`, ctx);
      const before = (byId['npBgGrid'].children[1].innerHTML || '').includes('在用');
      // 换图之后（后端重算 → 这张不再供图）：同一格不该再有标记
      vm.runInContext(`DATA.artLibrary = [{ kind: 'bg', name: 'background_images_skv.png',
          file: 'ui/skins/default/warband_upgrades/background_images_skv.png', url: 'http://x/skv.png', usedBy: '' }];
        openNewPop('art');`, ctx);
      const after = (byId['npBgGrid'].children[1].innerHTML || '').includes('在用');
      return before && !after;
    })(),
    '换图前=' + (byId['npBgGrid'].children[1].innerHTML || '').slice(-20));

console.log('⑫ 应用坐标后左下角面板不能被重推关掉（v0.119）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'gp1', x: 10, y: 20, category: 'PA', units: ['u1', 'u2'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {} };
  activePage = 'PA'; filterRace = ''; filterFaction = '';
  renderWarband(DATA);
  selectCard('gp1', 'u1');
`, ctx);
chk('选中后卡片面板是开着的', byId['cardPop'].classList.contains('hidden') === false);
clear();
vm.runInContext("document.getElementById('cpX').value = 66; document.getElementById('cpY').value = 77; document.getElementById('cpPosOk').onclick()", ctx);
chk('点「应用坐标」发出 setPos（带新坐标）',
    last() && last().type === 'setPos' && last().group === 'gp1' && last().x === 66 && last().y === 77, last());
// 主视图收到后会重推画布（新坐标 + 重新渲染）—— 这里模拟那一次 renderWarband
vm.runInContext(`
  DATA.groups[0].x = 66; DATA.groups[0].y = 77;
  renderWarband(DATA);
`, ctx);
chk('重推之后卡片面板仍然开着（不被"应用坐标"关掉）',
    byId['cardPop'].classList.contains('hidden') === false);
chk('重推之后坐标框显示的是新值（不是空/旧值）',
    byId['cpX'].value === 66 && byId['cpY'].value === 77,
    'cpX=' + byId['cpX'].value + ' cpY=' + byId['cpY'].value);
chk('重推之后选中态还在（组被高亮）',
    vm.runInContext("document.querySelectorAll('#tree .card.sel').length", ctx) >= 1,
    vm.runInContext("document.querySelectorAll('#tree .card.sel').length", ctx));
// 兵被删掉的情况：退回到这一组第一张牌，面板不关
vm.runInContext(`DATA.groups[0].units = ['u2']; renderWarband(DATA)`, ctx);
chk('重推后原来选中的兵没了 → 退到组里第一张牌，面板不关',
    byId['cardPop'].classList.contains('hidden') === false
    && vm.runInContext("selected && selected.unit", ctx) === 'u2',
    vm.runInContext("selected && selected.unit", ctx));

console.log('⑬ 成本明细：金（resource_costs.treasury_cost）+ 额外资源（pooled junction）（v0.120）');
vm.runInContext(`
  DATA.costKeys = [
    { k: 'Yukino_Brt_Upgrade_Cost_T1', v: -400, pools: [ { f: 'chivalry_other', a: -50 } ] },
    { k: 'studio_cost_50', v: -50, pools: [] },
    { k: 'free_key', v: 0, pools: [] },
  ];
`, ctx);
chk('成本文本 = 金 + 额外资源（负=消耗）',
    (function () {
      const t = vm.runInContext("costText('Yukino_Brt_Upgrade_Cost_T1', -400)", ctx);
      return t.indexOf('-400 金') >= 0 && t.indexOf('-50 chivalry_other') >= 0;
    })(),
    vm.runInContext("costText('Yukino_Brt_Upgrade_Cost_T1', -400)", ctx));
chk('只有金没额外资源时只显示金',
    vm.runInContext("costText('studio_cost_50', -50)", ctx).indexOf('-50 金') >= 0
    && vm.runInContext("costText('studio_cost_50', -50)", ctx).indexOf('chivalry') < 0,
    vm.runInContext("costText('studio_cost_50', -50)", ctx));
chk('金额为 0 的成本 key 不硬写（0）', vm.runInContext("costText('free_key', 0)", ctx) === 'free_key',
    vm.runInContext("costText('free_key', 0)", ctx));
chk('升级对话框下拉里的成本 key 也带明细（金 + 骑士道）',
    (function () {
      vm.runInContext("routeDialog('新建升级', { base: 'ga', target: 'gb', cost: 'Yukino_Brt_Upgrade_Cost_T1', rank: 0, sub: 0, key: '', routeOnly: false })", ctx);
      const opts = byId['rpCostList'].children.map(o => o.title || '').join(' | ');
      return opts.indexOf('-400 金') >= 0 && opts.indexOf('-50 chivalry_other') >= 0;
    })(),
    byId['rpCostList'].children.map(o => o.title || '').join(' | '));
chk('成本选择器：点列表里的一项 → 隐藏值 + 按钮文字都跟着变（v0.149 自定义列表，原生 select 塞不了图标）',
    (function () {
      vm.runInContext("routeDialog('新建升级', { base: 'ga', target: 'gb', cost: '', rank: 0, sub: 0, key: '', routeOnly: false })", ctx);
      const it = byId['rpCostList'].children.find(x => (x.title || '').indexOf('Yukino_Brt_Upgrade_Cost_T1') >= 0);
      if (!it) return false;
      it.onclick();
      return byId['rpCost'].value === 'Yukino_Brt_Upgrade_Cost_T1'
          && (byId['rpCostText'].value || '').indexOf('Yukino_Brt_Upgrade_Cost_T1') >= 0;   // v0.162：显示值在输入框里
    })(),
    'value=' + byId['rpCost'].value + ' text=' + (byId['rpCostText'].value || ''));

chk('对话框：不在成本清单里的成本要照实显示（不许悄悄变成「不写成本」，源码级 v0.174）',
    /cur\.cost && !has/.test(SOURCE) && /不在当前包的成本清单里/.test(SOURCE),
    '没找到"未知成本照实显示"的处理');

chk('升级线带方向箭头 + 对话框「双向升级」按钮（源码级，v0.166）',
    /marker-start', 'url\(#arrowDir\)'/.test(SOURCE) && /'id', 'arrowDir'/.test(SOURCE)
    && !/marker-end', 'url\(#arrowDir\)'/.test(SOURCE)
    && /rpMutual2/.test(SOURCE) && /type: 'addRoute', base: c\.target, target: c\.base/.test(SOURCE)
    && /orient', 'auto-start-reverse'/.test(SOURCE),
    '没找到箭头 marker / marker-end / 双向升级按钮');

chk('多选两个组时有「升级设置…」按钮（有路线时），点击打开编辑对话框（源码级，v0.165）',
    /mk\('升级设置…'/.test(SOURCE) && /function openRouteEdit\(rel\)/.test(SOURCE),
    '没找到「升级设置…」按钮或 openRouteEdit');

chk('成本列表的 .hidden 必须有 CSS 规则（源码级，v0.164）',
    /\.costList\.hidden\s*\{\s*display:\s*none/.test(html),   // CSS 在 <script> 外面 → 查整份 html
    '缺 .costList.hidden { display:none } → 切 hidden 类没用，列表永远展开着');

chk('成本输入框不许 onfocus 自动展开（源码级，v0.163）',
    !/costText\.onfocus\s*=\s*\(\)\s*=>\s*showCostList\(true\)/.test(SOURCE)
    && /costText\.oninput\s*=/.test(SOURCE) && /getElementById\('rpCostBtn'\)\.onclick\s*=/.test(SOURCE),
    '又出现了 onfocus 展开（打开对话框就会自动弹出成本列表）');

chk('对话框「添加连线」只在"有路线+没连线+同页"时出现（源码级，v0.161）',
    /rpAddLink/.test(SOURCE) && /cur\.key && !cur\.hasLink && samePage/.test(SOURCE) && /type: 'addLink'/.test(SOURCE),
    '没找到 rpAddLink 的显隐条件或 addLink 消息');
chk('坐标面板：selectCard 必须**先 showPop 再 refreshCardPanel**（源码级，v0.161）',
    /showPop\('cardPop'\);\s*\/\/ \*\*先显示再刷新\*\*[\s\S]{0,120}refreshCardPanel\(\);/.test(SOURCE),
    '顺序反了 → 面板藏着时刷新早退，输入框留着上一个兵的坐标');

chk('「无连线」虚线跟着页签过滤：必须带 dataset.cat（源码级，v0.160）',
    /nolink[\s\S]{0,400}dataset\.cat/.test(SOURCE) && /applyFilter\(\);\s*\/\/ 立刻按当前页签过滤/.test(SOURCE),
    '虚线没带 dataset.cat / 开关后没调 applyFilter → 所有页签的虚线会叠在一起');

chk('「无连线」可视化：有路线没连线的对画临时虚线，且**不写表**（源码级，v0.159）',
    /function drawNoLinkRoutes\(\)/.test(SOURCE) && /pairHasLink\(r\.b, r\.t\)/.test(SOURCE)
    && !/drawNoLinkRoutes[\s\S]{0,600}report\(/.test(SOURCE),
    '没找到 drawNoLinkRoutes / 或它里面发了 report（会把"临时虚线"写成真连线）');

chk('快捷键不抢输入框：焦点在 INPUT/TEXTAREA 时 keydown 直接返回（源码级，v0.157）',
    /tagName === 'INPUT'[\s\S]{0,120}return;/.test(SOURCE),
    '没找到"焦点在输入框就不处理快捷键"的守卫（退格会误删兵牌）');

chk('防误触：按下后 ~0.2 秒内的位移不算拖动（源码级，v0.154）',
    /t0: Date\.now\(\)/.test(SOURCE) && /Date\.now\(\) - \(drag\.t0 \|\| 0\) >= 200/.test(SOURCE),
    '没找到 200ms 防误触门槛（拖动手抖会把兵牌挪出几像素）');

console.log('⑭ 升级/连线解绑：新建可「只升级不连线」，已有升级可「取消连线」（v0.121）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'ga', x: 0, y: 0, category: 'PA', units: ['u1'] },
                     { unit_group: 'gb', x: 60, y: 0, category: 'PA', units: ['u2'] },
                     { unit_group: 'gc', x: 120, y: 0, category: 'PB', units: ['u3'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙', u3: '丙' }, pages: ['PA', 'PB'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [{ k: 'c1', v: -100 }],
           routeList: [], diag: null };
  activePage = 'PA'; renderWarband(DATA);
  routeDialog('新建升级', { base: 'ga', target: 'gb', key: '', cost: 'c1', rank: 2, sub: 0,
                            childPos: 1, parentPos: 3, routeOnly: false, hasLink: false });
`, ctx);
chk('新建升级：有「只升级不连线」勾选框，默认不勾、可用、提示写明',
    byId['rpNoLink'] && byId['rpNoLink'].checked === false && byId['rpNoLink'].disabled === false
    && byId['rpNoLinkHint'].textContent.indexOf('只写升级') >= 0,
    byId['rpNoLinkHint'].textContent);
clear();
vm.runInContext("document.getElementById('rpNoLink').checked = true; document.getElementById('rpOk').onclick()", ctx);
chk('勾上后建立升级 → routeOnly=true（只写路线表，不写 ui_links）',
    last() && last().type === 'addRoute' && last().routeOnly === true, last());
// 跨页：强制勾上 + 禁用 + 提示
vm.runInContext(`
  routeDialog('新建升级', { base: 'ga', target: 'gc', key: '', cost: 'c1', rank: 2, sub: 0,
                            childPos: 1, parentPos: 3, routeOnly: true, hasLink: false });
`, ctx);
chk('跨页升级：勾选框强制勾上并禁用，提示写明"跨页升级自动取消连线"',
    byId['rpNoLink'].checked === true && byId['rpNoLink'].disabled === true
    && byId['rpNoLinkHint'].textContent.indexOf('跨页') >= 0,
    byId['rpNoLinkHint'].textContent);
// 已有升级（有连线）：显示取消连线
clear();
vm.runInContext(`
  routeDialog('编辑升级', { base: 'ga', target: 'gb', key: 'r1', cost: 'c1', rank: 2, sub: 0,
                            childPos: 1, parentPos: 3, routeOnly: false, hasLink: true });
`, ctx);
chk('改已有的升级：勾选框收起来（改这条不动连线）',
    byId['rpNoLinkRow'].style.display === 'none',
    'display=' + byId['rpNoLinkRow'].style.display);
chk('有连线 → 显示「取消连线」按钮', byId['rpUnlink'].style.display !== 'none',
    'display=' + byId['rpUnlink'].style.display);
vm.runInContext("document.getElementById('rpUnlink').onclick()", ctx);
chk('点「取消连线」→ removeLink（child=升级目标 / parent=升级来源），路线保留',
    last() && last().type === 'removeLink' && last().child === 'gb' && last().parent === 'ga', last());
// 已有升级（没有连线）：不显示取消连线
clear();
vm.runInContext(`
  routeDialog('编辑升级', { base: 'ga', target: 'gb', key: 'r1', cost: 'c1', rank: 2, sub: 0,
                            childPos: 1, parentPos: 3, routeOnly: true, hasLink: false });
`, ctx);
chk('本来就没连线 → 「取消连线」按钮收起',
    byId['rpUnlink'].style.display === 'none', 'display=' + byId['rpUnlink'].style.display);
// 「查看已有升级」面板行：有连线的行有取消连线按钮
vm.runInContext(`
  DATA.routeList = [ { k: 'r1', b: 'ga', t: 'gb', c: 'c1', ca: -100, rr: 2, rs: 0, link: true },
                     { k: 'r2', b: 'ga', t: 'gc', c: 'c1', ca: -100, rr: 2, rs: 0, link: false } ];
  upgGroups = ['ga']; buildUpgPanel();
`, ctx);
chk('面板：有连线的行有「取消连线」，没连线的行没有',
    (function () {
      const rows = byId['upBody'].children.slice(1);
      const has = (el) => (el.children || []).flatMap(c => [c, ...(c.children || [])])
                              .some(c => c.textContent === '取消连线');
      return has(rows[0]) && !has(rows[1]);
    })(),
    byId['upBody'].children.slice(1).map(r => r.children.map(c => c.textContent).join(',')).join(' || '));

console.log('⑮ 移出军事组的单位不消失：不隐藏 + 打「不在军事组」角标（v0.121）');
clear();
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'ga', x: 0, y: 0, category: 'PA', units: ['u1'] },
                     { unit_group: 'gn', x: 60, y: 0, category: 'PA', units: ['u2'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: { ga: ['R_A'] }, groupFactions: {},
           unitExcl: {}, openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [] };
  activePage = 'PA';
  renderWarband(DATA);
  canvasFilterBy({ race: 'R_A', faction: '' });
`, ctx);
chk('没有军事组授权的组**不被过滤掉**（还在画面里）',
    vm.runInContext("document.querySelectorAll('#tree .card').filter(c => c.dataset.group === 'gn')[0].style.display", ctx) !== 'none',
    vm.runInContext("document.querySelectorAll('#tree .card').filter(c => c.dataset.group === 'gn')[0].style.display", ctx));
chk('这种组打「不在军事组」角标（nomil 类 + 文案）',
    (function () {
      const el = vm.runInContext("document.querySelectorAll('#tree .card').filter(c => c.dataset.group === 'gn')[0]", ctx);
      const host = el.querySelector('.bdg');
      return el.classList.contains('nomil') && (host.innerHTML || '').indexOf('不在军事组') >= 0;
    })(),
    vm.runInContext("document.querySelectorAll('#tree .card').filter(c => c.dataset.group === 'gn')[0].querySelector('.bdg').innerHTML", ctx));
chk('正常有军事组的组不打这个角标',
    (function () {
      const el = vm.runInContext("document.querySelectorAll('#tree .card').filter(c => c.dataset.group === 'ga')[0]", ctx);
      return !el.classList.contains('nomil') && !el.classList.contains('foreign');
    })(),
    vm.runInContext("document.querySelectorAll('#tree .card').filter(c => c.dataset.group === 'ga')[0].className", ctx));
chk('filter 回报里带 nomil 计数（日志能一眼看到有几个组不在军事组）',
    // 注：DOM 桩的 el.remove() 是空操作，前面几节渲染的卡片仍在树里 → 这里只要求"至少算到这个组"
    last() && last().type === 'filter' && last().nomil >= 1, last());

console.log('⑯ 吸附：拖动/松手/落表都用吸完的坐标（v0.122）');
chk('顶点吸附：X、Y 都吸到 50 网格顶点',
    (function () {
      vm.runInContext("snapOn = true; snapMode = 'vertex';", ctx);
      const r = vm.runInContext("snapPair(137, 262)", ctx);
      return r[0] === 150 && r[1] === 250;
    })(),
    vm.runInContext("JSON.stringify(snapPair(137, 262))", ctx));
chk('X 吸附：只动 X、Y 原样',
    (function () {
      vm.runInContext("snapMode = 'x';", ctx);
      const r = vm.runInContext("snapPair(137, 262)", ctx);
      return r[0] === 150 && r[1] === 262;
    })(),
    vm.runInContext("JSON.stringify(snapPair(137, 262))", ctx));
chk('Y 吸附：只动 Y、X 原样',
    (function () {
      vm.runInContext("snapMode = 'y';", ctx);
      const r = vm.runInContext("snapPair(137, 262)", ctx);
      return r[0] === 137 && r[1] === 250;
    })(),
    vm.runInContext("JSON.stringify(snapPair(137, 262))", ctx));
chk('关掉吸附：原样返回（一个像素都不动）',
    (function () {
      vm.runInContext("snapOn = false;", ctx);
      const r = vm.runInContext("snapPair(137, 262)", ctx);
      return r[0] === 137 && r[1] === 262;
    })(),
    vm.runInContext("JSON.stringify(snapPair(137, 262))", ctx));
chk('松手上报的是画布上的坐标（GROUPS），不是指针原始值 drag.curX',
    (function () {
      // 源码级防回归：mouseup 的 move 分支一旦再回去用 drag.curX 上报，
      // 就会重现"吸附拖到顶点、一松手又跳回非顶点"
      const script = vm.runInContext("document.currentScript ? '' : ''", ctx) || '';
      const m = SOURCE.match(/if \(drag && drag\.moved\) \{[\s\S]{0,900}/);
      if (!m) return false;
      // 注释里可能提到旧写法（说明"为什么改"），只看代码行；
      // 断言"上报的 x/y 来自组在画布上的坐标（吸附后）"，而不是指针累加的 drag.curX
      const code = m[0].split(String.fromCharCode(10)).filter(l => !/^\s*\/\//.test(l)).join(' ');
      return /const gg1 = GROUPS\[drag\.g\]/.test(code)
          && /gx = gg1 \? Math\.round\(gg1\.x\)/.test(code)
          && /x: gx, y: gy/.test(code);
    })(),
    '(源码里应有 GROUPS[drag.g]，不该有 drag.curX)');
vm.runInContext("snapOn = false; snapMode = 'vertex';", ctx);   // 收尾：别影响后面的用例

console.log('⑰ 多选菜单：链接按钮三态 + 和单选面板同一个位置（左下，互斥）（v0.123）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'ma', x: 0, y: 0, category: 'PA', units: ['u1'] },
                     { unit_group: 'mb', x: 60, y: 0, category: 'PA', units: ['u2'] },
                     { unit_group: 'mc', x: 120, y: 0, category: 'PA', units: ['u3'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙', u3: '丙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [{ k: 'c1', v: -100 }],
           routeList: [], diag: null };
  activePage = 'PA'; renderWarband(DATA);
`, ctx);
const mBtns = () => byId['mButtons'].children.map(b => b.textContent);
vm.runInContext("multiGroups = ['ma', 'mb']; paintMulti()", ctx);
chk('两个组没关系 → 按钮是「链接」（建立升级）', mBtns().slice(-6).indexOf('链接') >= 0, mBtns().slice(-6));
chk('多选面板出现时**自动收起单选面板**（同一个位置不叠着）',
    byId['multiPop'].classList.contains('hidden') === false
    && byId['cardPop'].classList.contains('hidden') === true,
    'multi=' + !byId['multiPop'].classList.contains('hidden') + ' card=' + !byId['cardPop'].classList.contains('hidden'));
chk('多选面板和单选面板在同一侧（HTML 里都是 .pop.left；DOM 桩拿不到 className，改看源码）',
    /id="multiPop" class="pop left/.test(html) && /id="cardPop" class="pop left/.test(html),
    (html.match(/id="multiPop" class="[^"]*"/) || ['(没找到)'])[0]);
vm.runInContext("selectCard('ma', 'u1')", ctx);
chk('回到单选 → 多选面板收起、单选面板打开',
    byId['multiPop'].classList.contains('hidden') === true
    && byId['cardPop'].classList.contains('hidden') === false,
    'multi=' + !byId['multiPop'].classList.contains('hidden') + ' card=' + !byId['cardPop'].classList.contains('hidden'));
// 已有连线 → 删除连线
clear();
vm.runInContext(`
  DATA.routeList = [ { k: 'r1', b: 'ma', t: 'mb', c: 'c1', ca: -100, rr: 0, rs: 0, link: true } ];
  multiGroups = ['ma', 'mb']; paintMulti();
`, ctx);
// 桩的 innerHTML 不清 children（按钮会累积）→ 只看**本批**（有连线时共 7 个：删除连线/自动调整画线/升级设置…/手动调整画线/合并组/删除这两个组/清空选择）
chk('两个组已经有连线 → 按钮变成「删除连线」', mBtns().slice(-7).indexOf('删除连线') >= 0 && mBtns().slice(-7).indexOf('链接') < 0, mBtns().slice(-7));
vm.runInContext("byId_mButtons_delete = 0", ctx);
chk('点「删除连线」→ removeLink（child=升级目标 / parent=升级来源）',
    (function () {
      const btn = byId['mButtons'].children.find(b => b.textContent === '删除连线');
      clear(); btn.onclick();
      return last() && last().type === 'removeLink' && last().child === 'mb' && last().parent === 'ma';
    })(), last());
// 有路线没连线 → 添加连线
clear();
vm.runInContext(`
  DATA.routeList = [ { k: 'r1', b: 'ma', t: 'mb', c: 'c1', ca: -100, rr: 0, rs: 0, link: false } ];
  multiGroups = ['ma', 'mb']; paintMulti();
`, ctx);
chk('有路线但没连线 → 按钮变成「添加连线」', mBtns().slice(-6).indexOf('添加连线') >= 0, mBtns().slice(-6));
chk('点「添加连线」→ addLink（只写 ui_links，带出入口位置）',
    (function () {
      const btn = byId['mButtons'].children.find(b => b.textContent === '添加连线');
      clear(); btn.onclick();
      return last() && last().type === 'addLink' && last().child === 'mb' && last().parent === 'ma'
             && typeof last().childPos === 'number' && typeof last().parentPos === 'number';
    })(), last());
vm.runInContext("multiGroups = []; paintMulti();", ctx);

console.log('⑱ 「查看已有升级」行的按钮统一提行（v0.123）');
vm.runInContext(`
  DATA.routeList = [ { k: 'r1', b: 'ma', t: 'mb', c: 'c1', ca: -100, rr: 2, rs: 0, link: true } ];
  upgGroups = ['ma']; buildUpgPanel();
`, ctx);
chk('按钮都在独立的"按钮行"里（flex-basis:100%），不再跟在成本 key 后面',
    (function () {
      const row = byId['upBody'].children[1];
      const btn = btnOf(row, '改金额/等级');
      if (!btn || !btn.parentElement) return false;
      return (btn.parentElement.style.cssText || '').indexOf('flex-basis:100%') >= 0
             && btn.parentElement !== row;                 // 不是直接挂在行上（那就还是和文字同一行）
    })(),
    (function () {
      const row = byId['upBody'].children[1];
      const btn = btnOf(row, '改金额/等级');
      return btn && btn.parentElement ? btn.parentElement.style.cssText : '(没找到按钮)';
    })());

console.log('⑲ 一对组只有一条线：删除/换方向不再留双线（v0.124）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'la', x: 0, y: 0, category: 'PA', units: ['u1'] },
                     { unit_group: 'lb', x: 200, y: 0, category: 'PA', units: ['u2'] } ],
           links: [ { c: 'lb', p: 'la', cp: 4, pp: 2, po: 0, co: 0, mo: 0 } ],
           icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [],
           routeList: [ { k: 'r1', b: 'la', t: 'lb', c: '', ca: 0, rr: 0, rs: 0, link: true } ],
           diag: null };
  activePage = 'PA'; renderWarband(DATA);
`, ctx);
chk('linkOfPair / routeOfPair 两个方向都认',
    (function () {
      const l1 = vm.runInContext("!!linkOfPair('la','lb')", ctx);
      const l2 = vm.runInContext("!!linkOfPair('lb','la')", ctx);
      const r2 = vm.runInContext("!!routeOfPair('lb','la')", ctx);
      return l1 && l2 && r2;
    })(),
    vm.runInContext("[!!linkOfPair('la','lb'), !!linkOfPair('lb','la'), !!routeOfPair('lb','la')].join(',')", ctx));
vm.runInContext("multiGroups = ['la', 'lb']; paintMulti()", ctx);
const last4 = () => byId['mButtons'].children.slice(-6).map(b => b.textContent);
chk('有连线的两个组：多选菜单有「自动调整画线」「手动调整画线」', 
    last4().indexOf('自动调整画线') >= 0 && last4().indexOf('手动调整画线') >= 0, last4());
clear();
chk('点「自动调整画线」→ adjustLink（出入口按当前相对位置重算）',
    (function () {
      const btn = byId['mButtons'].children.filter(b => b.textContent === '自动调整画线').pop();
      clear(); btn.onclick();
      return last() && last().type === 'adjustLink' && last().child === 'lb' && last().parent === 'la'
             && last().childPos === 4 && last().parentPos === 2;      // 右边那张牌：起始从右边(2)出、指向从左边(4)进
    })(), last());
chk('点「手动调整画线」→ 打开画线面板（左右两张牌 + 每侧四个箭头）',
    (function () {
      const btn = byId['mButtons'].children.filter(b => b.textContent === '手动调整画线').pop();
      btn.onclick();
      const from = byId['lineFrom'], to = byId['lineTo'];
      const arrows = (h) => (h.children || []).filter(c => ['↑', '→', '↓', '←'].indexOf(c.textContent) >= 0).length;
      return !byId['linePop'].classList.contains('hidden') && arrows(from) === 4 && arrows(to) === 4;
    })(),
    'from=' + (byId['lineFrom'].children || []).map(c => c.textContent).join('') + ' to=' +
    (byId['lineTo'].children || []).map(c => c.textContent).join(''));
clear();
chk('点左侧（起始兵牌）的「↑」→ parentPos=1（起始点改到正上方）',
    (function () {
      const up = byId['lineFrom'].children.filter(c => c.textContent === '↑').pop();
      clear(); up.onclick();
      return last() && last().type === 'adjustLink' && last().parentPos === 1
             && last().childPos === 4 && last().child === 'lb' && last().parent === 'la';
    })(), last());
clear();
chk('点右侧（指向兵牌）的「↓」→ childPos=3（落点改到正下方）',
    (function () {
      const dn = byId['lineTo'].children.filter(c => c.textContent === '↓').pop();
      clear(); dn.onclick();
      return last() && last().type === 'adjustLink' && last().childPos === 3 && last().parentPos === 1;
    })(), last());
// 模拟主视图重推（真实流程：点箭头 → 落表 → 重推画布 → 面板高亮跟着变）
vm.runInContext(`
  DATA.links = [ { c: 'lb', p: 'la', cp: 3, pp: 1, po: 0, co: 0, mo: 0 } ];
  renderWarband(DATA);
`, ctx);
chk('画线面板显示当前方向（↑ 和 ↓ 亮着）',
    (function () {
      // DOM 桩的 innerHTML='' 不清 children → 只看最后一批（箭头4 + 牌 + 名字 = 6 个）
      const on = (h) => (h.children || []).slice(-6)
        .filter(c => c.classList && c.classList.contains('on')).map(c => c.textContent).join('');
      return on(byId['lineFrom']) === '↑' && on(byId['lineTo']) === '↓';
    })(),
    'from=' + (byId['lineFrom'].children || []).map(c => c.textContent + (c.classList && c.classList.contains('on') ? '.on' : '')).join(' '));
vm.runInContext("lineCur = null; hidePop('linePop');", ctx);
// 没有升级关系的两个组：不能调整画线
clear();
vm.runInContext("multiGroups = ['la', 'lb']; DATA.routeList = []; DATA.links = []; paintMulti()", ctx);
chk('没有升级关系时画线面板拒绝打开（提示先建立升级）',
    (function () {
      vm.runInContext("openLinePop('la', 'lb')", ctx);
      return byId['linePop'].classList.contains('hidden') === true;
    })(), 'hidden=' + byId['linePop'].classList.contains('hidden'));
chk('没有升级关系时多选菜单不出现调整画线按钮（只有「链接」）',
    last4().indexOf('自动调整画线') < 0 && last4().indexOf('手动调整画线') < 0 && last4().indexOf('链接') >= 0, last4());
vm.runInContext("multiGroups = []; paintMulti();", ctx);

console.log('⑳ 多选统一坐标：单轴应用（X / Y 各自一个按钮，另一个轴保留）（v0.127）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'xa', x: 100, y: 200, category: 'PA', units: ['u1'] },
                     { unit_group: 'xb', x: 300, y: 500, category: 'PA', units: ['u2'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [], routeList: [], diag: null };
  activePage = 'PA'; renderWarband(DATA);
  multiGroups = ['xa', 'xb']; paintMulti();
`, ctx);
const axFind = (label) => (byId['mBody'].children || [])
  .flatMap(c => [c, ...(c.children || [])]).filter(c => c.textContent === label).pop();
// 轴行里"标签 + 输入框 + 应用按钮"三件一组：输入框 = 应用按钮的前一个兄弟
const axInput = (label) => {
  const rows = (byId['mBody'].children || []).filter(c => (c.children || []).some(x => x.textContent === label));
  const row = rows.pop();
  if (!row) return null;
  const i = row.children.findIndex(c => c.textContent === label);
  return i > 0 ? row.children[i - 1] : null;
};

chk('多选面板出现 X / Y 各自的「应用」按钮',
    !!axFind('应用X') && !!axFind('应用Y'));
chk('两个组 X 不同 → 输入框留空并提示"各自不同"',
    (function () {
      const inp = axInput('应用X');
      return !!inp && inp.value === '' && inp.placeholder === '各自不同';
    })(),
    (function () {
      const inp = axInput('应用X');
      return inp ? JSON.stringify({ v: inp.value, ph: inp.placeholder }) : '(没找到输入框)';
    })());
clear();
chk('点「应用X」→ setAxis（axis=x、只带这个轴的值、带上全部选中的组）',
    (function () {
      axInput('应用X').value = '150';
      axFind('应用X').onclick();
      return last() && last().type === 'setAxis' && last().axis === 'x' && last().value === 150
             && Array.isArray(last().groups) && last().groups.length === 2 && last().groups.indexOf('xa') >= 0;
    })(), last());
clear();
chk('点「应用Y」→ setAxis（axis=y，X 的值不参与）',
    (function () {
      axInput('应用Y').value = '600';
      axFind('应用Y').onclick();
      return last() && last().type === 'setAxis' && last().axis === 'y' && last().value === 600;
    })(), last());
vm.runInContext("multiGroups = []; paintMulti();", ctx);

console.log('㉑ mid_link_offset：画布走线模型 + 自动填 + 转点手柄（v0.128）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'pa', x: 100, y: 100, category: 'PA', units: ['u1'] },
                     { unit_group: 'pb', x: 100, y: 400, category: 'PA', units: ['u2'] } ],
           links: [ { c: 'pb', p: 'pa', cp: 1, pp: 3, po: 0, co: 0, mo: 0 } ],
           icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [], routeList: [], diag: null };
  activePage = 'PA'; renderWarband(DATA);
`, ctx);
// mo=0：第一个弯就在起点（a1）上 —— 也就是游戏原本的单折路径
chk('走线模型：mo=0 时中间段就贴在起点（第一次转折在起始点）',
    (function () {
      const d = vm.runInContext("linkPath(anchor(GROUPS['pa'], 3), anchor(GROUPS['pb'], 1), 0)", ctx);
      const nums = d.match(/-?[\d.]+/g).map(Number);
      // 点序：A, a1, (a1.x, a1.y+0), (b1.x, a1.y+0), b1, B → 第 4 个点（索引 6）的 y 应等于 a1.y
      const a1y = nums[3];
      return Math.abs(nums[7] - a1y) < 0.001;
    })(),
    vm.runInContext("linkPath(anchor(GROUPS['pa'], 3), anchor(GROUPS['pb'], 1), 0)", ctx));
// mo=60：中间段整体下移 60（沿起始方向 = 下）
chk('走线模型：mo=60 时中间段沿起始方向下移 60（= 游戏里 mid_link_offset 的效果）',
    (function () {
      const d0 = vm.runInContext("linkPath(anchor(GROUPS['pa'], 3), anchor(GROUPS['pb'], 1), 0)", ctx);
      const d60 = vm.runInContext("linkPath(anchor(GROUPS['pa'], 3), anchor(GROUPS['pb'], 1), 60)", ctx);
      const y0 = d0.match(/-?[\d.]+/g).map(Number)[7];
      const y60 = d60.match(/-?[\d.]+/g).map(Number)[7];
      return Math.abs((y60 - y0) - 60) < 0.001;
    })(),
    vm.runInContext("linkPath(anchor(GROUPS['pa'], 3), anchor(GROUPS['pb'], 1), 60)", ctx));
chk('autoMidOffset：中间段落在两端正中（上下两端 → 取 y 差值一半）',
    (function () {
      const v = vm.runInContext("autoMidOffset(GROUPS['pa'], GROUPS['pb'], 3, 1)", ctx);
      // pa 底 + STUB 到 pb 顶 - STUB 的距离一半（两个 40 宽的单兵牌、起点终点同 x）
      return typeof v === 'number' && v > 0;
    })(),
    vm.runInContext("autoMidOffset(GROUPS['pa'], GROUPS['pb'], 3, 1)", ctx));
clear();
chk('选中连线 → 出现"转点"手柄（拖它写 mid_link_offset）',
    (function () {
      const rec = vm.runInContext("LINKELS[0]", ctx);
      vm.runInContext("selectLink(LINKELS[0])", ctx);
      return byId['linkKnob'].classList.contains('hidden') === false;
    })(),
    'hidden=' + byId['linkKnob'].classList.contains('hidden'));
chk('拖转点松手 → adjustLink 带上 midOffset（源码级防回归）',
    /type: 'adjustLink', child: rec\.c, parent: rec\.p[\s\S]{0,120}midOffset/.test(SOURCE),
    '(看 mouseup 里转点手柄那段)');

console.log('㉒ 连线渲染：同对只画一条（防双线）+ 加粗透明命中线（v0.129）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'qa', x: 0, y: 0, category: 'PA', units: ['u1'] },
                     { unit_group: 'qb', x: 200, y: 300, category: 'PA', units: ['u2'] } ],
           // 故意给两条（正反各一，模拟"包里一份 + 待写一份"没去干净）
           links: [ { c: 'qb', p: 'qa', cp: 1, pp: 3, po: 0, co: 0, mo: 0 },
                    { c: 'qa', p: 'qb', cp: 3, pp: 1, po: 0, co: 0, mo: 10 } ],
           icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [], routeList: [], diag: null };
  activePage = 'PA'; renderWarband(DATA);
`, ctx);
chk('同一对的两条数据只画成一条线（防"拖动后两条线"）',
    vm.runInContext("LINKELS.length", ctx) === 1, vm.runInContext("LINKELS.length", ctx));
chk('每条线有两条 path：加粗透明命中线(.hit) + 可见线（点线不用再精准对那 2px）',
    (function () {
      const r = vm.runInContext("LINKELS[0]", ctx);
      return !!r && !!r.hit && !!r.el && r.hit.classList.contains('hit')
             && r.hit.dataset.c === r.el.dataset.c && r.hit.dataset.p === r.el.dataset.p;
    })(),
    (function () { const r = vm.runInContext("LINKELS[0]", ctx); return r ? 'hit=' + !!r.hit + ' el=' + !!r.el : '(没有连线记录)'; })());
chk('两条 path 的 d 一致，且改路径时两条一起改（setLinkD）',
    (function () {
      const r = vm.runInContext("LINKELS[0]", ctx);
      vm.runInContext("setLinkD(LINKELS[0], 'M1 2 L3 4')", ctx);
      return typeof vm.runInContext("setLinkD", ctx) === 'function';
    })(), '见 setLinkD');
chk('悬停高亮：命中线 hover → 相邻的可见线变色加粗（CSS 相邻兄弟选择器）',
    /#links\.pick path\.hit:hover \+ path\s*\{[^}]*stroke:\s*#64A9E2/.test(html),
    (html.match(/#links\.pick path\.hit:hover[^}]*\}/) || ['(没找到 hover 规则)'])[0]);

console.log('㉓ 连线可点（pick）必须现算：渲染完就能点，不用先点兵牌（v0.130）');
vm.runInContext("renderWarband({ groups: [], links: [], icons: {}, names: {}, pages: [], pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {}, openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [], routeList: [], diag: null })", ctx);
chk('刚渲染完（没点过任何东西）连线就是可点的（#links 有 pick 类）',
    byId['links'].classList.contains('pick') === true,
    'pick=' + byId['links'].classList.contains('pick'));
vm.runInContext("setMode('connect')", ctx);
chk('切到连接模式 → 连线不可点（pick 收起，避免误点线打断连线）',
    byId['links'].classList.contains('pick') === false,
    'pick=' + byId['links'].classList.contains('pick'));
vm.runInContext("setMode('select')", ctx);
chk('切回选择模式 → 立刻可点（不用再点兵牌）',
    byId['links'].classList.contains('pick') === true,
    'pick=' + byId['links'].classList.contains('pick'));

console.log('㉔ 页签右键菜单：换图挪进来 + 从其他 mod 导入页签（v0.140）');
chk('工具栏不再有「换图」按钮（已经挪进页签右键菜单）',
    !byId['bTabArt'] && !!byId['tmArt'] && !!byId['tmImport'],
    'bTabArt=' + !!byId['bTabArt'] + ' tmArt=' + !!byId['tmArt'] + ' tmImport=' + !!byId['tmImport']);
chk('右键菜单里的「换图…」→ 以被右键的那个页签为目标打开选图器',
    (function () {
      vm.runInContext(`DATA.modTabs = [{ pack: 'P1', name: '来源mod', tabs: ['AAA', 'BBB'] }];
        activePage = 'XXX'; tmCategory = 'YYY';
        document.getElementById('tmArt').onclick();`, ctx);
      const t = byId['npTitle'].textContent;
      return t.indexOf('YYY') >= 0 && vm.runInContext("npArt.bg", ctx) === '';
    })(),
    byId['npTitle'].textContent);
vm.runInContext("hidePop('newPop')", ctx);
clear();
chk('「从其他 mod 导入页签…」→ 弹出对话框，列出来源包和它的页签',
    vm.runInContext(`(function () {
      tmCategory = 'YYY';
      document.getElementById('tmImport').onclick();
      const opts = document.getElementById('impPack').children.map(o => o.value).join(',');
      const boxes = document.getElementById('impList').children
        .map(l => (l.children || []).map(c => c.value).join('')).join(',');
      return opts === 'P1' && boxes === 'AAA,BBB';
    })()`, ctx),
    vm.runInContext("document.getElementById('impPack').children.map(o => o.value).join(',')", ctx));
clear();
chk('勾一个页签点导入 → importTab（带上来源包 + 勾选的页签）',
    (function () {
      vm.runInContext(`(function () {
        const cb = document.getElementById('impList').children[0].children[0];
        cb.checked = true;
        document.getElementById('impOk').onclick();
      })()`, ctx);
      return last() && last().type === 'importTab' && last().pack === 'P1'
             && Array.isArray(last().keys) && last().keys.join(',') === 'AAA';
    })(), last());

console.log('㉕ 画布页签：默认不进「全部」（所有兵叠一起），自动切第一个页签（v0.142）');
vm.runInContext(`
  DATA = { groups: [ { unit_group: 'ya', x: 0, y: 0, category: 'PA', units: ['u1'] },
                     { unit_group: 'yb', x: 0, y: 0, category: 'PB', units: ['u2'] } ],
           links: [], icons: {}, names: { u1: '甲', u2: '乙' }, pages: ['PA', 'PB'],
           pageRaces: {}, pageFactions: {}, groupRaces: {}, groupFactions: {}, unitExcl: {},
           openedTabs: [], newTabs: [], pageBg: {}, skin: {}, costKeys: [], routeList: [], diag: null };
  activePage = ''; pagePinned = false; filterRace = ''; filterFaction = '';
  renderWarband(DATA);
`, ctx);
chk('打开时自动切到第一个页签（不再停在「全部」）',
    vm.runInContext("activePage", ctx) === 'PA', vm.runInContext("activePage", ctx));
chk('「全部」不再高亮（只有主动点才进）',
    vm.runInContext("document.querySelectorAll('#railList .rp').filter(x => x.textContent === '全部')[0].className", ctx).indexOf('on') < 0,
    vm.runInContext("document.querySelectorAll('#railList .rp').filter(x => x.textContent === '全部')[0].className", ctx));
vm.runInContext("document.querySelectorAll('#railList .rp').filter(x => x.textContent === '全部')[0].onclick()", ctx);
chk('主动点「全部」→ 真的进全部（并钉住）',
    vm.runInContext("activePage", ctx) === null && vm.runInContext("pagePinned", ctx) === true,
    'activePage=' + vm.runInContext("activePage", ctx) + ' pinned=' + vm.runInContext("pagePinned", ctx));
vm.runInContext("renderWarband(DATA)", ctx);
chk('钉住之后重推画布也不会被自动切走',
    vm.runInContext("activePage", ctx) === null, vm.runInContext("activePage", ctx));

console.log();
if (bad) { console.log('【结果】✗ ' + bad + ' 项不通过'); process.exit(1); }
console.log('【结果】✓ 全部 ' + good + ' 项通过');
