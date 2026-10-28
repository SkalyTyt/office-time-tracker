// Учёт времени в офисе — сервер без внешних зависимостей (Node 22+)
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');
const XlsxLite = require('./public/xlsx-lite.js');

const PORT = process.env.PORT || 3000;
const DATA = process.env.DATA_DIR || path.join(__dirname, 'data');
const UPLOADS = path.join(DATA, 'uploads');
const PUBLIC = path.join(__dirname, 'public');
fs.mkdirSync(UPLOADS, { recursive: true });

const db = new DatabaseSync(path.join(DATA, 'tracker.db'));
db.exec(`
PRAGMA journal_mode = WAL;
CREATE TABLE IF NOT EXISTS users(
  id INTEGER PRIMARY KEY, login TEXT UNIQUE NOT NULL, fio TEXT NOT NULL,
  salt TEXT NOT NULL, hash TEXT NOT NULL, role TEXT NOT NULL DEFAULT 'worker',
  active INTEGER NOT NULL DEFAULT 1, must_change INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS sessions(
  token TEXT PRIMARY KEY, user_id INTEGER NOT NULL, expires INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS records(
  id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, date TEXT NOT NULL,
  start_ts TEXT NOT NULL, end_ts TEXT);
CREATE TABLE IF NOT EXISTS docs(
  id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, title TEXT NOT NULL,
  filename TEXT NOT NULL, stored TEXT NOT NULL, mime TEXT, size INTEGER,
  created_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS reset_requests(
  id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, created_at TEXT NOT NULL,
  done INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS log(
  id INTEGER PRIMARY KEY, user_id INTEGER, action TEXT NOT NULL,
  details TEXT, ts TEXT NOT NULL);
`);

if (!db.prepare("SELECT 1 FROM pragma_table_info('users') WHERE name='pw_enc'").get())
  db.exec('ALTER TABLE users ADD COLUMN pw_enc TEXT');
if (!db.prepare("SELECT 1 FROM pragma_table_info('records') WHERE name='confirmed_ts'").get())
  db.exec('ALTER TABLE records ADD COLUMN confirmed_ts TEXT; ALTER TABLE records ADD COLUMN end_manual INTEGER NOT NULL DEFAULT 0');
if (!db.prepare("SELECT 1 FROM pragma_table_info('records') WHERE name='kind'").get())
  db.exec("ALTER TABLE records ADD COLUMN kind TEXT NOT NULL DEFAULT 'office'");
// вход, запомненный программой на рабочем ПК (kind='pc'), живёт год; админ может «выйти» с ПК сотрудника
if (!db.prepare("SELECT 1 FROM pragma_table_info('sessions') WHERE name='kind'").get())
  db.exec("ALTER TABLE sessions ADD COLUMN kind TEXT NOT NULL DEFAULT 'web'");
// программа на рабочих ПК: связь, пауза при отключении питания
if (!db.prepare("SELECT 1 FROM pragma_table_info('records') WHERE name='agent'").get())
  db.exec('ALTER TABLE records ADD COLUMN agent INTEGER NOT NULL DEFAULT 0; ALTER TABLE records ADD COLUMN run_id TEXT; ALTER TABLE records ADD COLUMN last_seen_ts TEXT; ALTER TABLE records ADD COLUMN paused_ms INTEGER NOT NULL DEFAULT 0');
// паузы: перерывы, которые берёт сотрудник (kind='break'), и время, пока ПК был выключен (kind='power')
db.exec(`CREATE TABLE IF NOT EXISTS pauses(
  id INTEGER PRIMARY KEY, record_id INTEGER NOT NULL, user_id INTEGER NOT NULL,
  start_ts TEXT NOT NULL, end_ts TEXT, kind TEXT NOT NULL DEFAULT 'break', planned_min INTEGER)`);
