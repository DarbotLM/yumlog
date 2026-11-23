"use strict";

const $ = id => document.getElementById(id);
const bridge = window.chrome?.webview;
const media = id => "https://yumlog-media.local/" + encodeURIComponent(id);
const tabIcons = { video: "\uE714", screenshot: "\uE722", audio: "\uE767", transcript: "\uE8A5",
  agents: "\uE716", voices: "\uE720", insights: "\uE9D9", bitcube: "\uE9F9", harvest: "\uE7B8", settings: "\uE713", library: "\uE8F1" };
const outputLabels = { clip: "Clip", screenshot: "Screenshot", frame: "Frame", audio: "Audio", spectrogram: "Spectrogram",
  transcript: "Transcript", voice: "Narration", ocr: "Frame analysis", runbook: "MDKG runbook", timedMetadata: "Timed metadata", atomicLibrary: "Atomic library", semanticReport: "Semantic report", bitdot: "Bitdot layers", bitcube: "Inverse-HDR cube", bitcubeCube: "Bitcube document", bitcubePng: "Bitcube preview", document: "Source document", schema: "Atomic card schema", agentCard: "AgentCard 3.1",
  agentVoice: "Speaker excerpts", agentVoices: "Agent Voices timeline", systemSpecs: "System specs tags" };
const backLabels = { video: "Video inputs and clips", screenshot: "Screenshot inputs and frames",
  audio: "Audio outputs", transcript: "Transcript sources", agents: "Agent capabilities", voices: "Agent Voices and narration",
  insights: "Analysis outputs", bitcube: "Inverse-HDR cube", harvest: "Harvest and delivery", settings: "Workspace settings", library: "Atomic widget schemas" };
const busyLabels = { import: "Opening recordings…", record: "Recording the desktop…", clip: "Exporting clip…",
  scenes: "Finding scene changes…", transcribe: "Transcribing audio locally…", bundle: "Building Paperboy bundle…",
  loadOcr: "Loading the Windows AI OCR model…", spectrogram: "Generating spectrogram…", decimate: "Processing the full recording: transcript, visual changes and semantic layers…", importAnalysis: "Verifying and importing analysis…", library: "Loading atomic library…", attachDocument: "Distilling document fields…",
  detectAgentVoices: "Detecting speaker turns and exporting voice previews…", agentVoices: "Loading Agent Voices…",
  genAudioSpeak: "Rendering narration with gen-audio…", genAudioTranscribe: "Transcribing with gen-audio VibeVoice…",
  systemSpecs: "Opening Settings > System > About and tagging the system specs…", bitcube: "Building the Inverse-HDR bitcube…" };

let state = { videos: [], selectedId: "", selectedProjectId: "", projects: [], workspacePath: "", tabs: [], artifacts: [], settings: { cardSize: "studio" }, paperboy: null };
let active = "video";
let flipped = false;
let request = 0;
let pending = false;
let selectedOutput = "";
let clipPreview = "";
let stageKey = "";
let voices = [];
let capabilities = [];
let agentVoiceManifest = null;
let agentVoiceError = "";
let agentVoiceRevision = 0;
let voiceSlice = null;
const agentVoiceFlips = new Set();
const agentVoicePages = new Map();
let atomicLibrary = null;
let libraryRevision = 0;
let bitcubeAsset = null;
let bitcubeRevision = 0;
let atomicQuery = "";
let atomicModality = "all";
let atomicPage = 0;
const atomicFlips = new Set();
const responses = new Map();
const drafts = new Map();
const cursors = new Map();
const previews = new Map();

const video = () => state.videos.find(item => item.id === state.selectedId);
const outputs = (...kinds) => state.artifacts.filter(item => item.exists && (!kinds.length || kinds.includes(item.kind)));
const draftKey = key => active === "settings" ? "settings:" + key : state.selectedId + ":" + active + ":" + key;
const status = (text, error = false) => {
  $("status").textContent = text;
  $("status").dataset.error = String(error);
};

function element(tag, className = "", value = "") {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (value !== "") node.textContent = value;
  return node;
}

function button(label, onClick, { primary = false, enabled = true, className = "" } = {}) {
  const node = element("button", "button " + className + (primary ? " primary" : ""), label);
  node.type = "button";
  node.disabled = !enabled;
  node.addEventListener("click", onClick);
  return node;
}

function send(command, payload) {
  if (!bridge) return Promise.reject(new Error("Open this workspace in Yumlog Studio to run local tools."));
  const requestId = String(++request);
  return new Promise(resolve => {
    responses.set(requestId, resolve);
    bridge.postMessage({ command, requestId, ...payload });
  });
}

async function run(command, payload = {}) {
  if (pending) return;
  setBusy(true);
  if (command !== "readArtifact") status(busyLabels[command] || "Working…");
  try {
    const reply = await send(command, payload);
    if (reply.error) throw new Error(reply.error);
    if (reply.cancelled) {
      status("Ready");
      return;
    }
    if (reply.state) {
      if (state.selectedId !== reply.state.selectedId) {
        selectedOutput = "";
        clipPreview = "";
        flipped = false;
        active = "video";
        atomicLibrary = null;
        atomicQuery = "";
        atomicModality = "all";
        atomicPage = 0;
        atomicFlips.clear();
        libraryRevision++;
        agentVoiceManifest = null;
        agentVoiceError = "";
        agentVoiceFlips.clear();
        agentVoicePages.clear();
        agentVoiceRevision++;
        voiceSlice = null;
      }
      state = reply.state;
    }
    if (command === "catalog") capabilities = reply.data || [];
    if (command === "voices") voices = reply.data || [];
    if (command === "agentVoices" || command === "detectAgentVoices") {
      agentVoiceManifest = typeof reply.data === "string" ? JSON.parse(reply.data) : reply.data;
      agentVoiceError = "";
      agentVoiceRevision++;
      if (command === "detectAgentVoices") {
        agentVoiceFlips.clear();
        agentVoicePages.clear();
      }
    }
    if (command === "library") {
      atomicLibrary = typeof reply.data === "string" ? JSON.parse(reply.data) : reply.data;
      libraryRevision++;
    }
    if (command === "bitcube") {
      bitcubeAsset = typeof reply.data === "string" ? JSON.parse(reply.data) : reply.data;
      bitcubeRevision++;
    }
    if (command === "newProject" || command === "selectProject" || command === "refreshContext") {
      bitcubeAsset = null;
    }
    if (["decimate", "importAnalysis", "attachDocument"].includes(command)) {
      atomicLibrary = null;
      atomicPage = 0;
      libraryRevision++;
      if (outputs("atomicLibrary").length) {
        const libraryReply = await send("library", {});
        if (!libraryReply.error) {
          atomicLibrary = typeof libraryReply.data === "string" ? JSON.parse(libraryReply.data) : libraryReply.data;
          libraryRevision++;
        }
      }
    }
    if (command === "readArtifact") previews.set(payload.artifactId, reply.data || "");
    if (command === "saveSettings") {
      for (const name of ["outputDirectory", "ffmpegPath", "whisperPath", "whisperModel", "genAudioPython", "genAudioRoot", "genAudioEngine", "genAudioVoice", "vibeVoiceModel"]) drafts.delete("settings:" + name);
    }
    render();
    status(reply.message || "Ready");
  } catch (error) {
    status(error.message, true);
    if (command === "setSize") $("card-size").value = state.settings.cardSize;
    if (command === "agentVoices" || command === "detectAgentVoices") {
      agentVoiceError = error.message;
      agentVoiceRevision++;
      renderCard();
    }
  } finally {
    setBusy(false);
  }
}

