"use strict";

// ===================================================================
// 共有期間状態（P1c: 機能①）
// サンプルデータは 2026-06-27 固定。判定に Date.now を使わない。
// ===================================================================
const SAMPLE_DATE = "2026-06-27";
// 期間の基準日（アンカー）。Live は実日付(JST)、Sample は固定日。boot() で確定する。
let ANCHOR = SAMPLE_DATE;
const period = { from: ANCHOR, to: ANCHOR };

// JST の今日を YYYY-MM-DD で返す（ブラウザのタイムゾーンに依存しない）。
function jstToday() {
  return new Date().toLocaleDateString("sv-SE", { timeZone: "Asia/Tokyo" });
}
// 日付(YYYY-MM-DD)の加算・月初・月末（UTC基準でTZ非依存）。
function addDays(d, n) {
  const [y, m, dd] = d.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, dd + n)).toISOString().slice(0, 10);
}
function monthStart(d) { const [y, m] = d.split("-"); return `${y}-${m}-01`; }
function monthEnd(d) {
  const [y, m] = d.split("-").map(Number);
  const last = new Date(Date.UTC(y, m, 0)).getUTCDate();
  return `${y}-${String(m).padStart(2, "0")}-${String(last).padStart(2, "0")}`;
}

// P4 書込モック通知文言（全「保存」ボタン共通）
const PROTO_MSG = "プロトタイプ：本番では P4 の限定書込ロールで保存されます（現在は未永続）";

// ===================================================================
// DOM / ユーティリティ
// ===================================================================
const view = document.getElementById("view");
const $ = (sel, ctx = document) => ctx.querySelector(sel);
const $$ = (sel, ctx = document) => [...ctx.querySelectorAll(sel)];

function nextDay(d) { return addDays(d, 1); }
const fromIso = (d) => d + "T00:00:00+09:00";
const toIso   = (d) => nextDay(d) + "T00:00:00+09:00";

function fmtTime(iso) {
  const dt = new Date(iso);
  const p = (n) => String(n).padStart(2, "0");
  return `${p(dt.getMonth() + 1)}/${p(dt.getDate())} ${p(dt.getHours())}:${p(dt.getMinutes())}`;
}
function shortFile(path) {
  if (!path) return "";
  const i = path.indexOf("Data\\");
  return i >= 0 ? path.slice(i + 5) : path;
}
// ファイル列のフルパス表示。
// 🟦ビューアー: folder(フォルダ経路) + file(ファイル名) を結合して経路まで出す。
// 🟥直接/⬜未帰属: file が既にフルパスなので Data\ 以降に短縮。
function fullPath(r) {
  if (r.source === "viewer" && r.folder) {
    // 先頭の SFE 表示名「技術部データ」を実フォルダ名「技術部」に揃える（直接アクセスと統一）。
    const base = String(r.folder).replace(/[\\/]+$/, "").replace(/^技術部データ(?=\\|$)/, "技術部");
    return r.file ? base + "\\" + r.file : base;
  }
  return shortFile(r.file);
}
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const num = (n) => Number(n).toLocaleString();
const getJson = (url) => fetch(url).then((r) => r.json());

// ユーザー表示名（設定 users.display_name）。アカウント名→表示名。boot()/設定保存後に読み込む。
// ドリル/検索/遷移は内部的にアカウント名を使うため、ここでは「表示」だけ差し替える。
let USER_MAP = {};
function userDisp(name) { return USER_MAP[String(name ?? "").toLowerCase()] || (name ?? ""); }
function userCell(name) {
  const raw = String(name ?? "");
  const d = USER_MAP[raw.toLowerCase()];
  return d ? `<span title="${esc(raw)}">${esc(d)}</span>` : esc(raw);
}
async function loadUserMap() {
  try {
    const s = await getJson("/api/settings");
    const m = {};
    (s.users || []).forEach((u) => {
      const acc = String(u.name ?? "").toLowerCase();
      if (acc && u.display) m[acc] = u.display;
    });
    USER_MAP = m;
  } catch { /* 取得失敗時は空(=アカウント名のまま表示) */ }
}

/** 現在の共有期間をクエリパラメータにセットする。extra は追加パラメータ {key:value}。 */
function dateParams(extra) {
  const p = new URLSearchParams();
  p.set("from", fromIso(period.from));
  p.set("to",   toIso(period.to));
  if (extra) for (const [k, v] of Object.entries(extra)) if (v != null && v !== "") p.set(k, String(v));
  return p;
}

const SRC_LABEL = { viewer: "🟦 ビューアー経由", direct: "🟥 サーバー直接", unknown: "⬜ 未帰属" };
const SRC_CLS   = { viewer: "v", direct: "d", unknown: "u" };
const sevCls    = (s) => "sev " + String(s).toLowerCase();

const KIND_LABEL = { read: "閲覧/読取", write: "編集", delete: "削除", copy: "コピー疑い", search: "検索", login: "ログイン" };
function kindLabel(k) { return KIND_LABEL[String(k).toLowerCase()] || k; }

// ===================================================================
// 期間バー (P1c: 機能①)
// ===================================================================
function periodBarHtml() {
  return `<div class="period-bar">
    <span class="t">期間:</span>
    <input type="date" id="pb-from" value="${period.from}">
    <span class="pb-sep">〜</span>
    <input type="date" id="pb-to" value="${period.to}">
    <button class="qbtn" data-q="day">当日(${ANCHOR.slice(5)})</button>
    <button class="qbtn" data-q="3days">直近3日(${addDays(ANCHOR, -2).slice(5)}〜${ANCHOR.slice(5)})</button>
    <button class="qbtn" data-q="month">当月(${monthStart(ANCHOR).slice(5)}〜${monthEnd(ANCHOR).slice(5)})</button>
  </div>`;
}
function bindPeriodBar(onApply) {
  $$(".qbtn", view).forEach((b) => b.onclick = () => {
    switch (b.dataset.q) {
      case "day":   period.from = period.to = ANCHOR; break;
      case "3days": period.from = addDays(ANCHOR, -2); period.to = ANCHOR; break;
      case "month": period.from = monthStart(ANCHOR); period.to = monthEnd(ANCHOR); break;
    }
    onApply();
  });
  const pbFrom = $("#pb-from", view);
  const pbTo   = $("#pb-to",   view);
  if (pbFrom) pbFrom.onchange = (e) => { period.from = e.target.value; onApply(); };
  if (pbTo)   pbTo.onchange   = (e) => { period.to   = e.target.value; onApply(); };
}

// ===================================================================
// 詳細スライドインパネル (P1c: 機能② + B3)
// ===================================================================
const dpOverlay = document.getElementById("dp-overlay");
const dpPanel   = document.getElementById("dp-panel");
const dpTitle   = document.getElementById("dp-title");
const dpBody    = document.getElementById("dp-body");

function openPanel(title, html) {
  dpTitle.textContent = title;
  dpBody.innerHTML    = html;
  dpOverlay.classList.add("open");
  dpPanel.classList.add("open");
}
function closePanel() {
  dpOverlay.classList.remove("open");
  dpPanel.classList.remove("open");
}

/** ログ行詳細パネル（P1c: 機能②）。 */
function openDetail(r) {
  openPanel("アクセス詳細", [
    ["ID",       esc(r.id)],
    ["日時",     fmtTime(r.time)],
    ["ソース",   `<span class="src ${r.source}">${SRC_LABEL[r.source]}</span>`],
    ["部署",     esc(r.dept)],
    ["ユーザー", userCell(r.user)],
    ["操作",     `<span class="op ${r.kind}">${esc(r.action)}</span>`],
    ["ファイル", `<span class="dp-val file">${esc(fullPath(r) || "—")}</span>`],
    ["フォルダ", `<span class="dp-val file">${esc(r.folder || "—")}</span>`],
    ["PC",       esc(r.pc  || "—")],
    ["IP",       esc(r.ip  || "—")],
    ["結果",     `<span class="${r.success ? "ok" : "ng"}">${r.success ? "OK" : "拒否"}</span>`],
    ...(r.note ? [["メモ", esc(r.note)]] : []),
  ].map(([lbl, val]) =>
    `<div class="dp-row"><div class="dp-label">${lbl}</div><div class="dp-val">${val}</div></div>`
  ).join(""));
}

dpOverlay.onclick = closePanel;
document.getElementById("dp-close").onclick = closePanel;

// ===================================================================
// トースト (#0 Part A)
// ===================================================================
function showToast(msg) {
  const t = document.getElementById("toast");
  t.textContent = msg;
  t.classList.add("show");
  clearTimeout(t._tid);
  t._tid = setTimeout(() => t.classList.remove("show"), 3800);
}

// ===================================================================
// モーダル (#0 Part A)
// ===================================================================
function openModal(title, bodyHtml, saveFn) {
  const box = document.getElementById("modal-box");
  box.innerHTML = `
    <div class="modal-header"><span>${esc(title)}</span><button id="mc-x">×</button></div>
    <div class="modal-body">${bodyHtml}</div>
    <div class="modal-footer">
      <button id="mc-cancel">キャンセル</button>
      <button class="primary" id="mc-save">保存</button>
    </div>`;
  const ov = document.getElementById("modal-overlay");
  ov.classList.add("open");
  const close = () => ov.classList.remove("open");
  $("#mc-x").onclick      = close;
  $("#mc-cancel").onclick = close;
  ov.onclick = (e) => { if (e.target === ov) close(); };
  // P4a: saveFn が Promise を返す場合は API 呼出。
  // false を返した場合はバリデーションエラー（モーダルを閉じない）。
  // undefined/null を返した場合はクライアントモック（PROTO_MSG トースト）。
  $("#mc-save").onclick = async () => {
    const result = saveFn();
    if (result === false) return;
    if (result && typeof result.then === "function") {
      const btn = $("#mc-save");
      btn.disabled = true;
      try {
        await result;
        close();
        showToast("保存しました");
      } catch (e) {
        showToast("エラー: " + (e.message || String(e)));
      } finally {
        btn.disabled = false;
      }
    } else {
      close(); showToast(PROTO_MSG);
    }
  };
}