const openPauseOf = rid => db.prepare('SELECT * FROM pauses WHERE record_id=? AND end_ts IS NULL ORDER BY id DESC').get(rid) || null;
function endPause(p, endIso) { // закрывает паузу; её длительность вычитается из рабочего времени (records.paused_ms)
  const dur = Math.max(0, new Date(endIso) - new Date(p.start_ts));
  db.prepare('UPDATE pauses SET end_ts=? WHERE id=?').run(endIso, p.id);
  db.prepare('UPDATE records SET paused_ms=paused_ms+? WHERE id=?').run(dur, p.record_id);
  return dur;
}
const PAUSE_MIN_MS = 90 * 1000;                                         // меньшие перерывы не считаем паузой
const MAX_GAP_MS = (+process.env.RESUME_MAX_GAP_HOURS || 4) * 3600e3;    // после такого перерыва смена закрывается сама
// ---------- утилиты ----------
const now = () => new Date().toISOString();
const today = () => new Date().toLocaleDateString('sv'); // YYYY-MM-DD по времени сервера
const hashPw = (pw, salt) => crypto.scryptSync(pw, salt, 64).toString('hex');
// Пароли хранятся ещё и в зашифрованном виде (AES-256-GCM), чтобы администратор мог их посмотреть.
// Ключ лежит в data/secret.key — не показывайте и не копируйте его вместе с базой.
const KEY_FILE = path.join(DATA, 'secret.key');
if (!fs.existsSync(KEY_FILE)) fs.writeFileSync(KEY_FILE, crypto.randomBytes(32));
const KEY = fs.readFileSync(KEY_FILE);
function encPw(pw) {
  const iv = crypto.randomBytes(12);
  const c = crypto.createCipheriv('aes-256-gcm', KEY, iv);
  const enc = Buffer.concat([c.update(pw, 'utf8'), c.final()]);
  return Buffer.concat([iv, c.getAuthTag(), enc]).toString('base64');
}
function decPw(s) {
  if (!s) return null;
  try {
    const b = Buffer.from(s, 'base64');
    const d = crypto.createDecipheriv('aes-256-gcm', KEY, b.subarray(0, 12));
    d.setAuthTag(b.subarray(12, 28));
    return Buffer.concat([d.update(b.subarray(28)), d.final()]).toString('utf8');
  } catch { return null; }
}
function makeUser(login, fio, pw, role = 'worker', mustChange = 0) {
  const salt = crypto.randomBytes(16).toString('hex');
  return db.prepare('INSERT INTO users(login,fio,salt,hash,role,must_change,created_at,pw_enc) VALUES(?,?,?,?,?,?,?,?)')
    .run(login, fio, salt, hashPw(pw, salt), role, mustChange, now(), encPw(pw)).lastInsertRowid;
}
function setPassword(uid, pw, mustChange = 0) {
  const salt = crypto.randomBytes(16).toString('hex');
  db.prepare('UPDATE users SET salt=?,hash=?,pw_enc=?,must_change=? WHERE id=?').run(salt, hashPw(pw, salt), encPw(pw), mustChange, uid);
}
function addLog(userId, action, details = '') {
  db.prepare('INSERT INTO log(user_id,action,details,ts) VALUES(?,?,?,?)').run(userId, action, details, now());
}
class HttpError extends Error { constructor(code, msg) { super(msg); this.code = code; } }
const fail = (code, msg) => { throw new HttpError(code, msg); };

// Демо-режим (DEMO_MODE=1): готовые аккаунты для показа, пароли демо-аккаунтов менять нельзя.
const DEMO = process.env.DEMO_MODE === '1';
const DEMO_LOGINS = new Set(['admin', 'test']);
const demoLock = u => { if (DEMO && DEMO_LOGINS.has(u.login)) fail(403, 'В демо-режиме пароль этого аккаунта менять нельзя'); };

// Первый запуск: создаём администратора
if (!db.prepare('SELECT 1 FROM users LIMIT 1').get()) {
  if (DEMO) {
    makeUser('admin', 'Администратор', 'admin123', 'admin', 0);
    makeUser('test', 'Томилов', 'test123', 'worker', 0);
    console.log('Демо-режим: admin / admin123 (администратор), test / test123 (сотрудник)');
  } else if (process.env.ADMIN_PASSWORD) {
    makeUser('admin', 'Администратор', process.env.ADMIN_PASSWORD, 'admin', 0);
    console.log('Создан администратор: логин admin, пароль из переменной ADMIN_PASSWORD');
  } else {
    makeUser('admin', 'Администратор', 'admin123', 'admin', 1);
    console.log('Создан администратор: логин admin / пароль admin123 (будет предложено сменить)');
  }
}

// ---------- сессии ----------
const SESSION_MS = 30 * 24 * 3600 * 1000; // вход сохраняется на 30 дней, закрытие вкладки ничего не сбрасывает
function parseCookies(req) {
  const out = {};
  (req.headers.cookie || '').split(';').forEach(p => {
    const i = p.indexOf('=');
    if (i > 0) out[p.slice(0, i).trim()] = decodeURIComponent(p.slice(i + 1).trim());
  });
  return out;
}
function getUser(req) {
  const t = parseCookies(req).sid;
  if (!t) return null;
  const s = db.prepare('SELECT * FROM sessions WHERE token=?').get(t);
  if (!s || s.expires < Date.now()) return null;
  const u = db.prepare('SELECT * FROM users WHERE id=? AND active=1').get(s.user_id);
  return u || null;
}
const publicUser = u => ({ id: u.id, login: u.login, fio: u.fio, role: u.role, mustChange: !!u.must_change });

const tickets = new Map(); // одноразовые коды входа для окна администратора
// простая защита от подбора пароля
const attempts = new Map();
function checkRate(ip) {
  const a = attempts.get(ip);
  if (a && a.until > Date.now()) fail(429, 'Слишком много попыток. Подождите несколько минут.');
}
function noteFail(ip) {
  const a = attempts.get(ip) || { n: 0, until: 0 };
  a.n++;
  if (a.n >= 8) { a.until = Date.now() + 5 * 60 * 1000; a.n = 0; }
  attempts.set(ip, a);
}

// ---------- тело запроса ----------
function readJson(req, limit = 15 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    const chunks = []; let size = 0;
    req.on('data', c => {
      size += c.length;
      if (size > limit) { reject(new HttpError(413, 'Файл слишком большой (максимум 10 МБ)')); req.destroy(); return; }
      chunks.push(c);
    });
    req.on('end', () => {
      if (!chunks.length) return resolve({});
      try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
      catch { reject(new HttpError(400, 'Неверный формат запроса')); }
    });
    req.on('error', reject);
  });
}
function send(res, code, body, headers = {}) {
  const isObj = typeof body === 'object' && !Buffer.isBuffer(body);
  res.writeHead(code, { 'Content-Type': isObj ? 'application/json; charset=utf-8' : 'text/plain; charset=utf-8', ...headers });
  res.end(isObj ? JSON.stringify(body) : body);
}

