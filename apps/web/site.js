// Site de download do Ximbuismo: arte em pontos (como no app) e status do servidor/instalador.
(() => {
  const still = matchMedia("(prefers-reduced-motion: reduce)").matches;
  const dpr = Math.min(window.devicePixelRatio || 1, 2);

  // ---------- fundo: grade de pontos que acende perto do cursor ----------
  const field = document.getElementById("field");
  const fctx = field.getContext("2d");
  let mouse = { x: -9999, y: -9999 };
  function drawField() {
    const w = innerWidth, h = innerHeight, step = 22;
    field.width = w * dpr; field.height = h * dpr;
    fctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    fctx.clearRect(0, 0, w, h);
    for (let y = step / 2; y < h; y += step)
      for (let x = step / 2; x < w; x += step) {
        const d = Math.hypot(x - mouse.x, y - mouse.y);
        const a = 0.07 + Math.max(0, 1 - d / 220) * 0.35;
        fctx.fillStyle = `rgba(237,237,237,${a.toFixed(3)})`;
        fctx.fillRect(x, y, 1, 1);
      }
  }
  let fieldQueued = false;
  const queueField = () => { if (!fieldQueued) { fieldQueued = true; requestAnimationFrame(() => { fieldQueued = false; drawField(); }); } };
  addEventListener("resize", queueField);
  if (!still) addEventListener("pointermove", e => { mouse = { x: e.clientX, y: e.clientY }; queueField(); }, { passive: true });
  drawField();

  // ---------- esfera de pontos girando em volta do botão BOOST ----------
  const sphere = document.getElementById("sphere");
  const sctx = sphere.getContext("2d");
  const points = [];
  const N = 1400;
  for (let i = 0; i < N; i++) { // distribuição de Fibonacci na esfera
    const y = 1 - (i / (N - 1)) * 2, r = Math.sqrt(1 - y * y), t = i * 2.399963;
    points.push([Math.cos(t) * r, y, Math.sin(t) * r]);
  }
  const dust = Array.from({ length: 700 }, () => [Math.random() * 2 - 1, (Math.random() - 0.5) * (Math.random() * 0.16), Math.random()]);
  let angle = 0, size = 0;
  function resizeSphere() {
    size = sphere.clientWidth;
    sphere.width = size * dpr; sphere.height = size * dpr;
    sctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  function drawSphere() {
    const c = size / 2, R = size * 0.34;
    sctx.clearRect(0, 0, size, size);
    const cos = Math.cos(angle), sin = Math.sin(angle), tilt = 0.38, ct = Math.cos(tilt), st = Math.sin(tilt);
    for (const [x, y, z] of points) {
      const x1 = x * cos + z * sin, z1 = -x * sin + z * cos;
      const y2 = y * ct - z1 * st, z2 = y * st + z1 * ct;
      const a = 0.12 + 0.6 * Math.max(0, z2) + 0.25 * Math.max(0, -x1 * 0.6 - y2 * 0.4);
      sctx.fillStyle = `rgba(237,237,237,${Math.min(a, 0.9).toFixed(3)})`;
      const s = z2 > 0.4 ? 1.4 : 1;
      sctx.fillRect(c + x1 * R, c + y2 * R, s, s);
    }
    // faixa de poeira horizontal, como no app
    for (const [dx, dy, a] of dust) {
      const x = c + dx * size * 0.5, y = size * 0.84 + dy * size;
      const fade = 1 - Math.abs(dx);
      sctx.fillStyle = `rgba(237,237,237,${(a * fade * 0.55).toFixed(3)})`;
      sctx.fillRect(x, y, 1, 1);
    }
  }
  resizeSphere(); drawSphere();
  addEventListener("resize", () => { resizeSphere(); drawSphere(); });
  if (!still) {
    // anima só enquanto a esfera está visível
    let running = false;
    new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting && !running) { running = true; (function tick() { if (!running) return; angle += 0.0035; drawSphere(); requestAnimationFrame(tick); })(); }
      else if (!entry.isIntersecting) running = false;
    }).observe(sphere);
  }

  // ---------- quem não está no Windows recebe um aviso ----------
  const ua = navigator.userAgent;
  if (!/Windows/i.test(ua) && /Android|iPhone|iPad|Macintosh|Linux|CrOS/i.test(ua))
    document.querySelector("[data-platform-note]").hidden = false;

  // ---------- status do servidor e do instalador ----------
  const set = (sel, text) => document.querySelectorAll(sel).forEach(el => (el.textContent = text));
  const dots = document.querySelectorAll("[data-status-dot]");
  fetch("/api/health", { cache: "no-store" })
    .then(r => (r.ok ? r.json() : Promise.reject(r.status)))
    .then(info => {
      dots.forEach(d => d.classList.add("on"));
      set("[data-status-foot]", "SERVIDOR ONLINE");
      set("[data-status]", "SERVIDOR ONLINE · GRÁTIS");
      if (info.download && info.download.size) set("[data-size]", `${Math.round(info.download.size / 1048576)} MB`);
      if (info.download && info.download.version) set("[data-version]", `VERSÃO ${info.download.version} · WINDOWS 10 · 11 · 64 BITS`);
      if (info.download && info.download.available === false) {
        document.querySelectorAll("[data-download]").forEach(a => { a.classList.add("disabled"); a.setAttribute("aria-disabled", "true"); });
        set("[data-size]", "EM BREVE");
      }
    })
    .catch(() => {
      dots.forEach(d => d.classList.add("off"));
      set("[data-status-foot]", "SERVIDOR FORA DO AR");
    });
})();
