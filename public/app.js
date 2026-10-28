// Учёт времени — клиент
const $app = document.getElementById('app');
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtTime = t => t ? new Date(t).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }) : '—';
const fmtDT = t => t ? new Date(t).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'short' }) : '—';
const fmtDate = d => d ? d.split('-').reverse().join('.') : '';
const hours = (a, b) => b ? ((new Date(b) - new Date(a)) / 3600000).toFixed(2) : '';
const fmtSize = n => n > 1048576 ? (n / 1048576).toFixed(1) + ' МБ' : Math.max(1, Math.round(n / 1024)) + ' КБ';

const kindPill = k => k === 'evening' ? '<span class="pill amber">вечернее</span>' : '<span class="pill blue">офис</span>';
const sumHours = recs => recs.reduce((t, r) => t + (new Date(r.end_ts || Date.now()) - new Date(r.start_ts)) / 3600000, 0).toFixed(2);

let me = null, timerId = null;

function toast(msg) {
  const t = document.getElementById('toast');
  t.textContent = msg; t.classList.add('show');
  clearTimeout(toast.id); toast.id = setTimeout(() => t.classList.remove('show'), 2600);
}

async function api(url, method = 'GET', body) {
  const opts = { method, headers: {} };
  if (method !== 'GET') { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body || {}); }
  const r = await fetch(url, opts);
  const data = await r.json().catch(() => ({}));
  if (r.status === 401 && me) { me = null; render(); }
  if (!r.ok) throw new Error(data.error || 'Ошибка');
  return data;
}
const act = async (fn) => { try { await fn(); } catch (e) { toast(e.message); } };

// ---------- вход ----------
function viewLogin(err = '') {
  clearInterval(timerId);
  $app.innerHTML = `
  <div class="wrap login"><div class="card">
    <h1>Учёт времени</h1><p class="muted">Войдите, чтобы отметиться на рабочем месте</p>
    <form id="f">
      <label>Логин</label><input name="login" autocomplete="username" autofocus required>
      <label>Пароль</label><input name="password" type="password" autocomplete="current-password" required>
      <p class="err" id="err">${esc(err)}</p>
      <button class="btn big" style="font-size:1.05rem;padding:12px">Войти</button>
    </form>
    <button class="btn ghost" id="forgot" style="width:100%;margin-top:10px">Забыли пароль?</button></div></div>`;
  document.getElementById('forgot').onclick = viewForgot;
  document.getElementById('f').onsubmit = async e => {
    e.preventDefault();
    const fd = Object.fromEntries(new FormData(e.target));
    try { me = (await api('/api/login', 'POST', fd)).user; render(); }
    catch (x) { document.getElementById('err').textContent = x.message; }
  };
}

function viewForgot() {
  $app.innerHTML = `
  <div class="wrap login"><div class="card">
    <h1>Забыли пароль?</h1>
    <p class="muted">Введите свой логин. Заявка уйдёт администратору — он задаст новый пароль и сообщит его вам.</p>
    <form id="f"><label>Логин</label><input name="login" autocomplete="username" autofocus required>
    <p class="err" id="err"></p>
    <button class="btn big" style="font-size:1.05rem;padding:12px">Отправить заявку</button></form>
    <button class="btn ghost" id="back" style="width:100%;margin-top:10px">Назад ко входу</button></div></div>`;
  document.getElementById('back').onclick = () => viewLogin();
  document.getElementById('f').onsubmit = async e => {
    e.preventDefault();
    try {
      await api('/api/forgot', 'POST', Object.fromEntries(new FormData(e.target)));
      $app.querySelector('.card').innerHTML = `<h1>Заявка отправлена</h1>
        <p class="muted">Если такой логин есть, администратор увидит вашу заявку, задаст новый пароль и передаст его вам лично.</p>
        <button class="btn big" id="ok" style="font-size:1.05rem;padding:12px">Ко входу</button>`;
      document.getElementById('ok').onclick = () => viewLogin();
    } catch (x) { document.getElementById('err').textContent = x.message; }
  };
}