// ---------- логика ----------
const ALLOWED_EXT = new Set(['.pdf', '.doc', '.docx', '.odt', '.xls', '.xlsx', '.jpg', '.jpeg', '.png', '.txt']);
const cleanText = (s, max = 200) => String(s ?? '').trim().slice(0, max);

const KINDS = { office: 'работу в офисе', evening: 'вечернее время', remote: 'удалённую работу' };
const LABELS = { office: 'офис', evening: 'вечернее время', remote: 'удалённо' };
const parseKind = k => (k === 'evening' || k === 'remote') ? k : 'office';
function myState(uid) {
  const d = today();
  const openOf = kind => db.prepare('SELECT * FROM records WHERE user_id=? AND kind=? AND end_ts IS NULL ORDER BY id DESC LIMIT 1').get(uid, kind) || null;
  return {
    today: d,
    open: openOf('office'),
    openEvening: db.prepare("SELECT * FROM records WHERE user_id=? AND kind!='office' AND end_ts IS NULL ORDER BY id DESC LIMIT 1").get(uid) || null,
    todayRecords: db.prepare("SELECT * FROM records WHERE user_id=? AND date=? AND kind='office' ORDER BY id").all(uid, d),
    todayEvening: db.prepare("SELECT * FROM records WHERE user_id=? AND date=? AND kind!='office' ORDER BY id").all(uid, d),
    history: db.prepare('SELECT * FROM records WHERE user_id=? ORDER BY id DESC LIMIT 60').all(uid),
    docs: db.prepare('SELECT id,title,filename,size,created_at FROM docs WHERE user_id=? ORDER BY id DESC').all(uid),
  };
}

function adminOverview() {
  const d = today();
  return db.prepare(`
    SELECT u.id,u.fio,u.login,u.role,
      (SELECT start_ts FROM records r WHERE r.user_id=u.id AND r.kind='office' AND r.end_ts IS NULL ORDER BY id DESC LIMIT 1) AS open_since,
      (SELECT MIN(start_ts) FROM records r WHERE r.user_id=u.id AND r.kind='office' AND r.date=?) AS first_start,
      (SELECT MAX(end_ts) FROM records r WHERE r.user_id=u.id AND r.kind='office' AND r.date=?) AS last_end,
      (SELECT start_ts FROM records r WHERE r.user_id=u.id AND r.kind!='office' AND r.end_ts IS NULL ORDER BY id DESC LIMIT 1) AS evening_open,
      (SELECT COUNT(*) FROM records r WHERE r.user_id=u.id AND r.kind!='office' AND r.date=?) AS evening,
      (SELECT agent FROM records r WHERE r.user_id=u.id AND r.kind='office' AND r.end_ts IS NULL ORDER BY id DESC LIMIT 1) AS open_agent,
      (SELECT last_seen_ts FROM records r WHERE r.user_id=u.id AND r.kind='office' AND r.end_ts IS NULL ORDER BY id DESC LIMIT 1) AS open_last_seen,
      (SELECT p.start_ts FROM pauses p JOIN records r ON r.id=p.record_id WHERE r.user_id=u.id AND p.end_ts IS NULL AND p.kind='break' ORDER BY p.id DESC LIMIT 1) AS break_since,
      (SELECT p.planned_min FROM pauses p JOIN records r ON r.id=p.record_id WHERE r.user_id=u.id AND p.end_ts IS NULL AND p.kind='break' ORDER BY p.id DESC LIMIT 1) AS break_planned
    FROM users u WHERE u.active=1 ORDER BY u.fio`).all(d, d, d);
}

function recordsQuery(q, limit = 1000) {
  const where = ['1=1']; const args = [];
  if (q.user) { where.push('r.user_id=?'); args.push(+q.user); }
  if (q.from) { where.push('r.date>=?'); args.push(q.from); }
  if (q.to) { where.push('r.date<=?'); args.push(q.to); }
  if (q.kind === 'extra') where.push("r.kind!='office'"); // вечернее время и удалённая работа
  else if (q.kind === 'office' || q.kind === 'evening' || q.kind === 'remote') { where.push('r.kind=?'); args.push(q.kind); }
  return attachPauses(db.prepare(`
    SELECT r.id,r.date,r.start_ts,r.end_ts,r.kind,r.end_manual,r.paused_ms,r.agent,u.fio,u.id AS user_id
    FROM records r JOIN users u ON u.id=r.user_id
    WHERE ${where.join(' AND ')} ORDER BY r.id DESC LIMIT ${Math.floor(limit)}`).all(...args));
}

function attachPauses(rows) { // каждой отметке добавляем список её пауз
  const byId = new Map(rows.map(r => [r.id, (r.pauses = [])]));
  const ids = [...byId.keys()];
  for (let i = 0; i < ids.length; i += 500) {
    const chunk = ids.slice(i, i + 500);
    db.prepare(`SELECT record_id,start_ts,end_ts,kind,planned_min FROM pauses WHERE record_id IN (${chunk.map(() => '?').join(',')}) ORDER BY start_ts`)
      .all(...chunk).forEach(p => byId.get(p.record_id).push(p));
  }
  return rows;
}

