// Демо-режим для GitHub Pages: вместо сервера API эмулируется прямо в браузере,
// данные хранятся в localStorage. На обычном сервере этот файл ничего не делает.
(() => {
  if (!/\.github\.io$/.test(location.hostname) && !/[?&]demo\b/.test(location.search)) return;

  window.__DEMO = true;
  const KEY = 'officeTrackerDemo';
  const now = () => new Date().toISOString();
  const today = () => new Date().toLocaleDateString('sv');
  const DEMO_LOGINS = ['admin', 'test'];

  class HttpErr extends Error { constructor(code, msg) { super(msg); this.code = code; } }
  const fail = (code, msg) => { throw new HttpErr(code, msg); };
  const clean = (s, max = 200) => String(s ?? '').trim().slice(0, max);

  // ---------- хранилище ----------
  function seed() {
    const d = { users: [], sessions: null, records: [], docs: [], log: [], resets: [] };
    const mk = (login, fio, password, role) => d.users.push({ id: d.users.length + 1, login, fio, password, role, active: 1, created_at: now() });
    mk('admin', 'Администратор', 'admin123', 'admin');
    mk('test', 'Томилов', 'test123', 'worker');
    mk('petrova', 'Петрова Анна Сергеевна', 'demo1234', 'worker');
    mk('sidorov', 'Сидоров Павел Игоревич', 'demo1234', 'worker');
    const rec = (uid, daysAgo, h1, h2, kind = 'office') => {
      const s = new Date(); s.setDate(s.getDate() - daysAgo); s.setHours(h1, 0, 0, 0);
      const e = new Date(s); e.setHours(h2, 0, 0, 0);
      const row = { id: d.records.length + 1, user_id: uid, date: s.toLocaleDateString('sv'), start_ts: s.toISOString(), end_ts: e.toISOString(), kind, end_manual: 0, confirmed_ts: null, paused_ms: 0, pauses: [] };
      if (kind === 'office') { // в демо у каждой офисной смены обеденный перерыв
        const a = new Date(s); a.setHours(13, 0, 0, 0);
        const b2 = new Date(a); b2.setMinutes(30);
        row.pauses = [{ start_ts: a.toISOString(), end_ts: b2.toISOString(), kind: 'break', planned_min: 30 }];
        row.paused_ms = 30 * 60000;
      }
      d.records.push(row);
    };
    for (let i = 1; i <= 5; i++) { rec(2, i, 9, 18); rec(3, i, 8, 17); rec(4, i, 10, 19); }
    rec(2, 2, 20, 22, 'evening'); rec(3, 3, 19, 21, 'evening'); rec(4, 1, 20, 22, 'remote');
    return d;
  }
  let mem = null;
  const load = () => {
    try { const s = localStorage.getItem(KEY); if (s) return JSON.parse(s); } catch { /* нет доступа к localStorage */ }
    return mem;
  };
  let db = load() || seed();
  const save = () => { mem = db; try { localStorage.setItem(KEY, JSON.stringify(db)); } catch { /* квота или запрет */ } };
  save();

  const nextId = arr => arr.reduce((m, r) => Math.max(m, r.id), 0) + 1;
  const user = id => db.users.find(u => u.id === id);
  const addLog = (uid, action, details = '') => db.log.push({ id: nextId(db.log), user_id: uid, action, details, ts: now() });

  // ---------- логика (повторяет server.js) ----------
  const KINDS = { office: 'работу в офисе', evening: 'вечернее время', remote: 'удалённую работу' };
  const LABELS = { office: 'офис', evening: 'вечернее время', remote: 'удалённо' };
  const parseKind = k => (k === 'evening' || k === 'remote') ? k : 'office';
  const openOf = (uid, kind) => [...db.records].reverse().find(r => r.user_id === uid && r.kind === kind && !r.end_ts) || null;
  const openExtra = uid => [...db.records].reverse().find(r => r.user_id === uid && r.kind !== 'office' && !r.end_ts) || null;
  function myState(uid) {
    const d = today(), mine = db.records.filter(r => r.user_id === uid);
    return {
      today: d, open: openOf(uid, 'office'), openEvening: openExtra(uid),
      todayRecords: mine.filter(r => r.date === d && r.kind === 'office'),
      todayEvening: mine.filter(r => r.date === d && r.kind !== 'office'),
      history: [...mine].reverse().slice(0, 60),
      docs: db.docs.filter(x => x.user_id === uid).reverse().map(({ data, ...x }) => x),
    };
  }
  const publicUser = u => ({ id: u.id, login: u.login, fio: u.fio, role: u.role, mustChange: false });
  const adminCount = () => db.users.filter(u => u.role === 'admin' && u.active).length;
  const demoLock = u => { if (DEMO_LOGINS.includes(u.login)) fail(403, 'В демо-версии пароль этого аккаунта менять нельзя'); };

  function recordsQuery(q) {
    return [...db.records].reverse().filter(r =>
      (!q.user || r.user_id === +q.user) && (!q.from || r.date >= q.from) && (!q.to || r.date <= q.to) &&
      (!q.kind || (q.kind === 'extra' ? r.kind !== 'office' : r.kind === q.kind))).slice(0, 1000)
      .map(r => ({ id: r.id, date: r.date, start_ts: r.start_ts, end_ts: r.end_ts, kind: r.kind, end_manual: r.end_manual, paused_ms: r.paused_ms || 0, pauses: r.pauses || [], fio: user(r.user_id).fio, user_id: r.user_id }));
  }

  function route(method, path, q, b) {
    if (method === 'POST' && path === '/api/forgot') {
      const u = db.users.find(x => x.login === clean(b.login, 64).toLowerCase() && x.active);
      if (u && !db.resets.some(r => r.user_id === u.id && !r.done)) {
        db.resets.push({ id: nextId(db.resets), user_id: u.id, created_at: now(), done: 0 });
        addLog(u.id, 'Запросил(а) новый пароль');
      }
      return { ok: true };
    }
    if (method === 'POST' && path === '/api/login') {
      const u = db.users.find(x => x.login === clean(b.login, 64).toLowerCase() && x.active && x.password === String(b.password || ''));
      if (!u) fail(401, 'Неверный логин или пароль');
      db.session = u.id; addLog(u.id, 'Вход в систему');
      return { user: publicUser(u) };
    }
    const me = user(db.session);
    if (!me || !me.active) fail(401, 'Требуется вход');
    const isAdmin = me.role === 'admin';

    if (method === 'POST' && path === '/api/logout') { addLog(me.id, 'Выход из системы'); db.session = null; return { ok: true }; }
    if (method === 'GET' && path === '/api/me') return { user: publicUser(me) };
    if (method === 'POST' && path === '/api/password') {
      if (!isAdmin) fail(403, 'Пароль может изменить только администратор');
      demoLock(me);
      if (String(b.password || '').length < 6) fail(400, 'Пароль должен быть не короче 6 символов');
      me.password = String(b.password); addLog(me.id, 'Смена пароля'); return { ok: true };
    }
    if (method === 'GET' && path === '/api/my') return myState(me.id);
    if (method === 'POST' && path === '/api/start') {
      const kind = parseKind(b.kind);
      const open = db.records.find(r => r.user_id === me.id && !r.end_ts);
      if (open) fail(400, open.kind === kind ? 'Вы уже отметили начало' : `Сначала завершите ${KINDS[open.kind]}`);
      db.records.push({ id: nextId(db.records), user_id: me.id, date: today(), start_ts: now(), end_ts: null, kind, end_manual: 0, confirmed_ts: null });
      addLog(me.id, kind === 'office' ? 'Начал(а) работу' : `Начал(а): ${LABELS[kind]}`);
      return myState(me.id);
    }
    if (method === 'POST' && path === '/api/finish') {
      const kind = parseKind(b.kind);
      const open = openOf(me.id, kind);
      if (!open) fail(400, 'Сначала отметьте начало');
      let end = now(), manual = 0;
      if (b.end) {
        const t = new Date(b.end);
        if (isNaN(t) || t <= new Date(open.start_ts)) fail(400, 'Время окончания должно быть позже начала работы');
        if (t > new Date(Date.now() + 60000)) fail(400, 'Время окончания не может быть в будущем');
        end = t.toISOString(); manual = 1;
      }
      open.end_ts = end; open.end_manual = manual;
      const what = kind === 'office' ? 'Закончил(а) работу' : `Закончил(а): ${LABELS[kind]}`;
      addLog(me.id, manual ? what + ' (время указано вручную)' : what, manual ? `Указано окончание: ${new Date(end).toLocaleString('ru-RU')}` : '');
      return myState(me.id);
    }
    if (method === 'POST' && path === '/api/confirm') {
      const open = openOf(me.id, parseKind(b.kind));
      if (!open) fail(400, 'Нет активной отметки');
      open.confirmed_ts = now(); addLog(me.id, 'Подтвердил(а), что ещё работает'); return myState(me.id);
    }
    if (method === 'POST' && path === '/api/docs') {
      const title = clean(b.title, 150), filename = clean(b.filename, 200).split(/[\\/]/).pop();
      if (!title) fail(400, 'Укажите название заявления');
      if (!/\.(pdf|docx?|odt|xlsx?|jpe?g|png|txt)$/i.test(filename)) fail(400, 'Допустимые форматы: PDF, DOC, DOCX, ODT, XLS, XLSX, JPG, PNG, TXT');
      const data = String(b.data || '');
      if (!data) fail(400, 'Файл пустой');
      if (data.length > 1.5 * 1024 * 1024) fail(413, 'В демо-версии файл должен быть не больше 1 МБ');
      db.docs.push({ id: nextId(db.docs), user_id: me.id, title, filename, mime: clean(b.mime, 100), size: Math.floor(data.length * 3 / 4), created_at: now(), data });
      addLog(me.id, 'Прикрепил(а) заявление', `${title} (${filename})`);
      return myState(me.id);
    }

    if (!path.startsWith('/api/admin/')) fail(404, 'Не найдено');
    if (!isAdmin) fail(403, 'Недостаточно прав');
    let m;
    if (method === 'GET') {
      if (path === '/api/admin/overview') {
        const d = today();
        return { today: d, people: db.users.filter(u => u.active).sort((a, c) => a.fio.localeCompare(c.fio)).map(u => {
          const off = db.records.filter(r => r.user_id === u.id && r.kind === 'office' && r.date === d);
          const ev = db.records.filter(r => r.user_id === u.id && r.kind !== 'office');
          return { id: u.id, fio: u.fio, login: u.login, role: u.role,
            open_since: openOf(u.id, 'office')?.start_ts || null,
            first_start: off.map(r => r.start_ts).sort()[0] || null,
            last_end: off.map(r => r.end_ts).filter(Boolean).sort().pop() || null,
            evening_open: openExtra(u.id)?.start_ts || null,
            evening: ev.filter(r => r.date === d).length };
        }) };
      }
      if (path === '/api/admin/records') return recordsQuery(q);
      if (path === '/api/admin/evening') return recordsQuery({ ...q, kind: 'extra' });
      if (path === '/api/admin/resets') return db.resets.filter(r => !r.done).map(r => ({ id: r.id, created_at: r.created_at, user_id: r.user_id, fio: user(r.user_id).fio, login: user(r.user_id).login }));
      if (path === '/api/admin/log') return [...db.log].reverse().slice(0, 500).map(l => ({ id: l.id, ts: l.ts, action: l.action, details: l.details, fio: l.user_id ? user(l.user_id)?.fio : null }));
      if (path === '/api/admin/docs') return [...db.docs].reverse().map(({ data, mime, user_id, ...x }) => ({ ...x, fio: user(user_id).fio }));
      if (path === '/api/admin/users') return [...db.users].sort((a, c) => (c.active - a.active) || a.fio.localeCompare(c.fio)).map(({ password, ...u }) => u);
      if ((m = path.match(/^\/api\/admin\/users\/(\d+)\/password$/))) {
        const t = user(+m[1]); if (!t) fail(404, 'Сотрудник не найден');
        addLog(me.id, 'Просмотрел(а) пароль', t.fio); return { password: t.password };
      }
    }
    if (method === 'POST' && path === '/api/admin/users') {
      const login = clean(b.login, 64).toLowerCase(), fio = clean(b.fio, 120), pw = String(b.password || '');
      if (!/^[a-z0-9._-]{3,}$/.test(login)) fail(400, 'Логин: от 3 символов, латиница, цифры, . _ -');
      if (!fio) fail(400, 'Укажите ФИО');
      if (pw.length < 6) fail(400, 'Пароль должен быть не короче 6 символов');
      if (db.users.some(u => u.login === login)) fail(400, 'Такой логин уже существует');
      const role = b.role === 'admin' ? 'admin' : 'worker';
      db.users.push({ id: nextId(db.users), login, fio, password: pw, role, active: 1, created_at: now() });
      addLog(me.id, 'Создал(а) сотрудника', `${fio} (${login}), роль: ${role}`); return { ok: true };
    }
    if (method === 'PATCH' && (m = path.match(/^\/api\/admin\/users\/(\d+)$/))) {
      const t = user(+m[1]); if (!t) fail(404, 'Сотрудник не найден');
      if (((b.role === 'worker' || b.active === false) && t.role === 'admin') && adminCount() <= 1) fail(400, 'Нельзя убрать последнего администратора');
      if (t.id === me.id && b.active === false) fail(400, 'Нельзя отключить самого себя');
      if (b.role === 'admin' || b.role === 'worker') { t.role = b.role; addLog(me.id, b.role === 'admin' ? 'Назначил(а) администратором' : 'Снял(а) права администратора', t.fio); }
      if (typeof b.active === 'boolean') { t.active = b.active ? 1 : 0; addLog(me.id, b.active ? 'Включил(а) сотрудника' : 'Отключил(а) сотрудника', t.fio); }
      if (b.fio !== undefined && clean(b.fio, 120)) { addLog(me.id, 'Изменил(а) ФИО', `${t.fio} → ${clean(b.fio, 120)}`); t.fio = clean(b.fio, 120); }
      if (b.password !== undefined) {
        if (String(b.password).length < 6) fail(400, 'Пароль должен быть не короче 6 символов');
        demoLock(t); t.password = String(b.password);
        db.resets.forEach(r => { if (r.user_id === t.id) r.done = 1; });
        addLog(me.id, 'Сбросил(а) пароль', t.fio);
      }
      return { ok: true };
    }
    fail(404, 'Не найдено');
  }

  // ---------- подмена fetch ----------
  const realFetch = window.fetch.bind(window);
  window.fetch = async (input, init = {}) => {
    const url = new URL(typeof input === 'string' ? input : input.url, location.href);
    if (!url.pathname.startsWith('/api/')) return realFetch(input, init);
    const method = (init.method || 'GET').toUpperCase();
    let body = {}; try { body = init.body ? JSON.parse(init.body) : {}; } catch { /* пустое тело */ }
    let status = 200, data;
    try { data = route(method, url.pathname, Object.fromEntries(url.searchParams), body); save(); }
    catch (e) { if (!(e instanceof HttpErr)) { console.error(e); } status = e.code || 500; data = { error: e instanceof HttpErr ? e.message : 'Ошибка демо-версии' }; }
    return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } });
  };

  // ссылки на скачивание (файлы заявлений и CSV) обрабатываем локально
  const download = (name, blob) => {
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name;
    document.body.appendChild(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  };
  document.addEventListener('click', e => {
    const a = e.target.closest('a[href^="/api/"]'); if (!a) return;
    e.preventDefault();
    const u = new URL(a.getAttribute('href'), location.href), m = u.pathname.match(/^\/api\/docs\/(\d+)\/file$/);
    if (m) {
      const d = db.docs.find(x => x.id === +m[1]); if (!d) return;
      const bin = atob(d.data), arr = Uint8Array.from(bin, c => c.charCodeAt(0));
      download(d.filename, new Blob([arr], { type: d.mime || 'application/octet-stream' }));
    } else if (u.pathname === '/api/admin/export.xlsx') {
      download('tabel.xlsx', new Blob([XlsxLite.report(recordsQuery(Object.fromEntries(u.searchParams)))],
        { type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' }));
    }
  });

  // плашка с подсказкой
  window.addEventListener('DOMContentLoaded', () => {
    const bar = document.createElement('div');
    bar.style.cssText = 'position:fixed;left:0;right:0;bottom:0;z-index:5;background:#263042;color:#fff;font:13px/1.4 system-ui,sans-serif;padding:8px 12px;text-align:center';
    bar.innerHTML = 'Демо-версия: данные хранятся только в вашем браузере. Админ: <b>admin / admin123</b> · Сотрудник: <b>test / test123</b> · <a href="#" id="demoReset" style="color:#9cc0ff">сбросить демо</a>';
    document.body.appendChild(bar); document.body.style.paddingBottom = '56px';
    document.getElementById('demoReset').onclick = e => { e.preventDefault(); try { localStorage.removeItem(KEY); } catch { /* ничего */ } location.reload(); };
  });
})();
