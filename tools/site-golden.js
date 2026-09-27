// Records what the website shows for a set of views, as reference data for the desktop app's tests.
// Usage: node tools/site-golden.js > app/tests/GpoCatalogue.Tests/Fixtures/site-golden.json
const path = require('path');
const { JSDOM } = require(path.join(__dirname, 'node_modules', 'jsdom'));

const HEAD = 40;
const TAIL = 10;

(async () => {
  const dom = await JSDOM.fromFile(path.join(__dirname, '..', 'site', 'index.html'), {
    runScripts: 'dangerously', resources: 'usable', pretendToBeVisual: true,
  });
  const w = dom.window;
  await new Promise(res => w.addEventListener('load', res));
  await new Promise(res => setTimeout(res, 100));
  const app = w.GPOApp;
  const views = [];

  function csv(rows) {
    const cols = app.COLUMNS.filter(c => !app.state.hidden.has(c.id));
    const lines = [['uid', ...cols.map(c => c.label), 'Explanation'].map(app.csvField).join(',')];
    for (const r of rows) lines.push([r.uid, ...cols.map(c => c.get(r)), r.explanation].map(app.csvField).join(','));
    return lines.join('\r\n');
  }

  // A view is described the way the desktop app stores it, so the test can replay it.
  function record(name, setup, withCsv) {
    app.reset();
    setup(app.state);
    const rows = app.compute();
    const filters = {};
    for (const c of app.COLUMNS) {
      const f = app.state.filters[c.id];
      if (c.filter === 'range') { if (f.min !== '' || f.max !== '') filters[c.id] = { min: f.min, max: f.max }; }
      else if (c.filter === 'baseline') { if (f.mode || f.text) filters[c.id] = { mode: f.mode, text: f.text }; }
      else if (c.filter === 'select') { if (f) filters[c.id] = { mode: f }; }
      else if (f) filters[c.id] = { text: f };
    }
    views.push({
      name,
      search: app.state.search,
      filters,
      sort: app.state.sort.dir ? { column: app.state.sort.col, direction: app.state.sort.dir } : null,
      count: rows.length,
      head: rows.slice(0, HEAD).map(r => r.uid),
      tail: rows.slice(-TAIL).map(r => r.uid),
      csv: withCsv ? csv(rows) : null,
    });
  }

  record('default', () => {});
  for (const preset of ['privacy', 'privacy_unbaselined', 'security', 'rtlfb', 'sb', 'both']) {
    // applyPreset renders too; only its effect on the state matters here.
    record('preset ' + preset, () => app.applyPreset(preset), preset === 'both');
  }
  for (const col of ['name', 'category', 'class', 'pdir', 'rtlfb', 'sb', 'privacy', 'security', 'ux']) {
    record('sort ' + col + ' ascending', s => { s.sort = { col, dir: 1 }; });
    record('sort ' + col + ' descending', s => { s.sort = { col, dir: -1 }; });
  }
  record('search cortana, class Machine, privacy descending', s => {
    s.search = 'cortana'; s.filters.class = 'Machine'; s.sort = { col: 'privacy', dir: -1 };
  }, true);
  record('name contains diagnostic data', s => { s.filters.name = 'diagnostic data'; });
  record('category contains windows components, user only', s => {
    s.filters.category = 'windows components'; s.filters.class = 'User'; s.sort = { col: 'name', dir: 1 };
  });
  record('privacy 80 to 100', s => { s.filters.privacy = { min: '80', max: '100' }; });
  record('ux at most 10, security at least 90', s => {
    s.filters.ux = { min: '', max: '10' }; s.filters.security = { min: '90', max: '' };
  });
  record('privacy direction disabled', s => { s.filters.pdir = 'Disabled'; });
  record('security direction none', s => { s.filters.sdir = '—'; });
  record('rtlfb disabled', s => { s.filters.rtlfb = { mode: 'Disabled', text: '' }; });
  record('rtlfb value contains deny', s => { s.filters.rtlfb = { mode: '', text: 'deny' }; });
  record('sb not configured, rtlfb configured', s => {
    s.filters.sb = { mode: 'unset', text: '' }; s.filters.rtlfb = { mode: 'set', text: '' };
  });
  const gpos = app.COLUMNS.find(c => c.id === 'sb').gpos;
  for (const gpo of gpos) record('sb policy object ' + gpo, s => { s.filters.sb = { mode: 'gpo:' + gpo, text: '' }; });
  record('hidden column filter: template', s => { s.filters.admx = 'WindowsDefender.admx'; }, true);
  record('hidden column filter: registry key', s => { s.filters.regkey = 'currentversion\\policies'; });

  const fields = ['a,b', '=1+1', '+1', '-1', '@x', 'say "hi"', 'line\nbreak', 'cr\rhere', '', 'plain', ' lead'];
  process.stdout.write(JSON.stringify({
    total: w.GPO_DATA.policies.length,
    columns: app.COLUMNS.map(c => ({ id: c.id, label: c.label, hidden: !!c.hidden })),
    csvFields: fields.map(f => [f, app.csvField(f)]),
    views,
  }, null, 1));
  dom.window.close();
})().catch(e => { console.error(e); process.exit(1); });
