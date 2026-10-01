"use strict";
/* Texts (tr first, en second) and the hand-built formatting the live board's page uses
   (docs/live-board.md). Loaded before board_ui_sessions.js and board_ui.js in the
   browser; required by them in Node, so the tests measure the same code the page runs. */
(function (root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.BoardText = api;
  }
})(typeof window !== "undefined" ? window : globalThis, function () {
  const I18N = {
    tr: {
      title: "Claude Monitor", mode: "Mod", lastEvent: "son olay:", agentsHdr: "Ajanlar",
      tasksHdr: "İşler", activityHdr: "Ajan hareketleri", colTask: "İş", colBranch: "Dal",
      colRole: "Rol / ajan", colStatus: "Durum", colTime: "Süre", colNote: "Not",
      colAgent: "Ajan", colStarted: "Başladı", turnOpen: "Claude çalışıyor", turnClosed: "Tur bitti",
      noSession: "Oturum yok", allDone: "Tüm işler bitti.", remove: "Çıkar", restore: "Geri al",
      noTasks: "Henüz iş yok — Claude plan yazınca burada görünecek.", noAgents: "Henüz ajan başlatılmadı.",
      running: "çalışan", stRunning: "Çalışıyor", stWaiting: "Bekliyor", stDone: "Bitti",
      stRemoved: "Çıkarıldı", stPlanned: "Planlandı", unreachable: "Sunucuya ulaşılamıyor",
      notMeasured: "ölçülemiyor", sec: "sn", min: "dk", hr: "sa", roleOff: "kapalı", roleOn: "açık",
      s_planned: "planlandı", s_running: "çalışıyor", s_agent_done: "ajan bitti · onay bekliyor",
      s_waiting: "bekliyor", s_done: "bitti", s_failed: "başarısız", s_removed: "çıkarıldı",
      s_starting: "başlıyor", s_denied: "reddedildi",
      noProjects: "Henüz proje yok — bir projede Claude oturumu başlayınca burada görünür.",
      s_needs_decision: "karar bekliyor", stDecision: "Karar bekliyor", decided: "Karar",
      continue: "Devam", reject: "Reddet", notePh: "not (isteğe bağlı)", colMerge: "Merge",
      sessionsHdr: "Oturumlar", colSession: "Oturum", colContext: "Bağlam", colCost: "Maliyet",
      colRate: "$/sa (son 60 dk)", colProjection: "Bitiş tahmini", colSend: "Görev / skill gönder",
      colCostEta: "Maliyet / ETA", busy: "meşgul", idle: "boşta", unknown: "bilinmiyor",
      compact: "compact önerilir", orchestration: "orkestrasyon", agentsCost: "ajanlar", tokens: "token",
      estimate: "tahmin", spent: "harcanan", left: "kalan", overEst: "tahmini aştı",
      noAgentLinked: "bağlı ajan yok", orchEst: "orkestratör ≈", orchBasis: "oturum {s}, görevin zaman penceresi (tahmin)", orchShared: "aynı pencerede {n} başka görev", queuePh: "yeni görev metni", send: "Gönder", runSkill: "Çalıştır",
      pickSkill: "skill seç", modeSwitch: "Modu değiştir:", modeByBoard: "panodan seçildi", queued: "sırada", todos: "yapılacaklar",
      noSessions: "Son 24 saatte oturum yok.", unpriced: "fiyatı bilinmeyen model", etaLeft: "ETA kalan",
      estCost: "tahmini bütçe", projected: "bitiş ≈", perHour: "/sa",
      confirmMode: "Çalışma modu {mode} olsun mu? .claude/mode yazılır; bu seçim onay sayılır (#27).",
      undercount: "Not: ajan transcript'leri output_tokens'ı akışın başındaki değerle kaydedebilir — ajan maliyeti eksik sayılmış olabilir.",
      helpCompact: "/compact bir CLI komutudur: ne Claude ne de bir hook onu tetikleyebilir. Bağlam %80'i geçince oturumda kendiniz yazın.",
      helpQueue: "Meşgul oturum: görev, oturumun bir sonraki ana-iş-parçacığı araç çağrısından sonra iletilir. Boşta oturum: yalnızca Stop hook'unun bekleme penceresinde (panoda cevapsız bir soru varken, en çok {s} sn) ya da sohbete yeni bir mesaj yazdığınızda.",
      colTokens: "Token (gir / çık / önbellek oku / yaz)", tokIn: "gir", tokOut: "çık", tokCacheR: "ön.oku",
      tokCacheW: "ön.yaz", total: "Toplam (ölçülen ajanlar)", notMeasuredYet: "henüz ölçülemiyor",
      agentRowsNote: "Bir ajanın maliyeti, kimliği ve transcript'i bilindiğinde okunur; çalışan ajan için o ana kadarki maliyet gösterilir. Devam ettirilen / Workflow ajanları için satır açılması T-2'ye bağlıdır.",
      channel: "kanal", channelTitle: "Boşta olsa da bu oturuma görev itilebilir (board-channel sunucusu bağlı)",
      channelOnly: "yalnızca kanalla ulaşılabilen oturumlar", noChannelSessions: "Kanalla ulaşılabilen oturum yok.",
      helpChannel: "Kanal: {n} oturum kanalla ulaşılabilir. Bir oturum kanalla ulaşılabilir olması için terminalde `claude --dangerously-load-development-channels server:{name}` ile başlatılmış olmalı (Claude masaüstü uygulaması bunu desteklemez). Kanal yalnızca BOŞTAKİ oturuma itilir; meşgul oturuma hook iletir.",
      basisTask: "tahmin: görevin kendi $/sa hızı × kalan ETA",
      basisSession: "tahmin: son 60 dk $/sa × açık işlerin kalan ETA'sı ({h} sa, {n} iş; ETA'sız {m} iş sayılmadı)",
    },
    en: {
      title: "Claude Monitor", mode: "Mode", lastEvent: "last event:", agentsHdr: "Agents",
      tasksHdr: "Tasks", activityHdr: "Agent activity", colTask: "Task", colBranch: "Branch",
      colRole: "Role / agent", colStatus: "Status", colTime: "Time", colNote: "Note",
      colAgent: "Agent", colStarted: "Started", turnOpen: "Claude is working", turnClosed: "Turn finished",
      noSession: "No session", allDone: "All tasks are done.", remove: "Remove", restore: "Restore",
      noTasks: "No tasks yet — they appear once Claude writes the plan.", noAgents: "No agent started yet.",
      running: "running", stRunning: "Running", stWaiting: "Waiting", stDone: "Done",
      stRemoved: "Removed", stPlanned: "Planned", unreachable: "Server unreachable",
      notMeasured: "not measured", sec: "s", min: "m", hr: "h", roleOff: "off", roleOn: "on",
      s_planned: "planned", s_running: "running", s_agent_done: "agent done · awaiting review",
      s_waiting: "waiting", s_done: "done", s_failed: "failed", s_removed: "removed",
      s_starting: "starting", s_denied: "denied",
      noProjects: "No project yet — one appears when a Claude session starts in it.",
      s_needs_decision: "awaiting decision", stDecision: "Awaiting decision", decided: "Decision",
      continue: "Continue", reject: "Reject", notePh: "note (optional)", colMerge: "Merge",
      sessionsHdr: "Sessions", colSession: "Session", colContext: "Context", colCost: "Cost",
      colRate: "$/h (last 60 min)", colProjection: "Projected finish", colSend: "Send a task / skill",
      colCostEta: "Cost / ETA", busy: "busy", idle: "idle", unknown: "unknown",
      compact: "compact suggested", orchestration: "orchestration", agentsCost: "agents", tokens: "tokens",
      estimate: "estimate", spent: "spent", left: "left", overEst: "over the estimate",
      noAgentLinked: "no agent linked", orchEst: "orchestrator ≈", orchBasis: "session {s}, the task's time window (estimate)", orchShared: "{n} other task(s) in the same window", queuePh: "new task text", send: "Send", runSkill: "Run",
      pickSkill: "pick a skill", modeSwitch: "Switch mode:", modeByBoard: "picked on the board", queued: "queued", todos: "todo",
      noSessions: "No session in the last 24 hours.", unpriced: "unpriced model", etaLeft: "ETA left",
      estCost: "est. cost", projected: "finish ≈", perHour: "/h",
      confirmMode: "Switch the working mode to {mode}? .claude/mode is written; this selection is the approval (#27).",
      undercount: "Note: subagent transcripts may record output_tokens at stream start — agent cost may be undercounted.",
      helpCompact: "/compact is a CLI command: neither Claude nor a hook can trigger it. Past 80% context, type it in the session yourself.",
      helpQueue: "Busy session: the task arrives after the session's next main-thread tool call. Idle session: only inside the Stop hook's wait window (while a question on the board is unanswered, at most {s} s) or when you type a new message in the chat.",
      colTokens: "Tokens (in / out / cache read / write)", tokIn: "in", tokOut: "out", tokCacheR: "c.read",
      tokCacheW: "c.write", total: "Total (measured agents)", notMeasuredYet: "cannot be measured yet",
      agentRowsNote: "An agent's cost is read once its id and transcript are known; a running agent shows its cost so far. Rows for resumed / Workflow agents depend on T-2.",
      channel: "channel", channelTitle: "A task can be pushed to this session even while it is idle (a board-channel server is connected)",
      channelOnly: "only sessions a channel can reach", noChannelSessions: "No session a channel can reach.",
      helpChannel: "Channel: {n} session(s) reachable by channel. A session is reachable only when it was started in a terminal with `claude --dangerously-load-development-channels server:{name}` (the Claude desktop app cannot). A channel pushes to an IDLE session only; a busy session gets the task from the hooks.",
      basisTask: "estimate: the task's own $/h × its remaining ETA",
      basisSession: "estimate: $/h over the last 60 min × remaining ETA of open tasks ({h} h, {n} tasks; {m} without ETA not counted)",
    },
  };

  const makeT = (lang) => (k) => (I18N[lang] || {})[k] ?? I18N.tr[k] ?? k;
  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const pad = (n) => String(n).padStart(2, "0");
  // #12: dd/mm/yyyy, built by hand — no locale-aware formatting.
  const fmtDate = (d) => `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
  const fmtTime = (d) => `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;

  function fmtStamp(iso, now = new Date()) {
    if (!iso) return "–";
    const d = new Date(iso);
    return fmtDate(d) === fmtDate(now) ? fmtTime(d) : `${fmtDate(d)} ${fmtTime(d)}`;
  }

  function fmtDur(t, fromIso, toIso, now = new Date()) {
    if (!fromIso) return t("notMeasured");
    const s = Math.max(0, Math.round(((toIso ? new Date(toIso) : now) - new Date(fromIso)) / 1000));
    if (s < 60) return `${s} ${t("sec")}`;
    if (s < 3600) return `${Math.floor(s / 60)} ${t("min")} ${s % 60} ${t("sec")}`;
    return `${Math.floor(s / 3600)} ${t("hr")} ${Math.floor(s / 60) % 60} ${t("min")}`;
  }

  /** Replaces {name} in a text with vars.name. */
  const fill = (text, vars) => text.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m));

  return { I18N, makeT, esc, pad, fmtDate, fmtTime, fmtStamp, fmtDur, fill };
});