// Escape キーで両パネル・モーダルを閉じる
document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  const mo = document.getElementById("modal-overlay");
  if (mo.classList.contains("open")) { mo.classList.remove("open"); return; }
  closePanel();
});

// ===================================================================
// ① ダッシュボード  #/dashboard
// ===================================================================
async function dashboard() {
  const d = await getJson("/api/dashboard?" + dateParams());
  const s = d.summary;
  const pct = Math.round(s.adoptionRate * 100);

  const kpisHtml = `<div class="kpis">
    <div class="kpi"><div class="lbl">期間内アクセス（合計）</div><div class="num">${num(s.total)}</div></div>
    <div class="kpi viewer clickable" data-drill-src="viewer" title="クリックで検索へ">
      <div class="lbl">🟦 ビューアー経由</div><div class="num">${num(s.viewer)}</div></div>
    <div class="kpi direct clickable" data-drill-src="direct" title="クリックで検索へ">
      <div class="lbl">🟥 サーバー直接</div><div class="num">${num(s.direct)}</div>
      <div class="sub2">実ユーザー ${s.directUsers}名 / ${s.directFiles}ファイル</div></div>
    <div class="kpi unknown clickable" data-drill-src="unknown" title="クリックで検索へ">
      <div class="lbl">⬜ 未帰属(MTSV$/不明)</div><div class="num">${num(s.unknown)}</div></div>
    <div class="kpi rate"><div class="lbl">ビューアー利用率</div><div class="num">${pct}%</div>
      <div class="sub2">青/(青+赤)・GAP ${s.gapMinutes}分を除外</div></div>
  </div>`;

  const hrs  = d.hourly.filter((h) => h.viewer + h.direct + h.unknown > 0);
  const maxH = Math.max(1, ...hrs.map((h) => h.viewer + h.direct + h.unknown));
  const barsHtml = hrs.map((h) => {
    const seg = (v, c) => v ? `<div class="seg ${c}" style="height:${(v / maxH) * 130}px" title="${c}:${v}"></div>` : "";
    return `<div class="hcol"><div class="hbar">${seg(h.viewer,"v")}${seg(h.direct,"d")}${seg(h.unknown,"u")}</div><div class="hx">${h.hour}時</div></div>`;
  }).join("") || `<div class="muted">データなし</div>`;

  const hbarsHtml = (list, cls, attr, labelFn) => {
    const mx = Math.max(1, ...list.map((x) => x.count));
    return list.map((x) => `
      <div class="hbrow clickable" ${attr}="${esc(x.name)}" title="${esc(x.name)} — クリックで検索へ">
        <span class="hbname">${esc(labelFn ? labelFn(x.name) : x.name)}</span>
        <span class="hbtrack"><span class="hbfill ${cls}" style="width:${(x.count / mx) * 100}%"></span></span>
        <span class="hbval">${num(x.count)}</span>
      </div>`).join("") || `<div class="muted">該当なし</div>`;
  };

  // B1: 操作種別内訳横棒
  const actBreak   = d.actionBreakdown || [];
  const actMax     = Math.max(1, ...actBreak.map((x) => x.count));
  const actBarsHtml = actBreak.map((x) => `
    <div class="hbrow">
      <span class="hbname">${esc(kindLabel(x.name))}</span>
      <span class="hbtrack"><span class="hbfill v" style="width:${(x.count / actMax) * 100}%"></span></span>
      <span class="hbval">${num(x.count)}</span>
    </div>`).join("") || `<div class="muted">該当なし</div>`;

  const _incCache = {};
  d.recentIncidents.forEach((i) => { _incCache[i.id] = i; });
  const incHtml = d.recentIncidents.map((i) => `
    <tr class="clickrow-inc" data-id="${i.id}">
      <td>${fmtTime(i.time)}</td><td><span class="tag">${esc(incTypeLabel(i.type))}</span></td>
      <td><span class="${sevCls(i.severity)}">${esc(sevLabel(i.severity))}</span></td>
      <td>${userCell(i.user)}</td><td>${num(i.matchCount)}</td>
      <td class="muted">${metricSummary(i.metric)}</td>
    </tr>`).join("") || `<tr><td colspan="6" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <div class="page-head">
      <h1>ダッシュボード</h1>
      <div class="sub">ビューアー経由（🟦）/ サーバー直接（🟥）/ 未帰属（⬜）で全社アクセスの効果を測定します。</div>
      ${periodBarHtml()}
    </div>
    ${kpisHtml}
    <div class="grid2">
      <div class="card">
        <h2>時間帯別アクセス（3色スタック）</h2>
        <div class="hourchart">${barsHtml}</div>
        <div class="legend">
          <span><span class="sw v"></span>🟦 ビューアー経由</span>
          <span><span class="sw d"></span>🟥 サーバー直接</span>
          <span><span class="sw u"></span>⬜ 未帰属</span>
        </div>
      </div>
      <div class="card">
        <h2>🟥 直接アクセス Top ユーザー <span class="muted small">（クリックで検索）</span></h2>
        <div class="hbars">${hbarsHtml(d.directTopUsers, "d", "data-drill-user", userDisp)}</div>
      </div>
    </div>
    <div class="grid2">
      <div class="card">
        <h2>部署別アクセス件数 <span class="muted small">（クリックで検索）</span></h2>
        <div class="hbars">${hbarsHtml(d.deptCounts, "v", "data-drill-dept")}</div>
      </div>
      <div class="card">
        <h2>操作種別内訳</h2>
        <div class="hbars">${actBarsHtml}</div>
      </div>
    </div>
    <div class="card">
      <h2>直近インシデント <span class="muted small">（行クリックで詳細）</span></h2>
      <table>
        <thead><tr><th>日時</th><th>種別</th><th>重要度</th><th>ユーザー</th><th>一致</th><th>指標</th></tr></thead>
        <tbody>${incHtml}</tbody>
      </table>
      <p class="muted small"><a href="#/incidents">→ 検知インシデント一覧へ</a></p>
    </div>`;

  bindPeriodBar(dashboard);

  $$("[data-drill-src]", view).forEach((el) =>
    el.onclick = () => drillToSearch({ sources: [el.dataset.drillSrc], user: "", dept: "", q: "" }));
  $$("[data-drill-user]", view).forEach((el) =>
    el.onclick = () => drillToSearch({ user: el.dataset.drillUser, sources: ["viewer","direct","unknown"], dept: "", q: "" }));
  $$("[data-drill-dept]", view).forEach((el) =>
    el.onclick = () => drillToSearch({ dept: el.dataset.drillDept, sources: ["viewer","direct","unknown"], user: "", q: "" }));

  // B3: インシデント行クリック → スライドインパネル（ダッシュボード版）
  $$(".clickrow-inc", view).forEach((tr) => {
    tr.style.cursor = "pointer";
    tr.onclick = () => openIncidentPanel(_incCache[Number(tr.dataset.id)]);
  });
}

// ===================================================================
// ② ログ検索  #/search
// ===================================================================
const searchState = {
  user: "", q: "", dept: "",
  sources: ["viewer", "direct", "unknown"],
  page: 1, pageSize: 50,
  sort: "time", desc: true,
};
let _rowCache = {};

/** P1c: ドリルダウン — 検索ステートを上書きして #/search へ。 */
function drillToSearch(updates) {
  Object.assign(searchState, { page: 1 }, updates);
  if (location.hash === "#/search") search(); else location.hash = "#/search";
}

// B5: localStorage 保存済み検索条件
const LS_PRESETS = "val-search-presets";
function loadPresets()  { try { return JSON.parse(localStorage.getItem(LS_PRESETS) || "[]"); } catch { return []; } }
function savePresets(p) { localStorage.setItem(LS_PRESETS, JSON.stringify(p)); }

