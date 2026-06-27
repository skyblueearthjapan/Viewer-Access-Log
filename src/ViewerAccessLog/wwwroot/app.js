"use strict";

// ===================================================================
// 共有期間状態（P1c: 機能①）
// サンプルデータは 2026-06-27 固定。判定に Date.now を使わない。
// ===================================================================
const SAMPLE_DATE = "2026-06-27";
const period = { from: SAMPLE_DATE, to: SAMPLE_DATE };

// ===================================================================
// DOM / ユーティリティ
// ===================================================================
const view = document.getElementById("view");
const $ = (sel, ctx = document) => ctx.querySelector(sel);
const $$ = (sel, ctx = document) => [...ctx.querySelectorAll(sel)];

function nextDay(d) {
  const dt = new Date(d + "T00:00:00+09:00");
  dt.setDate(dt.getDate() + 1);
  return dt.toISOString().slice(0, 10);
}
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
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;"}[c]));
const num = (n) => Number(n).toLocaleString();
const getJson = (url) => fetch(url).then((r) => r.json());

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

// ===================================================================
// 期間バー（P1c: 機能①）— dashboard / search の両ビューに挿入する
// ===================================================================
function periodBarHtml() {
  return `<div class="period-bar">
    <span class="t">期間:</span>
    <input type="date" id="pb-from" value="${period.from}">
    <span class="pb-sep">〜</span>
    <input type="date" id="pb-to" value="${period.to}">
    <button class="qbtn" data-q="day">当日(06-27)</button>
    <button class="qbtn" data-q="3days">直近3日(06-25〜06-27)</button>
    <button class="qbtn" data-q="month">当月(06-01〜06-30)</button>
  </div>`;
}

/** 期間バーをバインドする。onApply はフォーム変更時に呼び出されるコールバック。 */
function bindPeriodBar(onApply) {
  $$(".qbtn", view).forEach((b) => b.onclick = () => {
    switch (b.dataset.q) {
      case "day":    period.from = period.to = SAMPLE_DATE; break;
      case "3days":  period.from = "2026-06-25"; period.to = SAMPLE_DATE; break;
      case "month":  period.from = "2026-06-01"; period.to = "2026-06-30"; break;
    }
    onApply();
  });
  const pbFrom = $("#pb-from", view);
  const pbTo   = $("#pb-to",   view);
  if (pbFrom) pbFrom.onchange = (e) => { period.from = e.target.value; onApply(); };
  if (pbTo)   pbTo.onchange   = (e) => { period.to   = e.target.value; onApply(); };
}

// ===================================================================
// 詳細スライドインパネル（P1c: 機能②）
// ===================================================================
const dpOverlay = document.getElementById("dp-overlay");
const dpPanel   = document.getElementById("dp-panel");
const dpBody    = document.getElementById("dp-body");

function openDetail(r) {
  dpBody.innerHTML = [
    ["ID",       esc(r.id)],
    ["日時",     fmtTime(r.time)],
    ["ソース",   `<span class="src ${r.source}">${SRC_LABEL[r.source]}</span>`],
    ["部署",     esc(r.dept)],
    ["ユーザー", esc(r.user)],
    ["操作",     `<span class="op ${r.kind}">${esc(r.action)}</span>`],
    ["ファイル", `<span class="dp-val file">${esc(r.file || "—")}</span>`],
    ["フォルダ", `<span class="dp-val file">${esc(r.folder || "—")}</span>`],
    ["PC",       esc(r.pc  || "—")],
    ["IP",       esc(r.ip  || "—")],
    ["結果",     `<span class="${r.success ? "ok" : "ng"}">${r.success ? "OK" : "拒否"}</span>`],
    ...(r.note ? [["メモ", esc(r.note)]] : []),
  ].map(([label, val]) =>
    `<div class="dp-row"><div class="dp-label">${label}</div><div class="dp-val">${val}</div></div>`
  ).join("");
  dpOverlay.classList.add("open");
  dpPanel.classList.add("open");
}

function closeDetail() {
  dpOverlay.classList.remove("open");
  dpPanel.classList.remove("open");
}

dpOverlay.onclick = closeDetail;
document.getElementById("dp-close").onclick = closeDetail;
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeDetail(); });

