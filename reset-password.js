// Аварийный сброс пароля с сервера (например, если администратор забыл свой пароль).
// Запуск при остановленном сайте:  node reset-password.js <логин> <новый пароль>
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');

const [login, pw] = process.argv.slice(2);
if (!login || !pw || pw.length < 6) {
  console.log('Использование: node reset-password.js <логин> <новый пароль (от 6 символов)>');
  process.exit(1);
}
const DATA = process.env.DATA_DIR || path.join(__dirname, 'data');
const db = new DatabaseSync(path.join(DATA, 'tracker.db'));
const u = db.prepare('SELECT id FROM users WHERE login=?').get(login.toLowerCase());
if (!u) { console.log('Пользователь не найден'); process.exit(1); }

const key = fs.readFileSync(path.join(DATA, 'secret.key'));
const iv = crypto.randomBytes(12);
const c = crypto.createCipheriv('aes-256-gcm', key, iv);
const enc = Buffer.concat([c.update(pw, 'utf8'), c.final()]);
const pwEnc = Buffer.concat([iv, c.getAuthTag(), enc]).toString('base64');
const salt = crypto.randomBytes(16).toString('hex');
db.prepare('UPDATE users SET salt=?,hash=?,pw_enc=?,must_change=0 WHERE id=?')
  .run(salt, crypto.scryptSync(pw, salt, 64).toString('hex'), pwEnc, u.id);
db.prepare('DELETE FROM sessions WHERE user_id=?').run(u.id);
db.prepare('UPDATE reset_requests SET done=1 WHERE user_id=?').run(u.id);
console.log(`Пароль пользователя ${login} изменён.`);
