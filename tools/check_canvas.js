#!/usr/bin/env node
// canvas.html 静态检查（改完画布必跑）：
//   ① 抽出 <script> 段 → node --check 的等价语法检查（**解析期错误不会触发页面 onerror**，
//      表现只是"画布空白/等待数据"，所以这一步不能省）；
//   ② 防呆：JS 里 getElementById('x') 用到的 id 必须在 HTML 里存在
//      （v0.27 的"整个画布没反应"就是 getElementById 拿到 null 后 .onclick 抛错，
//       它后面的顶层语句全没执行 —— 鼠标/滚轮/键盘都没注册上）。
//
// 用法：node tools/check_canvas.js [canvas.html 路径]
const fs = require('fs');
const path = require('path');

const file = process.argv[2] || path.join(__dirname, '..', 'src', 'WarbandStudio.Ui', 'Web', 'canvas.html');
const html = fs.readFileSync(file, 'utf8');

const m = html.match(/<script>([\s\S]*?)<\/script>/);
if (!m) { console.error('[✗] 没找到 <script> 段'); process.exit(2); }
const script = m[1];
const head = html.split('<script>')[0];

// ① 语法：用 vm.Script 编译（等价 node --check）
let ok = true;
try {
  new (require('vm').Script)(script, { filename: 'canvas.html<script>' });
  console.log('[✓] 脚本语法通过（' + script.split('\n').length + ' 行）');
} catch (e) {
  console.error('[✗] 语法错误：' + e.message);
  ok = false;
}

// ② id 防呆
const declared = new Set();
for (const x of head.matchAll(/\bid="([^"]+)"/g)) declared.add(x[1]);
const madeInJs = new Set();
for (const x of script.matchAll(/\bid\s*=\s*'([^']+)'/g)) madeInJs.add(x[1]);
const used = new Set();
for (const x of script.matchAll(/getElementById\('([^']+)'\)/g)) used.add(x[1]);
const bad = [...used].filter(x => !declared.has(x) && !madeInJs.has(x));
if (bad.length) { console.error('[✗] JS 引用但 DOM 里没有的 id：' + bad.join(', ')); ok = false; }
else console.log('[✓] id 检查通过（' + used.size + ' 个引用 / ' + declared.size + ' 个 DOM id）');

process.exit(ok ? 0 : 1);