// ===================================================================
// ① ダッシュボード  #/dashboard
// ===================================================================
async function dashboard() {
  const d = await getJson("/api/dashboard?" + dateParams());
  const s = d.summary;
  const pct = Math.round(s.adoptionRate * 100);

  // KPI カード（🟦/🟥/⬜ は drillToSearch クリッカブル）
  const kpisHtml = `<div class="kpis">
    <div class="kpi"><div class="lbl">期間内アクセス（合計）</div><div class="num">${num(s.total)}</div></div>
    <div class="kpi viewer clickable" data-drill-src="viewer" title="クリックで検索へ">
      <div class="lbl">🟦 ビューアー経由</div><div class="num">${num(s.viewer)}</div></div>
    <div class="kpi direct clickable" data-drill-src="direct" title="クリックで検索へ">
      <div class="lbl">🟥 サーバー直接</div><div class="num">${num(s.direct)}</div>
      <div class="sub2">実ユーザー ${s.directUsers}名 / ${s.directFiles}ファイル</div></div>
    <div class="kpi unknown clickable" data-drill-src="unknown" title="クリックで検索へ">
      <div class="lbl">⬜ 未帰属(MTSV$/不明)</div><div class="num">${num(s.unknown)}</div></div>
    <div class="kpi rate">
      <div class="lbl">ビューアー利用率</div><div class="num">${pct}%</div>
      <div class="sub2">青/(青+赤)・GAP ${s.gapMinutes}分を除外</div></div>
  </div>`;

  // 時間帯別スタック棒
  const hrs    = d.hourly.filter((h) => h.viewer + h.direct + h.unknown > 0);
  const maxH   = Math.max(1, ...hrs.map((h) => h.viewer + h.direct + h.unknown));
  const barsHtml = hrs.map((h) => {
    const seg = (v, c) => v ? `<div class="seg ${c}" style="height:${(v / maxH) * 130}px" title="${c}:${v}"></div>` : "";
    return `<div class="hcol"><div class="hbar">${seg(h.viewer,"v")}${seg(h.direct,"d")}${seg(h.unknown,"u")}</div><div class="hx">${h.hour}時</div></div>`;
  }).join("") || `<div class="muted">データなし</div>`;

  // 横棒ヘルパー（drill-attr はデータ属性名）
  const hbarsHtml = (list, cls, drillAttr) => {
    const max = Math.max(1, ...list.map((x) => x.count));
    return list.map((x) => `
      <div class="hbrow clickable" ${drillAttr}="${esc(x.name)}" title="${esc(x.name)} — クリックで検索へ">
        <span class="hbname">${esc(x.name)}</span>
        <span class="hbtrack"><span class="hbfill ${cls}" style="width:${(x.count / max) * 100}%"></span></span>
        <span class="hbval">${num(x.count)}</span>
      </div>`).join("") || `<div class="muted">該当なし</div>`;
  };

  const incHtml = d.recentIncidents.map((i) => `<tr>
    <td>${fmtTime(i.time)}</td><td><span class="tag">${esc(i.type)}</span></td>
    <td><span class="${sevCls(i.severity)}">${esc(i.severity)}</span></td>
    <td>${esc(i.user)}</td><td>${num(i.matchCount)}</td><td class="muted">${esc(i.metric)}</td>
  </tr>`).join("") || `<tr><td colspan="6" class="muted">なし</td></tr>`;

  view.innerHTML = `
    <h1>ダッシュボード</h1>
    <div class="sub">ビューアー経由（🟦）/ サーバー直接（🟥）/ 未帰属（⬜）で全社アクセスの効果を測定します。</div>
    ${periodBarHtml()}
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
        <div class="hbars">${hbarsHtml(d.directTopUsers, "d", "data-drill-user")}</div>
      </div>
    </div>
    <div class="grid2">
      <div class="card">
        <h2>部署別アクセス件数 <span class="muted small">（クリックで検索）</span></h2>
        <div class="hbars">${hbarsHtml(d.deptCounts, "v", "data-drill-dept")}</div>
      </div>
      <div class="card">
        <h2>直近インシデント</h2>
        <table>
          <thead><tr><th>日時</th><th>種別</th><th>重要度</th><th>ユーザー</th><th>一致</th><th>指標</th></tr></thead>
          <tbody>${incHtml}</tbody>
        </table>
        <p class="muted small"><a href="#/incidents">→ 検知インシデント一覧へ</a></p>
      </div>
    </div>`;

  // 期間バーをバインド（変更で再取得・再描画）
  bindPeriodBar(dashboard);

  // ドリルダウン: KPI カード（P1c: 機能④）
  $$("[data-drill-src]", view).forEach((el) => {
    el.onclick = () => drillToSearch({ sources: [el.dataset.drillSrc], user: "", dept: "", q: "" });
  });
  // ドリルダウン: Top ユーザー棒
  $$("[data-drill-user]", view).forEach((el) => {
    el.onclick = () => drillToSearch({ user: el.dataset.drillUser, sources: ["viewer","direct","unknown"], dept: "", q: "" });
  });
  // ドリルダウン: 部署別棒
  $$("[data-drill-dept]", view).forEach((el) => {
    el.onclick = () => drillToSearch({ dept: el.dataset.drillDept, sources: ["viewer","direct","unknown"], user: "", q: "" });
  });
}

