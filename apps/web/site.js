// Site de download do Ximbuismo: arte em pontos (como no app) e status do servidor/instalador.
(() => {
  const motion = matchMedia("(prefers-reduced-motion: reduce)");
  let still = motion.matches;
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  const trigger = document.getElementById("boost-trigger");
  const label = trigger.querySelector("span");
  const caption = document.getElementById("boost-caption");
  const energy = document.getElementById("energy");
  const ectx = energy.getContext("2d");
  const chargeTime = 1500, burstTime = 1100;
  let phase = "idle", started = 0, exploded = 0, frame = 0, previous = 0;
  let visible = true, origin = { x: 0, y: 0 }, sparks = [];
  let mouse = { x: -9999, y: -9999 };

  function setPhase(next) {
    phase = next;
    document.body.classList.toggle("boost-charging", next === "charging");
    document.body.classList.toggle("boost-burst", next === "burst");
    document.body.classList.toggle("boost-energized", next === "burst" || next === "energized");
    trigger.disabled = next === "charging" || next === "burst";
    trigger.setAttribute("aria-pressed", String(next === "burst" || next === "energized"));
    trigger.setAttribute("aria-label", next === "energized" ? "Desligar a energia do site" : "Energizar o site");
    label.textContent = next === "charging" ? "CARGA" : next === "idle" ? "BOOST" : "BOOST ON";
    caption.textContent = next === "charging" ? "CONCENTRANDO ENERGIA" : next === "idle" ? "CLIQUE PARA ENERGIZAR" : "ENERGIA NO MÁXIMO · CLIQUE PARA DESLIGAR";
  }
  function resizeCanvas(canvas, context, w, h) {
    canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr);
    context.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  // ---------- fundo: grade de pontos que acende perto do cursor ----------
  const field = document.getElementById("field");
  const fctx = field.getContext("2d");
  function drawField(now = 0) {
    const w = innerWidth, h = innerHeight, step = 22;
    fctx.clearRect(0, 0, w, h);
    const powered = phase === "burst" || phase === "energized";
    const wave = phase === "burst" ? (now - exploded) * 1.8 : -9999;
    for (let y = step / 2; y < h; y += step)
      for (let x = step / 2; x < w; x += step) {
        const d = Math.hypot(x - mouse.x, y - mouse.y);
        const radius = Math.hypot(x - origin.x, y - origin.y);
        const pulse = powered ? .09 + (still ? .05 : (1 + Math.sin(radius / 80 - now / 650)) * .055) : 0;
        const shock = Math.max(0, 1 - Math.abs(radius - wave) / 110) * .65;
        const a = .07 + pulse + shock + Math.max(0, 1 - d / 220) * .35;
        fctx.fillStyle = `rgba(237,237,237,${a.toFixed(3)})`;
        const dotSize = shock > .2 ? 2 : 1;
        fctx.fillRect(x, y, dotSize, dotSize);
      }
  }
  let fieldQueued = false;
  const queueField = () => { if (!fieldQueued) { fieldQueued = true; requestAnimationFrame(now => { fieldQueued = false; drawField(now); }); } };
  addEventListener("pointermove", e => { if (!still) { mouse = { x: e.clientX, y: e.clientY }; queueField(); } }, { passive: true });
  document.documentElement.addEventListener("pointerleave", () => { mouse = { x: -9999, y: -9999 }; queueField(); });

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
    resizeCanvas(sphere, sctx, size, size);
  }
  function drawSphere(now = 0) {
    const charge = phase === "charging" ? Math.min(1, (now - started) / chargeTime) : 0;
    const burst = phase === "burst" ? Math.min(1, (now - exploded) / burstTime) : 0;
    const powered = phase === "burst" || phase === "energized";
    const c = size / 2, R = size * .34 * (1 - charge * .23 + Math.sin(burst * Math.PI) * .22);
    sctx.clearRect(0, 0, size, size);
    const cos = Math.cos(angle), sin = Math.sin(angle), tilt = 0.38, ct = Math.cos(tilt), st = Math.sin(tilt);
    for (const [x, y, z] of points) {
      const x1 = x * cos + z * sin, z1 = -x * sin + z * cos;
      const y2 = y * ct - z1 * st, z2 = y * st + z1 * ct;
      const a = .12 + .6 * Math.max(0, z2) + .25 * Math.max(0, -x1 * .6 - y2 * .4) + charge * .45 + (powered ? .2 : 0);
      sctx.fillStyle = `rgba(237,237,237,${Math.min(a, 0.9).toFixed(3)})`;
      const s = z2 > 0.4 ? 1.4 : 1;
      sctx.fillRect(c + x1 * R, c + y2 * R, s, s);
    }
    if (charge > 0) {
      sctx.strokeStyle = `rgba(237,237,237,${charge * .32})`;
      sctx.lineWidth = 1;
      for (let i = 0; i < 28; i++) {
        const a = i * Math.PI * 2 / 28 + angle;
        const travel = ((now / 650 + i * .137) % 1);
        const r = size * (.25 + .23 * (1 - travel));
        sctx.beginPath(); sctx.moveTo(c + Math.cos(a) * r, c + Math.sin(a) * r);
        sctx.lineTo(c + Math.cos(a) * (r + 5 + charge * 13), c + Math.sin(a) * (r + 5 + charge * 13)); sctx.stroke();
      }
    }
    // faixa de poeira horizontal, como no app
    for (const [dx, dy, a] of dust) {
      const x = c + dx * size * 0.5, y = size * 0.84 + dy * size;
      const fade = 1 - Math.abs(dx);
      sctx.fillStyle = `rgba(237,237,237,${(a * fade * (powered ? .85 : .55)).toFixed(3)})`;
      sctx.fillRect(x, y, 1, 1);
    }
  }
  function explode(now) {
    exploded = now;
    const rect = trigger.getBoundingClientRect();
    origin = { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
    sparks = Array.from({ length: 180 }, (_, i) => {
      const a = i * 2.399963, speed = 160 + Math.random() * 820;
      return { dx: Math.cos(a), dy: Math.sin(a), speed, length: 3 + Math.random() * 16 };
    });
    setPhase("burst");
  }
  function drawEnergy(now) {
    ectx.clearRect(0, 0, innerWidth, innerHeight);
    if (phase !== "burst") return;
    const t = Math.min(1, (now - exploded) / burstTime), fade = (1 - t) ** 2;
    const distance = 1 - (1 - t) ** 3;
    for (const p of sparks) {
      const r = 45 + distance * p.speed;
      ectx.strokeStyle = `rgba(237,237,237,${fade * .8})`; ectx.lineWidth = 1;
      ectx.beginPath(); ectx.moveTo(origin.x + p.dx * r, origin.y + p.dy * r);
      ectx.lineTo(origin.x + p.dx * (r + p.length * (1 - t)), origin.y + p.dy * (r + p.length * (1 - t))); ectx.stroke();
    }
    // Uma onda suave, sem flash nem deslocamento do conteúdo.
    ectx.strokeStyle = `rgba(237,237,237,${fade * .4})`; ectx.lineWidth = 1.5;
    ectx.beginPath(); ectx.arc(origin.x, origin.y, 50 + distance * Math.hypot(innerWidth, innerHeight), 0, Math.PI * 2); ectx.stroke();
  }
  function tick(now) {
    frame = 0;
    if (document.hidden || still) return;
    const delta = Math.min(50, now - (previous || now)); previous = now;
    if (phase === "charging" && now - started >= chargeTime) explode(now);
    if (phase === "burst" && now - exploded >= burstTime) { sparks = []; setPhase("energized"); }
    angle += delta * (phase === "charging" ? .00025 + Math.min(1, (now - started) / chargeTime) * .002 : phase === "idle" ? .00021 : .00055);
    if (visible) drawSphere(now);
    if (phase !== "idle") drawField(now);
    drawEnergy(now);
    if (visible || phase !== "idle") frame = requestAnimationFrame(tick);
  }
  function wake() {
    if (!frame && !still && !document.hidden) { previous = 0; frame = requestAnimationFrame(tick); }
  }
  trigger.addEventListener("click", () => {
    if (phase === "charging" || phase === "burst") return;
    if (phase === "energized") { setPhase("idle"); sparks = []; ectx.clearRect(0, 0, innerWidth, innerHeight); }
    else if (still) setPhase("energized");
    else { started = performance.now(); setPhase("charging"); }
    drawField(performance.now()); drawSphere(performance.now()); wake();
  });
  function resize() {
    resizeCanvas(field, fctx, innerWidth, innerHeight); resizeCanvas(energy, ectx, innerWidth, innerHeight);
    resizeSphere(); drawField(performance.now()); drawSphere(performance.now()); wake();
  }
  addEventListener("resize", resize);
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) { cancelAnimationFrame(frame); frame = 0; }
    else wake();
  });
  motion.addEventListener("change", e => {
    still = e.matches;
    if (still) {
      cancelAnimationFrame(frame); frame = 0;
      if (phase === "charging" || phase === "burst") setPhase("energized");
      sparks = []; ectx.clearRect(0, 0, innerWidth, innerHeight);
      drawField(); drawSphere();
    } else wake();
  });
  new IntersectionObserver(([entry]) => { visible = entry.isIntersecting; if (visible) wake(); }).observe(sphere);
  resize();

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