function showUpdateNotice(notice) {
  // Non-blocking: only a newer release shows a banner; every other result stays in the log.
  if (!notice || notice.status !== "update-available") return;
  const bar = $("update-notice");
  bar.replaceChildren(
    element("span", "", `Yumlog ${notice.latest} is available (running ${notice.current}).`),
    button("View release", () => send("openRelease", {}).then(reply => reply.error && status(reply.error, true))),
    button("Dismiss", () => { bar.hidden = true; }));
  bar.hidden = false;
}

bridge?.addEventListener("message", event => {
  const reply = event.data;
  if (reply.updateNotice) { showUpdateNotice(reply.updateNotice); return; }
  if (typeof reply.requestId === "string" && reply.requestId.startsWith("startup-")) {
    if (reply.state) { state = reply.state; render(); }
    status(reply.message || "Ready");
    return;
  }
  responses.get(reply.requestId)?.(reply);
  responses.delete(reply.requestId);
});

function setBusy(value) {
  pending = value;
  $("workspace").setAttribute("aria-busy", String(value));
  for (const control of document.querySelectorAll("[data-native-action], #import, #card-size, #output-folder")) {
    control.disabled = value || control.dataset.nativeAction === "unavailable";
  }
}

function act(title, command, payload = {}, options = {}) {
  const available = options.enabled !== false;
  const control = button(title, () => run(command, typeof payload === "function" ? payload() : payload),
    { ...options, enabled: available && !pending });
  control.dataset.nativeAction = available ? "available" : "unavailable";
  return control;
}

function clock(seconds) {
  const whole = Math.max(0, Math.floor(seconds || 0));
  const h = Math.floor(whole / 3600);
  const m = String(Math.floor(whole / 60) % 60).padStart(2, "0");
  const s = String(whole % 60).padStart(2, "0");
  return h ? h + ":" + m + ":" + s : m + ":" + s;
}

function bytes(value) {
  return value < 1048576 ? Math.round(value / 1024) + " KB" : (value / 1048576).toFixed(1) + " MB";
}

function stack(...children) {
  const node = element("div", "stack");
  node.append(...children);
  return node;
}

function field(label, name, fallback = "", { type = "text", min, max, step, options } = {}) {
  const wrapper = element("label", "field", label);
  const input = element(options ? "select" : type === "textarea" ? "textarea" : "input");
  if (options) {
    for (const value of options) {
      const option = element("option", "", value);
      option.value = value;
      input.append(option);
    }
  } else if (type === "number") {
    Object.assign(input, { type, min, max, step });
  }
  input.value = drafts.get(draftKey(name)) ?? fallback;
  if (options && !options.includes(input.value)) input.value = options[0] || "";
  const key = draftKey(name);
  input.addEventListener("input", () => drafts.set(key, input.value));
  wrapper.append(input);
  return { wrapper, input, value: () => input.value };
}

const numberField = (label, name, fallback, min, max, step = "0.01") => field(label, name, fallback, { type: "number", min, max, step });

function facts(entries) {
  const node = element("dl", "facts");
  for (const [label, value] of entries) {
    const item = element("div");
    item.append(element("dt", "", label), element("dd", "", String(value)));
    node.append(item);
  }
  return node;
}

function empty(title, description, action) {
  const surface = element("div", "stage");
  const content = element("div", "empty-stage");
  const icon = element("span", "play-glyph", active === "video" ? "▷" : "◈");
  icon.setAttribute("aria-hidden", "true");
  content.append(icon, element("h3", "", title), element("p", "", description));
  if (action) content.append(action);
  surface.append(content);
  return surface;
}

function projectVideos() {
  const selected = state.selectedProjectId || "";
  return selected ? (state.videos || []).filter(item => item.projectId === selected) : (state.videos || []);
}

function renderProjects() {
  const projects = state.projects || [];
  const count = $("project-count");
  if (count) count.textContent = String(projects.length);
  const path = $("workspace-copy");
  if (path) {
    path.textContent = state.workspacePath || state.settings?.outputDirectory || "Select output folder";
    path.title = path.textContent;
  }
  const list = $("project-list");
  if (!list) return;
  list.replaceChildren();
  if (!projects.length) {
    list.append(element("p", "library-empty", "New project groups recordings, extracted audio and bitcubes."));
  }
  for (const project of projects) {
    const tile = element("button", "project-item");
    tile.type = "button";
    tile.title = project.path;
    tile.setAttribute("aria-current", String(project.id === (state.selectedProjectId || "")));
    const avatar = element("span", "project-avatar", (project.name || "?")[0]);
    const text = element("span");
    text.append(element("span", "tile-title", project.name),
      element("small", "tile-meta", (project.recordingCount || 0) + " recordings · " + (project.outputCount || 0) + " outputs"));
    tile.append(avatar, text);
    tile.addEventListener("click", async () => {
      if (pending) return;
      await run("selectProject", { projectId: project.id === state.selectedProjectId ? "" : project.id });
    });
    list.append(tile);
  }
}

function renderLibrary() {
  renderProjects();
  const query = $("search").value.trim().toLocaleLowerCase();
  const visible = projectVideos();
  const matches = visible.filter(item => item.name.toLocaleLowerCase().includes(query));
  $("library-count").textContent = visible.length;
  const list = $("recordings");
  list.replaceChildren();
  if (!matches.length) {
    list.append(element("p", "library-empty", visible.length ? "No matching recordings." : (state.selectedProjectId ? "Open a video or record to add it to this project." : "Open a video or record your desktop to start.")));
  }
  for (const item of matches) {
    const tile = element("button", "recording-tile");
    tile.type = "button";
    tile.title = item.name;
    tile.setAttribute("aria-current", String(item.id === state.selectedId));
    const thumbnail = element("span", "tile-preview");
    if (item.hasPoster) {
      const img = element("img");
      Object.assign(img, { src: media(item.id + "-poster"), alt: "", loading: "lazy" });
      thumbnail.append(img);
    } else {
      thumbnail.textContent = "▷";
      thumbnail.setAttribute("aria-hidden", "true");
    }
    const text = element("span");
    text.append(element("span", "tile-title", item.title),
      element("small", "tile-meta", !item.exists ? "Source unavailable" : item.media?.duration ? clock(item.media.duration) : item.name.split(".").pop().toUpperCase()));
    tile.append(thumbnail, text);
    tile.addEventListener("click", async () => {
      if (pending) return;
      await run("select", { videoId: item.id });
      if (document.body.dataset.libraryOpen === "true") setLibrary(false);
      else list.querySelector('[aria-current="true"]')?.focus();
    });
    list.append(tile);
  }
}

function setLibrary(open) {
  document.body.dataset.libraryOpen = String(open);
  $("library-toggle").setAttribute("aria-expanded", String(open));
  if (open) $("search").focus();
}

function pauseMedia() {
  $("tool-face").querySelectorAll("video, audio").forEach(item => item.pause());
}

function setFlip(value) {
  flipped = value && !!state.paperboy;
  if (flipped) pauseMedia();
  $("card").dataset.flipped = String(flipped);
  $("tool-face").inert = flipped;
  $("paperboy-face").inert = !flipped;
  $("flip").setAttribute("aria-expanded", String(flipped));
  $("flip").textContent = flipped ? "⇄  Tool view" : "⇄  Paperboy";
  renderPane();
}

function switchTab(key) {
  if (key !== active) pauseMedia();
  const changed = active !== key;
  if (changed) {
    selectedOutput = "";
    if (key !== "video") voiceSlice = null;
  }
  active = key;
  renderCard();
  if (changed && key === "library" && !atomicLibrary && outputs("atomicLibrary").length && !pending) run("library");
  if (changed && key === "bitcube" && outputs("bitcube").length && !pending) run("readArtifact", { artifactId: outputs("bitcube")[0].id });
  if (changed && ["agents", "voices"].includes(key) && !agentVoiceManifest && outputs("agentVoices").length && !pending) {
    run("agentVoices");
  }
  if (changed && !flipped && matchMedia("(max-width: 820px)").matches) {
    $("card-title").scrollIntoView({ block: "nearest" });
  }
}