function viewChangePassword() {
  $app.innerHTML = `
  <div class="wrap login"><div class="card">
    <h1>Новый пароль</h1><p class="muted">Задайте свой пароль (не короче 6 символов), чтобы продолжить.</p>
    <form id="f"><label>Новый пароль</label><input name="password" type="password" minlength="6" required autofocus>
    <p class="err" id="err"></p>
    <button class="btn big" style="font-size:1.05rem;padding:12px">Сохранить</button></form></div></div>`;
  document.getElementById('f').onsubmit = async e => {
    e.preventDefault();
    try { await api('/api/password', 'POST', Object.fromEntries(new FormData(e.target))); me.mustChange = false; toast('Пароль изменён'); render(); }
    catch (x) { document.getElementById('err').textContent = x.message; }
  };
}

// ---------- шапка ----------
function header() {
  return `<div class="top"><div><div class="who">${esc(me.fio)}</div>
    <div class="muted">${me.role === 'admin' ? 'Администратор' : 'Сотрудник'}</div></div>
    <div style="display:flex;gap:8px">${me.role === 'admin' ? '<button class="btn ghost small" id="pw">Сменить пароль</button>' : ''}
    <button class="btn ghost small" id="out">Выйти</button></div></div>`;
}
function bindHeader() {
  document.getElementById('out').onclick = () => act(async () => { await api('/api/logout', 'POST'); me = null; render(); });
  const pwBtn = document.getElementById('pw');
  if (pwBtn) pwBtn.onclick = () => {
    const p = prompt('Введите новый пароль (не короче 6 символов):');
    if (p) act(async () => { await api('/api/password', 'POST', { password: p }); toast('Пароль изменён'); });
  };
}

// ---------- «Вы ещё работаете?» после 8 часов ----------
const LONG_MS = 8 * 3600 * 1000, REASK_MS = 2 * 3600 * 1000;
function needAsk(rec) {
  const t = Date.now();
  if (t - new Date(rec.start_ts) < LONG_MS) return false;
  return !rec.confirmed_ts || t - new Date(rec.confirmed_ts) >= REASK_MS;
}
function askStillWorking(rec, kind, reload) {
  if (document.querySelector('.modal-bg')) return;
  const bg = document.createElement('div');
  bg.className = 'modal-bg';
  document.body.appendChild(bg);
  const close = () => bg.remove();
  const hrs = Math.floor((Date.now() - new Date(rec.start_ts)) / 3600000);
  const stepAsk = () => {
    bg.innerHTML = `<div class="modal"><h2>Вы ещё работаете?</h2>
      <p class="muted">Таймер идёт уже более ${hrs} ч (с ${fmtTime(rec.start_ts)}).</p>
      <div class="btns"><button class="btn" id="yes">Да, работаю</button><button class="btn ghost" id="no">Нет, уже закончил(а)</button></div></div>`;
    bg.querySelector('#yes').onclick = () => act(async () => { await api('/api/confirm', 'POST', { kind }); close(); toast('Таймер продолжает идти'); reload(); });
    bg.querySelector('#no').onclick = stepEnd;
  };
  const stepEnd = () => {
    const d = new Date(), val = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
    bg.innerHTML = `<div class="modal"><h2>Во сколько вы закончили?</h2>
      <p class="muted">Укажите время окончания работы — оно будет записано вместо текущего.</p>
      <input type="time" id="t" value="${val}" required>
      <p class="err" id="e"></p>
      <div class="btns"><button class="btn" id="ok">Записать</button><button class="btn ghost" id="back">Назад</button></div></div>`;
    bg.querySelector('#back').onclick = stepAsk;
    bg.querySelector('#ok').onclick = async () => {
      const v = bg.querySelector('#t').value;
      if (!v) return (bg.querySelector('#e').textContent = 'Укажите время');
      const [h, m] = v.split(':').map(Number);
      const end = new Date(); end.setHours(h, m, 0, 0);
      if (end > new Date()) end.setDate(end.getDate() - 1);
      if (end <= new Date(rec.start_ts)) return (bg.querySelector('#e').textContent = 'Время должно быть позже начала (' + fmtTime(rec.start_ts) + ')');
      try { await api('/api/finish', 'POST', { kind, end: end.toISOString() }); close(); toast('Работа завершена в ' + v); reload(); }
      catch (x) { bg.querySelector('#e').textContent = x.message; }
    };
  };
  stepAsk();
}

