"use strict";

// 日付は固定（サンプル=2026-06-27）。判定に Date.now を使わない。
const FROM = "2026-06-27";
const TO = "2026-06-27";

const view = document.getElementById("view");
const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

// ---- 共通ユーティリティ -----------------------------------------------------
function nextDay(d) {
  const dt = new Date(d + "T00:00:00+09:00");
  dt.setDate(dt.getDate() + 1);
  return dt.toISOString().slice(0, 10);
}
const fromIso = (d) => d + "T00:00:00+09:00";
const toIso = (d) => nextDay(d) + "T00:00:00+09:00";

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
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
const num = (n) => Number(n).toLocaleString();
const getJson = (url) => fetch(url).then((r) => r.json());

function dateParams(extra) {
  const p = new URLSearchParams();
  p.set("from", fromIso(FROM));
  p.set("to", toIso(TO));
  if (extra) for (const [k, v] of Object.entries(extra)) if (v) p.set(k, v);
  return p;
}

const SRC_LABEL = { viewer: "🟦 ビューアー経由", direct: "🟥 サーバー直接", unknown: "⬜ 未帰属" };
const SRC_CLS = { viewer: "v", direct: "d", unknown: "u" };
const sevCls = (s) => "sev " + String(s).toLowerCase();

// ===================================================================
// ① ダッシュボード  #/dashboard
// ===================================================================
async function dashboard() {
  const d = await getJson("/api/dashboard?" + dateParams());
  const s = d.summary;
  const pct = Math.round(s.adoptionRate * 100);

  const kpis = `
    <div class="kpis">
      <div class="kpi"><div class="lbl">期間内アクセス（合計）</div><div class="num">${num(s.total)}</div></div>
      <div class="kpi viewer"><div class="lbl">🟦 ビューアー経由</div><div class="num">${num(s.viewer)}</div></div>
      <div class="kpi direct"><div class="lbl">🟥 サーバー直接</div><div class="num">${num(s.direct)}</div><div class="sub2">実ユーザー ${s.directUsers}名 / ${s.directFiles}ファイル</div></div>
      <div class="kpi unknown"><div class="lbl">⬜ 未帰属(MTSV$/不明)</div><div class="num">${num(s.unknown)}</div></div>
      <div class="kpi rate"><div class="lbl">ビューアー利用率</div><div class="num">${pct}%</div><div class="sub2">青/(青+赤)・GAP ${s.gapMinutes}分を除外</div></div>
    </div>`;

  // 時間帯別スタック棒（活動のある時間帯のみ）
  const hrs = d.hourly.filter((h) => h.viewer + h.direct + h.unknown > 0);
  const maxH = Math.max(1, ...hrs.map((h) => h.viewer + h.direct + h.unknown));
  const bars = hrs.map((h) => {
    const seg = (val, cls) => val ? `<div class="seg ${cls}" style="height:${(val / maxH) * 130}px" title="${cls}:${val}"></div>` : "";
    return `<div class="hcol">
      <div class="hbar">${seg(h.viewer, "v")}${seg(h.direct, "d")}${seg(h.unknown, "u")}</div>
      <div class="hx">${h.hour}時</div>
    </div>`;
  }).join("") || `<div class="muted">データなし</div>`;

  const hbars = (list, cls) => {
    const max = Math.max(1, ...list.map((x) => x.count));
    return list.map((x) => `
      <div class="hbrow">
        <span class="hbname">${esc(x.name)}</span>
        <span class="hbtrack"><span class="hbfill ${cls}" style="width:${(x.count / max) * 100}%"></span></span>
        <span class="hbval">${num(x.count)}</span>
      </div>`).join("") || `<div class="muted">該当なし</div>`;
  };

  const inc = d.recentIncidents.map((i) => `
    <tr>
      <td>${fmtTime(i.time)}</td>
      <td><span class="tag">${esc(i.type)}</span></td>
      <td><span class="${sevCls(i.severity)}">${esc(i.severity)}</span></td>
      <td>${esc(i.user)}</td>
      <td>${num(i.matchCount)}</td>
      <td class="muted">${esc(i.metric)}</td>
    </tr>`).join("") || `<tr><td colspan="6" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <h1>ダッシュボード</h1>
    <div class="sub">ビューアー経由（🟦）/ サーバー直接（🟥）/ 未帰属（⬜）で全社アクセスの効果を測定します。</div>
    ${kpis}
    <div class="grid2">
      <div class="card">
        <h2>時間帯別アクセス（3色スタック）</h2>
        <div class="hourchart">${bars}</div>
        <div class="legend">
          <span><span class="sw v"></span>🟦 ビューアー経由</span>
          <span><span class="sw d"></span>🟥 サーバー直接</span>
          <span><span class="sw u"></span>⬜ 未帰属</span>
        </div>
      </div>
      <div class="card">
        <h2>🟥 直接アクセス Top ユーザー</h2>
        <div class="hbars">${hbars(d.directTopUsers, "d")}</div>
      </div>
    </div>
    <div class="grid2">
      <div class="card">
        <h2>部署別アクセス件数</h2>
        <div class="hbars">${hbars(d.deptCounts, "v")}</div>
      </div>
      <div class="card">
        <h2>直近インシデント</h2>
        <table>
          <thead><tr><th>日時</th><th>種別</th><th>重要度</th><th>ユーザー</th><th>一致</th><th>指標</th></tr></thead>
          <tbody>${inc}</tbody>
        </table>
        <p class="muted small"><a href="#/incidents">→ 検知インシデント一覧へ</a></p>
      </div>
    </div>`;
}

