// Учёт времени в офисе — сервер без внешних зависимостей (Node 22+)
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');

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

// Первый запуск: создаём администратора
if (!db.prepare('SELECT 1 FROM users LIMIT 1').get()) {
  makeUser('admin', 'Администратор', 'admin123', 'admin', 1);
  console.log('Создан администратор: логин admin / пароль admin123 (будет предложено сменить)');
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

const KINDS = { office: 'работу в офисе', evening: 'вечернее время' };
function myState(uid) {
  const d = today();
  const openOf = kind => db.prepare('SELECT * FROM records WHERE user_id=? AND kind=? AND end_ts IS NULL ORDER BY id DESC LIMIT 1').get(uid, kind) || null;
  return {
    today: d,
    open: openOf('office'),
    openEvening: openOf('evening'),
    todayRecords: db.prepare("SELECT * FROM records WHERE user_id=? AND date=? AND kind='office' ORDER BY id").all(uid, d),
    todayEvening: db.prepare("SELECT * FROM records WHERE user_id=? AND date=? AND kind='evening' ORDER BY id").all(uid, d),
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
      (SELECT start_ts FROM records r WHERE r.user_id=u.id AND r.kind='evening' AND r.end_ts IS NULL ORDER BY id DESC LIMIT 1) AS evening_open,
      (SELECT COUNT(*) FROM records r WHERE r.user_id=u.id AND r.kind='evening' AND r.date=?) AS evening
    FROM users u WHERE u.active=1 ORDER BY u.fio`).all(d, d, d);
}

function recordsQuery(q) {
  const where = ['1=1']; const args = [];
  if (q.user) { where.push('r.user_id=?'); args.push(+q.user); }
  if (q.from) { where.push('r.date>=?'); args.push(q.from); }
  if (q.to) { where.push('r.date<=?'); args.push(q.to); }
  if (q.kind === 'office' || q.kind === 'evening') { where.push('r.kind=?'); args.push(q.kind); }
  return db.prepare(`
    SELECT r.id,r.date,r.start_ts,r.end_ts,r.kind,r.end_manual,u.fio,u.id AS user_id
    FROM records r JOIN users u ON u.id=r.user_id
    WHERE ${where.join(' AND ')} ORDER BY r.id DESC LIMIT 1000`).all(...args);
}

function adminCount() {
  return db.prepare("SELECT COUNT(*) c FROM users WHERE role='admin' AND active=1").get().c;
}

async function api(req, res, url) {
  const route = `${req.method} ${url.pathname}`;
  const ip = req.socket.remoteAddress;

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
      'Set-Cookie': `sid=${token}; HttpOnly; SameSite=Strict; Path=/; Max-Age=${SESSION_MS / 1000}`,
    });
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
      const kind = b.kind === 'evening' ? 'evening' : 'office';
      const open = db.prepare('SELECT kind FROM records WHERE user_id=? AND end_ts IS NULL').get(user.id);
      if (open) fail(400, open.kind === kind ? 'Вы уже отметили начало' : `Сначала завершите ${KINDS[open.kind]}`);
      db.prepare('INSERT INTO records(user_id,date,start_ts,kind) VALUES(?,?,?,?)').run(user.id, today(), now(), kind);
      addLog(user.id, kind === 'evening' ? 'Начал(а) вечернее время' : 'Начал(а) работу');
      return send(res, 200, myState(user.id));
    }
    case 'POST /api/finish': {
      const b = await readJson(req);
      const kind = b.kind === 'evening' ? 'evening' : 'office';
      const open = db.prepare('SELECT * FROM records WHERE user_id=? AND kind=? AND end_ts IS NULL ORDER BY id DESC').get(user.id, kind);
      if (!open) fail(400, 'Сначала отметьте начало');
      let end = now(), manual = 0;
      if (b.end) { // сотрудник сам указал время окончания (после напоминания «вы ещё работаете?»)
        const t = new Date(b.end);
        if (isNaN(t) || t <= new Date(open.start_ts)) fail(400, 'Время окончания должно быть позже начала работы');
        if (t > new Date(Date.now() + 60000)) fail(400, 'Время окончания не может быть в будущем');
        end = t.toISOString(); manual = 1;
      }
      db.prepare('UPDATE records SET end_ts=?, end_manual=? WHERE id=?').run(end, manual, open.id);
      const what = kind === 'evening' ? 'Закончил(а) вечернее время' : 'Закончил(а) работу';
      addLog(user.id, manual ? what + ' (время указано вручную)' : what,
        manual ? `Указано окончание: ${new Date(end).toLocaleString('ru-RU')}; отметка сделана ${new Date().toLocaleString('ru-RU')}` : '');
      return send(res, 200, myState(user.id));
    }
    case 'POST /api/confirm': {
      const b = await readJson(req);
      const kind = b.kind === 'evening' ? 'evening' : 'office';
      const open = db.prepare('SELECT * FROM records WHERE user_id=? AND kind=? AND end_ts IS NULL ORDER BY id DESC').get(user.id, kind);
      if (!open) fail(400, 'Нет активной отметки');
      db.prepare('UPDATE records SET confirmed_ts=? WHERE id=?').run(now(), open.id);
      addLog(user.id, 'Подтвердил(а), что ещё работает', kind === 'evening' ? 'вечернее время' : 'офис');
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
        return send(res, 200, recordsQuery({ ...q, kind: 'evening' }));
      case 'GET /api/admin/log':
        return send(res, 200, db.prepare(`SELECT l.id,l.ts,l.action,l.details,u.fio FROM log l LEFT JOIN users u ON u.id=l.user_id ORDER BY l.id DESC LIMIT 500`).all());
      case 'GET /api/admin/docs':
        return send(res, 200, db.prepare(`SELECT d.id,d.title,d.filename,d.size,d.created_at,u.fio FROM docs d JOIN users u ON u.id=d.user_id ORDER BY d.id DESC`).all());
      case 'GET /api/admin/users':
        return send(res, 200, db.prepare('SELECT id,login,fio,role,active,created_at FROM users ORDER BY active DESC, fio').all());
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
      case 'GET /api/admin/export.csv': {
        const rows = recordsQuery(q);
        const esc = v => `"${String(v ?? '').replace(/"/g, '""')}"`;
        const fmt = t => t ? new Date(t).toLocaleString('ru-RU') : '';
        const lines = ['ФИО;Тип;Дата;Начало;Конец;Часов'].concat(rows.map(r => {
          const h = r.end_ts ? ((new Date(r.end_ts) - new Date(r.start_ts)) / 3600000).toFixed(2) : '';
          return [r.fio, r.kind === 'evening' ? 'вечернее время' : 'офис', r.date, fmt(r.start_ts), fmt(r.end_ts), h].map(esc).join(';');
        }));
        return send(res, 200, '﻿' + lines.join('\r\n'), {
          'Content-Type': 'text/csv; charset=utf-8', 'Content-Disposition': 'attachment; filename="tabel.csv"',
        });
      }
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
        setPassword(target.id, String(b.password));
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