function renderTabs() {
  const bar = $("tabs");
  if (bar.children.length !== state.tabs.length) {
    const controls = state.tabs.map((tab, index) => {
      const node = element("button", "tab");
      node.type = "button";
      node.id = "tab-" + tab.key;
      node.setAttribute("role", "tab");
      node.setAttribute("aria-controls", "card-panel");
      const icon = element("i", "tab-icon", tabIcons[tab.key] || "");
      icon.setAttribute("aria-hidden", "true");
      node.append(icon, element("span", "", tab.label));
      node.addEventListener("click", () => switchTab(tab.key));
      node.addEventListener("keydown", event => {
        let next;
        switch (event.key) {
          case "ArrowRight": next = (index + 1) % state.tabs.length; break;
          case "ArrowLeft": next = (index + state.tabs.length - 1) % state.tabs.length; break;
          case "Home": next = 0; break;
          case "End": next = state.tabs.length - 1; break;
          default: return;
        }
        event.preventDefault();
        switchTab(state.tabs[next].key);
        bar.children[next].focus();
        bar.children[next].scrollIntoView({ block: "nearest", inline: "nearest" });
      });
      return node;
    });
    bar.replaceChildren(...controls);
  }
  Array.from(bar.children).forEach((node, index) => {
    const isActive = state.tabs[index].key === active;
    node.tabIndex = isActive ? 0 : -1;
    node.setAttribute("aria-selected", String(isActive));
  });
  if (bar.children.length) $("card-panel").setAttribute("aria-labelledby", "tab-" + active);
}

function seek(seconds, slice = null) {
  if (!Number.isFinite(seconds) || !video()?.exists) return;
  cursors.set(state.selectedId, seconds);
  voiceSlice = slice;
  clipPreview = "";
  setFlip(false);
  switchTab("video");
  const player = $("player");
  if (!slice && player?.readyState > 0) player.currentTime = Math.min(seconds, player.duration || seconds);
}

function videoSurface(item) {
  if (!item) return empty("Open a recording", "Review video, pull frames and audio, and package the results with Paperboy.", act("Open video", "import", {}, { primary: true }));
  if (!item.exists) return empty("Source file unavailable", "Open the file from its new location to add it to the library.", act("Open video", "import"));
  const selected = outputs("clip").find(clip => clip.id === clipPreview);
  const wrap = element("div", "stage");
  const player = element("video");
  Object.assign(player, { id: "player", controls: true, preload: "metadata", src: media(selected?.id || item.id) });
  player.setAttribute("aria-label", selected ? "Clip preview" : "Recording preview");
  if (item.hasPoster && !selected) player.poster = media(item.id + "-poster");
  if (!selected && voiceSlice) configureVoiceSlice(player, voiceSlice.turn);
  if (!selected && !voiceSlice) {
    player.addEventListener("loadedmetadata", () => {
      const saved = cursors.get(item.id) || 0;
      if (saved && Number.isFinite(player.duration)) player.currentTime = Math.min(saved, player.duration);
    });
  }
  if (!selected) player.addEventListener("timeupdate", () => cursors.set(item.id, player.currentTime));
  player.addEventListener("error", () => {
    if (player.isConnected) wrap.replaceChildren(empty("Preview unavailable",
      "Open the source in your default player, or export a compatible MP4 clip.",
      selected ? act("Open clip", "openArtifact", { artifactId: selected.id }) : act("Open source", "openSource")).firstChild);
  });
  wrap.append(player);
  return wrap;
}

function outputList(items, { frames = false } = {}) {
  const list = element("div", frames ? "frame-grid" : "item-list");
  for (const item of items) {
    const control = element("button", frames ? "frame-tile" : "output-row");
    control.type = "button";
    control.title = item.name;
    control.dataset.output = item.id;
    control.setAttribute("aria-pressed", String(item.id === selectedOutput));
    const details = item.start == null ? item.name : clock(item.start) + " · " + item.name;
    if (frames) {
      const image = element("img");
      Object.assign(image, { src: media(item.id), alt: "", loading: "lazy" });
      control.append(image, element("span", "", details));
    } else {
      control.append(element("span", "", details), element("small", "", outputLabels[item.kind] || item.kind));
    }
    control.addEventListener("click", () => {
      selectedOutput = item.id;
      list.querySelectorAll("button").forEach(row => row.setAttribute("aria-pressed", String(row === control)));
      if (["transcript", "insights", "voices"].includes(active)) {
        renderFront(video());
        Array.from($("tool-face").querySelectorAll("[data-output]")).find(row => row.dataset.output === item.id)?.focus();
      }
      renderPane();
    });
    list.append(control);
  }
  return list;
}

function audioPlayer(item) {
  const audio = element("audio", "track");
  Object.assign(audio, { controls: true, preload: "metadata", src: media(item.id) });
  audio.setAttribute("aria-label", item.name);
  return audio;
}

function subtitleSeconds(value) {
  const fields = value.replace(",", ".").split(":").map(Number);
  return fields.reduce((seconds, field) => seconds * 60 + field, 0);
}

function transcript(raw) {
  const chunks = raw.split(/\r?\n\s*\r?\n/);
  const cues = element("div", "item-list");
  for (const chunk of chunks) {
    const lines = chunk.split(/\r?\n/);
    const timestamp = lines.findIndex(line => /\d{2}:\d{2}[,.]\d+\s+-->/.test(line));
    if (timestamp < 0) continue;
    const time = lines[timestamp].trim().split(/\s+/)[0];
    const content = lines.slice(timestamp + 1).join("\n").trim();
    if (!content) continue;
    const cue = element("button", "cue");
    cue.type = "button";
    cue.append(element("time", "", time), element("span", "", content));
    cue.addEventListener("click", () => seek(subtitleSeconds(time)));
    cues.append(cue);
  }
  return cues.childElementCount ? cues : element("pre", "code", raw);
}


function currentBitcube() {
  if (bitcubeAsset) return bitcubeAsset;
  const artifact = outputs("bitcube")[0];
  if (!artifact) return null;
  const preview = previews.get(artifact.id);
  if (!preview) return null;
  if (typeof preview === "object") return preview;
  try { return JSON.parse(preview); } catch { return null; }
}