function adminCount() {
  return db.prepare("SELECT COUNT(*) c FROM users WHERE role='admin' AND active=1").get().c;
}

async function api(req, res, url) {
  const route = `${req.method} ${url.pathname}`;
  // за прокси (хостинг) реальный адрес приходит в X-Forwarded-For
  const ip = String(req.headers['x-forwarded-for'] || '').split(',')[0].trim() || req.socket.remoteAddress;
  const secure = req.headers['x-forwarded-proto'] === 'https' ? '; Secure' : '';

  if (route === 'POST /api/forgot') {
    // «Забыли пароль»: заявка администратору, пароль он задаёт и передаёт сам
    checkRate(ip);
    const b = await readJson(req);
    const u = db.prepare('SELECT * FROM users WHERE login=? AND active=1').get(cleanText(b.login, 64).toLowerCase());
    noteFail(ip); // ограничиваем частоту заявок так же, как попытки входа
    if (u && !db.prepare('SELECT 1 FROM reset_requests WHERE user_id=? AND done=0').get(u.id)) {
      db.prepare('INSERT INTO reset_requests(user_id,created_at) VALUES(?,?)').run(u.id, now());
      addLog(u.id, 'Запросил(а) новый пароль');
    }
    return send(res, 200, { ok: true }); // ответ одинаковый, чтобы нельзя было подбирать логины
  }

  if (route === 'POST /api/login') {
    checkRate(ip);
    const b = await readJson(req);
    const u = db.prepare('SELECT * FROM users WHERE login=? AND active=1').get(cleanText(b.login, 64).toLowerCase());
    const ok = u && crypto.timingSafeEqual(Buffer.from(hashPw(String(b.password || ''), u.salt), 'hex'), Buffer.from(u.hash, 'hex'));
    if (!ok) { noteFail(ip); fail(401, 'Неверный логин или пароль'); }
    attempts.delete(ip);
    const token = crypto.randomBytes(32).toString('hex');
    db.prepare('INSERT INTO sessions(token,user_id,expires) VALUES(?,?,?)').run(token, u.id, Date.now() + SESSION_MS);
    addLog(u.id, 'Вход в систему');
    return send(res, 200, { user: publicUser(u) }, {
      'Set-Cookie': `sid=${token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${SESSION_MS / 1000}${secure}`,
    });
  }

  // ----- программа на рабочих компьютерах -----
  if (route === 'POST /api/agent/login') { // единственный раз: логин и пароль, дальше программа входит по запомненному ключу
    checkRate(ip);
    const b = await readJson(req);
    const u = db.prepare('SELECT * FROM users WHERE login=? AND active=1').get(cleanText(b.login, 64).toLowerCase());
    const ok = u && crypto.timingSafeEqual(Buffer.from(hashPw(String(b.password || ''), u.salt), 'hex'), Buffer.from(u.hash, 'hex'));
    if (!ok) { noteFail(ip); fail(401, 'Неверный логин или пароль'); }
    attempts.delete(ip);
    const token = crypto.randomBytes(32).toString('hex');
    db.prepare("INSERT INTO sessions(token,user_id,expires,kind) VALUES(?,?,?,'pc')").run(token, u.id, Date.now() + 365 * 24 * 3600e3);
    addLog(u.id, 'Вошёл(ла) на рабочем ПК', 'вход запомнен на этом компьютере');
    return send(res, 200, { token, user: { fio: u.fio, login: u.login, role: u.role } });
  }
  if (route === 'GET /api/agent/open') { // окно администратора из программы: одноразовый код → вход на сайт
    const code = url.searchParams.get('c') || '', t = tickets.get(code);
    tickets.delete(code);
    if (!t || t.exp < Date.now()) fail(410, 'Ссылка устарела. Откройте панель из программы ещё раз.');
    const token = crypto.randomBytes(32).toString('hex');
    db.prepare("INSERT INTO sessions(token,user_id,expires,kind) VALUES(?,?,?,'web')").run(token, t.uid, Date.now() + SESSION_MS);
    res.writeHead(200, {
      'Content-Type': 'text/html; charset=utf-8',
      'Set-Cookie': `sid=${token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${SESSION_MS / 1000}${secure}`,
    });
    return res.end('<!doctype html><meta charset="utf-8"><title>Учёт времени</title><script>location.replace("/")</script>');
  }
  if (url.pathname.startsWith('/api/agent/')) {
    const dev = getUser(req);
    if (!dev) fail(401, 'Требуется вход');
    if (req.method !== 'GET' && !(req.headers['content-type'] || '').includes('application/json')) fail(415, 'Неверный тип запроса');
    const b = req.method === 'POST' ? await readJson(req) : {};
    const uid = dev.id, runId = cleanText(b.run_id, 64);
    // текущая смена сотрудника: офис, вечернее время или удалённо (на ПК одновременно идёт одна)
    const openOffice = () => db.prepare('SELECT * FROM records WHERE user_id=? AND end_ts IS NULL ORDER BY id DESC').get(uid) || null;
    const state = extra => {
      const open = openOffice();
      return {
      now: now(), user: { fio: dev.fio, login: dev.login, role: dev.role }, open,
      pause: open ? openPauseOf(open.id) : null,
      other: null,
      ...extra,
      };
    };
    const parseTime = (v, minMs) => {
      const t = new Date(v);
      if (isNaN(t) || t > new Date(Date.now() + 60000) || t < new Date(Date.now() - minMs)) fail(400, 'Неверное время');
      return t;
    };

    switch (route) {
      case 'GET /api/agent/state': return send(res, 200, state());
      case 'POST /api/agent/ticket': { // одноразовый код на 30 секунд для открытия сайта без ввода пароля
        const code = crypto.randomBytes(24).toString('hex');
        tickets.set(code, { uid, exp: Date.now() + 30000 });
        return send(res, 200, { code });
      }
      case 'POST /api/agent/start': {
        const kind = parseKind(b.kind); // «Офис», «Вечернее время» или «Удалённо» — выбирается на плашке
        const open = openOffice();
        if (open) {
          if (open.run_id === runId) return send(res, 200, state()); // повторная отправка
          fail(409, 'Смена уже идёт');
        }
        const t = b.at ? parseTime(b.at, 12 * 3600e3) : new Date(); // «at»: начало, отмеченное на ПК без связи
        db.prepare('INSERT INTO records(user_id,date,start_ts,kind,agent,run_id,last_seen_ts) VALUES(?,?,?,?,1,?,?)')
          .run(uid, t.toLocaleDateString('sv'), t.toISOString(), kind, runId, now());
        addLog(uid, kind === 'office' ? 'Начал(а) работу (на рабочем ПК)' : `Начал(а): ${LABELS[kind]} (на рабочем ПК)`, b.at ? 'время начала сохранено на ПК при отсутствии связи' : '');
        return send(res, 200, state());
      }
      case 'POST /api/agent/resume': { // компьютер включили снова, смена ещё открыта
        const open = openOffice();
        if (!open) fail(404, 'Нет открытой смены');
        if (open.run_id === runId) {
          db.prepare('UPDATE records SET last_seen_ts=? WHERE id=?').run(now(), open.id);
          return send(res, 200, state());
        }
        const last = new Date(open.last_seen_ts || open.start_ts).getTime(), gap = Date.now() - last;
        const brk = openPauseOf(open.id);
        if (open.agent && gap > MAX_GAP_MS) { // слишком давно: закрываем смену по последней связи с ПК
          const lastIso = new Date(last).toISOString();
          if (brk) endPause(brk, lastIso);
          db.prepare('UPDATE records SET end_ts=?, end_manual=2 WHERE id=?').run(lastIso, open.id);
          addLog(uid, 'Смена закрыта автоматически', `нет связи с ПК с ${new Date(last).toLocaleString('ru-RU')}`);
          return send(res, 200, state({ closed: true, closedAt: lastIso }));
        }
        let paused = 0;
        if (brk) { // ПК выключили во время паузы: пауза продолжалась до возвращения
          paused = endPause(brk, now());
          addLog(uid, 'Вернулся(ась) после выключения ПК во время паузы', `пауза ${Math.round(paused / 60000)} мин`);
        } else if (open.agent && gap > PAUSE_MIN_MS) { // время, пока ПК был выключен, не считается рабочим
          paused = gap;
          db.prepare("INSERT INTO pauses(record_id,user_id,start_ts,end_ts,kind) VALUES(?,?,?,?,'power')").run(open.id, uid, new Date(last).toISOString(), now());
          db.prepare('UPDATE records SET paused_ms=paused_ms+? WHERE id=?').run(gap, open.id);
          addLog(uid, 'Продолжил(а) смену после перерыва', `пауза ${Math.round(gap / 60000)} мин (ПК был выключен)`);
        }
        db.prepare('UPDATE records SET run_id=?, agent=1, last_seen_ts=? WHERE id=?').run(runId, now(), open.id);
        return send(res, 200, state({ pausedMin: Math.round(paused / 60000) }));
      }
      case 'POST /api/agent/pause': { // сотрудник отходит: указывает, на сколько минут
        const open = openOffice();
        if (!open) fail(400, 'Смена не начата');
        if (openPauseOf(open.id)) fail(409, 'Пауза уже идёт');
        const minutes = Math.floor(+b.minutes);
        if (!(minutes >= 1 && minutes <= 720)) fail(400, 'Укажите время паузы в минутах, от 1 до 720');
        db.prepare("INSERT INTO pauses(record_id,user_id,start_ts,kind,planned_min) VALUES(?,?,?,'break',?)").run(open.id, uid, now(), minutes);
        addLog(uid, 'Взял(а) паузу', `на ${minutes} мин`);
        return send(res, 200, state());
      }
      case 'POST /api/agent/pause-end': { // вернулся к работе; время паузы считается до этого момента
        const open = openOffice();
        if (!open) fail(400, 'Смена не начата');
        const p = openPauseOf(open.id);
        if (p) {
          const dur = endPause(p, now());
          addLog(uid, 'Вернулся(ась) с паузы', `пауза ${Math.round(dur / 60000)} мин, планировалось ${p.planned_min} мин`);
        }
        return send(res, 200, state());
      }
      case 'POST /api/agent/heartbeat': {
        const open = openOffice();
        if (!open) return send(res, 200, { open: false, now: now() });
        if (open.run_id !== runId) return send(res, 200, { open: true, stale: true, now: now() });
        db.prepare('UPDATE records SET last_seen_ts=? WHERE id=?').run(now(), open.id);
        return send(res, 200, { open: true, now: now() });
      }
      case 'POST /api/agent/finish': {
        const open = openOffice();
        if (!open) fail(400, 'Смена не начата');
        let end = new Date(), manual = 0;
        if (b.end) { // «end»: окончание, отмеченное на ПК без связи
          end = parseTime(b.end, 14 * 24 * 3600e3);
          if (end <= new Date(open.start_ts)) fail(400, 'Время окончания должно быть позже начала');
          manual = 1;
        }
        const brk = openPauseOf(open.id);
        if (brk) endPause(brk, end.toISOString()); // смену закрыли во время паузы: пауза заканчивается вместе с ней
        db.prepare('UPDATE records SET end_ts=?, end_manual=? WHERE id=?').run(end.toISOString(), manual, open.id);
        addLog(uid, 'Закончил(а) работу (на рабочем ПК)', manual ? 'время окончания сохранено на ПК при отсутствии связи' : '');
        return send(res, 200, state());
      }
    }
    fail(404, 'Не найдено');
  }

  const user = getUser(req);
  if (!user) fail(401, 'Требуется вход');
  const isAdmin = user.role === 'admin';
  const requireAdmin = () => { if (!isAdmin) fail(403, 'Недостаточно прав'); };
  // защита от CSRF: изменяющие запросы только как JSON
  if (req.method !== 'GET' && !(req.headers['content-type'] || '').includes('application/json')) fail(415, 'Неверный тип запроса');

  switch (route) {
    case 'POST /api/logout': {
      db.prepare('DELETE FROM sessions WHERE token=?').run(parseCookies(req).sid);
      addLog(user.id, 'Выход из системы');
      return send(res, 200, { ok: true }, { 'Set-Cookie': 'sid=; HttpOnly; Path=/; Max-Age=0' });
    }
    case 'GET /api/me': return send(res, 200, { user: publicUser(user) });
    case 'POST /api/password': {
      if (!isAdmin) fail(403, 'Пароль может изменить только администратор');
      demoLock(user);
      const b = await readJson(req);
      if (!b.password || String(b.password).length < 6) fail(400, 'Пароль должен быть не короче 6 символов');
      if (b.password === 'admin123') fail(400, 'Выберите другой пароль');
      setPassword(user.id, String(b.password));
      addLog(user.id, 'Смена пароля');
      return send(res, 200, { ok: true });
    }
    case 'GET /api/my': return send(res, 200, myState(user.id));
    case 'POST /api/start': {
      const b = await readJson(req);
      const kind = parseKind(b.kind);
      const open = db.prepare('SELECT kind FROM records WHERE user_id=? AND end_ts IS NULL').get(user.id);
      if (open) fail(400, open.kind === kind ? 'Вы уже отметили начало' : `Сначала завершите ${KINDS[open.kind]}`);
      db.prepare('INSERT INTO records(user_id,date,start_ts,kind) VALUES(?,?,?,?)').run(user.id, today(), now(), kind);
      addLog(user.id, kind === 'office' ? 'Начал(а) работу' : `Начал(а): ${LABELS[kind]}`);
      return send(res, 200, myState(user.id));
    }
    case 'POST /api/finish': {
      const b = await readJson(req);
      const kind = parseKind(b.kind);
      const open = db.prepare('SELECT * FROM records WHERE user_id=? AND kind=? AND end_ts IS NULL ORDER BY id DESC').get(user.id, kind);
      if (!open) fail(400, 'Сначала отметьте начало');
      let end = now(), manual = 0;
      if (b.end) { // сотрудник сам указал время окончания (после напоминания «вы ещё работаете?»)
        const t = new Date(b.end);
        if (isNaN(t) || t <= new Date(open.start_ts)) fail(400, 'Время окончания должно быть позже начала работы');
        if (t > new Date(Date.now() + 60000)) fail(400, 'Время окончания не может быть в будущем');
        end = t.toISOString(); manual = 1;
      }
      const brk = openPauseOf(open.id);
      if (brk) endPause(brk, end);
      db.prepare('UPDATE records SET end_ts=?, end_manual=? WHERE id=?').run(end, manual, open.id);
      const what = kind === 'office' ? 'Закончил(а) работу' : `Закончил(а): ${LABELS[kind]}`;
      addLog(user.id, manual ? what + ' (время указано вручную)' : what,
        manual ? `Указано окончание: ${new Date(end).toLocaleString('ru-RU')}; отметка сделана ${new Date().toLocaleString('ru-RU')}` : '');
      return send(res, 200, myState(user.id));
    }
    case 'POST /api/confirm': {
      const b = await readJson(req);
      const kind = parseKind(b.kind);
      const open = db.prepare('SELECT * FROM records WHERE user_id=? AND kind=? AND end_ts IS NULL ORDER BY id DESC').get(user.id, kind);
      if (!open) fail(400, 'Нет активной отметки');
      db.prepare('UPDATE records SET confirmed_ts=? WHERE id=?').run(now(), open.id);
      addLog(user.id, 'Подтвердил(а), что ещё работает', LABELS[kind]);
      return send(res, 200, myState(user.id));
    }
    case 'POST /api/docs': {
      const b = await readJson(req);
      const title = cleanText(b.title, 150);
      const filename = path.basename(cleanText(b.filename, 200));
      const ext = path.extname(filename).toLowerCase();
      if (!title) fail(400, 'Укажите название заявления');
      if (!ALLOWED_EXT.has(ext)) fail(400, 'Допустимые форматы: PDF, DOC, DOCX, ODT, XLS, XLSX, JPG, PNG, TXT');
      const buf = Buffer.from(String(b.data || ''), 'base64');
      if (!buf.length) fail(400, 'Файл пустой');
      if (buf.length > 10 * 1024 * 1024) fail(413, 'Файл слишком большой (максимум 10 МБ)');
      const stored = crypto.randomBytes(16).toString('hex') + ext;
      fs.writeFileSync(path.join(UPLOADS, stored), buf);
      db.prepare('INSERT INTO docs(user_id,title,filename,stored,mime,size,created_at) VALUES(?,?,?,?,?,?,?)')
        .run(user.id, title, filename, stored, cleanText(b.mime, 100), buf.length, now());
      addLog(user.id, 'Прикрепил(а) заявление', `${title} (${filename})`);
      return send(res, 200, myState(user.id));
    }
  }

  let m;
  if ((m = url.pathname.match(/^\/api\/docs\/(\d+)\/file$/)) && req.method === 'GET') {
    const doc = db.prepare('SELECT * FROM docs WHERE id=?').get(+m[1]);
    if (!doc || (doc.user_id !== user.id && !isAdmin)) fail(404, 'Файл не найден');
    const file = path.join(UPLOADS, doc.stored);
    if (!fs.existsSync(file)) fail(404, 'Файл не найден');
    if (doc.user_id !== user.id) addLog(user.id, 'Открыл(а) заявление', doc.title);
    res.writeHead(200, {
      'Content-Type': doc.mime || 'application/octet-stream',
      'Content-Disposition': `attachment; filename*=UTF-8''${encodeURIComponent(doc.filename)}`,
      'X-Content-Type-Options': 'nosniff',
    });
    return fs.createReadStream(file).pipe(res);
  }

  // ----- только администратор -----
  if (url.pathname.startsWith('/api/admin/')) {
    requireAdmin();
    const q = Object.fromEntries(url.searchParams);
    switch (route) {
      case 'GET /api/admin/overview': return send(res, 200, { today: today(), people: adminOverview() });
      case 'GET /api/admin/records': return send(res, 200, recordsQuery(q));
      case 'GET /api/admin/evening':
        return send(res, 200, recordsQuery({ ...q, kind: 'extra' }));
      case 'GET /api/admin/resets':
        return send(res, 200, db.prepare(`SELECT r.id,r.created_at,u.id AS user_id,u.fio,u.login FROM reset_requests r JOIN users u ON u.id=r.user_id WHERE r.done=0 ORDER BY r.id`).all());
      case 'GET /api/admin/log':
        return send(res, 200, db.prepare(`SELECT l.id,l.ts,l.action,l.details,u.fio FROM log l LEFT JOIN users u ON u.id=l.user_id ORDER BY l.id DESC LIMIT 500`).all());
      case 'GET /api/admin/docs':
        return send(res, 200, db.prepare(`SELECT d.id,d.title,d.filename,d.size,d.created_at,u.fio FROM docs d JOIN users u ON u.id=d.user_id ORDER BY d.id DESC`).all());
      case 'GET /api/admin/users':
        return send(res, 200, db.prepare("SELECT id,login,fio,role,active,created_at,(SELECT COUNT(*) FROM sessions s WHERE s.user_id=users.id AND s.kind='pc' AND s.expires>?) AS pc FROM users ORDER BY active DESC, fio").all(Date.now()));
      case 'POST /api/admin/users': {
        const b = await readJson(req);
        const login = cleanText(b.login, 64).toLowerCase(), fio = cleanText(b.fio, 120), pw = String(b.password || '');
        if (!/^[a-z0-9._-]{3,}$/.test(login)) fail(400, 'Логин: от 3 символов, латиница, цифры, . _ -');
        if (!fio) fail(400, 'Укажите ФИО');
        if (pw.length < 6) fail(400, 'Пароль должен быть не короче 6 символов');
        if (db.prepare('SELECT 1 FROM users WHERE login=?').get(login)) fail(400, 'Такой логин уже существует');
        const role = b.role === 'admin' ? 'admin' : 'worker';
        makeUser(login, fio, pw, role, 0);
        addLog(user.id, 'Создал(а) сотрудника', `${fio} (${login}), роль: ${role}`);
        return send(res, 200, { ok: true });
      }
      case 'GET /api/admin/export.xlsx': {
        const file = Buffer.from(XlsxLite.report(recordsQuery(q, 100000)));
        res.writeHead(200, {
          'Content-Type': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
          'Content-Disposition': 'attachment; filename="tabel.xlsx"', 'Content-Length': file.length,
        });
        return res.end(file);
      }
    }
    if ((m = url.pathname.match(/^\/api\/admin\/users\/(\d+)\/pc-logout$/)) && req.method === 'POST') {
      const target = db.prepare('SELECT * FROM users WHERE id=?').get(+m[1]);
      if (!target) fail(404, 'Сотрудник не найден');
      db.prepare("DELETE FROM sessions WHERE user_id=? AND kind='pc'").run(target.id);
      addLog(user.id, 'Вышел(ла) из аккаунта на рабочем ПК', target.fio);
      return send(res, 200, { ok: true });
    }
    if ((m = url.pathname.match(/^\/api\/admin\/users\/(\d+)\/password$/)) && req.method === 'GET') {
      const target = db.prepare('SELECT * FROM users WHERE id=?').get(+m[1]);
      if (!target) fail(404, 'Сотрудник не найден');
      addLog(user.id, 'Просмотрел(а) пароль', target.fio);
      return send(res, 200, { password: decPw(target.pw_enc) });
    }
    if ((m = url.pathname.match(/^\/api\/admin\/users\/(\d+)$/)) && req.method === 'PATCH') {
      const target = db.prepare('SELECT * FROM users WHERE id=?').get(+m[1]);
      if (!target) fail(404, 'Сотрудник не найден');
      const b = await readJson(req);
      const losesAdmin = (b.role === 'worker' && target.role === 'admin') || (b.active === false && target.role === 'admin');
      if (losesAdmin && adminCount() <= 1) fail(400, 'Нельзя убрать последнего администратора');
      if (target.id === user.id && (b.active === false)) fail(400, 'Нельзя отключить самого себя');
      if (b.role === 'admin' || b.role === 'worker') {
        db.prepare('UPDATE users SET role=? WHERE id=?').run(b.role, target.id);
        addLog(user.id, b.role === 'admin' ? 'Назначил(а) администратором' : 'Снял(а) права администратора', target.fio);
      }
      if (typeof b.active === 'boolean') {
        db.prepare('UPDATE users SET active=? WHERE id=?').run(b.active ? 1 : 0, target.id);
        if (!b.active) db.prepare('DELETE FROM sessions WHERE user_id=?').run(target.id);
        addLog(user.id, b.active ? 'Включил(а) сотрудника' : 'Отключил(а) сотрудника', target.fio);
      }
      if (b.fio !== undefined && cleanText(b.fio, 120)) {
        db.prepare('UPDATE users SET fio=? WHERE id=?').run(cleanText(b.fio, 120), target.id);
        addLog(user.id, 'Изменил(а) ФИО', `${target.fio} → ${cleanText(b.fio, 120)}`);
      }
      if (b.password !== undefined) {
        if (String(b.password).length < 6) fail(400, 'Пароль должен быть не короче 6 символов');
        demoLock(target);
        setPassword(target.id, String(b.password));
        db.prepare('UPDATE reset_requests SET done=1 WHERE user_id=?').run(target.id);
        db.prepare('DELETE FROM sessions WHERE user_id=?').run(target.id);
        addLog(user.id, 'Сбросил(а) пароль', target.fio);
      }
      return send(res, 200, { ok: true });
    }
  }
  fail(404, 'Не найдено');
}