// ---------- панель сотрудника ----------
async function renderWorker(into, withHeader = true) {
  const s = await api('/api/my');
  const open = s.open, eve = s.openEvening;
  into.innerHTML = `${withHeader ? header() : ''}
  <div class="card">
    <button class="btn big start" id="start" ${open ? 'disabled' : ''}>Начал(а) работу</button>
    <div class="status">
      <span class="badge ${open ? 'on' : ''}">${open ? 'На рабочем месте с ' + fmtTime(open.start_ts) : 'Не отмечен(а) на рабочем месте'}</span>
      <div class="timer" id="timer">${open ? '' : '00:00:00'}</div>
      <div class="muted">Сегодня: ${fmtDate(s.today)}${s.todayRecords.length ? ' · отметок: ' + s.todayRecords.length : ''}</div>
    </div>
    <button class="btn big stop" id="finish" ${open ? '' : 'disabled'}>Закончил(а) работу</button>
  </div>
  <div class="card evening">
    <h2>Вечернее время</h2>
    <p class="muted">Работаете дома или после основного дня? Запустите отдельный таймер — он учитывается как вечернее время.</p>
    <div class="status" style="padding:10px 0">
      <span class="badge ${eve ? 'on' : ''}">${eve ? 'Вечернее время идёт с ' + fmtTime(eve.start_ts) : 'Таймер не запущен'}</span>
      <div class="timer" id="etimer" style="font-size:1.8rem">${eve ? '' : '00:00:00'}</div>
      ${s.todayEvening.length ? `<div class="muted">Сегодня вечернего времени: ${sumHours(s.todayEvening)} ч</div>` : ''}
    </div>
    <div class="row">
      <button class="btn" id="estart" ${eve || open ? 'disabled' : ''}>Начать вечернее время</button>
      <button class="btn ghost" id="efinish" ${eve ? '' : 'disabled'}>Закончить вечернее время</button>
    </div>
    ${open ? '<p class="muted" style="margin:10px 0 0">Сначала завершите работу в офисе.</p>' : ''}
  </div>
  <div class="card">
    <h2>Заявления</h2>
    <form id="docf">
      <div class="row"><div><label>Название заявления</label><input name="title" maxlength="150" required placeholder="Например: заявление на отгул"></div>
      <div><label>Файл (PDF, DOC, DOCX, JPG, PNG…)</label><input name="file" type="file" required accept=".pdf,.doc,.docx,.odt,.xls,.xlsx,.jpg,.jpeg,.png,.txt"></div>
      <button class="btn">Прикрепить</button></div>
    </form>
    <div class="tablewrap" style="margin-top:12px">${s.docs.length ? `<table><tr><th>Название</th><th>Файл</th><th>Дата</th></tr>${s.docs.map(d =>
      `<tr><td>${esc(d.title)}</td><td><a href="/api/docs/${d.id}/file">${esc(d.filename)}</a> <span class="muted">${fmtSize(d.size)}</span></td><td>${fmtDT(d.created_at)}</td></tr>`).join('')}</table>` : '<div class="empty">Заявлений пока нет</div>'}</div>
  </div>
  <div class="card"><h2>Мои отметки</h2><div class="tablewrap">${s.history.length ? `<table><tr><th>Дата</th><th>Тип</th><th>Начало</th><th>Конец</th><th>Часов</th></tr>${s.history.map(r =>
    `<tr><td>${fmtDate(r.date)}</td><td>${kindPill(r.kind)}</td><td>${fmtTime(r.start_ts)}</td><td>${r.end_ts ? fmtTime(r.end_ts) : `<span class="pill green">${r.kind === 'evening' ? 'идёт' : 'на месте'}</span>`}${r.end_manual ? ' <span class="mark">указано вручную</span>' : ''}</td><td>${hours(r.start_ts, r.end_ts)}</td></tr>`).join('')}</table>` : '<div class="empty">Пока пусто</div>'}</div></div>`;

  clearInterval(timerId);
  const running = open ? [open, 'timer'] : eve ? [eve, 'etimer'] : null;
  let onTick = null;
  if (running) {
    const [rec, id] = running;
    const tick = () => {
      onTick?.();
      const sec = Math.max(0, Math.floor((Date.now() - new Date(rec.start_ts)) / 1000));
      const el = document.getElementById(id);
      if (!el) return clearInterval(timerId);
      el.textContent = [sec / 3600, sec / 60 % 60, sec % 60].map(n => String(Math.floor(n)).padStart(2, '0')).join(':');
    };
    tick(); timerId = setInterval(tick, 1000);
  }
  const reload = () => renderWorker(into, withHeader);
  const mark = (id, path, kind, msg) => into.querySelector(id).onclick = () => act(async () => { await api(path, 'POST', { kind }); toast(msg); reload(); });
  mark('#start', '/api/start', 'office', 'Начало работы отмечено');
  mark('#finish', '/api/finish', 'office', 'Окончание работы отмечено');
  mark('#estart', '/api/start', 'evening', 'Вечернее время запущено');
  mark('#efinish', '/api/finish', 'evening', 'Вечернее время завершено');
  if (running) {
    const [rec] = running, kind = rec.kind;
    onTick = () => { if (needAsk(rec)) askStillWorking(rec, kind, reload); };
    onTick();
  }
  into.querySelector('#docf').onsubmit = e => {
    e.preventDefault();
    const f = e.target.file.files[0], title = e.target.title.value;
    if (f.size > 10 * 1024 * 1024) return toast('Файл больше 10 МБ');
    const rd = new FileReader();
    rd.onload = () => act(async () => {
      await api('/api/docs', 'POST', { title, filename: f.name, mime: f.type, data: rd.result.split(',')[1] });
      toast('Заявление прикреплено'); reload();
    });
    rd.readAsDataURL(f);
  };
  if (withHeader) bindHeader();
}

