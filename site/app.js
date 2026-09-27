/* Windows 11 24H2 Group Policy catalogue: sortable, filterable table over window.GPO_DATA. */
(function () {
  'use strict';

  const DATA = window.GPO_DATA;
  const ROWS = DATA.policies;
  const PCT_LOW = 35;
  const PCT_HIGH = 65;
  const DEFAULT_PAGE_SIZE = 100;
  const DIR_LABEL = { enabled: 'Enabled', disabled: 'Disabled', neither: '—' };

  function pct(v) { return v == null ? null : Math.round(v * 100); }

  // The direction question is independent of the yes/no question; only show a direction when the
  // matching probability also says the policy matters.
  function dirLabel(ml, kind) {
    if (!ml) return '';
    const choice = ml[kind + '_dir'];
    if (choice === 'neither' || ml[kind] < 0.5) return '—';
    return DIR_LABEL[choice] || choice;
  }

  for (const r of ROWS) {
    r._search = [r.name, r.category_path, r.admx, r.internal_name, r.value_name || '', r.registry_key, r.explanation]
      .join('\n').toLowerCase();
    r._privacy = r.ml ? pct(r.ml.privacy) : null;
    r._security = r.ml ? pct(r.ml.security) : null;
    r._ux = r.ml ? pct(r.ml.ux_degrade) : null;
    r._pdir = dirLabel(r.ml, 'privacy');
    r._sdir = dirLabel(r.ml, 'security');
    r._rtlfbText = r.rtlfb.map(a => (r.class === 'Both' ? a.scope + ': ' : '') + a.summary).join(' | ');
    r._sbText = r.sb.map(a => a.gpo.replace(/^MSFT /, '') + ': ' + a.summary).join(' | ');
    r._rtlfbStates = r.rtlfb.map(a => a.state);
    r._sbStates = r.sb.map(a => a.state);
    r._sbGpos = r.sb.map(a => a.gpo);
  }
  const SB_GPOS = [...new Set(ROWS.flatMap(r => r._sbGpos))].sort();
  const ADMX = [...new Set(ROWS.map(r => r.admx))].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));

  const COLUMNS = [
    { id: 'name', label: 'Policy', get: r => r.name, type: 'text', filter: 'text' },
    { id: 'category', label: 'Category', get: r => r.category_path, type: 'text', filter: 'text' },
    { id: 'class', label: 'Class', get: r => r.class, type: 'text', filter: 'select', options: ['Machine', 'User', 'Both'] },
    { id: 'admx', label: 'Template', get: r => r.admx, type: 'text', filter: 'select', options: ADMX, hidden: true },
    { id: 'supported', label: 'Supported on', get: r => r.supported_on, type: 'text', filter: 'text', hidden: true },
    { id: 'privacy', label: 'Privacy', get: r => r._privacy, type: 'pct', filter: 'range', title: 'Probability that configuring the policy improves privacy' },
    { id: 'security', label: 'Security', get: r => r._security, type: 'pct', filter: 'range', title: 'Probability that configuring the policy improves security' },
    { id: 'ux', label: 'UX loss', get: r => r._ux, type: 'pct', filter: 'range', title: 'Probability that the restrictive setting directly degrades user experience' },
    { id: 'pdir', label: 'Privacy: set to', get: r => r._pdir, type: 'text', filter: 'select', options: ['Enabled', 'Disabled', '—'] },
    { id: 'sdir', label: 'Security: set to', get: r => r._sdir, type: 'text', filter: 'select', options: ['Enabled', 'Disabled', '—'] },
    { id: 'rtlfb', label: 'RTLFB', get: r => r._rtlfbText, type: 'text', filter: 'baseline', states: r => r._rtlfbStates },
    { id: 'sb', label: 'Security Baseline', get: r => r._sbText, type: 'text', filter: 'baseline', states: r => r._sbStates, gpos: SB_GPOS },
    { id: 'regkey', label: 'Registry key', get: r => r.registry_key, type: 'text', filter: 'text', hidden: true },
    { id: 'valuename', label: 'Value name', get: r => r.value_name || '', type: 'text', filter: 'text', hidden: true },
  ];
  const BY_ID = Object.fromEntries(COLUMNS.map(c => [c.id, c]));
  const DEFAULT_HIDDEN = COLUMNS.filter(c => c.hidden).map(c => c.id);

  function emptyFilter(c) {
    if (c.filter === 'range') return { min: '', max: '' };
    if (c.filter === 'baseline') return { mode: '', text: '' };
    return '';
  }

  const state = {
    search: '',
    filters: Object.fromEntries(COLUMNS.map(c => [c.id, emptyFilter(c)])),
    sort: { col: '', dir: 0 },
    page: 1,
    pageSize: DEFAULT_PAGE_SIZE,
    hidden: new Set(DEFAULT_HIDDEN),
    expanded: new Set(),
  };
  let visible = [];

  // ---- filtering and sorting -------------------------------------------------------------------

  function rowMatches(r) {
    if (state.search && !r._search.includes(state.search)) return false;
    for (const c of COLUMNS) {
      const f = state.filters[c.id];
      const v = c.get(r);
      if (c.filter === 'text') {
        if (f && !String(v).toLowerCase().includes(f)) return false;
      } else if (c.filter === 'select') {
        if (f && v !== f) return false;
      } else if (c.filter === 'range') {
        if (f.min !== '' && (v == null || v < Number(f.min))) return false;
        if (f.max !== '' && (v == null || v > Number(f.max))) return false;
      } else if (c.filter === 'baseline') {
        const states = c.states(r);
        if (f.mode === 'set' && !states.length) return false;
        if (f.mode === 'unset' && states.length) return false;
        if ((f.mode === 'Enabled' || f.mode === 'Disabled') && !states.includes(f.mode)) return false;
        if (f.mode.startsWith('gpo:') && !r._sbGpos.includes(f.mode.slice(4))) return false;
        if (f.text && !String(v).toLowerCase().includes(f.text)) return false;
      }
    }
    return true;
  }

  function compare(a, b, c, dir) {
    const va = c.get(a);
    const vb = c.get(b);
    if (c.type === 'pct') {
      if (va == null && vb == null) return 0;
      if (va == null) return 1;
      if (vb == null) return -1;
      return (va - vb) * dir;
    }
    return String(va).localeCompare(String(vb), undefined, { numeric: true, sensitivity: 'base' }) * dir;
  }

  function compute() {
    visible = ROWS.filter(rowMatches);
    if (state.sort.col && state.sort.dir) {
      const c = BY_ID[state.sort.col];
      const dir = state.sort.dir;
      visible.sort((a, b) => compare(a, b, c, dir) || a.uid.localeCompare(b.uid));
    }
  }

  function pageCount() {
    return state.pageSize ? Math.max(1, Math.ceil(visible.length / state.pageSize)) : 1;
  }

  function pageRows() {
    if (!state.pageSize) return visible;
    const start = (state.page - 1) * state.pageSize;
    return visible.slice(start, start + state.pageSize);
  }

  // ---- URL state -------------------------------------------------------------------------------

  function writeHash() {
    const p = new URLSearchParams();
    if (state.search) p.set('q', state.search);
    if (state.sort.col && state.sort.dir) p.set('sort', state.sort.col + ':' + (state.sort.dir > 0 ? 'asc' : 'desc'));
    if (state.page > 1) p.set('page', String(state.page));
    if (state.pageSize !== DEFAULT_PAGE_SIZE) p.set('ps', String(state.pageSize));
    for (const c of COLUMNS) {
      const f = state.filters[c.id];
      if (c.filter === 'range') {
        if (f.min !== '' || f.max !== '') p.set('f_' + c.id, f.min + '..' + f.max);
      } else if (c.filter === 'baseline') {
        if (f.mode || f.text) p.set('f_' + c.id, f.mode + '|' + f.text);
      } else if (f) {
        p.set('f_' + c.id, f);
      }
    }
    const hidden = [...state.hidden].sort().join(',');
    if (hidden !== [...DEFAULT_HIDDEN].sort().join(',')) p.set('hide', hidden);
    const hash = p.toString();
    // Some embedders refuse history updates; fall back to plain fragment assignment.
    try {
      window.history.replaceState(null, '', hash ? '#' + hash : window.location.pathname + window.location.search);
    } catch (e) {
      try { window.location.hash = hash; } catch (e2) { /* read-only location */ }
    }
  }

  function readHash() {
    const p = new URLSearchParams(window.location.hash.slice(1));
    state.search = (p.get('q') || '').toLowerCase();
    const sort = p.get('sort');
    if (sort) {
      const [col, dir] = sort.split(':');
      if (BY_ID[col]) state.sort = { col, dir: dir === 'desc' ? -1 : 1 };
    }
    state.page = Math.max(1, parseInt(p.get('page') || '1', 10) || 1);
    if (p.has('ps')) state.pageSize = Math.max(0, parseInt(p.get('ps'), 10) || 0);
    for (const c of COLUMNS) {
      const raw = p.get('f_' + c.id);
      if (raw == null) continue;
      if (c.filter === 'range') {
        const [min, max] = raw.split('..');
        state.filters[c.id] = { min: min || '', max: max || '' };
      } else if (c.filter === 'baseline') {
        const i = raw.indexOf('|');
        state.filters[c.id] = { mode: i < 0 ? raw : raw.slice(0, i), text: i < 0 ? '' : raw.slice(i + 1).toLowerCase() };
      } else {
        state.filters[c.id] = c.filter === 'text' ? raw.toLowerCase() : raw;
      }
    }
    if (p.has('hide')) state.hidden = new Set(p.get('hide').split(',').filter(id => BY_ID[id]));
  }

  // ---- DOM helpers -----------------------------------------------------------------------------

  const $ = id => document.getElementById(id);

  function el(tag, attrs, children) {
    const node = document.createElement(tag);
    if (attrs) {
      for (const [k, v] of Object.entries(attrs)) {
        if (k === 'class') node.className = v;
        else if (k === 'text') node.textContent = v;
        else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
        else if (v != null) node.setAttribute(k, v);
      }
    }
    if (children) for (const ch of children) if (ch != null) node.appendChild(typeof ch === 'string' ? document.createTextNode(ch) : ch);
    return node;
  }

  function debounce(fn, ms) {
    let t;
    return (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
  }

  function visibleColumns() {
    return COLUMNS.filter(c => !state.hidden.has(c.id));
  }

  // ---- header and filters ----------------------------------------------------------------------

  function filterControl(c) {
    const f = state.filters[c.id];
    const onChange = () => { state.page = 1; update(); };
    if (c.filter === 'text') {
      const input = el('input', { type: 'search', 'aria-label': 'Filter ' + c.label, placeholder: 'contains…', value: f });
      input.addEventListener('input', debounce(() => { state.filters[c.id] = input.value.trim().toLowerCase(); onChange(); }, 150));
      return input;
    }
    if (c.filter === 'select') {
      const sel = el('select', { 'aria-label': 'Filter ' + c.label }, [el('option', { value: '', text: 'any' }),
        ...c.options.map(o => el('option', { value: o, text: o }))]);
      sel.value = f;
      sel.addEventListener('change', () => { state.filters[c.id] = sel.value; onChange(); });
      return sel;
    }
    if (c.filter === 'range') {
      const min = el('input', { type: 'number', min: 0, max: 100, placeholder: 'min', 'aria-label': c.label + ' minimum %', value: f.min });
      const max = el('input', { type: 'number', min: 0, max: 100, placeholder: 'max', 'aria-label': c.label + ' maximum %', value: f.max });
      const apply = debounce(() => { state.filters[c.id] = { min: min.value.trim(), max: max.value.trim() }; onChange(); }, 150);
      min.addEventListener('input', apply);
      max.addEventListener('input', apply);
      return el('div', { class: 'range' }, [min, max]);
    }
    if (c.filter === 'baseline') {
      const opts = [['', 'any'], ['set', 'configured'], ['unset', 'not configured'], ['Enabled', 'Enabled'], ['Disabled', 'Disabled']];
      for (const g of c.gpos || []) opts.push(['gpo:' + g, g.replace(/^MSFT /, '')]);
      const sel = el('select', { 'aria-label': c.label + ' state' }, opts.map(([v, t]) => el('option', { value: v, text: t })));
      sel.value = f.mode;
      const text = el('input', { type: 'search', placeholder: 'value contains…', 'aria-label': c.label + ' value contains', value: f.text });
      const apply = () => { state.filters[c.id] = { mode: sel.value, text: text.value.trim().toLowerCase() }; onChange(); };
      sel.addEventListener('change', apply);
      text.addEventListener('input', debounce(apply, 150));
      return el('div', { class: 'baseline' }, [sel, text]);
    }
    return el('span');
  }

  function renderHead() {
    const headRow = $('headRow');
    const filterRow = $('filterRow');
    headRow.replaceChildren(el('th', { class: 'expander-col', 'aria-label': 'Details' }));
    filterRow.replaceChildren(el('th'));
    for (const c of visibleColumns()) {
      const btn = el('button', { type: 'button', class: 'sort', title: c.title || ('Sort by ' + c.label), onclick: () => toggleSort(c.id) },
        [c.label, el('span', { class: 'arrow', 'aria-hidden': 'true' })]);
      headRow.appendChild(el('th', { scope: 'col', class: 'col-' + c.id, 'data-col': c.id }, [btn]));
      filterRow.appendChild(el('th', { class: 'col-' + c.id }, [filterControl(c)]));
    }
    updateSortIndicators();
  }

  function updateSortIndicators() {
    for (const th of $('headRow').querySelectorAll('th[data-col]')) {
      const active = th.dataset.col === state.sort.col && state.sort.dir;
      th.setAttribute('aria-sort', active ? (state.sort.dir > 0 ? 'ascending' : 'descending') : 'none');
      th.querySelector('.arrow').textContent = active ? (state.sort.dir > 0 ? ' ▲' : ' ▼') : '';
    }
  }

  // Percent columns start with the strongest answers on top; every further click reverses the order.
  function toggleSort(id) {
    if (state.sort.col !== id) state.sort = { col: id, dir: BY_ID[id].type === 'pct' ? -1 : 1 };
    else state.sort.dir = -state.sort.dir;
    state.page = 1;
    updateSortIndicators();
    update();
  }

  function renderColumnMenu() {
    const list = $('colsList');
    list.replaceChildren(...COLUMNS.map(c => {
      const cb = el('input', { type: 'checkbox' });
      cb.checked = !state.hidden.has(c.id);
      cb.addEventListener('change', () => {
        if (cb.checked) state.hidden.delete(c.id); else state.hidden.add(c.id);
        renderHead();
        update();
      });
      return el('label', null, [cb, ' ' + c.label]);
    }));
  }

  // ---- body ------------------------------------------------------------------------------------

  function pctCell(td, v) {
    if (v == null) {
      td.textContent = '–';
      td.classList.add('pct-none');
      return;
    }
    const band = v < PCT_LOW ? 'pct-low' : v > PCT_HIGH ? 'pct-high' : 'pct-mid';
    td.appendChild(el('span', { class: 'pct ' + band, text: v + '%' }));
  }

  function fillCell(td, c, r) {
    const v = c.get(r);
    if (c.type === 'pct') return pctCell(td, v);
    if (c.id === 'pdir' || c.id === 'sdir') {
      td.textContent = v;
      if (r.ml) {
        const kind = c.id === 'pdir' ? 'privacy' : 'security';
        td.title = 'Model choice: ' + r.ml[kind + '_dir'] + ' (confidence ' + pct(r.ml[kind + '_dir_conf']) + '%)';
      }
      return;
    }
    if (c.id === 'name') {
      td.textContent = v;
      for (const f of r.flags) td.appendChild(el('span', { class: 'flag', text: f.replace(/_/g, ' '), title: 'Flag: ' + f }));
      return;
    }
    if (c.id === 'sb' || c.id === 'rtlfb') {
      const entries = c.id === 'sb' ? r.sb : r.rtlfb;
      for (const a of entries) {
        td.appendChild(el('div', null, [
          a.summary,
          el('span', { class: 'gpo', text: (c.id === 'sb' ? a.gpo.replace(/^MSFT /, '') + ' · ' : '') + (a.scope === 'Machine' ? 'Computer' : 'User') }),
        ]));
      }
      return;
    }
    td.textContent = v == null ? '' : v;
  }

  function rowEl(r) {
    const open = state.expanded.has(r.uid);
    const btn = el('button', {
      type: 'button', class: 'expander', 'aria-expanded': String(open), 'aria-label': 'Details for ' + r.name,
      text: open ? '▾' : '▸', onclick: () => toggleExpand(r.uid),
    });
    const tr = el('tr', { 'data-uid': r.uid }, [el('td', { class: 'expander-cell' }, [btn])]);
    for (const c of visibleColumns()) {
      const td = el('td', { class: 'col-' + c.id });
      fillCell(td, c, r);
      tr.appendChild(td);
    }
    return tr;
  }

  function paragraphs(text) {
    return text.split(/\n{2,}/).map(t => el('p', { text: t }));
  }

  function baselineEntries(entries, withGpo) {
    if (!entries.length) return [el('p', { class: 'muted', text: 'not configured' })];
    return entries.map(a => el('div', { class: 'entry' }, [
      el('div', null, [
        withGpo ? el('span', { class: 'gpo', text: a.gpo + ' · ' + (a.scope === 'Machine' ? 'Computer' : 'User') }) : el('span', { class: 'gpo', text: a.scope === 'Machine' ? 'Computer' : 'User' }),
        el('span', { class: 'state', text: a.summary }),
      ]),
      ...a.raw.map(line => el('div', { class: 'raw', text: line })),
    ]));
  }

  function detailEl(r) {
    const cols = 1 + visibleColumns().length;
    const dl = el('dl', null, [
      el('dt', { text: 'Template' }), el('dd', { text: r.admx + ' (' + r.internal_name + ')' }),
      el('dt', { text: 'Class' }), el('dd', { text: r.class }),
      el('dt', { text: 'Supported on' }), el('dd', { text: r.supported_on }),
      el('dt', { text: 'Registry' }), el('dd', null, r.registry_values.map(v => el('div', { class: 'raw', text: v }))),
    ]);
    if (r.ml) {
      const m = r.ml;
      dl.append(
        el('dt', { text: 'Model' }),
        el('dd', { text: 'privacy ' + pct(m.privacy) + '%, security ' + pct(m.security) + '%, UX loss ' + pct(m.ux_degrade) + '%; ' +
          'privacy direction ' + m.privacy_dir + ' (' + pct(m.privacy_dir_conf) + '%), security direction ' + m.security_dir + ' (' + pct(m.security_dir_conf) + '%)' }),
      );
    } else {
      dl.append(el('dt', { text: 'Model' }), el('dd', { text: 'not scored' }));
    }
    if (r.flags.length) dl.append(el('dt', { text: 'Flags' }), el('dd', { text: r.flags.join(', ') }));
    const options = r.options.length
      ? el('ul', null, r.options.map(o => el('li', { text: o.label + ' (' + o.type.replace('_', ' ') + (o.choices ? ': ' + o.choices.join(' / ') : '') + ')' })))
      : null;
    const left = el('div', null, [
      el('h3', { text: r.name }),
      el('div', { class: 'explain' }, paragraphs(r.explanation)),
      options ? el('h3', { text: 'Options' }) : null, options,
    ]);
    const right = el('div', null, [
      el('h3', { text: 'Details' }), dl,
      el('h3', { text: 'Restricted Traffic Limited Functionality Baseline' }), ...baselineEntries(r.rtlfb, false),
      el('h3', { text: 'Security Baseline' }), ...baselineEntries(r.sb, true),
    ]);
    return el('tr', { class: 'detail' }, [el('td', { colspan: cols }, [el('div', { class: 'detail-grid' }, [left, right])])]);
  }

  function renderBody() {
    const frag = document.createDocumentFragment();
    for (const r of pageRows()) {
      frag.appendChild(rowEl(r));
      if (state.expanded.has(r.uid)) frag.appendChild(detailEl(r));
    }
    $('body').replaceChildren(frag);
  }

  function toggleExpand(uid) {
    if (state.expanded.has(uid)) state.expanded.delete(uid); else state.expanded.add(uid);
    renderBody();
  }

  // ---- pager, count, meta ----------------------------------------------------------------------

  function renderPager(nav) {
    const pages = pageCount();
    const goto = p => { state.page = Math.min(Math.max(1, p), pages); update(); };
    const input = el('input', { type: 'number', min: 1, max: pages, value: state.page, 'aria-label': 'Page number' });
    input.addEventListener('change', () => goto(parseInt(input.value, 10) || 1));
    nav.replaceChildren(
      el('button', { type: 'button', text: '« First', disabled: state.page <= 1 ? '' : null, onclick: () => goto(1) }),
      el('button', { type: 'button', text: '‹ Prev', disabled: state.page <= 1 ? '' : null, onclick: () => goto(state.page - 1) }),
      el('span', null, ['Page ', input, ' of ' + pages]),
      el('button', { type: 'button', text: 'Next ›', disabled: state.page >= pages ? '' : null, onclick: () => goto(state.page + 1) }),
      el('button', { type: 'button', text: 'Last »', disabled: state.page >= pages ? '' : null, onclick: () => goto(pages) }),
    );
  }

  function renderCount() {
    const shown = pageRows().length;
    const start = visible.length ? (state.pageSize ? (state.page - 1) * state.pageSize + 1 : 1) : 0;
    const end = start ? start + shown - 1 : 0;
    $('count').textContent = 'Showing ' + start + '–' + end + ' of ' + visible.length + ' matching policies (' + ROWS.length + ' total)';
  }

  function renderMeta() {
    const m = DATA.meta;
    const c = m.counts;
    $('meta').textContent = c.policies + ' policies · ' + c.with_ml + ' scored by ' + m.model + ' · ' +
      c.in_rtlfb + ' in RTLFB · ' + c.in_sb + ' in Security Baseline · generated ' + m.generated.slice(0, 10);
  }

  function update() {
    compute();
    state.page = Math.min(state.page, pageCount());
    renderBody();
    renderPager($('pagerTop'));
    renderPager($('pagerBottom'));
    renderCount();
    writeHash();
  }

  // ---- presets, export, reset ------------------------------------------------------------------

  function applyPreset(name) {
    for (const c of COLUMNS) state.filters[c.id] = emptyFilter(c);
    state.search = '';
    $('search').value = '';
    if (name === 'privacy') { state.filters.privacy = { min: '50', max: '' }; state.filters.ux = { min: '', max: '49' }; state.sort = { col: 'privacy', dir: -1 }; }
    if (name === 'privacy_unbaselined') { state.filters.privacy = { min: '50', max: '' }; state.filters.rtlfb = { mode: 'unset', text: '' }; state.sort = { col: 'privacy', dir: -1 }; }
    if (name === 'security') { state.filters.security = { min: '50', max: '' }; state.filters.ux = { min: '', max: '49' }; state.sort = { col: 'security', dir: -1 }; }
    if (name === 'rtlfb') state.filters.rtlfb = { mode: 'set', text: '' };
    if (name === 'sb') state.filters.sb = { mode: 'set', text: '' };
    if (name === 'both') { state.filters.rtlfb = { mode: 'set', text: '' }; state.filters.sb = { mode: 'set', text: '' }; }
    state.page = 1;
    renderHead();
    update();
  }

  function csvField(v) {
    let s = v == null ? '' : String(v);
    if (/^[=+\-@]/.test(s)) s = "'" + s;
    return /[",\r\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
  }

  function exportCsv() {
    const cols = visibleColumns();
    const lines = [['uid', ...cols.map(c => c.label), 'Explanation'].map(csvField).join(',')];
    for (const r of visible) lines.push([r.uid, ...cols.map(c => c.get(r)), r.explanation].map(csvField).join(','));
    const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const a = el('a', { href: URL.createObjectURL(blob), download: 'gpo-catalogue.csv' });
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(a.href), 1000);
  }

  function reset() {
    for (const c of COLUMNS) state.filters[c.id] = emptyFilter(c);
    state.search = '';
    state.sort = { col: '', dir: 0 };
    state.page = 1;
    state.pageSize = DEFAULT_PAGE_SIZE;
    state.hidden = new Set(DEFAULT_HIDDEN);
    state.expanded.clear();
    $('search').value = '';
    $('preset').value = '';
    $('pageSize').value = String(DEFAULT_PAGE_SIZE);
    renderColumnMenu();
    renderHead();
    update();
  }

  // ---- init ------------------------------------------------------------------------------------

  function init() {
    readHash();
    renderMeta();
    $('search').value = state.search;
    $('search').addEventListener('input', debounce(() => { state.search = $('search').value.trim().toLowerCase(); state.page = 1; update(); }, 150));
    $('pageSize').value = String(state.pageSize);
    $('pageSize').addEventListener('change', () => { state.pageSize = parseInt($('pageSize').value, 10) || 0; state.page = 1; update(); });
    $('preset').addEventListener('change', () => applyPreset($('preset').value));
    $('export').addEventListener('click', exportCsv);
    $('reset').addEventListener('click', reset);
    document.addEventListener('click', e => { if (!$('colsMenu').contains(e.target)) $('colsMenu').open = false; });
    renderColumnMenu();
    renderHead();
    update();
  }

  window.GPOApp = { state, COLUMNS, compute: () => { compute(); return visible; }, update, applyPreset, reset, csvField };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init); else init();
})();