function bitcubeSurface(item) {
  const hasAudio = !!(item?.media?.audioCodec);
  const asset = currentBitcube();
  if (!item?.exists) return empty("Inverse-HDR cube", "Open a recording and extract its audio track to build a bitcube.");
  if (!hasAudio) return empty("No audio track", "This recording has no audio to map into a bitcube.");
  if (!asset) {
    return empty("Inverse-HDR cube", "Build the gen-audio library_r3 cube to inspect signal, tonality, confidence and quality layers, plus the cube uid, revision and covered range.",
      act("Build bitcube", "bitcube", {}, { primary: true }));
  }
  const fields = asset.fields || {};
  const body = asset.body || {};
  const cube = asset.cube || {};
  const wrap = element("section", "bitcube-panel");
  const header = element("div");
  header.append(element("h3", "", cube.title || "Inverse-HDR cube"));
  const meta = element("dl", "bitcube-meta");
  const rows = [
    ["Cube uid", asset.uid || ""],
    ["Revision", String(fields.cube_revision ?? cube.cube_revision ?? "")],
    ["Covered range", (body.cube_covers_s != null ? Number(body.cube_covers_s).toFixed(3) + " s" : "") + (fields.covers_ms != null ? " · " + fields.covers_ms + " ms" : "")],
    ["Bins", "f×t " + (Array.isArray(body.cube_shape_f_t) ? body.cube_shape_f_t.join(" × ") : "—") + (body.bin_seconds != null ? " · " + Number(body.bin_seconds).toFixed(4) + " s/bin" : "")],
    ["inv_hdr", body.inv_hdr != null ? String(body.inv_hdr) : ""],
    ["layer_score", body.layer_score != null ? String(body.layer_score) : ""],
    ["layer_method", fields.layer_method || (cube.provenance && cube.provenance.layer_method) || "library_r3"]
  ];
  for (const [key, value] of rows) {
    if (!value) continue;
    const dt = element("dt", "", key);
    const dd = element("dd", "", value);
    meta.append(dt, dd);
  }
  wrap.append(header, meta);
  const layerWrap = element("div", "bitcube-layers");
  const layerStats = cube.layers || {};
  const layerAssets = Array.isArray(asset.layers) ? asset.layers : [];
  for (let i = 0; i < 4; i++) {
    const name = ["signal", "tonality", "confidence", "quality"][i];
    const stats = layerStats[name] || (layerAssets[i] && layerAssets[i].body) || {};
    const card = element("article", "bitcube-layer");
    card.append(element("h3", "", name));
    card.append(element("p", "", [
      stats.mean != null ? "mean " + Number(stats.mean).toFixed(3) : "",
      stats.p90 != null ? "p90 " + Number(stats.p90).toFixed(3) : "",
      stats.active_frac != null ? "active " + Number(stats.active_frac).toFixed(3) : ""
    ].filter(Boolean).join(" · ")));
    layerWrap.append(card);
  }
  wrap.append(layerWrap);
  const canvas = element("canvas", "bitcube-canvas");
  canvas.width = 720;
  canvas.height = 280;
  canvas.setAttribute("aria-label", "Bitcube point preview");
  wrap.append(canvas);
  const legend = element("div", "bitcube-legend");
  const colors = { signal: "#3373f2", tonality: "#26bf66", confidence: "#f28c1a", quality: "#f266bf" };
  for (const name of Object.keys(colors)) {
    const itemEl = element("span");
    const swatch = element("i");
    swatch.style.background = colors[name];
    itemEl.append(swatch, document.createTextNode(name));
    legend.append(itemEl);
  }
  wrap.append(legend);
  requestAnimationFrame(() => drawBitcube(canvas, cube.points_preview || []));
  return wrap;
}

function drawBitcube(canvas, points) {
  const ctx = canvas.getContext("2d");
  if (!ctx) return;
  const width = canvas.width, height = canvas.height;
  ctx.fillStyle = "#0b0d11";
  ctx.fillRect(0, 0, width, height);
  const pad = 24;
  for (const point of points) {
    const x = pad + (Number(point.t) || 0) * (width - pad * 2);
    const y = height - pad - (Number(point.f) || 0) * (height - pad * 2);
    const depth = Number(point.z) || 0;
    const radius = 1.2 + depth * 1.8;
    ctx.fillStyle = "rgba(" + Math.round((point.r || 0) * 255) + "," + Math.round((point.g || 0) * 255) + "," + Math.round((point.b || 0) * 255) + "," + (point.a != null ? point.a : 0.8) + ")";
    ctx.beginPath();
    ctx.arc(x, y + (0.5 - depth) * 18, radius, 0, Math.PI * 2);
    ctx.fill();
  }
}

function atomicLibrarySurface() {
  if (!atomicLibrary || !Array.isArray(atomicLibrary.cards)) {
    return empty("Atomic Library", "Run full decimation in Insights to distill source-linked cards for visual frames, speech and document fields.",
      outputs("atomicLibrary").length ? act("Load library", "library", {}, { primary: true }) : button("Open Insights", () => switchTab("insights")));
  }
  const wrap = element("section", "atomic-library");
  const toolbar = element("div", "atomic-toolbar");
  const search = element("input");
  Object.assign(search, { type: "search", value: atomicQuery, placeholder: "Find a schema, tag, field or phrase" });
  search.setAttribute("aria-label", "Search atomic library");
  const modality = element("select");
  modality.setAttribute("aria-label", "Filter card modality");
  for (const value of ["all", "video", "audio", "document"]) {
    const option = element("option", "", value === "all" ? "All modalities" : value[0].toUpperCase() + value.slice(1));
    option.value = value;
    modality.append(option);
  }
  modality.value = atomicModality;
  const count = element("p", "hint");
  count.setAttribute("role", "status");
  const grid = element("div", "atomic-grid");
  const pager = element("div", "surface-toolbar");
  function updateCards() {
    const query = atomicQuery.trim().toLocaleLowerCase();
    const matches = atomicLibrary.cards.filter(card => (atomicModality === "all" || card.modality === atomicModality) && (!query || JSON.stringify(card).toLocaleLowerCase().includes(query)));
    const pages = Math.max(1, Math.ceil(matches.length / 24));
    atomicPage = Math.min(atomicPage, pages - 1);
    const visible = matches.slice(atomicPage * 24, (atomicPage + 1) * 24);
    count.textContent = matches.length + " of " + atomicLibrary.cards.length + " atomic cards · " + "source-linked, with explicit unknowns";
    grid.replaceChildren(...visible.map(atomicCard));
    if (!visible.length) grid.append(element("p", "hint", "No cards match these filters."));
    pager.replaceChildren(button("Previous", () => { atomicPage--; updateCards(); }, { enabled: atomicPage > 0 }),
      element("span", "", "Page " + (atomicPage + 1) + " of " + pages),
      button("Next", () => { atomicPage++; updateCards(); }, { enabled: atomicPage + 1 < pages }));
  }
  search.addEventListener("input", () => { atomicQuery = search.value; atomicPage = 0; updateCards(); });
  modality.addEventListener("change", () => { atomicModality = modality.value; atomicPage = 0; updateCards(); });
  toolbar.append(search, modality);
  wrap.append(toolbar, count, grid, pager);
  updateCards();
  return wrap;
}

function atomicCard(card) {
  const tile = element("article", "atomic-card");
  const head = element("div", "atomic-card-head");
  head.append(element("span", "atomic-modality", card.modality || "unknown"), element("span", "hint", card.kind || "schema"));
  const title = element("h3", "", card.title || card.id || "Untitled card");
  const body = element("div", "atomic-card-body");
  const toggle = button("", () => { atomicFlips.has(card.id) ? atomicFlips.delete(card.id) : atomicFlips.add(card.id); show(); });
  const actions = element("div", "atomic-actions");
  actions.append(toggle);
  const seconds = Number(card.startSeconds ?? card.evidence?.startSeconds);
  if (Number.isFinite(seconds) && card.modality !== "document") actions.append(button("Source " + clock(seconds), () => seek(seconds), { enabled: !!video()?.exists }));
  const formatValue = value => value == null ? "Unknown" : typeof value === "object" ? JSON.stringify(value) : String(value);
  function show() {
    const back = atomicFlips.has(card.id);
    toggle.textContent = back ? "Show card" : "Flip: JSON & evidence";
    toggle.setAttribute("aria-expanded", String(back));
    tile.dataset.flipped = String(back);
    if (back) {
      body.replaceChildren(element("p", "hint", "Atomic card instance · includes schema fields, provenance and source evidence"), element("pre", "code atomic-json", JSON.stringify(card, null, 2)));
      return;
    }
    const nodes = [];
    const evidencePath = card.evidence?.path || "";
    const normalizePath = path => String(path || "").replace(/\\/g, "/").toLowerCase();
    const images = outputs("frame", "screenshot");
    const exact = evidencePath && state.artifactPaths
      ? images.find(image => normalizePath(state.artifactPaths[image.id]) === normalizePath(evidencePath)) : null;
    const basenameMatches = images.filter(image => image.name === evidencePath.split(/[\\/]/).pop());
    const preview = exact || (!state.artifactPaths && basenameMatches.length === 1 ? basenameMatches[0] : null);
    if (preview) {
      const image = element("img", "atomic-preview");
      Object.assign(image, { src: media(preview.id), alt: "Source frame for " + (card.title || "this card"), loading: "lazy" });
      nodes.push(image);
    }
    const tags = element("div", "atomic-tags");
    for (const tag of (Array.isArray(card.tags) ? card.tags : []).slice(0, 10)) tags.append(element("span", "", tag));
    if (tags.children.length) nodes.push(tags);
    const attrs = Object.entries(card.attributes || {});
    const measured = attrs.filter(([, value]) => value != null && value !== "unknown" && !(typeof value === "object" && ["unknown", "unavailable-or-empty"].includes(value.status)));
    if (measured.length) {
      const list = element("dl", "atomic-attributes");
      for (const [key, value] of measured.slice(0, 5)) {
        const row = element("div");
        const label = key.replace(/([a-z])([A-Z])/g, "$1 $2");
        const wrapped = value && typeof value === "object" && "status" in value;
        row.append(element("dt", "", label + (wrapped ? " · " + value.status : "")),
          element("dd", "", formatValue(wrapped ? value.value : value)));
        list.append(row);
      }
      nodes.push(list);
    }
    const unknownCount = attrs.length - measured.length;
    if (unknownCount) nodes.push(element("p", "hint", unknownCount + (unknownCount === 1 ? " attribute unavailable" : " attributes unavailable") + " · inspect the reverse for details"));
    nodes.push(element("p", "atomic-evidence", card.provenance?.method || card.provenance?.type || "See provenance on reverse"));
    body.replaceChildren(...nodes);
  }
  tile.append(head, title, body, actions);
  show();
  return tile;
}