async function search() {
  view.innerHTML = `
    <div class="page-head">
    <h1>ログ検索</h1>
    <div class="sub">🟦 ビューアー経由 / 🟥 サーバー直接 / ⬜ 未帰属 を区別して全社アクセスを検索します。</div>
    ${periodBarHtml()}
    <div class="srcfilter">
      <span class="t">ソース:</span>
      <span class="chip viewer on" data-src="viewer"><span class="dot"></span>🟦 ビューアー経由</span>
      <span class="chip direct on" data-src="direct"><span class="dot"></span>🟥 サーバー直接</span>
      <span class="chip unknown on" data-src="unknown"><span class="dot"></span>⬜ 未帰属(MTSV$/不明)</span>
    </div>
    <div class="card">
      <div class="filters">
        <div class="f"><label>部署</label><select id="dept"><option value="">（全部署）</option></select></div>
        <div class="f"><label>ユーザー</label><input type="text" id="user" placeholder="部分一致"></div>
        <div class="f"><label>検索語</label><input type="text" id="q" placeholder="ファイル名 / メモ"></div>
        <div class="btns">
          <button class="primary" id="run">検索</button>
          <button id="clear">クリア</button>
          <button id="csvbtn" class="csv-btn" title="全件 CSV をダウンロード">⬇ CSV</button>
          <button id="savebtn" title="現在の条件を保存">★ 保存</button>
        </div>
      </div>
      <div class="ops" id="kinds">
        <span class="lbl">操作:</span>
        <label><input type="checkbox" value="read"   checked>閲覧/読取</label>
        <label><input type="checkbox" value="write"  checked>編集</label>
        <label><input type="checkbox" value="delete" checked>削除</label>
        <label><input type="checkbox" value="copy"   checked>コピー疑い</label>
        <label><input type="checkbox" value="search" checked>検索</label>
        <label><input type="checkbox" value="login">ログイン</label>
      </div>
      <div id="presets-row" class="presets-row"></div>
    </div>
    </div>
    <div class="card">
      <h2>検索結果 <span id="count" class="muted"></span></h2>
      <table id="logtbl">
        <thead><tr>
          <th class="sortable" data-col="time">日時<span class="sort-ind"></span></th>
          <th>ソース</th>
          <th class="sortable" data-col="dept">部署<span class="sort-ind"></span></th>
          <th class="sortable" data-col="user">ユーザー<span class="sort-ind"></span></th>
          <th class="sortable" data-col="kind">操作<span class="sort-ind"></span></th>
          <th>ファイル</th><th>PC / IP</th><th>結果</th>
        </tr></thead>
        <tbody id="rows"></tbody>
      </table>
      <div class="pager"><span id="pageinfo"></span><span><button id="prev">◀</button> <button id="next">▶</button></span></div>
      <div class="legend">
        <span>🟦 <b>ビューアー経由</b>＝SFEログにある＝確実</span>
        <span>🟥 <b>サーバー直接</b>＝実ユーザーの直読み（要注目）</span>
        <span>⬜ <b>未帰属</b>＝MTSV$/サービス/不明</span>
        <span class="muted small">行クリックで詳細を表示</span>
      </div>
    </div>
    <p class="muted small">※ DataMode=Sample（サンプルデータ）。本番では SFE SQLite + AuditLogger を読み取り専用で同期します。</p>`;

  const filters = await getJson("/api/filters");
  const selDept = $("#dept");
  selDept.innerHTML = `<option value="">（全部署）</option>` +
    filters.depts.map((d) => `<option value="${esc(d)}"${d === searchState.dept ? " selected" : ""}>${esc(d)}</option>`).join("");

  $("#user").value = searchState.user;
  $("#q").value    = searchState.q;
  $$(".chip", view).forEach((c) => c.classList.toggle("on", searchState.sources.includes(c.dataset.src)));

  const selectedKinds = () => $$("#kinds input:checked").map((c) => c.value);

  function buildParams(forCsv = false) {
    const p = dateParams({ dept: searchState.dept, user: searchState.user, q: searchState.q });
    p.set("sources", searchState.sources.join(","));
    const kinds = selectedKinds();
    if (kinds.length) p.set("kinds", kinds.join(","));
    p.set("sort", searchState.sort);
    p.set("dir",  searchState.desc ? "desc" : "asc");
    if (!forCsv) { p.set("page", searchState.page); p.set("pageSize", searchState.pageSize); }
    return p;
  }

  function updateSortIndicators() {
    $$("th.sortable", view).forEach((th) => {
      const ind = $(".sort-ind", th);
      if (!ind) return;
      ind.textContent = th.dataset.col === searchState.sort ? (searchState.desc ? " ▼" : " ▲") : " ⇅";
      ind.classList.toggle("sort-active", th.dataset.col === searchState.sort);
    });
  }

  function renderPresets() {
    const presets = loadPresets();
    const row = $("#presets-row");
    if (!row) return;
    if (!presets.length) { row.innerHTML = ""; return; }
    row.innerHTML = `<span class="preset-lbl">保存した条件:</span>` +
      presets.map((p, i) =>
        `<span class="preset-chip">${esc(p.name)}<button class="preset-apply" data-i="${i}" title="適用">▶</button><button class="preset-del" data-i="${i}" title="削除">×</button></span>`
      ).join("");
    $$(".preset-apply", row).forEach((b) => b.onclick = () => {
      const p = loadPresets()[Number(b.dataset.i)]; if (!p) return;
      Object.assign(searchState, { user: p.user||"", q: p.q||"", dept: p.dept||"",
        sources: p.sources||["viewer","direct","unknown"], page: 1 });
      if (p.from) period.from = p.from;
      if (p.to)   period.to   = p.to;
      search();
    });
    $$(".preset-del", row).forEach((b) => b.onclick = () => {
      const ps = loadPresets(); ps.splice(Number(b.dataset.i), 1); savePresets(ps); renderPresets();
    });
  }

  async function loadLogs() {
    const data = await getJson("/api/logs?" + buildParams());
    _rowCache = {};
    data.rows.forEach((r) => { _rowCache[r.id] = r; });

    $("#rows").innerHTML = data.rows.map((r) => `
      <tr class="${SRC_CLS[r.source]} clickrow" data-id="${r.id}">
        <td>${fmtTime(r.time)}</td>
        <td><span class="src ${r.source}">${SRC_LABEL[r.source]}</span></td>
        <td>${esc(r.dept)}</td>
        <td>${userCell(r.user)}</td>
        <td><span class="op ${r.kind}">${esc(r.action)}</span></td>
        <td class="file">${esc(fullPath(r))}${r.note ? ` <span class="muted">— ${esc(r.note)}</span>` : ""}</td>
        <td>${esc(r.pc || "")}${r.ip ? " / " + r.ip.split(".").slice(-1) : ""}</td>
        <td class="${r.success ? "ok" : "ng"}">${r.success ? "OK" : "拒否"}</td>
      </tr>`).join("") || `<tr><td colspan="8" class="muted">該当なし</td></tr>`;

    $$("tr.clickrow", view).forEach((tr) => {
      tr.onclick = () => { const row = _rowCache[Number(tr.dataset.id)]; if (row) openDetail(row); };
    });

    const start = (data.page - 1) * data.pageSize + 1;
    const end   = Math.min(data.page * data.pageSize, data.total);
    $("#count").textContent    = `${num(data.total)} 件`;
    $("#pageinfo").textContent = data.total ? `${start}〜${end} / ${data.total}` : "0 件";
    updateSortIndicators();
  }

  bindPeriodBar(() => { searchState.page = 1; loadLogs(); });

  $$("th.sortable", view).forEach((th) => th.onclick = () => {
    const col = th.dataset.col;
    if (searchState.sort === col) searchState.desc = !searchState.desc;
    else { searchState.sort = col; searchState.desc = true; }
    searchState.page = 1; loadLogs();
  });

  $("#run").onclick = () => {
    searchState.user = $("#user").value.trim();
    searchState.q    = $("#q").value.trim();
    searchState.dept = selDept.value;
    searchState.page = 1; loadLogs();
  };
  $("#clear").onclick = () => {
    searchState.user = ""; searchState.q = ""; searchState.dept = "";
    $("#user").value = ""; $("#q").value = ""; selDept.value = "";
    searchState.page = 1; loadLogs();
  };
  $("#q").addEventListener("keydown", (e) => { if (e.key === "Enter") $("#run").click(); });
  $$(".chip", view).forEach((c) => c.onclick = () => {
    c.classList.toggle("on");
    searchState.sources = $$(".chip.on", view).map((x) => x.dataset.src);
    searchState.page = 1; loadLogs();
  });
  $$("#kinds input").forEach((c) => c.onchange = () => { searchState.page = 1; loadLogs(); });
  $("#prev").onclick = () => { if (searchState.page > 1) { searchState.page--; loadLogs(); } };
  $("#next").onclick = () => { searchState.page++; loadLogs(); };

  $("#csvbtn").onclick = () => { window.location.href = "/api/logs.csv?" + buildParams(true); };

  // B5: 現在の条件を名前をつけて保存（localStorage）
  $("#savebtn").onclick = () => {
    const name = prompt("保存する条件名を入力してください:", "");
    if (!name) return;
    const ps = loadPresets();
    ps.push({ name, user: searchState.user, q: searchState.q, dept: searchState.dept,
              sources: [...searchState.sources], from: period.from, to: period.to });
    savePresets(ps); renderPresets();
    showToast(`「${name}」を保存しました（localStorage に保存）`);
  };

  renderPresets();
  loadLogs();
}