// ===================================================================
// ② ログ検索  #/search
// ===================================================================
const searchState = {
  user: "", q: "", dept: "",
  sources: ["viewer", "direct", "unknown"],
  page: 1, pageSize: 50,
};

async function search() {
  view.innerHTML = `
    <h1>ログ検索</h1>
    <div class="sub">🟦 ビューアー経由 / 🟥 サーバー直接 / ⬜ 未帰属 を区別して全社アクセスを検索します。</div>

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
        <div class="btns"><button class="primary" id="run">検索</button><button id="clear">クリア</button></div>
      </div>
      <div class="ops" id="kinds">
        <span class="lbl">操作:</span>
        <label><input type="checkbox" value="read" checked>閲覧/読取</label>
        <label><input type="checkbox" value="write" checked>編集</label>
        <label><input type="checkbox" value="delete" checked>削除</label>
        <label><input type="checkbox" value="copy" checked>コピー疑い</label>
        <label><input type="checkbox" value="search" checked>検索</label>
        <label><input type="checkbox" value="login">ログイン</label>
      </div>
    </div>

    <div class="card">
      <h2>検索結果 <span id="count" class="muted"></span></h2>
      <table>
        <thead><tr>
          <th>日時</th><th>ソース</th><th>部署</th><th>ユーザー</th><th>操作</th><th>ファイル</th><th>PC / IP</th><th>結果</th>
        </tr></thead>
        <tbody id="rows"></tbody>
      </table>
      <div class="pager"><span id="pageinfo"></span><span><button id="prev">◀</button> <button id="next">▶</button></span></div>
      <div class="legend">
        <span>🟦 <b>ビューアー経由</b>＝SFEログにある＝確実</span>
        <span>🟥 <b>サーバー直接</b>＝実ユーザーの直読み（要注目）</span>
        <span>⬜ <b>未帰属</b>＝MTSV$/サービス/不明（ビューアーの証明ではない）</span>
      </div>
    </div>
    <p class="muted small">※ DataMode=Sample（サンプルデータ）。本番では SFE SQLite + AuditLogger を読み取り専用で同期します。</p>`;

  // 部署プルダウンを充填
  const filters = await getJson("/api/filters");
  const sel = $("#dept");
  sel.innerHTML = `<option value="">（全部署）</option>` +
    filters.depts.map((d) => `<option value="${esc(d)}"${d === searchState.dept ? " selected" : ""}>${esc(d)}</option>`).join("");

  // 状態をUIへ反映
  $("#user").value = searchState.user;
  $("#q").value = searchState.q;
  $$(".chip").forEach((c) => c.classList.toggle("on", searchState.sources.includes(c.dataset.src)));

  const selectedKinds = () => $$("#kinds input:checked").map((c) => c.value);

  async function loadLogs() {
    const p = dateParams({ dept: searchState.dept, user: searchState.user, q: searchState.q });
    p.set("sources", searchState.sources.join(","));
    const kinds = selectedKinds();
    if (kinds.length) p.set("kinds", kinds.join(","));
    p.set("page", searchState.page);
    p.set("pageSize", searchState.pageSize);

    const data = await getJson("/api/logs?" + p);
    $("#rows").innerHTML = data.rows.map((r) => `
      <tr class="${SRC_CLS[r.source]}">
        <td>${fmtTime(r.time)}</td>
        <td><span class="src ${r.source}">${SRC_LABEL[r.source]}</span></td>
        <td>${esc(r.dept)}</td>
        <td>${esc(r.user)}</td>
        <td><span class="op ${r.kind}">${esc(r.action)}</span></td>
        <td class="file">${esc(shortFile(r.file))}${r.note ? ` <span class="muted">— ${esc(r.note)}</span>` : ""}</td>
        <td>${esc(r.pc || "")}${r.ip ? " / " + r.ip.split(".").slice(-1) : ""}</td>
        <td class="${r.success ? "ok" : "ng"}">${r.success ? "OK" : "拒否"}</td>
      </tr>`).join("") || `<tr><td colspan="8" class="muted">該当なし</td></tr>`;

    const start = (data.page - 1) * data.pageSize + 1;
    const end = Math.min(data.page * data.pageSize, data.total);
    $("#count").textContent = `${num(data.total)} 件`;
    $("#pageinfo").textContent = data.total ? `${start}〜${end} / ${data.total}` : "0 件";
  }

  $("#run").onclick = () => {
    searchState.user = $("#user").value.trim();
    searchState.q = $("#q").value.trim();
    searchState.dept = $("#dept").value;
    searchState.page = 1;
    loadLogs();
  };
  $("#clear").onclick = () => {
    searchState.user = ""; searchState.q = ""; searchState.dept = "";
    $("#user").value = ""; $("#q").value = ""; $("#dept").value = "";
    searchState.page = 1; loadLogs();
  };
  $("#q").addEventListener("keydown", (e) => { if (e.key === "Enter") $("#run").click(); });
  $$(".chip").forEach((c) => c.onclick = () => {
    c.classList.toggle("on");
    searchState.sources = $$(".chip.on").map((x) => x.dataset.src);
    searchState.page = 1; loadLogs();
  });
  $$("#kinds input").forEach((c) => c.onchange = () => { searchState.page = 1; loadLogs(); });
  $("#prev").onclick = () => { if (searchState.page > 1) { searchState.page--; loadLogs(); } };
  $("#next").onclick = () => { searchState.page++; loadLogs(); };

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
      <td><b>${esc(u.user)}</b></td>
      <td>${esc(u.dept)}</td>
      <td class="cv">${num(u.viewer)}</td>
      <td class="cd">${num(u.direct)}</td>
      <td class="cu">${num(u.unknown)}</td>
      <td>${fmtTime(u.lastAccess)}</td>
    </tr>`).join("") || `<tr><td colspan="6" class="muted">該当なし</td></tr>`;

  view.innerHTML = `
    <h1>ユーザー別</h1>
    <div class="sub">ユーザーごとに 🟦/🟥/⬜ を横断表示。行クリックでタイムライン詳細を開きます。</div>
    <div class="card">
      <table>
        <thead><tr><th>ユーザー</th><th>部署</th><th>🟦 ビューアー</th><th>🟥 直接</th><th>⬜ 未帰属</th><th>最終アクセス</th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;

  $$("tr.clickable").forEach((tr) => tr.onclick = () => {
    location.hash = "#/users/" + encodeURIComponent(tr.dataset.name);
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

  const timeline = d.timeline.map((r) => `
    <div class="tl ${SRC_CLS[r.source]}">
      <span class="tl-t">${fmtTime(r.time)}</span>
      <span class="src ${r.source}">${SRC_LABEL[r.source]}</span>
      <span class="op ${r.kind}">${esc(r.action)}</span>
      <span class="tl-f file">${esc(shortFile(r.file))}</span>
      ${r.note ? `<span class="muted">— ${esc(r.note)}</span>` : ""}
      ${r.success ? "" : `<span class="ng">拒否</span>`}
    </div>`).join("");

  view.innerHTML = `
    <h1><a href="#/users" class="back">ユーザー別</a> / ${esc(d.user)}</h1>
    <div class="sub">部署: <b>${esc(d.dept)}</b> ・ 期間内 ${num(total)} 件</div>
    <div class="kpis">
      <div class="kpi viewer"><div class="lbl">🟦 ビューアー経由</div><div class="num">${num(d.viewer)}</div></div>
      <div class="kpi direct"><div class="lbl">🟥 サーバー直接</div><div class="num">${num(d.direct)}</div></div>
      <div class="kpi unknown"><div class="lbl">⬜ 未帰属</div><div class="num">${num(d.unknown)}</div></div>
    </div>
    <div class="card">
      <h2>タイムライン（時系列・3色）</h2>
      <div class="timeline">${timeline}</div>
    </div>`;
}

// ===================================================================
// ④ アラート  #/alerts
// ===================================================================
async function alerts() {
  const list = await getJson("/api/alerts");
  const body = list.map((a) => `
    <tr>
      <td>${fmtTime(a.time)}</td>
      <td><span class="${sevCls(a.severity)}">${esc(a.severity)}</span></td>
      <td><span class="tag">${esc(a.rule)}</span></td>
      <td>${esc(a.user)}</td>
      <td>${num(a.count)}</td>
      <td><span class="state">${esc(a.status)}</span></td>
    </tr>`).join("") || `<tr><td colspan="6" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <h1>アラート</h1>
    <div class="sub">ルール検知の一覧（閲覧）。状態変更は P4 で書込対応予定。</div>
    <div class="card">
      <table>
        <thead><tr><th>日時</th><th>重要度</th><th>ルール</th><th>ユーザー</th><th>件数</th><th>状態</th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;
}

// ===================================================================
// ⑤ 検知インシデント  #/incidents
// ===================================================================
async function incidents() {
  const list = await getJson("/api/incidents");
  const body = list.map((i) => `
    <tr>
      <td>${fmtTime(i.time)}</td>
      <td><span class="tag">${esc(i.type)}</span></td>
      <td><span class="${sevCls(i.severity)}">${esc(i.severity)}</span></td>
      <td>${esc(i.user)}</td>
      <td>${num(i.matchCount)}</td>
      <td class="muted">${esc(i.metric)}</td>
      <td><span class="state">${esc(i.status)}</span></td>
    </tr>`).join("") || `<tr><td colspan="7" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <h1>検知インシデント</h1>
    <div class="sub">大量持ち出し（BULK_CONTENT_READ）/ 部署外アクセス（CROSS_DEPT_ACCESS）等を検知します。</div>
    <div class="card">
      <table>
        <thead><tr><th>日時</th><th>種別</th><th>重要度</th><th>ユーザー</th><th>一致件数</th><th>指標</th><th>状態</th></tr></thead>
        <tbody>${body}</tbody>
      </table>
    </div>`;
}

// ===================================================================
// ⑥ サーバー状態 / 健全性  #/status
// ===================================================================
async function status() {
  const h = await getJson("/api/health");

  const banner = h.gaps.length
    ? `<div class="health warn">⚠ <b>監査GAP ${h.gaps.length}件 / 合計 ${Math.round(h.gaps.reduce((a, g) => a + (new Date(g.end) - new Date(g.start)) / 60000, 0))}分</b>（この区間は直接アクセスを取りこぼしている可能性。利用率の分母から除外）&nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)}（遅延 ${h.lagSeconds}s） データ源: <b>${esc(h.dataMode)}</b></div>`
    : `<div class="health ok">✅ 監査GAPなし &nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)} データ源: <b>${esc(h.dataMode)}</b></div>`;

  const collectors = h.collectors.map((c) => `
    <tr>
      <td><b>${esc(c.server)}</b></td>
      <td>${esc(c.channel)}</td>
      <td>${fmtTime(c.lastEvent)}</td>
      <td>${c.lagSeconds}s</td>
      <td><span class="state ${c.status === "OK" ? "ok" : "ng"}">${esc(c.status)}</span></td>
    </tr>`).join("");

  const gaps = h.gaps.map((g) => `
    <tr>
      <td>${fmtTime(g.start)}</td>
      <td>${fmtTime(g.end)}</td>
      <td>${Math.round((new Date(g.end) - new Date(g.start)) / 60000)}分</td>
      <td class="muted">${esc(g.reason)}</td>
    </tr>`).join("") || `<tr><td colspan="4" class="muted">GAPなし</td></tr>`;

  view.innerHTML = `
    <h1>サーバー状態 / 健全性</h1>
    <div class="sub">コレクター稼働状態・監査GAP区間・最終同期を集約します。</div>
    ${banner}
    <div class="card">
      <h2>コレクター状態</h2>
      <table>
        <thead><tr><th>サーバー</th><th>チャネル</th><th>最終イベント</th><th>遅延</th><th>状態</th></tr></thead>
        <tbody>${collectors}</tbody>
      </table>
    </div>
    <div class="card">
      <h2>監査GAP区間</h2>
      <table>
        <thead><tr><th>開始</th><th>終了</th><th>長さ</th><th>理由</th></tr></thead>
        <tbody>${gaps}</tbody>
      </table>
    </div>
    <p class="muted small">最終同期: <b>${fmtTime(h.lastSync)}</b> ／ 監査最新イベント: <b>${fmtTime(h.auditLatestEvent)}</b> ／ 同期遅延: <b>${h.lagSeconds}s</b></p>`;
}

// ===================================================================
// ルーター
// ===================================================================
const ROUTES = { dashboard, search, users, alerts, incidents, status };

function router() {
  const raw = location.hash.replace(/^#\//, "");
  const parts = raw ? raw.split("/") : ["dashboard"];
  const name = parts[0] || "dashboard";
  const fn = ROUTES[name] || dashboard;

  $$("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === name));
  view.scrollTop = 0;
  fn(parts.slice(1));
}

document.getElementById("globalq").addEventListener("keydown", (e) => {
  if (e.key === "Enter") {
    searchState.q = e.target.value.trim();
    searchState.page = 1;
    if (location.hash === "#/search") search(); else location.hash = "#/search";
  }
});

window.addEventListener("hashchange", router);
if (!location.hash) location.hash = "#/dashboard"; else router();