function voiceTime(seconds) {
  const milliseconds = Math.round(seconds * 1000);
  return clock(Math.floor(milliseconds / 1000)) + "." + String(milliseconds % 1000).padStart(3, "0");
}

function voiceRange(turn) {
  return voiceTime(turn.startSeconds) + " – " + voiceTime(turn.endSeconds);
}

function playAgentVoiceTurn(voice, index, returnTab = active) {
  const turn = voice.turns[index];
  if (turn) seek(turn.startSeconds, { voice, turn, index, returnTab });
}

function configureVoiceSlice(player, turn) {
  const start = () => Math.min(turn.startSeconds, player.duration || turn.startSeconds);
  const end = () => Math.min(turn.endSeconds, player.duration || turn.endSeconds);
  let stopTimer;
  const cancelStop = () => clearTimeout(stopTimer);
  const scheduleStop = () => {
    cancelStop();
    if (player.paused || player.seeking || !player.isConnected) return;
    const remaining = end() - player.currentTime;
    if (remaining <= 0) {
      player.pause();
      player.currentTime = end();
    } else {
      stopTimer = setTimeout(scheduleStop, Math.max(16, remaining * 1000 / player.playbackRate));
    }
  };
  player.addEventListener("play", () => {
    if (player.currentTime < start() || player.currentTime >= end()) player.currentTime = start();
    scheduleStop();
  });
  player.addEventListener("seeking", cancelStop);
  player.addEventListener("seeked", () => {
    if (!player.paused && player.currentTime < start()) player.currentTime = start();
    else scheduleStop();
  });
  player.addEventListener("pause", cancelStop);
  player.addEventListener("timeupdate", scheduleStop);
  player.addEventListener("ratechange", scheduleStop);
  player.addEventListener("loadedmetadata", () => {
    if (!player.isConnected || end() <= start()) return;
    const play = () => {
      if (player.isConnected) player.play().catch(() => {});
    };
    if (Math.abs(player.currentTime - start()) < 0.001) play();
    else {
      player.addEventListener("seeked", play, { once: true });
      player.currentTime = start();
    }
  }, { once: true });
}

function voiceSliceControls(slice) {
  const controls = element("section", "voice-slice-controls");
  controls.setAttribute("aria-label", "Agent voice appearance");
  const description = element("p", "hint",
    "Appearance " + (slice.index + 1) + " of " + slice.voice.turns.length + " · " + voiceRange(slice.turn));
  const actions = element("div", "voice-actions");
  actions.append(button("Previous appearance", () => playAgentVoiceTurn(slice.voice, slice.index - 1, slice.returnTab),
      { enabled: slice.index > 0 }),
    button("Next appearance", () => playAgentVoiceTurn(slice.voice, slice.index + 1, slice.returnTab),
      { enabled: slice.index + 1 < slice.voice.turns.length }),
    button("Return to Agent Voices", () => {
      switchTab(slice.returnTab);
      const card = $("agent-voice-" + slice.voice.id);
      card?.scrollIntoView({ block: "nearest" });
      card?.querySelector(".voice-flip")?.focus({ preventScroll: true });
    }),
    button("Full recording", () => seek($("player")?.currentTime ?? slice.turn.startSeconds)));
  controls.append(element("h3", "", slice.voice.alias), description, actions);
  return controls;
}

function agentVoiceTile(voice, item, displayDuration) {
  const tile = element("article", "voice-tile");
  tile.id = "agent-voice-" + voice.id;
  const title = element("h4", "", voice.alias);
  const preview = outputs("agentVoice").find(output => output.id === voice.artifactId);
  const summary = element("p", "hint", voice.turns.length + (voice.turns.length === 1 ? " appearance" : " appearances") +
    " · " + clock(voice.durationSeconds) + " total");
  if (voice.durationSeconds < 3) summary.append(element("span", "voice-brief", "Brief detection"));
  const timeline = element("div", "voice-timeline");
  timeline.setAttribute("aria-hidden", "true");
  if (displayDuration > 0) {
    for (const turn of voice.turns) {
      const segment = element("i");
      segment.style.left = 100 * turn.startSeconds / displayDuration + "%";
      segment.style.width = 100 * (turn.endSeconds - turn.startSeconds) / displayDuration + "%";
      timeline.append(segment);
    }
  }
  const body = element("div", "voice-tile-body");
  body.id = "agent-voice-content-" + voice.id;
  const flip = button("", () => {
    if (agentVoiceFlips.has(voice.id)) agentVoiceFlips.delete(voice.id);
    else agentVoiceFlips.add(voice.id);
    show();
  }, { className: "voice-flip" });
  flip.setAttribute("aria-controls", body.id);
  const showTimeline = () => {
    const pageSize = 8;
    const pageCount = Math.ceil(voice.turns.length / pageSize);
    const page = Math.min(agentVoicePages.get(voice.id) || 0, Math.max(0, pageCount - 1));
    const list = element("ol", "voice-appearances");
    list.start = page * pageSize + 1;
    for (const [offset, turn] of voice.turns.slice(page * pageSize, (page + 1) * pageSize).entries()) {
      const index = page * pageSize + offset;
      const row = element("li");
      row.append(button(voiceRange(turn), () => playAgentVoiceTurn(voice, index), { enabled: !!item?.exists }));
      list.append(row);
    }
    const children = [element("p", "hint", "Play a source appearance"), list];
    if (pageCount > 1) {
      const go = value => { agentVoicePages.set(voice.id, value); show(); };
      const pager = element("div", "voice-actions voice-pager");
      pager.append(button("Previous page", () => go(page - 1), { enabled: page > 0 }),
        element("span", "hint", (page + 1) + " of " + pageCount),
        button("Next page", () => go(page + 1), { enabled: page + 1 < pageCount }));
      children.push(pager);
    }
    if (preview) children.push(act("Save speaker WAV", "exportArtifact", { artifactId: preview.id }));
    body.replaceChildren(...children);
  };
  function show() {
    const back = agentVoiceFlips.has(voice.id);
    tile.dataset.flipped = String(back);
    flip.textContent = back ? "Show audio preview" : "Flip to appearances";
    flip.setAttribute("aria-expanded", String(back));
    flip.setAttribute("aria-label", flip.textContent + " for " + voice.alias);
    if (back) showTimeline();
    else if (preview) {
      const audio = audioPlayer(preview);
      audio.preload = "none";
      audio.setAttribute("aria-label", "Combined source excerpts for " + voice.alias);
      audio.addEventListener("play", () => {
        $("tool-face").querySelectorAll("audio").forEach(other => { if (other !== audio) other.pause(); });
      });
      body.replaceChildren(element("p", "hint", "Combined source excerpts"), audio);
    } else body.replaceChildren(element("p", "hint", "Source excerpts are unavailable."));
  }
  tile.append(title, summary, timeline, body, flip);
  show();
  return tile;
}