// ===================================================================
// ③ ユーザー別  #/users  と  #/users/{name}
// ===================================================================
async function users(rest) {
  if (rest && rest.length && rest[0]) return userDetail(decodeURIComponent(rest[0]));

  const list = await getJson("/api/users?" + dateParams());
  const body = list.map((u) => `
    <tr class="clickable" data-name="${esc(u.user)}">
      <td><b>${userCell(u.user)}</b></td><td>${esc(u.dept)}</td>
      <td class="cv">${num(u.viewer)}</td><td class="cd">${num(u.direct)}</td>
      <td class="cu">${num(u.unknown)}</td><td>${fmtTime(u.lastAccess)}</td>
    </tr>`).join("") || `<tr><td colspan="6" class="muted">該当なし</td></tr>`;

  view.innerHTML = `
    <div class="page-head">
      <h1>ユーザー別</h1>
      <div class="sub">ユーザーごとに 🟦/🟥/⬜ を横断表示。行クリックでタイムライン詳細を開きます。</div>
    </div>
    <div class="card">
      <table>
        <thead><tr><th>ユーザー</th><th>部署</th><th>🟦 ビューアー</th><th>🟥 直接</th><th>⬜ 未帰属</th><th>最終アクセス</th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;

  $$("tr.clickable", view).forEach((tr) => {
    tr.onclick = () => { location.hash = "#/users/" + encodeURIComponent(tr.dataset.name); };
  });
}

async function userDetail(name) {
  const res = await fetch("/api/user?" + dateParams({ name }));
  if (!res.ok) {
    view.innerHTML = `<h1>ユーザー別</h1><div class="card muted">「${esc(name)}」のデータがありません。<a href="#/users">← 一覧へ</a></div>`;
    return;
  }
  const d = await res.json();
  const total = d.viewer + d.direct + d.unknown;

  // B2: 時間帯別スタック棒
  const hrs  = (d.hourly || []).filter((h) => h.viewer + h.direct + h.unknown > 0);
  const maxH = Math.max(1, ...hrs.map((h) => h.viewer + h.direct + h.unknown));
  const userBarsHtml = hrs.map((h) => {
    const seg = (v, c) => v ? `<div class="seg ${c}" style="height:${(v / maxH) * 100}px" title="${c}:${v}"></div>` : "";
    return `<div class="hcol"><div class="hbar">${seg(h.viewer,"v")}${seg(h.direct,"d")}${seg(h.unknown,"u")}</div><div class="hx">${h.hour}時</div></div>`;
  }).join("") || `<div class="muted">データなし</div>`;

  // B2: 操作種別内訳横棒
  const acts   = d.actionBreakdown || [];
  const actMax = Math.max(1, ...acts.map((x) => x.count));
  const actHtml = acts.map((x) => `
    <div class="hbrow">
      <span class="hbname">${esc(kindLabel(x.name))}</span>
      <span class="hbtrack"><span class="hbfill v" style="width:${(x.count / actMax) * 100}%"></span></span>
      <span class="hbval">${num(x.count)}</span>
    </div>`).join("") || `<div class="muted">データなし</div>`;

  // タイムラインは最新時間順（降順）で表示する。
  const timeline = [...d.timeline]
    .sort((a, b) => new Date(b.time) - new Date(a.time))
    .map((r) => `
    <div class="tl ${SRC_CLS[r.source]}">
      <span class="tl-t">${fmtTime(r.time)}</span>
      <span class="src ${r.source}">${SRC_LABEL[r.source]}</span>
      <span class="op ${r.kind}">${esc(r.action)}</span>
      <span class="tl-f file">${esc(fullPath(r))}</span>
      ${r.note  ? `<span class="muted">— ${esc(r.note)}</span>` : ""}
      ${r.success ? "" : `<span class="ng">拒否</span>`}
    </div>`).join("");

  const drillBtn = `<button class="qbtn" onclick="drillToSearch({user:'${esc(d.user)}',sources:['viewer','direct','unknown'],dept:'',q:''})">ログ検索で絞り込む →</button>`;

  view.innerHTML = `
    <div class="page-head">
      <h1><a href="#/users" class="back">ユーザー別</a> / ${userCell(d.user)}</h1>
      <div class="sub">部署: <b>${esc(d.dept)}</b> ・ 期間内 ${num(total)} 件 &nbsp;${drillBtn}</div>
    </div>
    <div class="kpis">
      <div class="kpi viewer"><div class="lbl">🟦 ビューアー経由</div><div class="num">${num(d.viewer)}</div></div>
      <div class="kpi direct"><div class="lbl">🟥 サーバー直接</div><div class="num">${num(d.direct)}</div></div>
      <div class="kpi unknown"><div class="lbl">⬜ 未帰属</div><div class="num">${num(d.unknown)}</div></div>
    </div>
    <div class="grid2">
      <div class="card">
        <h2>時間帯別アクセス（3色）</h2>
        <div class="hourchart" style="height:130px">${userBarsHtml}</div>
        <div class="legend">
          <span><span class="sw v"></span>🟦</span>
          <span><span class="sw d"></span>🟥</span>
          <span><span class="sw u"></span>⬜</span>
        </div>
      </div>
      <div class="card">
        <h2>操作種別内訳</h2>
        <div class="hbars">${actHtml}</div>
      </div>
    </div>
    <div class="card">
      <h2>タイムライン（時系列・3色）</h2>
      <div class="timeline">${timeline || `<div class="muted">データなし</div>`}</div>
    </div>`;
}

// ===================================================================
// ④ アラート  #/alerts   (B4: 行クリック → ログ検索へドリルダウン)
// ===================================================================
async function alerts() {
  const list = await getJson("/api/alerts");

  // P4b: 状態変更ヘルパー（PATCH /api/alerts/{id}/status）
  async function patchAlertStatus(id, status) {
    const r = await fetch(`/api/alerts/${id}/status`, {
      method: "PATCH", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ status })
    });
    if (!r.ok) throw new Error(await r.text());
    showToast("状態を更新しました");
    alerts(); // 一覧再取得
  }

  const body = list.map((a) => `
    <tr class="clickable" data-user="${esc(a.user)}" title="${esc(a.user)} のログを検索">
      <td>${fmtTime(a.time)}</td>
      <td><span class="${sevCls(a.severity)}">${esc(a.severity)}</span></td>
      <td><span class="tag">${esc(a.rule)}</span></td>
      <td>${userCell(a.user)}</td>
      <td>${num(a.count)}</td>
      <td><span class="state">${esc(a.status)}</span></td>
      <td style="white-space:nowrap">
        <button class="btn-ack" data-id="${a.id}" data-status="ack"
          title="確認済みにする" ${a.status==="ack"||a.status==="closed"?"disabled":""}>確認</button>
        <button class="btn-close-st" data-id="${a.id}" data-status="closed"
          title="クローズ" ${a.status==="closed"?"disabled":""}>クローズ</button>
      </td>
    </tr>`).join("") || `<tr><td colspan="7" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <div class="page-head">
      <h1>アラート</h1>
      <div class="sub">ルール検知の一覧。行クリックで該当ユーザーのログを検索。「確認」「クローズ」は alert_histories の status 列のみ変更します。</div>
    </div>
    <div class="card">
      <table>
        <thead><tr><th>日時</th><th>重要度</th><th>ルール</th><th>ユーザー</th><th>件数</th><th>状態</th><th></th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;

  // B4: 行クリック → drillToSearch（ボタンのクリックは伝播しない）
  $$("tr.clickable", view).forEach((tr) => {
    tr.onclick = (e) => {
      if (e.target.tagName === "BUTTON") return;
      drillToSearch({ user: tr.dataset.user, sources: ["viewer","direct","unknown"], dept: "", q: "" });
    };
  });
  $$(".btn-ack, .btn-close-st", view).forEach((b) => {
    if (b.disabled) return;
    b.onclick = async (e) => {
      e.stopPropagation();
      try { await patchAlertStatus(Number(b.dataset.id), b.dataset.status); }
      catch (err) { showToast("エラー: " + (err.message || String(err))); }
    };
  });
}

// ===================================================================
// ⑤ 検知インシデント  #/incidents   (B3: スライドインパネル + B4: ドリルダウン)
// ===================================================================
// ===================================================================
// 検知インシデントの日本語表記
// ===================================================================
const INC_TYPE_LABEL = {
  CROSS_DEPT_ACCESS: "部署外アクセス",
  BULK_CONTENT_READ: "大量読み取り（持ち出し前兆）",
  SERVER_EXTERNAL_COPY_SUSPECTED: "外部コピー疑い",
  BULK_DELETE: "大量削除",
  OFF_HOURS_ACCESS: "時間外アクセス",
  MASS_DOWNLOAD: "大量ダウンロード",
};
function incTypeLabel(t) { return INC_TYPE_LABEL[String(t).toUpperCase()] || t; }

const INC_STATUS_LABEL = {
  new: "新規", open: "新規", ack: "確認済", acknowledged: "確認済",
  investigating: "調査中", closed: "クローズ済", resolved: "解決済",
  false_positive: "誤検知", dismissed: "却下",
};
function incStatusLabel(s) { return INC_STATUS_LABEL[String(s).toLowerCase()] || s; }

const SEV_LABEL = { low: "低", medium: "中", high: "高", critical: "重大" };
function sevLabel(s) { return SEV_LABEL[String(s).toLowerCase()] || s; }

// 指標(metrics JSON) → 日本語の短いサマリー。0や欠落は省く。
const METRIC_LABELS = [
  ["distinct_files",      (v) => `対象ファイル${num(v)}件`],
  ["distinct_folders",    (v) => `フォルダ${num(v)}`],
  ["window_minutes",      (v) => `直近${v}分`],
  ["deleted_after_read",  (v) => Number(v) > 0 ? `読取後削除${num(v)}` : null],
  ["external_like_files", (v) => Number(v) > 0 ? `外部送信様${num(v)}` : null],
  ["threshold",           (v) => `しきい値${v}`],
];
function parseMetric(m) {
  if (m == null) return null;
  if (typeof m === "object") return m;
  try { return JSON.parse(m); } catch { return null; }
}
function metricSummary(m) {
  const o = parseMetric(m);
  if (!o) return esc(m ?? "");
  const parts = [];
  for (const [k, fn] of METRIC_LABELS) {
    if (o[k] != null) { const s = fn(o[k]); if (s) parts.push(s); }
  }
  return parts.length ? esc(parts.join(" / ")) : esc(typeof m === "string" ? m : JSON.stringify(m));
}
// 詳細パネル用：サマリー＋ルール/クローズ理由などの補足＋生JSON(折りたたみ)。
function metricDetailHtml(m) {
  const o = parseMetric(m);
  let extra = "";
  if (o) {
    if (o.rule)          extra += `<div class="muted small">ルール: ${esc(o.rule)}</div>`;
    if (o.closed_reason) extra += `<div class="muted small">クローズ理由: ${esc(o.closed_reason)}</div>`;
    if (o.closed_at)     extra += `<div class="muted small">クローズ日: ${esc(o.closed_at)}</div>`;
  }
  const raw = esc(typeof m === "string" ? m : JSON.stringify(m ?? ""));
  return `<div>${metricSummary(m)}</div>${extra}
    <details style="margin-top:6px;"><summary class="muted small" style="cursor:pointer;">生データ(JSON)</summary>
      <pre style="white-space:pre-wrap;word-break:break-all;font-size:11px;margin:6px 0 0;">${raw}</pre></details>`;
}