// ---------- панель администратора ----------
const ADMIN_TABS = [['today', 'Сегодня'], ['records', 'Отметки'], ['evening', 'Вечернее время'], ['docs', 'Заявления'], ['log', 'Журнал действий'], ['users', 'Сотрудники'], ['me', 'Моя отметка']];
let adminTab = 'today';

async function renderAdmin() {
  const resets = await api('/api/admin/resets').catch(() => []);
  $app.innerHTML = `<div class="wrap wide">${header()}<div class="tabs">${ADMIN_TABS.map(([k, n]) =>
    `<button class="tab ${k === adminTab ? 'on' : ''}" data-t="${k}">${n}${k === 'users' && resets.length ? ` <span class="pill amber">${resets.length}</span>` : ''}</button>`).join('')}</div>
    ${resets.length ? `<div class="card"><h2>Запросы на новый пароль</h2>${resets.map(r =>
      `<div class="row" style="align-items:center;margin-top:8px"><div><b>${esc(r.fio)}</b> <span class="muted">(${esc(r.login)}) · ${fmtDT(r.created_at)}</span></div>
       <button class="btn small" data-reset="${r.user_id}">Задать новый пароль</button></div>`).join('')}
      <p class="muted" style="margin-top:10px">Задайте пароль и сообщите его сотруднику лично — сайт ему ничего не отправляет.</p></div>` : ''}
    <div id="pane"></div></div>`;
  bindHeader();
  $app.querySelectorAll('[data-reset]').forEach(b => b.onclick = () => act(async () => {
    const p = prompt('Новый пароль для сотрудника (не короче 6 символов):');
    if (!p) return;
    await api('/api/admin/users/' + b.dataset.reset, 'PATCH', { password: p });
    toast('Пароль изменён — сообщите его сотруднику'); renderAdmin();
  }));
  $app.querySelectorAll('.tab').forEach(b => b.onclick = () => { adminTab = b.dataset.t; renderAdmin(); });
  const pane = document.getElementById('pane');
  clearInterval(timerId);
  try { await ({ today: tabToday, records: tabRecords, evening: tabEvening, docs: tabDocs, log: tabLog, users: tabUsers, me: tabMe }[adminTab])(pane); }
  catch (e) { pane.innerHTML = `<div class="card empty">${esc(e.message)}</div>`; }
}

