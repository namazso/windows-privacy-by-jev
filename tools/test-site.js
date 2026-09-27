// Headless functional test of site/index.html using jsdom.
const path = require('path');
const { JSDOM } = require(path.join(__dirname, 'node_modules', 'jsdom'));

function fail(msg) { console.error('FAIL ' + msg); process.exitCode = 1; }
function ok(cond, msg) { if (cond) console.log('ok   ' + msg); else fail(msg); }

(async () => {
  const dom = await JSDOM.fromFile(path.join(__dirname, '..', 'site', 'index.html'), {
    runScripts: 'dangerously', resources: 'usable', pretendToBeVisual: true,
  });
  const w = dom.window;
  await new Promise(res => w.addEventListener('load', res));
  await new Promise(res => setTimeout(res, 100));
  const d = w.document;
  const app = w.GPOApp;
  const rows = w.GPO_DATA.policies;
  ok(app && rows.length > 3000, 'data loaded: ' + rows.length + ' policies');
  const body = d.getElementById('body');
  ok(body.querySelectorAll('tr').length === 100, 'first page renders 100 rows');
  ok(d.getElementById('count').textContent.includes('of ' + rows.length), 'count line shows total: ' + d.getElementById('count').textContent);
  ok(d.querySelectorAll('#headRow th').length === 1 + app.COLUMNS.filter(c => !c.hidden).length, 'header has expander + visible columns');

  // sort by name asc then desc
  const nameBtn = d.querySelector('#headRow th[data-col="name"] .sort');
  nameBtn.click();
  let vis = app.compute();
  ok(vis[0].name.localeCompare(vis[vis.length - 1].name, undefined, { sensitivity: 'base' }) <= 0, 'sort name asc: ' + vis[0].name + ' … ' + vis[vis.length - 1].name);
  nameBtn.click();
  vis = app.compute();
  ok(vis[0].name.localeCompare(vis[vis.length - 1].name, undefined, { sensitivity: 'base' }) >= 0, 'sort name desc');
  ok(d.querySelector('#headRow th[data-col="name"]').getAttribute('aria-sort') === 'descending', 'aria-sort updated');

  // numeric sort puts nulls last
  d.querySelector('#headRow th[data-col="privacy"] .sort').click();
  vis = app.compute();
  const scored = vis.filter(r => r._privacy != null);
  ok(vis.slice(0, scored.length).every(r => r._privacy != null) && vis.slice(scored.length).every(r => r._privacy == null), 'privacy sort keeps unscored rows last');
  ok(scored.length < 2 || scored[0]._privacy >= scored[scored.length - 1]._privacy, 'first click on a percent column sorts descending');
  d.querySelector('#headRow th[data-col="privacy"] .sort').click();
  vis = app.compute();
  ok(app.state.sort.col === 'privacy' && app.state.sort.dir === 1 && vis[0]._privacy <= vis[1]._privacy, 'second click reverses to ascending: top is ' + vis[0]._privacy + '%');
  d.querySelector('#headRow th[data-col="privacy"] .sort').click();
  ok(app.state.sort.dir === -1, 'third click flips back to descending');

  // text filter
  const nameFilter = d.querySelector('#filterRow th.col-name input');
  nameFilter.value = 'diagnostic data';
  nameFilter.dispatchEvent(new w.Event('input', { bubbles: true }));
  await new Promise(res => setTimeout(res, 250));
  vis = app.compute();
  ok(vis.length > 0 && vis.every(r => r.name.toLowerCase().includes('diagnostic data')), 'name filter: ' + vis.length + ' rows');
  ok(body.querySelectorAll('tr:not(.detail)').length === Math.min(100, vis.length), 'body re-rendered after filter');
  ok(w.location.hash.includes('f_name=diagnostic'), 'hash records filter: ' + w.location.hash);

  // select filter
  const classSel = d.querySelector('#filterRow th.col-class select');
  classSel.value = 'User';
  classSel.dispatchEvent(new w.Event('change', { bubbles: true }));
  vis = app.compute();
  ok(vis.every(r => r.class === 'User'), 'class filter combined with name filter: ' + vis.length + ' rows');

  // reset
  d.getElementById('reset').click();
  vis = app.compute();
  ok(vis.length === rows.length, 'reset restores all rows');
  ok(!w.location.hash, 'reset clears hash');

  // range filter + baseline filter via preset
  app.applyPreset('privacy');
  vis = app.compute();
  ok(vis.every(r => r._privacy >= 50 && r._ux <= 49), 'preset privacy: ' + vis.length + ' rows all privacy>=50 and ux<=49');
  app.applyPreset('rtlfb');
  vis = app.compute();
  ok(vis.length > 100 && vis.every(r => r.rtlfb.length), 'preset rtlfb: ' + vis.length + ' rows all configured in RTLFB');
  const sbSel = d.querySelector('#filterRow th.col-sb select');
  const gpoOpt = [...sbSel.options].find(o => o.value.startsWith('gpo:') && o.value.includes('Defender'));
  sbSel.value = gpoOpt.value;
  sbSel.dispatchEvent(new w.Event('change', { bubbles: true }));
  vis = app.compute();
  ok(vis.every(r => r._sbGpos.includes(gpoOpt.value.slice(4))), 'baseline per-GPO filter (' + gpoOpt.text + '): ' + vis.length + ' rows');
  ok(d.querySelectorAll('#body td.col-sb .gpo').length > 0, 'SB cells show GPO sub-line');

  // expand a row
  d.getElementById('reset').click();
  const firstBtn = body.querySelector('button.expander');
  firstBtn.click();
  const detail = body.querySelector('tr.detail');
  ok(detail && detail.previousElementSibling.dataset.uid === app.compute()[0].uid, 'detail row inserted under the expanded row');
  ok(detail.textContent.includes('Security Baseline') && detail.querySelector('.explain p'), 'detail shows explanation and baseline sections');
  body.querySelector('button.expander').click();
  ok(!body.querySelector('tr.detail'), 'detail row collapses');

  // pagination and page size
  d.getElementById('pagerTop').querySelector('button:nth-child(4)').click();
  ok(app.state.page === 2 && body.querySelectorAll('tr').length === 100, 'next page');
  const ps = d.getElementById('pageSize');
  ps.value = '0';
  ps.dispatchEvent(new w.Event('change', { bubbles: true }));
  ok(body.querySelectorAll('tr').length === rows.length, 'page size All renders every row');
  ps.value = '50';
  ps.dispatchEvent(new w.Event('change', { bubbles: true }));
  ok(body.querySelectorAll('tr').length === 50, 'page size 50');

  // column visibility
  const cbs = d.querySelectorAll('#colsList input');
  const regkeyCb = [...cbs].find(cb => cb.parentElement.textContent.trim() === 'Registry key');
  regkeyCb.click();
  ok(d.querySelector('#headRow th[data-col="regkey"]') && body.querySelector('td.col-regkey'), 'registry key column can be shown');

  // hash restore: build a hash then reload
  const hash = '#q=cortana&sort=privacy%3Adesc&f_class=Machine&ps=50';
  const dom2 = await JSDOM.fromFile(path.join(__dirname, '..', 'site', 'index.html'), { runScripts: 'dangerously', resources: 'usable', pretendToBeVisual: true, url: 'file://' + path.join(__dirname, '..', 'site', 'index.html') + hash });
  await new Promise(res => dom2.window.addEventListener('load', res));
  await new Promise(res => setTimeout(res, 100));
  const app2 = dom2.window.GPOApp;
  const vis2 = app2.compute();
  ok(app2.state.search === 'cortana' && app2.state.sort.col === 'privacy' && app2.state.pageSize === 50, 'hash restores search, sort and page size');
  ok(vis2.length > 0 && vis2.every(r => r.class === 'Machine' && r._search.includes('cortana')), 'hash-restored filters applied: ' + vis2.length + ' rows');
  ok(dom2.window.document.getElementById('search').value === 'cortana', 'search box reflects restored state');

  // csv quoting
  ok(app.csvField('a,b') === '"a,b"' && app.csvField('=1+1') === "'=1+1" && app.csvField('say "hi"') === '"say ""hi"""', 'csv escaping');
  console.log(process.exitCode ? 'SITE TESTS FAILED' : 'SITE TESTS PASSED');
})().catch(e => { console.error(e); process.exit(1); });