function openIncidentPanel(i, refreshFn) {
  // P4b: detected_incidents の status 列のみ変更（PATCH /api/incidents/{id}/status）
  async function patchStatus(status) {
    try {
      const r = await fetch(`/api/incidents/${i.id}/status`, {
        method: "PATCH", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ status })
      });
      if (!r.ok) throw new Error(await r.text());
      showToast("状態を更新しました");
      closePanel();
      if (refreshFn) refreshFn(); // 一覧再取得
    } catch (e) { showToast("エラー: " + (e.message || String(e))); }
  }

  openPanel("インシデント詳細", `
    <div class="dp-row"><div class="dp-label">ID</div><div class="dp-val">${i.id}</div></div>
    <div class="dp-row"><div class="dp-label">日時</div><div class="dp-val">${fmtTime(i.time)}</div></div>
    <div class="dp-row"><div class="dp-label">種別</div><div class="dp-val"><span class="tag">${esc(incTypeLabel(i.type))}</span></div></div>
    <div class="dp-row"><div class="dp-label">重要度</div><div class="dp-val"><span class="${sevCls(i.severity)}">${esc(sevLabel(i.severity))}</span></div></div>
    <div class="dp-row"><div class="dp-label">ユーザー</div><div class="dp-val">${userCell(i.user)}</div></div>
    <div class="dp-row"><div class="dp-label">一致件数</div><div class="dp-val">${num(i.matchCount)}</div></div>
    <div class="dp-row"><div class="dp-label">指標</div><div class="dp-val">${metricDetailHtml(i.metric)}</div></div>
    <div class="dp-row"><div class="dp-label">状態</div><div class="dp-val"><span class="state">${esc(incStatusLabel(i.status))}</span></div></div>
    <div style="padding-top:14px;display:flex;gap:8px;flex-wrap:wrap;">
      <button class="primary" id="inc-drill">関連ログへ →</button>
      <button id="inc-ack"   ${i.status==="ack"||i.status==="closed"?"disabled":""}>確認</button>
      <button id="inc-close" ${i.status==="closed"?"disabled":""}>クローズ</button>
    </div>`);
  document.getElementById("inc-drill").onclick = () => {
    closePanel();
    drillToSearch({ user: i.user, sources: ["viewer","direct","unknown"], dept: "", q: "" });
  };
  const ackBtn   = document.getElementById("inc-ack");
  const closeBtn = document.getElementById("inc-close");
  if (ackBtn   && !ackBtn.disabled)   ackBtn.onclick   = () => patchStatus("ack");
  if (closeBtn && !closeBtn.disabled) closeBtn.onclick = () => patchStatus("closed");
}