function agentVoicesSurface(item) {
  const section = element("section", "agent-voices");
  section.id = "agent-voices";
  section.setAttribute("aria-labelledby", "agent-voices-title");
  const heading = element("h3", "", "Agent Voices");
  heading.id = "agent-voices-title";
  const detected = [...(agentVoiceManifest?.voices || [])].sort((a, b) => b.durationSeconds - a.durationSeconds);
  const canDetect = !!item?.exists && (!item.media || !!item.media.audioCodec);
  const header = element("div", "voice-actions agent-voices-header");
  header.append(heading);
  if (canDetect) header.append(act(detected.length ? "Detect again" : "Detect agent voices", "detectAgentVoices", {},
    { primary: !detected.length }));
  section.append(header);
  if (agentVoiceError) {
    const error = element("p", "voice-error", agentVoiceError);
    error.setAttribute("role", "alert");
    section.append(error);
  }
  if (!detected.length) {
    let message = "Open a recording with audio to detect its speakers.";
    if (item?.exists && !canDetect) message = "This recording has no audio track.";
    else if (agentVoiceManifest) message = "No speaker voices were detected.";
    else if (outputs("agentVoices").length) message = "Load the saved speaker timeline and excerpts.";
    else if (canDetect) message = "Group speech by speaker and assign each voice a fictional alias.";
    section.append(element("p", "hint", message));
    if (!agentVoiceManifest && outputs("agentVoices").length) {
      section.append(act("Load agent voices", "agentVoices"));
    }
    return section;
  }
  section.append(element("p", "hint", detected.length + (detected.length === 1 ? " detected speaker group" : " detected speaker groups") +
    " with fictional aliases. Brief groups may be fragments of another speaker or contain overlapping speech."));
  const grid = element("div", "voice-grid");
  const duration = agentVoiceManifest.durationSeconds || item?.media?.duration || 0;
  grid.append(...detected.map(voice => agentVoiceTile(voice, item, duration)));
  section.append(grid);
  return section;
}

function renderFront(item) {
  const face = $("tool-face");
  const key = [active, item?.id, item?.exists, clipPreview, ...state.artifacts.map(output => output.id),
    capabilities.length, voices.length, previews.size, libraryRevision, bitcubeRevision, JSON.stringify(item?.media),
    active === "settings" ? JSON.stringify(state.settings) : "", ["transcript", "insights", "voices"].includes(active) ? selectedOutput : "",
    ["agents", "voices"].includes(active) ? agentVoiceRevision : "", voiceSlice?.voice.id, voiceSlice?.index].join(":");
  // Keep the native media element stable when a command returns the same view.
  if (key === stageKey) return;
  stageKey = key;
  pauseMedia();
  const list = (...kinds) => outputList(outputs(...kinds));
  let contents;
  switch (active) {
    case "video": {
      const clips = outputs("clip");
      contents = voiceSlice ? [voiceSliceControls(voiceSlice), videoSurface(item)] : [videoSurface(item)];
      if (clips.length && !voiceSlice) {
        const line = element("div", "surface-toolbar");
        line.append(element("span", "", clipPreview ? "Clip preview" : "Source recording"));
        if (clipPreview) line.append(button("Back to source", () => { clipPreview = ""; renderFront(item); }));
        contents.push(line);
      }
      break;
    }
    case "screenshot": {
      const images = outputs("frame", "screenshot");
      contents = images.length ? [outputList(images, { frames: true })] :
        [empty("Screenshots and frames", "Capture the desktop or pick a timestamp or scene from the recording.")];
      break;
    }
    case "audio": {
      const wave = outputs("audio")[0];
      const spectrum = outputs("spectrogram")[0];
      contents = [];
      if (wave) contents.push(audioPlayer(wave));
      if (spectrum) {
        const image = element("img", "art-preview");
        Object.assign(image, { src: media(spectrum.id), alt: "Spectrogram showing audio frequencies over time" });
        contents.push(image);
      }
      if (!contents.length) contents.push(empty("Audio track", "Extract a WAV or generate a spectrogram from the recording."));
      else contents.push(list("audio", "spectrogram"));
      break;
    }
    case "transcript": {
      const captions = outputs("transcript");
      const chosen = captions.find(value => value.id === selectedOutput) || captions[0];
      contents = chosen ? [list("transcript")] : [empty("Transcript", "Attach captions, extract an embedded subtitle track, or transcribe locally.")];
      if (chosen) {
        if (previews.has(chosen.id)) contents.push(transcript(previews.get(chosen.id)));
        else contents.push(act("View transcript", "readArtifact", { artifactId: chosen.id }));
      }
      break;
    }
    case "agents": {
      const stages = [
        ["Media inspector", !!item?.media, "Track metadata and source duration"],
        ["Transcript worker", !!outputs("transcript").length, "Local Whisper or attached captions; no speaker identity inference"],
        ["Visual sampler", !!outputs("frame", "screenshot").length, "Source-time scene frames and measured colors"],
        ["Semantic indexer", !!outputs("atomicLibrary").length, "Evidence-linked atomic schemas, lexical tags and bitdot layers"],
        ["Harvest packager", !!outputs().length, "Source and derived artifacts available to bundle"]
      ];
      const pipeline = element("div", "pipeline-stages");
      for (const [name, ready, description] of stages) {
        const row = element("article", "capability");
        row.append(element("strong", "", name), element("small", ready ? "stage-ready" : "", ready ? "Output available" : "Not produced yet"), element("p", "hint", description));
        pipeline.append(row);
      }
      contents = [element("h3", "", "Local processing pipeline"), pipeline, agentVoicesSurface(item)];
      if (capabilities.length) {
        const catalog = element("div", "item-list");
        for (const capability of capabilities) {
          const row = element("article", "capability");
          row.append(element("strong", "", capability.displayName), element("small", "", capability.kind + " · " + capability.status), element("p", "hint", capability.description));
          catalog.append(row);
        }
        contents.push(element("h3", "", "Native capability catalog"), catalog);
      }
      break;
    }
    case "bitcube": {
      contents = [bitcubeSurface(item)];
      break;
    }
    case "library": {
      contents = [atomicLibrarySurface()];
      break;
    }
    case "voices": {
      const narration = outputs("voice")[0];
      contents = [agentVoicesSurface(item)];
      if (narration) {
        const selected = outputs("voice").find(value => value.id === selectedOutput) || narration;
        contents.push(element("h3", "", "Script narration"), audioPlayer(selected), list("voice"));
      }
      break;
    }
    case "insights": {
      const info = item?.media;
      contents = !info ? [empty("Recording insights", "Inspect track metadata, analyze a frame, or build a runbook from MDKG.")] :
        [facts([["Duration", clock(info.duration)], ["Video", info.videoCodec.toUpperCase()], ["Frame size", info.width + " × " + info.height],
          ["Audio", info.audioCodec || "No audio track"], ["Subtitles", info.subtitleStreams], ["Size", bytes(info.bytes)]])];
      if (outputs("ocr", "runbook", "semanticReport", "bitdot", "atomicLibrary").length) {
        contents.push(list("ocr", "runbook", "semanticReport", "bitdot", "atomicLibrary"));
        if (previews.has(selectedOutput)) contents.push(element("pre", "code", previews.get(selectedOutput)));
      }
      break;
    }
    case "harvest":
      contents = outputs().length ? [list()] :
        [empty("Harvest outputs", "Frames, clips, transcripts, and other outputs are collected here for export.")];
      break;
    case "settings":
      contents = [facts([["Card size", state.settings.cardSize], ["Storage", "Local"],
        ["FFmpeg", state.settings.ffmpegPath ? "Custom executable" : "Configured or on PATH"],
        ["Whisper", state.settings.whisperModel && state.settings.whisperPath ? "Configured" : "Optional"]]),
      element("h3", "", "Output directory"), element("p", "hint", state.settings.outputDirectory || "Loading…")];
      break;
  }
  face.replaceChildren(...contents);
}

