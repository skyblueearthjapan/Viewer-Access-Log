"use strict";

const state = {
  from: "2026-06-27",
  to: "2026-06-27",
  user: "",
  q: "",
  sources: ["viewer", "direct", "unknown"],
  page: 1,
  pageSize: 50,
};

const $ = (id) => document.getElementById(id);

function nextDay(d) {
  const dt = new Date(d + "T00:00:00+09:00");
  dt.setDate(dt.getDate() + 1);
  return dt.toISOString().slice(0, 10);
}
function fromIso(d) { return d + "T00:00:00+09:00"; }
function toIso(d) { return nextDay(d) + "T00:00:00+09:00"; }

function fmtTime(iso) {
  const dt = new Date(iso);
  const p = (n) => String(n).padStart(2, "0");
  return `${p(dt.getMonth() + 1)}/${p(dt.getDate())} ${p(dt.getHours())}:${p(dt.getMinutes())}`;
}
function selectedKinds() {
  return [...document.querySelectorAll("#kinds input:checked")].map((c) => c.value);
}

function baseParams() {
  const p = new URLSearchParams();
  p.set("from", fromIso(state.from));
  p.set("to", toIso(state.to));
  if (state.user) p.set("user", state.user);
  if (state.q) p.set("q", state.q);
  return p;
}

async function loadHealth() {
  const h = await (await fetch("/api/health")).json();
  const el = $("health");
  const gapMin = h.gaps.reduce((a, g) => a + (new Date(g.end) - new Date(g.start)) / 60000, 0);
  if (h.gaps.length) {
    el.className = "health warn";
    el.innerHTML =
      `⚠ <b>監査GAP ${h.gaps.length}件 / 合計 ${Math.round(gapMin)}分</b>（この区間は直接アクセスを取りこぼしている可能性。利用率の分母から除外しています）` +
      ` &nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)}（遅延 ${h.lagSeconds}s） データ源: <b>${h.dataMode}</b>`;
  } else {
    el.className = "health ok";
    el.innerHTML = `✅ 監査GAPなし &nbsp;|&nbsp; 最終同期: ${fmtTime(h.lastSync)} データ源: <b>${h.dataMode}</b>`;
  }
}

async function loadSummary() {
  const s = await (await fetch("/api/summary?" + baseParams())).json();
  const pct = Math.round(s.adoptionRate * 100);
  $("kpis").innerHTML = `
    <div class="kpi"><div class="lbl">期間内アクセス（合計）</div><div class="num">${s.total.toLocaleString()}</div></div>
    <div class="kpi viewer"><div class="lbl">🟦 ビューアー経由</div><div class="num">${s.viewer.toLocaleString()}</div></div>
    <div class="kpi direct"><div class="lbl">🟥 サーバー直接</div><div class="num">${s.direct.toLocaleString()}</div><div class="sub2">直接アクセスした実ユーザー ${s.directUsers}名 / ${s.directFiles}ファイル</div></div>
    <div class="kpi unknown"><div class="lbl">⬜ 未帰属(MTSV$/不明)</div><div class="num">${s.unknown.toLocaleString()}</div></div>
    <div class="kpi rate"><div class="lbl">ビューアー利用率</div><div class="num">${pct}%</div><div class="sub2">青/(青+赤)・GAP ${s.gapMinutes}分を除外</div></div>`;
}

async function loadLogs() {
  const p = baseParams();
  p.set("sources", state.sources.join(","));
  const kinds = selectedKinds();
  if (kinds.length) p.set("kinds", kinds.join(","));
  p.set("page", state.page);
  p.set("pageSize", state.pageSize);

  const data = await (await fetch("/api/logs?" + p)).json();
  const srcLabel = { viewer: "🟦 ビューアー経由", direct: "🟥 サーバー直接", unknown: "⬜ 未帰属" };
  const srcCls = { viewer: "v", direct: "d", unknown: "u" };

  $("rows").innerHTML = data.rows.map((r) => `
    <tr class="${srcCls[r.source]}">
      <td>${fmtTime(r.time)}</td>
      <td><span class="src ${r.source}">${srcLabel[r.source]}</span></td>
      <td>${r.user}</td>
      <td><span class="op ${r.kind}">${r.action}</span></td>
      <td class="file">${shortFile(r.file)}${r.note ? ` <span class="muted">— ${r.note}</span>` : ""}</td>
      <td>${(r.pc || "")}${r.ip ? " / " + r.ip.split(".").slice(-1) : ""}</td>
      <td class="${r.success ? "ok" : "ng"}">${r.success ? "OK" : "拒否"}</td>
    </tr>`).join("") || `<tr><td colspan="7" class="muted">該当なし</td></tr>`;

  const start = (data.page - 1) * data.pageSize + 1;
  const end = Math.min(data.page * data.pageSize, data.total);
  $("count").textContent = `${data.total.toLocaleString()} 件`;
  $("pageinfo").textContent = data.total ? `${start}〜${end} / ${data.total}` : "0 件";
}

function shortFile(path) {
  if (!path) return "";
  const i = path.indexOf("技術部");
  return i >= 0 ? path.slice(i) : path;
}

function refresh() { loadHealth(); loadSummary(); loadLogs(); }

function wire() {
  $("from").value = state.from;
  $("to").value = state.to;

  $("run").onclick = () => {
    state.from = $("from").value; state.to = $("to").value;
    state.user = $("user").value.trim(); state.q = $("q").value.trim();
    state.page = 1; refresh();
  };
  $("clear").onclick = () => {
    $("user").value = ""; $("q").value = ""; state.user = ""; state.q = "";
    state.page = 1; loadLogs(); loadSummary();
  };
  $("q").addEventListener("keydown", (e) => { if (e.key === "Enter") $("run").click(); });

  document.querySelectorAll(".chip").forEach((c) => c.onclick = () => {
    c.classList.toggle("on");
    state.sources = [...document.querySelectorAll(".chip.on")].map((x) => x.dataset.src);
    state.page = 1; loadLogs();
  });
  document.querySelectorAll("#kinds input").forEach((c) => c.onchange = () => { state.page = 1; loadLogs(); });

  $("prev").onclick = () => { if (state.page > 1) { state.page--; loadLogs(); } };
  $("next").onclick = () => { state.page++; loadLogs(); };
}

wire();
refresh();