async function incidents() {
  const list = await getJson("/api/incidents");
  const _incMap = {};
  list.forEach((i) => { _incMap[i.id] = i; });

  const body = list.map((i) => `
    <tr class="clickrow-inc" data-id="${i.id}">
      <td>${fmtTime(i.time)}</td>
      <td><span class="tag">${esc(incTypeLabel(i.type))}</span></td>
      <td><span class="${sevCls(i.severity)}">${esc(sevLabel(i.severity))}</span></td>
      <td>${userCell(i.user)}</td>
      <td>${num(i.matchCount)}</td>
      <td class="muted">${metricSummary(i.metric)}</td>
      <td><span class="state">${esc(incStatusLabel(i.status))}</span></td>
    </tr>`).join("") || `<tr><td colspan="7" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <div class="page-head">
      <h1>検知インシデント</h1>
      <div class="sub">部署外アクセス・大量読み取り（持ち出し前兆）・外部コピー疑い 等を検知します。行クリックで詳細を表示します。</div>
    </div>
    <div class="card">
      <table>
        <thead><tr><th>日時</th><th>種別</th><th>重要度</th><th>ユーザー</th><th>一致件数</th><th>指標</th><th>状態</th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;

  $$(".clickrow-inc", view).forEach((tr) => {
    tr.style.cursor = "pointer";
    tr.onclick = () => openIncidentPanel(_incMap[Number(tr.dataset.id)], incidents);
  });
}

// ===================================================================
// ⑥ サーバー状態 / 健全性  #/status
// ===================================================================
async function status() {
  const h = await getJson("/api/health");
  const gapMin = h.gaps.reduce((a, g) => a + (new Date(g.end) - new Date(g.start)) / 60000, 0);

  const banner = h.gaps.length
    ? `<div class="health warn">⚠ <b>監査GAP ${h.gaps.length}件 / 合計 ${Math.round(gapMin)}分</b>（この区間は直接アクセスを取りこぼしている可能性。利用率の分母から除外）&nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)}（遅延 ${h.lagSeconds}s） データ源: <b>${esc(h.dataMode)}</b></div>`
    : `<div class="health ok">✅ 監査GAPなし &nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)} データ源: <b>${esc(h.dataMode)}</b></div>`;

  const collectors = h.collectors.map((c) => `
    <tr>
      <td><b>${esc(c.server)}</b></td><td>${esc(c.channel)}</td>
      <td>${fmtTime(c.lastEvent)}</td><td>${c.lagSeconds}s</td>
      <td><span class="state ${c.status === "OK" ? "ok" : "ng"}">${esc(c.status)}</span></td>
    </tr>`).join("");

  const gaps = h.gaps.map((g) => `
    <tr>
      <td>${fmtTime(g.start)}</td><td>${fmtTime(g.end)}</td>
      <td>${Math.round((new Date(g.end) - new Date(g.start)) / 60000)}分</td>
      <td class="muted">${esc(g.reason)}</td>
    </tr>`).join("") || `<tr><td colspan="4" class="muted">GAPなし</td></tr>`;

  view.innerHTML = `
    <div class="page-head">
      <h1>サーバー状態 / 健全性</h1>
      <div class="sub">コレクター稼働状態・監査GAP区間・最終同期を集約します。</div>
      ${banner}
    </div>
    <div class="card">
      <h2>コレクター状態</h2>
      <table><thead><tr><th>サーバー</th><th>チャネル</th><th>最終イベント</th><th>遅延</th><th>状態</th></tr></thead><tbody>${collectors}</tbody></table>
    </div>
    <div class="card">
      <h2>監査GAP区間</h2>
      <table><thead><tr><th>開始</th><th>終了</th><th>長さ</th><th>理由</th></tr></thead><tbody>${gaps}</tbody></table>
    </div>
    <p class="muted small">最終同期: <b>${fmtTime(h.lastSync)}</b> ／ 監査最新イベント: <b>${fmtTime(h.auditLatestEvent)}</b> ／ 同期遅延: <b>${h.lagSeconds}s</b></p>`;
}

// ===================================================================
// ⑦ 設定  #/settings  (Part A: P4 プロトタイプ — 書込はクライアントモックのみ)
// ===================================================================
async function settings() {
  const raw = await getJson("/api/settings");
  // deep copy で楽観更新用ローカルコピー（成功後にサーバー応答でマージ）。
  const S = JSON.parse(JSON.stringify(raw));

  const TABS = [
    { id: "folders",      label: "① 監視フォルダ" },
    { id: "users",        label: "② ユーザー" },
    { id: "rules",        label: "③ アラートルール" },
    { id: "exclusions",   label: "④ 検知除外" },
    { id: "commonfolders",label: "⑤ 共通フォルダ" },
    { id: "usergrants",   label: "⑥ フォルダ付与" },
    { id: "notify",       label: "⑦ 通知設定" },
    { id: "bulk",         label: "⑧ 持ち出し検知" },
    { id: "crossdept",    label: "⑨ 部署外検知" },
  ];

  let activeTab = "folders";

  view.innerHTML = `
    <div class="page-head">
      <h1>設定</h1>
      <div class="health ok">設定変更はサーバーへ送信されます（config_editor ロール・監査ログ記録）。ConfigPg 未設定時は 503 エラーが返ります。</div>
      <div class="tabs" id="stabs">${TABS.map((t) =>
        `<button class="tab${t.id === activeTab ? " active" : ""}" data-tab="${t.id}">${t.label}</button>`
      ).join("")}</div>
    </div>
    <div id="tab-content"></div>`;

  function renderTab(tabId) {
    activeTab = tabId;
    $$(".tab", view).forEach((t) => t.classList.toggle("active", t.dataset.tab === tabId));
    const el = $("#tab-content");
    switch (tabId) {
      case "folders":       el.innerHTML = renderFolders();       bindFolders(el);       break;
      case "users":         el.innerHTML = renderSettUsers();     bindSettUsers(el);     break;
      case "rules":         el.innerHTML = renderRules();         bindRules(el);         break;
      case "exclusions":    el.innerHTML = renderExclusions();    bindExclusions(el);    break;
      case "commonfolders": el.innerHTML = renderCommonFolders(); bindCommonFolders(el); break;
      case "usergrants":    el.innerHTML = renderUserGrants();    bindUserGrants(el);    break;
      case "notify":        renderAppSettings(el, ["notification."]); break;
      case "bulk":          renderAppSettings(el, ["detection.bulk.", "detection.offhours."]); break;
      case "crossdept":     renderAppSettings(el, ["detection.crossdept."]); break;
    }
  }

  $$(".tab", view).forEach((t) => t.onclick = () => renderTab(t.dataset.tab));
  renderTab(activeTab);

  // ---- ① 監視フォルダ -----------------------------------------------
  function renderFolders() {
    const rows = S.folders.map((f) => `
      <tr>
        <td>${f.id}</td><td><b>${esc(f.server)}</b></td>
        <td class="file" style="font-size:12px">${esc(f.path)}</td>
        <td><span class="${sevCls(f.importance)}">${esc(f.importance)}</span></td>
        <td>${f.readEnabled?"R":"—"}/${f.writeEnabled?"W":"—"}/${f.deleteEnabled?"D":"—"}</td>
        <td><button class="btn-tog ${f.enabled?"on":"off"}" data-id="${f.id}">${f.enabled?"有効":"無効"}</button></td>
        <td>
          <button class="btn-edit" data-id="${f.id}">編集</button>
          <button class="btn-del"  data-id="${f.id}">削除</button>
        </td>
      </tr>`).join("");
    return `<div class="card"><div class="stb-header"><h2>監視フォルダ</h2><button class="primary btn-add">+ 追加</button></div>
      <table><thead><tr><th>#</th><th>サーバー</th><th>パス</th><th>重要度</th><th>R/W/D</th><th>状態</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
  }
  // P4b: 監視フォルダは実 API（POST/PUT/DELETE）を使用する。
  function bindFolders(el) {
    $(".btn-add", el).onclick = () => openModal("監視フォルダ追加", folderForm(), () => {
      const f = collectFolder(); if (!f) return false;
      return fetch("/api/folders", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(f)
      }).then((r) => r.ok ? r.json()
          : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
        .then((created) => { S.folders.push(created); renderTab("folders"); });
    });
    $$(".btn-tog", el).forEach((b) => b.onclick = async () => {
      const f = S.folders.find((x) => x.id === Number(b.dataset.id)); if (!f) return;
      const upd = Object.assign({}, f, { enabled: !f.enabled });
      try {
        const r = await fetch(`/api/folders/${f.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        });
        if (!r.ok) throw new Error(await r.text());
        f.enabled = !f.enabled;
        renderTab("folders");
        showToast("保存しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-del", el).forEach((b) => b.onclick = async () => {
      if (!confirm("このフォルダ設定を削除しますか？")) return;
      const id = Number(b.dataset.id);
      try {
        const r = await fetch(`/api/folders/${id}`, { method: "DELETE" });
        if (!r.ok) throw new Error(await r.text());
        const idx = S.folders.findIndex((x) => x.id === id);
        if (idx >= 0) { S.folders.splice(idx, 1); renderTab("folders"); }
        showToast("削除しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-edit", el).forEach((b) => b.onclick = () => {
      const f = S.folders.find((x) => x.id === Number(b.dataset.id)); if (!f) return;
      openModal("監視フォルダ編集", folderForm(f), () => {
        const upd = collectFolder(f.id); if (!upd) return false;
        return fetch(`/api/folders/${f.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        }).then((r) => r.ok ? r.json()
            : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
          .then((updated) => { Object.assign(f, updated); renderTab("folders"); });
      });
    });
  }
  function folderForm(f = {}) {
    return `
      <div class="sf-row"><label>サーバー</label><input id="sf-server" value="${esc(f.server||"lineworks-sv")}"></div>
      <div class="sf-row"><label>パス (例: \\\\server\\share\\dept)</label><input id="sf-path" value="${esc(f.path||"")}"></div>
      <div class="sf-row"><label>重要度</label>
        <select id="sf-imp">${["High","Medium","Low"].map((v) => `<option${f.importance===v?" selected":""}>${v}</option>`).join("")}</select></div>
      <div class="sf-row" style="flex-direction:row;gap:16px;align-items:center">
        <label><input type="checkbox" id="sf-r" ${f.readEnabled!==false?"checked":""}> 読取</label>
        <label><input type="checkbox" id="sf-w" ${f.writeEnabled?"checked":""}> 書込</label>
        <label><input type="checkbox" id="sf-d" ${f.deleteEnabled?"checked":""}> 削除</label>
        <label><input type="checkbox" id="sf-en" ${f.enabled!==false?"checked":""}> 有効</label>
      </div>`;
  }
  function collectFolder(existingId) {
    const server = $("#sf-server")&&$("#sf-server").value.trim(); if (!server) return false;
    const path   = $("#sf-path")  &&$("#sf-path").value.trim();   if (!path)   return false;
    return { id: existingId ?? (Math.max(0, ...S.folders.map((x) => x.id)) + 1),
      server, path, importance: $("#sf-imp").value,
      readEnabled: $("#sf-r").checked, writeEnabled: $("#sf-w").checked,
      deleteEnabled: $("#sf-d").checked, enabled: $("#sf-en").checked };
  }

  // ---- ② ユーザー ---------------------------------------------------
  function renderSettUsers() {
    const rows = S.users.map((u) => `
      <tr>
        <td>${u.id}</td>
        <td>${esc(u.domain)}&#92;${esc(u.name)}</td>
        <td>${esc(u.display)}</td><td>${esc(u.dept)}</td>
        <td><span class="tag">${esc(u.role)}</span></td>
        <td><button class="btn-tog ${u.enabled?"on":"off"}" data-id="${u.id}">${u.enabled?"有効":"無効"}</button></td>
        <td><button class="btn-edit" data-id="${u.id}">編集</button></td>
      </tr>`).join("");
    return `<div class="card"><div class="stb-header"><h2>ユーザー</h2><button class="primary btn-add">+ 追加</button></div>
      <table><thead><tr><th>#</th><th>ドメイン&#92;ユーザー名</th><th>表示名</th><th>部署</th><th>ロール</th><th>状態</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
  }
  // P4b: ユーザーは実 API（POST/PUT/DELETE）を使用する。
  function bindSettUsers(el) {
    $(".btn-add", el).onclick = () => openModal("ユーザー追加", userForm(), () => {
      const u = collectUser(); if (!u) return false;
      return fetch("/api/users", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(u)
      }).then((r) => r.ok ? r.json()
          : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
        .then((created) => { S.users.push(created); renderTab("users"); });
    });
    $$(".btn-tog", el).forEach((b) => b.onclick = async () => {
      const u = S.users.find((x) => x.id === Number(b.dataset.id)); if (!u) return;
      const upd = Object.assign({}, u, { enabled: !u.enabled });
      try {
        const r = await fetch(`/api/users/${u.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        });
        if (!r.ok) throw new Error(await r.text());
        u.enabled = !u.enabled;
        renderTab("users");
        showToast("保存しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-edit", el).forEach((b) => b.onclick = () => {
      const u = S.users.find((x) => x.id === Number(b.dataset.id)); if (!u) return;
      openModal("ユーザー編集", userForm(u), () => {
        const upd = collectUser(u.id); if (!upd) return false;
        return fetch(`/api/users/${u.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        }).then((r) => r.ok ? r.json()
            : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
          .then((updated) => { Object.assign(u, updated); renderTab("users"); });
      });
    });
  }
  function userForm(u = {}) {
    const ROLES = ["viewer","admin","service"];
    return `
      <div class="sf-row"><label>ドメイン</label><input id="su-domain" value="${esc(u.domain||"LINEWORKS-NET")}"></div>
      <div class="sf-row"><label>ユーザー名</label><input id="su-name" value="${esc(u.name||"")}"></div>
      <div class="sf-row"><label>表示名</label><input id="su-display" value="${esc(u.display||"")}"></div>
      <div class="sf-row"><label>部署</label><input id="su-dept" value="${esc(u.dept||"")}"></div>
      <div class="sf-row"><label>ロール</label>
        <select id="su-role">${ROLES.map((v) => `<option${u.role===v?" selected":""}>${v}</option>`).join("")}</select></div>
      <div class="sf-row" style="flex-direction:row;gap:16px;align-items:center">
        <label><input type="checkbox" id="su-en" ${u.enabled!==false?"checked":""}> 有効</label></div>`;
  }
  function collectUser(existingId) {
    const name = $("#su-name")&&$("#su-name").value.trim(); if (!name) return false;
    return { id: existingId ?? (Math.max(0, ...S.users.map((x) => x.id)) + 1),
      domain: $("#su-domain").value.trim(), name,
      display: $("#su-display").value.trim(), dept: $("#su-dept").value.trim(),
      role: $("#su-role").value, enabled: $("#su-en").checked };
  }

  // ---- ③ アラートルール ----------------------------------------------
  function renderRules() {
    const rows = S.rules.map((r) => `
      <tr>
        <td>${r.id}</td><td>${esc(r.name)}</td>
        <td><span class="tag">${esc(r.condition)}</span></td>
        <td><span class="${sevCls(r.severity)}">${esc(r.severity)}</span></td>
        <td>${r.threshold} 件 / ${r.windowMinutes} 分</td>
        <td>${r.offHours?"夜間のみ":"常時"}</td>
        <td><button class="btn-tog ${r.enabled?"on":"off"}" data-id="${r.id}">${r.enabled?"有効":"無効"}</button></td>
        <td>
          <button class="btn-edit" data-id="${r.id}">編集</button>
          <button class="btn-del"  data-id="${r.id}">削除</button>
        </td>
      </tr>`).join("");
    return `<div class="card"><div class="stb-header"><h2>アラートルール</h2><button class="primary btn-add">+ 追加</button></div>
      <table><thead><tr><th>#</th><th>名前</th><th>条件</th><th>重要度</th><th>しきい値/時間窓</th><th>時間帯</th><th>状態</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
  }
  // P4b: アラートルールは実 API（POST/PUT/DELETE）を使用する。
  function bindRules(el) {
    $(".btn-add", el).onclick = () => openModal("ルール追加", ruleForm(), () => {
      const r = collectRule(); if (!r) return false;
      return fetch("/api/rules", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(r)
      }).then((rsp) => rsp.ok ? rsp.json()
          : rsp.json().then((j) => Promise.reject(new Error(j.detail || rsp.statusText))))
        .then((created) => { S.rules.push(created); renderTab("rules"); });
    });
    $$(".btn-tog", el).forEach((b) => b.onclick = async () => {
      const r = S.rules.find((x) => x.id === Number(b.dataset.id)); if (!r) return;
      const upd = Object.assign({}, r, { enabled: !r.enabled });
      try {
        const rsp = await fetch(`/api/rules/${r.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        });
        if (!rsp.ok) throw new Error(await rsp.text());
        r.enabled = !r.enabled;
        renderTab("rules");
        showToast("保存しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-del", el).forEach((b) => b.onclick = async () => {
      if (!confirm("このルールを削除しますか？")) return;
      const id = Number(b.dataset.id);
      try {
        const rsp = await fetch(`/api/rules/${id}`, { method: "DELETE" });
        if (!rsp.ok) throw new Error(await rsp.text());
        const idx = S.rules.findIndex((x) => x.id === id);
        if (idx >= 0) { S.rules.splice(idx, 1); renderTab("rules"); }
        showToast("削除しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-edit", el).forEach((b) => b.onclick = () => {
      const r = S.rules.find((x) => x.id === Number(b.dataset.id)); if (!r) return;
      openModal("ルール編集", ruleForm(r), () => {
        const upd = collectRule(r.id); if (!upd) return false;
        return fetch(`/api/rules/${r.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        }).then((rsp) => rsp.ok ? rsp.json()
            : rsp.json().then((j) => Promise.reject(new Error(j.detail || rsp.statusText))))
          .then((updated) => { Object.assign(r, updated); renderTab("rules"); });
      });
    });
  }
  function ruleForm(r = {}) {
    const SEVS = ["High","Medium","Low"];
    return `
      <div class="sf-row"><label>名前</label><input id="rr-name" value="${esc(r.name||"")}"></div>
      <div class="sf-row"><label>条件キー</label><input id="rr-cond" value="${esc(r.condition||"")}"></div>
      <div class="sf-row"><label>重要度</label>
        <select id="rr-sev">${SEVS.map((v) => `<option${r.severity===v?" selected":""}>${v}</option>`).join("")}</select></div>
      <div class="sf-row"><label>対象（* = 全部署）</label><input id="rr-target" value="${esc(r.target||"*")}"></div>
      <div class="sf-row"><label>しきい値（件数）</label><input type="number" id="rr-thr" value="${r.threshold||10}" min="1"></div>
      <div class="sf-row"><label>時間窓（分）</label><input type="number" id="rr-win" value="${r.windowMinutes||30}" min="1"></div>
      <div class="sf-row" style="flex-direction:row;gap:16px;align-items:center">
        <label><input type="checkbox" id="rr-off" ${r.offHours?"checked":""}> 夜間のみ検知</label>
        <label><input type="checkbox" id="rr-en" ${r.enabled!==false?"checked":""}> 有効</label></div>`;
  }
  function collectRule(existingId) {
    const name = $("#rr-name")&&$("#rr-name").value.trim(); if (!name) return false;
    return { id: existingId ?? (Math.max(0, ...S.rules.map((x) => x.id)) + 1),
      name, condition: $("#rr-cond").value.trim(),
      severity: $("#rr-sev").value, target: $("#rr-target").value.trim(),
      threshold: Number($("#rr-thr").value), windowMinutes: Number($("#rr-win").value),
      offHours: $("#rr-off").checked, enabled: $("#rr-en").checked };
  }

  // ---- ④ 検知除外 ---------------------------------------------------
  function renderExclusions() {
    const rows = S.exclusions.map((e) => `
      <tr>
        <td>${e.id}</td><td>${esc(e.user)}</td>
        <td class="muted">${esc(e.process || "—")}</td>
        <td class="muted file" style="font-size:12px">${esc(e.path || "—")}</td>
        <td>${esc(e.reason)}</td>
        <td>
          <button class="btn-edit" data-id="${e.id}">編集</button>
          <button class="btn-del"  data-id="${e.id}">削除</button>
        </td>
      </tr>`).join("");
    return `<div class="card"><div class="stb-header"><h2>検知除外</h2><button class="primary btn-add">+ 追加</button></div>
      <table><thead><tr><th>#</th><th>ユーザー</th><th>プロセス</th><th>パス</th><th>理由</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
  }
  // P4a: 検知除外は実 API（POST/PUT/DELETE）を使用する。
  // サーバーは config_editor ロール専用接続で設定テーブルのみに書き込み、
  // config_audit_log に操作者・変更内容を記録する。
  function bindExclusions(el) {
    $(".btn-add", el).onclick = () => openModal("検知除外追加", exclForm(), () => {
      const e = collectExcl(); if (!e) return false;
      return fetch("/api/exclusions", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(e)
      }).then((r) => r.ok ? r.json()
          : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
        .then((created) => { S.exclusions.push(created); renderTab("exclusions"); });
    });
    $$(".btn-del", el).forEach((b) => b.onclick = async () => {
      if (!confirm("この除外設定を削除しますか？")) return;
      const id = Number(b.dataset.id);
      try {
        const r = await fetch(`/api/exclusions/${id}`, { method: "DELETE" });
        if (!r.ok) throw new Error(await r.text());
        const idx = S.exclusions.findIndex((x) => x.id === id);
        if (idx >= 0) { S.exclusions.splice(idx, 1); renderTab("exclusions"); }
        showToast("削除しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-edit", el).forEach((b) => b.onclick = () => {
      const e = S.exclusions.find((x) => x.id === Number(b.dataset.id)); if (!e) return;
      openModal("検知除外編集", exclForm(e), () => {
        const upd = collectExcl(e.id); if (!upd) return false;
        return fetch(`/api/exclusions/${e.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        }).then((r) => r.ok ? r.json()
            : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
          .then((updated) => { Object.assign(e, updated); renderTab("exclusions"); });
      });
    });
  }
  function exclForm(e = {}) {
    return `
      <div class="sf-row"><label>ユーザー（必須）</label><input id="ex-user" value="${esc(e.user||"")}"></div>
      <div class="sf-row"><label>プロセス名（任意）</label><input id="ex-proc" value="${esc(e.process||"")}"></div>
      <div class="sf-row"><label>パス（任意）</label><input id="ex-path" value="${esc(e.path||"")}"></div>
      <div class="sf-row"><label>理由</label><input id="ex-reason" value="${esc(e.reason||"")}"></div>`;
  }
  function collectExcl(existingId) {
    const user = $("#ex-user")&&$("#ex-user").value.trim(); if (!user) return false;
    return { id: existingId ?? (Math.max(0, ...S.exclusions.map((x) => x.id)) + 1),
      user, process: $("#ex-proc").value.trim() || null,
      path: $("#ex-path").value.trim() || null, reason: $("#ex-reason").value.trim() };
  }

  // ---- ⑤ 共通フォルダ（PK は path。id は UI 連番で識別に使わない）---------
  function renderCommonFolders() {
    const rows = (S.commonFolders || []).map((f, i) => `
      <tr>
        <td>${i + 1}</td>
        <td class="file" style="font-size:12px">${esc(f.path)}</td>
        <td class="muted">${esc(f.description || "—")}</td>
        <td>
          <button class="btn-edit" data-path="${esc(f.path)}">編集</button>
          <button class="btn-del"  data-path="${esc(f.path)}">削除</button>
        </td>
      </tr>`).join("");
    return `<div class="card"><div class="stb-header"><h2>共通フォルダ</h2><button class="primary btn-add">+ 追加</button></div>
      <table><thead><tr><th>#</th><th>フォルダトップ</th><th>説明</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
  }
  function bindCommonFolders(el) {
    $(".btn-add", el).onclick = () => openModal("共通フォルダ追加", cfForm(), () => {
      const f = collectCf(); if (!f) return false;
      return fetch("/api/commonfolders", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(f)
      }).then((r) => r.ok ? r.json()
          : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
        .then((created) => { S.commonFolders.push(created); renderTab("commonfolders"); });
    });
    $$(".btn-del", el).forEach((b) => b.onclick = async () => {
      if (!confirm("この共通フォルダを削除しますか？")) return;
      const path = b.dataset.path;
      try {
        const r = await fetch(`/api/commonfolders?path=${encodeURIComponent(path)}`, { method: "DELETE" });
        if (!r.ok) throw new Error(await r.text());
        const idx = S.commonFolders.findIndex((x) => x.path === path);
        if (idx >= 0) { S.commonFolders.splice(idx, 1); renderTab("commonfolders"); }
        showToast("削除しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-edit", el).forEach((b) => b.onclick = () => {
      const f = S.commonFolders.find((x) => x.path === b.dataset.path); if (!f) return;
      openModal("共通フォルダ編集", cfForm(f), () => {
        const upd = collectCf(f.id); if (!upd) return false;
        return fetch("/api/commonfolders", {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        }).then((r) => r.ok ? r.json()
            : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
          .then((updated) => { Object.assign(f, updated); renderTab("commonfolders"); });
      });
    });
  }
  function cfForm(f = {}) {
    return `
      <div class="sf-row"><label>フォルダトップ（必須）</label><input id="cf-path" value="${esc(f.path||"")}"></div>
      <div class="sf-row"><label>説明</label><input id="cf-desc" value="${esc(f.description||"")}"></div>`;
  }
  function collectCf(existingId) {
    const path = $("#cf-path")&&$("#cf-path").value.trim(); if (!path) return false;
    return { id: existingId ?? 0, path, description: $("#cf-desc").value.trim() };
  }

  // ---- ⑥ フォルダ付与（user_folder_grants）----------------------------
  function renderUserGrants() {
    const rows = (S.userGrants || []).map((g) => `
      <tr>
        <td>${g.id}</td>
        <td>${esc(g.user)}</td>
        <td><span class="tag">${esc(g.kind)}</span></td>
        <td class="muted file" style="font-size:12px">${esc(g.value)}</td>
        <td>
          <button class="btn-edit" data-id="${g.id}">編集</button>
          <button class="btn-del"  data-id="${g.id}">削除</button>
        </td>
      </tr>`).join("");
    return `<div class="card"><div class="stb-header"><h2>フォルダ付与</h2><button class="primary btn-add">+ 追加</button></div>
      <table><thead><tr><th>#</th><th>ユーザー</th><th>種別</th><th>値</th><th></th></tr></thead>
      <tbody>${rows}</tbody></table></div>`;
  }
  function bindUserGrants(el) {
    $(".btn-add", el).onclick = () => openModal("フォルダ付与追加", grantForm(), () => {
      const g = collectGrant(); if (!g) return false;
      return fetch("/api/usergrants", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify(g)
      }).then((r) => r.ok ? r.json()
          : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
        .then((created) => { S.userGrants.push(created); renderTab("usergrants"); });
    });
    $$(".btn-del", el).forEach((b) => b.onclick = async () => {
      if (!confirm("このフォルダ付与設定を削除しますか？")) return;
      const id = Number(b.dataset.id);
      try {
        const r = await fetch(`/api/usergrants/${id}`, { method: "DELETE" });
        if (!r.ok) throw new Error(await r.text());
        const idx = S.userGrants.findIndex((x) => x.id === id);
        if (idx >= 0) { S.userGrants.splice(idx, 1); renderTab("usergrants"); }
        showToast("削除しました");
      } catch (e) { showToast("エラー: " + (e.message || String(e))); }
    });
    $$(".btn-edit", el).forEach((b) => b.onclick = () => {
      const g = S.userGrants.find((x) => x.id === Number(b.dataset.id)); if (!g) return;
      openModal("フォルダ付与編集", grantForm(g), () => {
        const upd = collectGrant(g.id); if (!upd) return false;
        return fetch(`/api/usergrants/${g.id}`, {
          method: "PUT", headers: { "Content-Type": "application/json" },
          body: JSON.stringify(upd)
        }).then((r) => r.ok ? r.json()
            : r.json().then((j) => Promise.reject(new Error(j.detail || r.statusText))))
          .then((updated) => { Object.assign(g, updated); renderTab("usergrants"); });
      });
    });
  }
  function grantForm(g = {}) {
    const KINDS = ["dept","postbox"];
    return `
      <div class="sf-row"><label>ユーザー名（必須）</label><input id="gr-user" value="${esc(g.user||"")}"></div>
      <div class="sf-row"><label>種別</label>
        <select id="gr-kind">${KINDS.map((v) => `<option${g.kind===v?" selected":""}>${v}</option>`).join("")}</select></div>
      <div class="sf-row"><label>値（フォルダパス / 部署名）</label><input id="gr-val" value="${esc(g.value||"")}"></div>`;
  }
  function collectGrant(existingId) {
    const user = $("#gr-user")&&$("#gr-user").value.trim(); if (!user) return false;
    return { id: existingId ?? 0,
      user, kind: $("#gr-kind").value, value: $("#gr-val").value.trim() };
  }

  // ---- ⑦⑧⑨ AppSettings キー値フォーム --------------------------------
  function renderAppSettings(el, prefixes) {
    const items = S.appSettings.filter((s) => prefixes.some((p) => s.key.startsWith(p)));
    const formHtml = items.map((s) => {
      const id  = "as_" + s.key.replace(/\./g, "_");
      const isToggle = s.value === "true" || s.value === "false";
      const isTime   = /^\d{1,2}:\d{2}$/.test(s.value);
      const isNum    = !isTime && /^\d+$/.test(s.value);
      const itype    = isTime ? "time" : (isNum ? "number" : "text");
      if (isToggle) {
        const on = s.value === "true";
        return `<div class="sf-row" style="flex-direction:row;align-items:center;gap:14px;padding:10px 0;border-bottom:1px solid var(--border)">
          <button class="btn-tog ${on?"on":"off"}" data-key="${esc(s.key)}">${on?"有効":"無効"}</button>
          <span>${esc(s.description)} <span class="muted small">(${esc(s.key)})</span></span>
        </div>`;
      }
      return `<div class="sf-row">
        <label for="${id}">${esc(s.description)} <span class="muted small">(${esc(s.key)})</span></label>
        <input type="${itype}" id="${id}" data-key="${esc(s.key)}" value="${esc(s.value)}">
      </div>`;
    }).join("");

    el.innerHTML = `<div class="card">
      <div class="stb-header"><h2>設定</h2><button class="primary" id="as-save">保存</button></div>
      <div id="as-form">${formHtml}</div>
    </div>`;

    $$(".btn-tog", el).forEach((b) => b.onclick = () => {
      const s = S.appSettings.find((x) => x.key === b.dataset.key); if (!s) return;
      s.value = s.value === "true" ? "false" : "true";
      b.textContent = s.value === "true" ? "有効" : "無効";
      b.className   = "btn-tog " + (s.value === "true" ? "on" : "off");
    });
    $$("input[data-key]", el).forEach((inp) => {
      inp.onchange = () => {
        const s = S.appSettings.find((x) => x.key === inp.dataset.key);
        if (s) s.value = inp.value;
      };
    });
    // P4a: app_settings は実 API（PUT /api/appsettings/{key}）を使用する。
    // 表示中の prefixes に属する設定キーを全て 1 件ずつ順次保存する。
    $("#as-save", el).onclick = async () => {
      const items = S.appSettings.filter((s) => prefixes.some((p) => s.key.startsWith(p)));
      let err = null;
      for (const s of items) {
        try {
          const r = await fetch(`/api/appsettings/${encodeURIComponent(s.key)}`, {
            method: "PUT", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ value: s.value })
          });
          if (!r.ok) {
            const j = await r.json().catch(() => ({}));
            throw new Error((j && j.detail) || r.statusText);
          }
        } catch (e) { err = e; break; }
      }
      showToast(err ? "エラー: " + (err.message || String(err)) : "設定を保存しました");
    };
  }
}

// ===================================================================
// ⑧ 部署別利用率  #/depts
// ===================================================================
async function depts() {
  view.innerHTML = `
    <div class="page-head">
      <h1>部署別利用率</h1>
      <div class="sub">部署ごとにビューアー経由（🟦）/ 直接（🟥）/ 未帰属（⬜）のセッション数と利用率を表示します。利用率 = 🟦/(🟦+🟥)。</div>
      ${periodBarHtml()}
    </div>
    <div class="card">
      <table id="dept-tbl">
        <thead><tr>
          <th>部署</th>
          <th class="cv">🟦 ビューアー</th>
          <th class="cd">🟥 直接</th>
          <th class="cu">⬜ 未帰属</th>
          <th>合計</th>
          <th>利用率</th>
        </tr></thead>
        <tbody id="dept-rows"><tr><td colspan="6" class="muted">読み込み中…</td></tr></tbody>
      </table>
    </div>`;

  bindPeriodBar(loadDepts);

  async function loadDepts() {
    const list = await getJson("/api/departments?" + dateParams());
    document.getElementById("dept-rows").innerHTML = list.length
      ? list.map((d) => {
          const pct = Math.round(d.adoption * 100);
          return `<tr>
            <td><b>${esc(d.dept)}</b></td>
            <td class="cv">${num(d.viewer)}</td>
            <td class="cd">${num(d.direct)}</td>
            <td class="cu">${num(d.unknown)}</td>
            <td>${num(d.total)}</td>
            <td class="dept-rate-cell">
              <span class="dept-rate-pct">${pct}%</span>
              <span class="dept-rate-bar"><span class="hbfill v" style="width:${pct}%"></span></span>
            </td>
          </tr>`;
        }).join("")
      : `<tr><td colspan="6" class="muted">データなし</td></tr>`;
  }

  await loadDepts();
}

// ===================================================================
// ルーター
// ===================================================================
const ROUTES = { dashboard, search, users, depts, alerts, incidents, status, settings };

function router() {
  closePanel();
  const raw   = location.hash.replace(/^#\//, "");
  const parts = raw ? raw.split("/") : ["dashboard"];
  const name  = parts[0] || "dashboard";
  const fn    = ROUTES[name] || dashboard;

  $$("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === name));
  view.scrollTop = 0;
  fn(parts.slice(1));
}

document.getElementById("globalq").addEventListener("keydown", (e) => {
  if (e.key === "Enter") {
    searchState.q = e.target.value.trim();
    searchState.page = 1;
    e.target.value = "";
    if (location.hash === "#/search") search(); else location.hash = "#/search";
  }
});

window.addEventListener("hashchange", router);

// boot: Live モードなら期間アンカーを実日付(JST)に切り替えてから初回描画する。
(async function boot() {
  try {
    const h = await getJson("/api/health");
    if (h && String(h.dataMode).toLowerCase() === "live") {
      ANCHOR = jstToday();
      period.from = period.to = ANCHOR;
    }
  } catch { /* health 取得失敗時はサンプル基準(2026-06-27)のまま */ }
  await loadUserMap();   // ユーザー表示名マップを先に読み込む
  if (!location.hash) location.hash = "#/dashboard"; else router();
})();