function renderBack() {
  const face = $("paperboy-face");
  const card = state.paperboy;
  if (!card) {
    face.replaceChildren();
    return;
  }
  const layer = card.flipcard.tabs.find(tab => tab.key === active) || { role: "atomic-library", inputs: [], outputs: [] };
  const code = element("pre", "code", JSON.stringify(card, null, 2));
  const details = element("details");
  details.append(element("summary", "", "Full card JSON"), code);
  face.replaceChildren(element("h3", "", card.title),
    facts([["State", card.liveTileState === "ready" ? "Ready" : "Source unavailable"],
      ["Outputs", card.artifacts.filter(item => item.exists).length], ["Size", card.flipcard.size]]),
    element("h3", "", backLabels[active]),
    facts([["Role", layer.role], ["Inputs", layer.inputs.join(", ")], ["Outputs", layer.outputs.join(", ")]]),
    details);
}

function renderPane() {
  const pane = $("tools");
  const item = video();
  const hasSource = !!item?.exists;
  const hasAudio = hasSource && (item.media == null || !!item.media.audioCodec);
  const playback = () => (cursors.get(state.selectedId) || 0).toFixed(2);
  const usePlayhead = (input, title = "Use playhead") => button(title, () => {
    input.value = playback();
    input.dispatchEvent(new Event("input"));
  }, { enabled: hasSource });
  const section = title => element("h4", "section-title", title);
  const label = title => element("h3", "", title);
  const hint = text => element("p", "hint", text);
  const current = outputs().find(output => output.id === selectedOutput);
  const actions = [];
  if (flipped) {
    pane.replaceChildren(label("Paperboy card"), hint("Export this recording’s AgentCard manifest or bundle its source and outputs."),
      stack(act("Save card JSON", "exportCard", {}, { primary: true }),
        act("Copy card JSON", "copyCard"), act("Copy JSON Schema", "copySchema"),
        act("Build Paperboy bundle", "bundle", {}, { enabled: hasSource })));
    return;
  }

  switch (active) {
    case "video": {
      const limit = item?.media?.duration || 604800;
      const start = numberField("Start (s)", "start", "0", 0, limit);
      const end = numberField("End (s)", "end", String(Math.min(limit, 10)), 0, limit);
      const row = element("div", "input-row");
      row.append(start.wrapper, end.wrapper);
      actions.push(label(item ? "Clip tools" : "Video tools"));
      if (hasSource) {
        const mark = element("div", "input-row");
        mark.append(usePlayhead(start.input, "Set start"), usePlayhead(end.input, "Set end"));
        for (const control of mark.children) control.title = "Use the current playhead";
        actions.push(row, mark, stack(act("Export clip", "clip", () => ({ start: start.value(), end: end.value() }), { primary: true }),
          act("Open source file", "openSource")));
        for (const clip of outputs("clip")) {
          actions.push(button("Preview " + clock(clip.start) + " clip", () => { clipPreview = clip.id; renderFront(item); }));
        }
      }
      const duration = numberField("Recording duration (s)", "duration", "10", 1, 600, "1");
      duration.input.id = "record-duration";
      actions.push(section("Record desktop"), duration.wrapper,
        stack(act("Start recording", "record", () => ({ duration: duration.value() }), { primary: !hasSource })));
      break;
    }
    case "screenshot": {
      const timestamp = numberField("Timestamp (s)", "timestamp", playback(), 0, item?.media?.duration || 604800);
      const threshold = numberField("Sensitivity threshold", "threshold", "0.04", 0.01, 0.95);
      const limit = numberField("Maximum frames", "limit", "100", 1, 300, "1");
      actions.push(label("Screenshot tools"), stack(act("Capture desktop screenshot", "capture", {}, { primary: !hasSource }),
        act("Capture system specs", "systemSpecs")));
      if (hasSource) {
        actions.push(section("Video frame"), timestamp.wrapper, stack(usePlayhead(timestamp.input),
          act("Save frame", "frame", () => ({ timestamp: timestamp.value() }), { primary: true })),
        section("Scene changes"), threshold.wrapper, limit.wrapper, hint("Lower thresholds pick up smaller changes."),
        stack(act("Export scene frames", "scenes", () => ({ threshold: threshold.value(), limit: limit.value() }))));
      }
      if (current?.start != null) actions.push(button("Jump to " + clock(current.start), () => seek(current.start), { enabled: hasSource }));
      break;
    }
    case "audio":
      actions.push(label("Audio tools"), hint(!hasSource ? "Open a recording to use its audio track." : !hasAudio ? "This recording has no audio track." : "Use the recording’s first audio track."),
        stack(act("Extract WAV", "wave", {}, { primary: true, enabled: hasAudio }), act("Generate spectrogram", "spectrogram", {}, { enabled: hasAudio })));
      break;
    case "transcript": {
      const configured = !!(state.settings.whisperPath && state.settings.whisperModel);
      actions.push(label("Transcript tools"),
        stack(act("Attach transcript", "attachTranscript", {}, { primary: true, enabled: !!item }),
          act("Extract embedded subtitles", "subtitles", {}, { enabled: hasSource && (item.media == null || item.media.subtitleStreams > 0) }),
          act("Transcribe with Whisper", "transcribe", {}, { enabled: hasAudio && configured }),
          act("Transcribe with gen-audio VibeVoice", "genAudioTranscribe", {}, { enabled: hasAudio && !!state.settings.vibeVoiceModel })));
      if (!configured) actions.push(hint("Add a whisper.cpp executable and model in Settings."));
      if (!state.settings.vibeVoiceModel) actions.push(hint("Set a VibeVoice ASR checkpoint folder in Settings to transcribe with gen-audio."));
      break;
    }
    case "agents":
      actions.push(label("Local workers"), hint("Run the full recording pipeline from here or Insights. Use Library to inspect the resulting evidence and schemas."),
        stack(act("Run full decimation", "decimate", {}, { primary: true, enabled: hasSource }), act("Load capability catalog", "catalog")),
        section("Agent Voices"), button("Browse detected voices", () => $("agent-voices")?.scrollIntoView({ block: "start" })),
        section("Windows AI OCR"), stack(act("Check OCR readiness", "ocrStatus"), act("Load OCR model", "loadOcr")));
      break;
    case "voices": {
      const voice = field("Installed Windows voice", "voice", voices[0]?.name, { options: voices.map(item => item.name) });
      const script = field("Narration script", "script", "", { type: "textarea" });
      script.input.maxLength = 10000;
      const genAudioReady = !!state.settings.genAudioVoice;
      actions.push(label("Script narration"), stack(act("List installed voices", "voices")));
      if (voices.length) actions.push(voice.wrapper);
      actions.push(script.wrapper);
      const renders = [];
      if (voices.length) renders.push(act("Render WAV narration", "speak",
        () => ({ voice: voice.value(), script: script.value() }), { primary: true, enabled: !!item }));
      renders.push(act("Render with gen-audio", "genAudioSpeak", () => ({ script: script.value() }),
        { primary: !voices.length, enabled: !!item && genAudioReady }));
      actions.push(stack(...renders));
      if (!genAudioReady) actions.push(hint("Set a gen-audio voice in Settings to narrate with gen-audio engines such as Kokoro."));
      if (!item) actions.push(hint("Open a recording to save narration to its card."));
      break;
    }
    case "insights": {
      const frames = outputs("frame", "screenshot");
      const frame = field("Frame for OCR", "ocrFrame", frames[0]?.name, { options: frames.map((image, index) => (index + 1) + ". " + image.name) });
      actions.push(label("Semantic layers"), hint("Process the full original recording. Existing attached captions are reused; otherwise configure local Whisper in Settings. Inferred fields are labeled and unsupported attributes stay unknown."),
        stack(act("Run full decimation", "decimate", {}, { primary: true, enabled: hasSource }),
          act("Import verified analysis", "importAnalysis", {}, { enabled: hasSource }),
          button("Browse atomic Library", () => switchTab("library"), { enabled: !!item })),
        section("Analysis tools"), stack(act("Refresh media metadata", "probe", {}, { enabled: hasSource }),
        act("Export MDKG runbook", "runbook", {}, { enabled: state.hasTimedMetadata })));
      if (!state.hasTimedMetadata) actions.push(hint("Runbook export needs a matching .mdkg sidecar."));
      if (frames.length) actions.push(section("Frame OCR"), frame.wrapper, stack(act("Analyze selected frame", "ocr",
        () => ({ artifactId: frames[frame.input.selectedIndex].id }))));
      if (current && ["ocr", "runbook", "semanticReport", "bitdot", "atomicLibrary"].includes(current.kind)) actions.push(act("View analysis", "readArtifact", { artifactId: current.id }));
      break;
    }
    case "bitcube": {
      const hasAudio = !!(item?.media?.audioCodec);
      const cube = outputs("bitcube")[0];
      actions.push(label("Inverse-HDR bitcube"), hint("Port of the gen-audio library_r3 cube: signal, tonality, confidence and quality layers, plus cube uid, revision and covered range."),
        stack(act("Build / refresh bitcube", "bitcube", {}, { primary: true, enabled: hasAudio && !!item }),
          act("Extract WAV", "wave", {}, { enabled: hasAudio && !!item })));
      if (cube) actions.push(section("Cube files"), stack(act("Open cube JSON", "openArtifact", { artifactId: cube.id }), act("Save cube JSON", "exportArtifact", { artifactId: cube.id })));
      const png = outputs("bitcubePng")[0];
      if (png) actions.push(act("Open cube preview", "openArtifact", { artifactId: png.id }));
      break;
    }
    case "library": {
      const libraryOutput = outputs("atomicLibrary")[0];
      actions.push(label("Atomic schema library"), hint("Video, audio and document cards retain source evidence. Flip a card to inspect its JSON, provenance and unknown attributes."),
        stack(act("Load / refresh library", "library", {}, { primary: true, enabled: !!libraryOutput }),
          act("Run full decimation", "decimate", {}, { enabled: hasSource }),
          act("Attach source document", "attachDocument", {}, { enabled: !!item })));
      if (libraryOutput) actions.push(section("Export"), stack(act("Save library JSON", "exportArtifact", { artifactId: libraryOutput.id }), act("Open library file", "openArtifact", { artifactId: libraryOutput.id })));
      const schemaOutput = outputs("schema")[0];
      if (schemaOutput) actions.push(act("Save atomic JSON Schema", "exportArtifact", { artifactId: schemaOutput.id }));
      actions.push(section("Attribute meaning"), hint("Measured: extracted directly. Inferred: a labeled estimate. Unknown: not measured. A field in the schema does not imply that a voice, emotion, avatar, font or identity was detected."));
      break;
    }
    case "harvest":
      actions.push(label("Paperboy delivery"),
        stack(act("Build Paperboy bundle", "bundle", {}, { primary: true, enabled: hasSource }),
          act("Save card JSON", "exportCard", {}, { enabled: !!item }),
          act("Open output folder", "openOutput")));
      break;
    case "settings": {
      const labels = { outputDirectory: "Output directory", ffmpegPath: "FFmpeg executable",
        whisperPath: "whisper.cpp executable", whisperModel: "Whisper model file",
        genAudioPython: "gen-audio Python executable", genAudioRoot: "gen-audio checkout folder (optional)",
        genAudioEngine: "gen-audio engine (default kokoro_onnx)", genAudioVoice: "gen-audio voice",
        vibeVoiceModel: "VibeVoice ASR checkpoint folder" };
      const fields = Object.entries(labels).map(([key, title]) => [key, field(title, key, state.settings[key] || "")]);
      actions.push(label("Local tools"), ...fields.map(([, value]) => value.wrapper),
        hint("Leave FFmpeg blank to use Yumlog’s existing configuration or PATH. Leave gen-audio fields blank to use GEN_AUDIO_PYTHON, GEN_AUDIO_ROOT, GEN_AUDIO_VIBEVOICE_ASR_MODEL and the YUMLOG_GEN_AUDIO_* variables."),
        stack(act("Save settings", "saveSettings", () => Object.fromEntries(fields.map(([key, value]) => [key, value.value()])), { primary: true })));
      break;
    }
  }
  if (current) {
    const payload = { artifactId: current.id };
    actions.push(section("Selected output"), hint(current.name), stack(act("Open file", "openArtifact", payload), act("Save a copy", "exportArtifact", payload)));
  }
  pane.replaceChildren(...actions);
}

