// Минимальный генератор .xlsx без внешних библиотек (работает и на сервере, и в браузере).
// Файл .xlsx — это zip-архив с XML; архив пишем без сжатия.
(function (root) {
  const enc = new TextEncoder();
  const esc = s => String(s ?? '')
    .replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F]/g, '')
    .replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const colName = i => { let s = ''; for (i++; i > 0; i = Math.floor((i - 1) / 26)) s = String.fromCharCode(65 + (i - 1) % 26) + s; return s; };

  const CRC = (() => { const t = []; for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
  const crc32 = u8 => { let c = 0xFFFFFFFF; for (const b of u8) c = CRC[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; };

  function zip(files) { // files: [{ name, data: Uint8Array }]
    const parts = [], central = []; let offset = 0;
    const d = new Date(), time = (d.getHours() << 11) | (d.getMinutes() << 5) | (d.getSeconds() >> 1),
      date = ((d.getFullYear() - 1980) << 9) | ((d.getMonth() + 1) << 5) | d.getDate();
    for (const f of files) {
      const name = enc.encode(f.name), crc = crc32(f.data), size = f.data.length;
      const h = new DataView(new ArrayBuffer(30));
      h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x0800, true); h.setUint16(8, 0, true);
      h.setUint16(10, time, true); h.setUint16(12, date, true); h.setUint32(14, crc, true);
      h.setUint32(18, size, true); h.setUint32(22, size, true); h.setUint16(26, name.length, true); h.setUint16(28, 0, true);
      parts.push(new Uint8Array(h.buffer), name, f.data);
      const c = new DataView(new ArrayBuffer(46));
      c.setUint32(0, 0x02014b50, true); c.setUint16(4, 20, true); c.setUint16(6, 20, true); c.setUint16(8, 0x0800, true); c.setUint16(10, 0, true);
      c.setUint16(12, time, true); c.setUint16(14, date, true); c.setUint32(16, crc, true);
      c.setUint32(20, size, true); c.setUint32(24, size, true); c.setUint16(28, name.length, true); c.setUint32(42, offset, true);
      central.push(new Uint8Array(c.buffer), name);
      offset += 30 + name.length + size;
    }
    const cdSize = central.reduce((n, p) => n + p.length, 0);
    const e = new DataView(new ArrayBuffer(22));
    e.setUint32(0, 0x06054b50, true); e.setUint16(8, files.length, true); e.setUint16(10, files.length, true);
    e.setUint32(12, cdSize, true); e.setUint32(16, offset, true);
    const all = [...parts, ...central, new Uint8Array(e.buffer)];
    const out = new Uint8Array(all.reduce((n, p) => n + p.length, 0)); let pos = 0;
    for (const p of all) { out.set(p, pos); pos += p.length; }
    return out;
  }

  // Стили: 0 обычный, 1 заголовок, 2 число 0.00, 3 жирное число, 4 жирный текст
  const STYLES = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFDDE8FB"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="2" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="2" fontId="1" fillId="0" borderId="0" xfId="0" applyNumberFormat="1" applyFont="1"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`;

  function sheetXml(sh) {
    const rows = sh.rows.map((row, r) => `<row r="${r + 1}">` + row.map((cell, c) => {
      const o = (cell !== null && typeof cell === 'object') ? cell : { v: cell };
      const s = r === 0 ? 1 : (o.s || 0), ref = colName(c) + (r + 1), st = s ? ` s="${s}"` : '';
      if (o.v === null || o.v === undefined || o.v === '') return s ? `<c r="${ref}"${st}/>` : '';
      if (typeof o.v === 'number') return `<c r="${ref}"${st}><v>${o.v}</v></c>`;
      return `<c r="${ref}"${st} t="inlineStr"><is><t xml:space="preserve">${esc(o.v)}</t></is></c>`;
    }).join('') + '</row>').join('');
    const cols = sh.widths ? '<cols>' + sh.widths.map((w, i) => `<col min="${i + 1}" max="${i + 1}" width="${w}" customWidth="1"/>`).join('') + '</cols>' : '';
    return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>${cols}<sheetData>${rows}</sheetData></worksheet>`;
  }

  // sheets: [{ name, widths?, rows: [[ячейка | {v, s}]] }], первая строка — заголовок
  function build(sheets) {
    const n = sheets.length, X = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>';
    const files = [
      ['[Content_Types].xml', `${X}<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>${sheets.map((_, i) => `<Override PartName="/xl/worksheets/sheet${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')}</Types>`],
      ['_rels/.rels', `${X}<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>`],
      ['xl/workbook.xml', `${X}<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>${sheets.map((s, i) => `<sheet name="${esc(s.name.replace(/[\\/?*[\]:]/g, ' ').slice(0, 31))}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join('')}</sheets></workbook>`],
      ['xl/_rels/workbook.xml.rels', `${X}<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">${sheets.map((_, i) => `<Relationship Id="rId${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`).join('')}<Relationship Id="rId${n + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>`],
      ['xl/styles.xml', STYLES],
      ...sheets.map((s, i) => [`xl/worksheets/sheet${i + 1}.xml`, sheetXml(s)]),
    ];
    return zip(files.map(([name, text]) => ({ name, data: enc.encode(text) })));
  }

  // Отчёт по отметкам: подробный лист и итоги по сотрудникам
  const LABEL = { office: 'Офис', evening: 'Вечернее время', remote: 'Удалённо' };
  function report(records) {
    // часы = конец − начало − паузы (перерывы и время, когда рабочий ПК был выключен)
    const hrs = r => r.end_ts ? Math.round((new Date(r.end_ts) - new Date(r.start_ts) - (r.paused_ms || 0)) / 36000) / 100 : null;
    const tm = t => t ? new Date(t).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }) : '';
    const dm = d => String(d).split('-').reverse().join('.');
    const sorted = [...records].sort((a, b) => a.fio.localeCompare(b.fio, 'ru') || a.date.localeCompare(b.date) || a.start_ts.localeCompare(b.start_ts));

    const r2 = n => Math.round(n * 100) / 100;
    const pmin = ms => Math.round((ms || 0) / 60000);

    // ----- по дням: один сотрудник за один день -----
    const days = new Map();
    for (const r of sorted) {
      const key = r.fio + '|' + r.date;
      const d = days.get(key) || { fio: r.fio, date: r.date, start: null, end: null, open: false, pauses: [], pauseMs: 0, office: 0, evening: 0, remote: 0 };
      days.set(key, d);
      const h = hrs(r);
      const office = r.kind === 'office' || !LABEL[r.kind];
      if (office) {
        if (!d.start || r.start_ts < d.start) d.start = r.start_ts;
        if (r.end_ts) { if (!d.end || r.end_ts > d.end) d.end = r.end_ts; } else d.open = true;
      }
      d.pauseMs += r.paused_ms || 0;
      for (const p of r.pauses || []) {   // паузы вечером и удалённо подписываются типом смены
        const tag = office ? '' : LABEL[r.kind].toLowerCase() + ': ';
        d.pauses.push(tag + (p.end_ts
          ? `${tm(p.start_ts)}–${tm(p.end_ts)} (${pmin(new Date(p.end_ts) - new Date(p.start_ts))} мин${p.kind === 'power' ? ', компьютер был выключен' : p.planned_min ? ', планировалось ' + p.planned_min : ''})`
          : `с ${tm(p.start_ts)} (идёт)`));
      }
      if (h !== null) d[office ? 'office' : r.kind] += h;
    }
    const daily = [['ФИО', 'Дата', 'Начал(а)', 'Закончил(а)', 'Паузы', 'Пауза всего, мин', 'Рабочее время (офис), ч', 'Вечернее время, ч', 'Удалённо, ч', 'Итого за день, ч']];
    for (const d of days.values()) {
      daily.push([d.fio, dm(d.date), tm(d.start), d.open ? 'не завершено' : tm(d.end), d.pauses.join('; '), { v: pmin(d.pauseMs) },
        { v: r2(d.office), s: 2 }, { v: r2(d.evening), s: 2 }, { v: r2(d.remote), s: 2 }, { v: r2(d.office + d.evening + d.remote), s: 3 }]);
    }

    const detail = [['ФИО', 'Тип', 'Дата', 'Начало', 'Конец', 'Пауза, мин', 'Часов', 'Примечание']];
    const sums = new Map();
    for (const r of sorted) {
      const h = hrs(r);
      detail.push([r.fio, LABEL[r.kind] || 'Офис', dm(r.date), tm(r.start_ts), tm(r.end_ts), { v: pmin(r.paused_ms) }, { v: h, s: 2 },
        r.end_ts ? (r.end_manual === 2 ? 'закрыто автоматически (нет связи с ПК)' : r.end_manual ? 'время окончания указано вручную' : '') : 'не завершено']);
      if (h !== null) {
        const s = sums.get(r.fio) || { office: 0, evening: 0, remote: 0 };
        s[LABEL[r.kind] ? r.kind : 'office'] += h; sums.set(r.fio, s);
      }
    }
    const total = [['ФИО', 'Офис, ч', 'Вечернее время, ч', 'Удалённо, ч', 'Всего, ч']];
    const grand = { office: 0, evening: 0, remote: 0 };
    for (const [fio, s] of sums) {
      total.push([fio, { v: r2(s.office), s: 2 }, { v: r2(s.evening), s: 2 }, { v: r2(s.remote), s: 2 }, { v: r2(s.office + s.evening + s.remote), s: 3 }]);
      for (const k in grand) grand[k] += s[k];
    }
    total.push([{ v: 'Итого', s: 4 }, { v: r2(grand.office), s: 3 }, { v: r2(grand.evening), s: 3 }, { v: r2(grand.remote), s: 3 }, { v: r2(grand.office + grand.evening + grand.remote), s: 3 }]);
    return build([
      { name: 'По дням', widths: [32, 12, 10, 12, 60, 14, 20, 16, 12, 16], rows: daily },
      { name: 'Отметки', widths: [32, 18, 12, 10, 10, 11, 9, 34], rows: detail },
      { name: 'Итого по сотрудникам', widths: [32, 12, 20, 14, 12], rows: total },
    ]);
  }

  const api = { build, report };
  if (typeof module !== 'undefined' && module.exports) module.exports = api; else root.XlsxLite = api;
})(typeof self !== 'undefined' ? self : this);