const table = (head, rows) => rows.length
  ? `<div class="tablewrap"><table><tr>${head.map(h => `<th>${h}</th>`).join('')}</tr>${rows.map(r => `<tr>${r.map(c => `<td>${c}</td>`).join('')}</tr>`).join('')}</table></div>`
  : '<div class="empty">Данных нет</div>';

async function tabToday(pane) {
  const { today, people } = await api('/api/admin/overview');
  const inside = people.filter(p => p.open_since).length;
  pane.innerHTML = `<div class="card"><h2>Кто на рабочем месте — ${fmtDate(today)}</h2>
    <p class="muted">Сейчас на месте: ${inside} из ${people.length}</p>
    ${table(['ФИО', 'Статус', 'Пришёл(ла)', 'Ушёл(ла)', 'Вечернее'], people.map(p => [
      esc(p.fio) + (p.role === 'admin' ? ' <span class="pill blue">админ</span>' : ''),
      p.open_since ? '<span class="pill green">на месте</span>' : (p.first_start ? '<span class="pill">ушёл(ла)</span>' : '<span class="pill">не отмечался(ась)</span>'),
      fmtTime(p.first_start), p.open_since ? '—' : fmtTime(p.last_end),
      p.evening_open ? '<span class="pill amber">идёт с ' + fmtTime(p.evening_open) + '</span>' : (p.evening ? '<span class="pill amber">было</span>' : '')
    ]))}</div>`;
}

async function tabRecords(pane) {
  const users = await api('/api/admin/users');
  const f = tabRecords.f || (tabRecords.f = { user: '', from: '', to: '' });
  const qs = () => new URLSearchParams(Object.entries(f).filter(([, v]) => v)).toString();
  const rows = await api('/api/admin/records?' + qs());
  pane.innerHTML = `<div class="card"><h2>Все отметки</h2>
    <div class="row"><div><label>Сотрудник</label><select id="fu"><option value="">Все</option>${users.map(u => `<option value="${u.id}" ${f.user == u.id ? 'selected' : ''}>${esc(u.fio)}</option>`).join('')}</select></div>
    <div><label>С даты</label><input type="date" id="ff" value="${f.from}"></div>
    <div><label>По дату</label><input type="date" id="ft" value="${f.to}"></div>
    <a class="btn ghost" href="/api/admin/export.csv?${qs()}" style="text-decoration:none">Скачать CSV</a></div>
    <div style="margin-top:12px">${table(['Сотрудник', 'Тип', 'Дата', 'Начало', 'Конец', 'Часов'], rows.map(r => [
      esc(r.fio), kindPill(r.kind), fmtDate(r.date), fmtTime(r.start_ts), (r.end_ts ? fmtTime(r.end_ts) : '<span class="pill green">идёт</span>') + (r.end_manual ? ' <span class="mark">указано вручную</span>' : ''), hours(r.start_ts, r.end_ts)
    ]))}</div></div>`;
  const upd = () => { f.user = pane.querySelector('#fu').value; f.from = pane.querySelector('#ff').value; f.to = pane.querySelector('#ft').value; renderAdmin(); };
  ['#fu', '#ff', '#ft'].forEach(s => pane.querySelector(s).onchange = upd);
}

async function tabEvening(pane) {
  const rows = await api('/api/admin/evening');
  pane.innerHTML = `<div class="card"><h2>Вечернее время</h2>${table(['Сотрудник', 'Дата', 'Начало', 'Конец', 'Часов'], rows.map(r => [esc(r.fio), fmtDate(r.date), fmtTime(r.start_ts), r.end_ts ? fmtTime(r.end_ts) : '<span class="pill amber">идёт</span>', hours(r.start_ts, r.end_ts)]))}</div>`;
}

async function tabDocs(pane) {
  const rows = await api('/api/admin/docs');
  pane.innerHTML = `<div class="card"><h2>Заявления сотрудников</h2>${table(['Сотрудник', 'Название', 'Файл', 'Дата'], rows.map(d => [
    esc(d.fio), esc(d.title), `<a href="/api/docs/${d.id}/file">${esc(d.filename)}</a> <span class="muted">${fmtSize(d.size)}</span>`, fmtDT(d.created_at)]))}</div>`;
}