function renderCard() {
  const current = video();
  const tab = state.tabs.find(tab => tab.key === active);
  $("card").dataset.size = state.settings.cardSize;
  $("card-size").value = state.settings.cardSize;
  $("card-title").textContent = tab?.label || "Video";
  $("flip").disabled = !state.paperboy;
  renderTabs();
  renderFront(current);
  renderBack();
  setFlip(flipped);
}

function render() {
  const item = video();
  const selectedProject = (state.projects || []).find(project => project.id === state.selectedProjectId);
  $("workspace-title").textContent = item?.title || selectedProject?.name || "Capture workspace";
  $("workspace-title").title = $("workspace-title").textContent;
  $("workspace-meta").textContent = item?.media
    ? [item.name, clock(item.media.duration), item.media.width + " × " + item.media.height].join("  ·  ")
    : selectedProject ? selectedProject.path : (item?.name || "Local recording library");
  $("workspace-meta").title = $("workspace-meta").textContent;
  renderLibrary();
  renderCard();
}

$("library-toggle").addEventListener("click", () => setLibrary(document.body.dataset.libraryOpen !== "true"));
$("search").addEventListener("input", renderLibrary);
$("import").addEventListener("click", () => run("import"));
$("record").addEventListener("click", () => {
  setFlip(false);
  switchTab("video");
  $("record-duration")?.focus();
});
$("card-size").addEventListener("change", event => run("setSize", { cardSize: event.target.value }));
$("output-folder").addEventListener("click", () => run("openOutput"));
$("workspace-card")?.addEventListener("click", () => { setFlip(false); switchTab("settings"); });
$("refresh-context")?.addEventListener("click", () => run("refreshContext"));
$("new-project")?.addEventListener("click", () => {
  const dialog = $("project-dialog");
  const input = $("project-name");
  if (input) input.value = "";
  if (dialog?.showModal) dialog.showModal();
  input?.focus();
});
$("project-form")?.addEventListener("submit", event => {
  const submitter = event.submitter;
  if (submitter && submitter.value === "create") {
    const name = $("project-name")?.value.trim();
    if (name) void run("newProject", { name });
  }
});
$("flip").addEventListener("click", () => setFlip(!flipped));
render();
run("snapshot");