// ===================================================================
// ② ログ検索  #/search
// ===================================================================

/** ログ検索の状態（ビュー切替後も保持）。 */
const searchState = {
  user: "", q: "", dept: "",
  sources: ["viewer", "direct", "unknown"],
  page: 1, pageSize: 50,
  sort: "time", desc: true,    // P1c: テーブルソート状態
};

/** 行データキャッシュ（P1c: 詳細パネル用）。loadLogs() のたびに更新。 */
let _rowCache = {};

/** ダッシュボードなど他ビューからフィルタを設定して検索ビューへ遷移する（P1c: 機能④）。 */
function drillToSearch(updates) {
  Object.assign(searchState, { page: 1 }, updates);
  if (location.hash === "#/search") search();
  else location.hash = "#/search";
}

async function search() {
  view.innerHTML = `
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
          <button id="csvbtn" class="csv-btn" title="現在の条件で全件 CSV をダウンロード">⬇ CSV</button>
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
      <div class="pager">
        <span id="pageinfo"></span>
        <span><button id="prev">◀</button> <button id="next">▶</button></span>
      </div>
      <div class="legend">
        <span>🟦 <b>ビューアー経由</b>＝SFEログにある＝確実</span>
        <span>🟥 <b>サーバー直接</b>＝実ユーザーの直読み（要注目）</span>
        <span>⬜ <b>未帰属</b>＝MTSV$/サービス/不明（ビューアーの証明ではない）</span>
        <span class="muted small">行クリックで詳細を表示</span>
      </div>
    </div>
    <p class="muted small">※ DataMode=Sample（サンプルデータ）。本番では SFE SQLite + AuditLogger を読み取り専用で同期します。</p>`;

  // 部署プルダウンを充填
  const filters = await getJson("/api/filters");
  const selDept = $("#dept");
  selDept.innerHTML = `<option value="">（全部署）</option>` +
    filters.depts.map((d) => `<option value="${esc(d)}"${d === searchState.dept ? " selected" : ""}>${esc(d)}</option>`).join("");

  // UI に searchState を反映
  $("#user").value = searchState.user;
  $("#q").value    = searchState.q;
  $$(".chip", view).forEach((c) => c.classList.toggle("on", searchState.sources.includes(c.dataset.src)));

  const selectedKinds = () => $$("#kinds input:checked").map((c) => c.value);

  /** ソート用クエリパラメータ付きの URLSearchParams を組み立てる（CSV にも使い回し）。 */
  function buildParams(forCsv = false) {
    const p = dateParams({ dept: searchState.dept, user: searchState.user, q: searchState.q });
    p.set("sources", searchState.sources.join(","));
    const kinds = selectedKinds();
    if (kinds.length) p.set("kinds", kinds.join(","));
    p.set("sort", searchState.sort);
    p.set("dir",  searchState.desc ? "desc" : "asc");
    if (!forCsv) {
      p.set("page",     searchState.page);
      p.set("pageSize", searchState.pageSize);
    }
    return p;
  }

  /** ソート列インジケータ（▼▲⇅）を更新する（P1c: 機能③）。 */
  function updateSortIndicators() {
    $$("th.sortable", view).forEach((th) => {
      const ind = $(".sort-ind", th);
      if (!ind) return;
      ind.textContent = th.dataset.col === searchState.sort
        ? (searchState.desc ? " ▼" : " ▲")
        : " ⇅";
      ind.classList.toggle("sort-active", th.dataset.col === searchState.sort);
    });
  }

  async function loadLogs() {
    const data = await getJson("/api/logs?" + buildParams());

    // 行キャッシュを更新（詳細パネル用）
    _rowCache = {};
    data.rows.forEach((r) => { _rowCache[r.id] = r; });

    $("#rows").innerHTML = data.rows.map((r) => `
      <tr class="${SRC_CLS[r.source]} clickrow" data-id="${r.id}">
        <td>${fmtTime(r.time)}</td>
        <td><span class="src ${r.source}">${SRC_LABEL[r.source]}</span></td>
        <td>${esc(r.dept)}</td>
        <td>${esc(r.user)}</td>
        <td><span class="op ${r.kind}">${esc(r.action)}</span></td>
        <td class="file">${esc(shortFile(r.file))}${r.note ? ` <span class="muted">— ${esc(r.note)}</span>` : ""}</td>
        <td>${esc(r.pc || "")}${r.ip ? " / " + r.ip.split(".").slice(-1) : ""}</td>
        <td class="${r.success ? "ok" : "ng"}">${r.success ? "OK" : "拒否"}</td>
      </tr>`).join("") || `<tr><td colspan="8" class="muted">該当なし</td></tr>`;

    // 行クリック → 詳細パネル（P1c: 機能②）
    $$("tr.clickrow", view).forEach((tr) => {
      tr.onclick = () => { const row = _rowCache[Number(tr.dataset.id)]; if (row) openDetail(row); };
    });

    const start = (data.page - 1) * data.pageSize + 1;
    const end   = Math.min(data.page * data.pageSize, data.total);
    $("#count").textContent    = `${num(data.total)} 件`;
    $("#pageinfo").textContent = data.total ? `${start}〜${end} / ${data.total}` : "0 件";

    updateSortIndicators();
  }

  // ---- イベントバインド ----

  // 期間バー（P1c: 機能①）— 期間変更で再検索
  bindPeriodBar(() => { searchState.page = 1; loadLogs(); });

  // ソートヘッダー（P1c: 機能③）
  $$("th.sortable", view).forEach((th) => {
    th.onclick = () => {
      const col = th.dataset.col;
      if (searchState.sort === col) searchState.desc = !searchState.desc;
      else { searchState.sort = col; searchState.desc = true; }
      searchState.page = 1;
      loadLogs();
    };
  });

  $("#run").onclick = () => {
    searchState.user = $("#user").value.trim();
    searchState.q    = $("#q").value.trim();
    searchState.dept = selDept.value;
    searchState.page = 1;
    loadLogs();
  };
  $("#clear").onclick = () => {
    searchState.user = ""; searchState.q = ""; searchState.dept = "";
    $("#user").value = ""; $("#q").value = ""; selDept.value = "";
    searchState.page = 1;
    loadLogs();
  };
  $("#q").addEventListener("keydown", (e) => { if (e.key === "Enter") $("#run").click(); });

  $$(".chip", view).forEach((c) => c.onclick = () => {
    c.classList.toggle("on");
    searchState.sources = $$(".chip.on", view).map((x) => x.dataset.src);
    searchState.page = 1;
    loadLogs();
  });
  $$("#kinds input").forEach((c) => c.onchange = () => { searchState.page = 1; loadLogs(); });

  $("#prev").onclick = () => { if (searchState.page > 1) { searchState.page--; loadLogs(); } };
  $("#next").onclick = () => { searchState.page++; loadLogs(); };

  // CSV ダウンロード（P1c: 機能⑤）— サーバー側で全件生成
  $("#csvbtn").onclick = () => {
    window.location.href = "/api/logs.csv?" + buildParams(true);
  };

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

  const timeline = d.timeline.map((r) => `
    <div class="tl ${SRC_CLS[r.source]}">
      <span class="tl-t">${fmtTime(r.time)}</span>
      <span class="src ${r.source}">${SRC_LABEL[r.source]}</span>
      <span class="op ${r.kind}">${esc(r.action)}</span>
      <span class="tl-f file">${esc(shortFile(r.file))}</span>
      ${r.note  ? `<span class="muted">— ${esc(r.note)}</span>` : ""}
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
      <div class="timeline">${timeline || `<div class="muted">データなし</div>`}</div>
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
  const gapMin = h.gaps.reduce((a, g) => a + (new Date(g.end) - new Date(g.start)) / 60000, 0);

  const banner = h.gaps.length
    ? `<div class="health warn">⚠ <b>監査GAP ${h.gaps.length}件 / 合計 ${Math.round(gapMin)}分</b>（この区間は直接アクセスを取りこぼしている可能性。利用率の分母から除外）&nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)}（遅延 ${h.lagSeconds}s） データ源: <b>${esc(h.dataMode)}</b></div>`
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
  closeDetail(); // ビュー切替時にパネルを閉じる
  const raw   = location.hash.replace(/^#\//, "");
  const parts = raw ? raw.split("/") : ["dashboard"];
  const name  = parts[0] || "dashboard";
  const fn    = ROUTES[name] || dashboard;

  $$("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.route === name));
  view.scrollTop = 0;
  fn(parts.slice(1));
}

// グローバル検索（Enter → ログ検索ビューへ遷移）
document.getElementById("globalq").addEventListener("keydown", (e) => {
  if (e.key === "Enter") {
    searchState.q    = e.target.value.trim();
    searchState.page = 1;
    e.target.value   = "";
    if (location.hash === "#/search") search(); else location.hash = "#/search";
  }
});

window.addEventListener("hashchange", router);
if (!location.hash) location.hash = "#/dashboard"; else router();