async function tabLog(pane) {
  const rows = await api('/api/admin/log');
  pane.innerHTML = `<div class="card"><h2>Журнал всех действий</h2>${table(['Время', 'Кто', 'Действие', 'Подробности'], rows.map(l => [
    fmtDT(l.ts), esc(l.fio || '—'), esc(l.action), esc(l.details)]))}</div>`;
}

async function tabUsers(pane) {
  const users = await api('/api/admin/users');
  pane.innerHTML = `<div class="card"><h2>Добавить сотрудника</h2>
    <form id="nu"><div class="row"><div><label>ФИО</label><input name="fio" required></div>
    <div><label>Логин</label><input name="login" required autocapitalize="none"></div>
    <div><label>Пароль</label><input name="password" minlength="6" required></div>
    <div><label>Роль</label><select name="role"><option value="worker">Сотрудник</option><option value="admin">Администратор</option></select></div>
    <button class="btn">Создать</button></div></form>
    <p class="muted">Пароли сотрудникам задаёте только вы; сами они менять пароль не могут.</p></div>
  <div class="card"><h2>Сотрудники</h2>${table(['ФИО', 'Логин', 'Пароль', 'Роль', 'Статус', 'Действия'], users.map(u => [
    esc(u.fio), esc(u.login),
    `<button class="btn ghost small" data-a="showpw" data-id="${u.id}">Показать</button>`,
    u.role === 'admin' ? '<span class="pill blue">админ</span>' : 'сотрудник',
    u.active ? '<span class="pill green">активен</span>' : '<span class="pill">отключён</span>',
    `<span style="display:flex;gap:6px;flex-wrap:wrap">
      <button class="btn ghost small" data-a="role" data-id="${u.id}" data-v="${u.role === 'admin' ? 'worker' : 'admin'}">${u.role === 'admin' ? 'Снять админа' : 'Сделать админом'}</button>
      <button class="btn ghost small" data-a="pw" data-id="${u.id}">Сбросить пароль</button>
      <button class="btn ghost small" data-a="fio" data-id="${u.id}" data-v="${esc(u.fio)}">Изменить ФИО</button>
      <button class="btn ghost small" data-a="active" data-id="${u.id}" data-v="${u.active ? 0 : 1}">${u.active ? 'Отключить' : 'Включить'}</button></span>`
  ]))}</div>`;
  pane.querySelector('#nu').onsubmit = e => {
    e.preventDefault();
    act(async () => { await api('/api/admin/users', 'POST', Object.fromEntries(new FormData(e.target))); toast('Сотрудник создан'); renderAdmin(); });
  };
  pane.querySelectorAll('[data-a]').forEach(b => b.onclick = () => act(async () => {
    const id = b.dataset.id, a = b.dataset.a, v = b.dataset.v;
    if (a === 'showpw') {
      const { password } = await api(`/api/admin/users/${id}/password`);
      b.outerHTML = `<code>${password ? esc(password) : 'неизвестен — задайте новый'}</code>`;
      return;
    }
    let body;
    if (a === 'role') body = { role: v };
    else if (a === 'active') { if (v === '0' && !confirm('Отключить сотрудника? Он не сможет войти, история сохранится.')) return; body = { active: v === '1' }; }
    else if (a === 'pw') { const p = prompt('Новый временный пароль (не короче 6 символов):'); if (!p) return; body = { password: p }; }
    else { const n = prompt('ФИО:', v); if (!n) return; body = { fio: n }; }
    await api('/api/admin/users/' + id, 'PATCH', body); toast('Готово'); renderAdmin();
  }));
}

async function tabMe(pane) { await renderWorker(pane, false); }

// ---------- старт ----------
function render() {
  if (!me) return viewLogin();
  if (me.mustChange) return viewChangePassword();
  if (me.role === 'admin') return renderAdmin();
  const wrap = document.createElement('div'); wrap.className = 'wrap';
  $app.innerHTML = ''; $app.appendChild(wrap);
  renderWorker(wrap).catch(e => toast(e.message));
}
api('/api/me').then(d => { me = d.user; render(); }).catch(() => viewLogin());