// ---------- статика ----------
const TYPES = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml' };
function serveStatic(req, res, url) {
  const rel = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
  const file = path.resolve(PUBLIC, rel);
  if (!file.startsWith(PUBLIC + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) return send(res, 404, 'Не найдено');
  res.writeHead(200, { 'Content-Type': TYPES[path.extname(file)] || 'application/octet-stream', 'X-Content-Type-Options': 'nosniff' });
  fs.createReadStream(file).pipe(res);
}

// Автопоиск сервера в локальной сети: программа на ПК сотрудника шлёт широковещательное «OFFICETIME?»,
// сервер отвечает своим портом, и вводить адрес вручную не нужно
function startDiscovery() {
  if (process.env.DISCOVERY === '0') return;
  try {
    const sock = require('node:dgram').createSocket({ type: 'udp4', reuseAddr: true });
    sock.on('message', (msg, rinfo) => {
      if (String(msg).startsWith('OFFICETIME?')) sock.send(Buffer.from('OFFICETIME ' + PORT), rinfo.port, rinfo.address);
    });
    sock.on('error', e => console.error('Автопоиск сервера отключён:', e.message));
    sock.bind(41234);
  } catch (e) { console.error('Автопоиск сервера отключён:', e.message); }
}
startDiscovery();

http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x');
  try {
    if (url.pathname.startsWith('/api/')) await api(req, res, url);
    else serveStatic(req, res, url);
  } catch (e) {
    if (!(e instanceof HttpError)) console.error(e);
    if (!res.headersSent) send(res, e.code || 500, { error: e instanceof HttpError ? e.message : 'Внутренняя ошибка сервера' });
  }
}).listen(PORT, () => console.log(`Сайт запущен: http://localhost:${PORT}`));

setInterval(() => db.prepare('DELETE FROM sessions WHERE expires<?').run(Date.now()), 3600 * 1000).unref();

// Резервная копия базы раз в сутки (при запуске и каждые 6 часов): data/backups, хранятся последние 14
function backupDb() {
  try {
    const dir = path.join(DATA, 'backups');
    fs.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, `tracker-${today()}.db`);
    if (!fs.existsSync(file)) {
      db.exec(`VACUUM INTO '${file.replace(/'/g, "''")}'`);
      console.log('Резервная копия базы:', file);
    }
    fs.readdirSync(dir).filter(f => /^tracker-\d{4}-\d{2}-\d{2}\.db$/.test(f)).sort().slice(0, -14)
      .forEach(f => fs.unlinkSync(path.join(dir, f)));
  } catch (e) { console.error('Не удалось сделать резервную копию:', e.message); }
}
backupDb();
setInterval(backupDb, 6 * 3600 * 1000).unref();
