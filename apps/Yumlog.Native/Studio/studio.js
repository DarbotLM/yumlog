"use strict";

const $ = id => document.getElementById(id);
const bridge = window.chrome?.webview;
const media = id => "https://yumlog-media.local/" + encodeURIComponent(id);
const tabIcons = { video: "\uE714", screenshot: "\uE722", audio: "\uE767", transcript: "\uE8A5",
  agents: "\uE716", voices: "\uE720", insights: "\uE9D9", harvest: "\uE7B8", settings: "\uE713" };
const outputLabels = { clip: "Clip", screenshot: "Screenshot", frame: "Frame", audio: "Audio", spectrogram: "Spectrogram",
  transcript: "Transcript", voice: "Narration", ocr: "Frame analysis", runbook: "MDKG runbook", timedMetadata: "Timed metadata" };
const backLabels = { video: "Video inputs and clips", screenshot: "Screenshot inputs and frames",
  audio: "Audio outputs", transcript: "Transcript sources", agents: "Agent capabilities", voices: "Voice synthesis",
  insights: "Analysis outputs", harvest: "Harvest and delivery", settings: "Workspace settings" };
const busyLabels = { import: "Opening recordings…", record: "Recording the desktop…", clip: "Exporting clip…",
  scenes: "Finding scene changes…", transcribe: "Transcribing audio locally…", bundle: "Building Paperboy bundle…",
  loadOcr: "Loading the Windows AI OCR model…", spectrogram: "Generating spectrogram…" };

let state = { videos: [], selectedId: "", tabs: [], artifacts: [], settings: { cardSize: "studio" }, paperboy: null };
let active = "video";
let flipped = false;
let request = 0;
let pending = false;
let selectedOutput = "";
let clipPreview = "";
let stageKey = "";
let voices = [];
let capabilities = [];
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
      }
      state = reply.state;
    }
    if (command === "catalog") capabilities = reply.data || [];
    if (command === "voices") voices = reply.data || [];
    if (command === "readArtifact") previews.set(payload.artifactId, reply.data || "");
    if (command === "saveSettings") {
      for (const name of ["outputDirectory", "ffmpegPath", "whisperPath", "whisperModel"]) drafts.delete("settings:" + name);
    }
    render();
    status(reply.message || "Ready");
  } catch (error) {
    status(error.message, true);
    if (command === "setSize") $("card-size").value = state.settings.cardSize;
  } finally {
    setBusy(false);
  }
}

bridge?.addEventListener("message", event => {
  const reply = event.data;
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

function renderLibrary() {
  const query = $("search").value.trim().toLocaleLowerCase();
  const matches = state.videos.filter(item => item.name.toLocaleLowerCase().includes(query));
  $("library-count").textContent = state.videos.length;
  const list = $("recordings");
  list.replaceChildren();
  if (!matches.length) {
    list.append(element("p", "library-empty", state.videos.length ? "No matching recordings." : "Open a video or record your desktop to start."));
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
  if (changed) selectedOutput = "";
  active = key;
  renderCard();
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

function seek(seconds) {
  if (!Number.isFinite(seconds) || !video()?.exists) return;
  cursors.set(state.selectedId, seconds);
  clipPreview = "";
  setFlip(false);
  switchTab("video");
  const player = $("player");
  if (player?.readyState > 0) player.currentTime = Math.min(seconds, player.duration || seconds);
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
  if (!selected) {
    player.addEventListener("loadedmetadata", () => {
      const saved = cursors.get(item.id) || 0;
      if (saved && Number.isFinite(player.duration)) player.currentTime = Math.min(saved, player.duration);
    });
    player.addEventListener("timeupdate", () => cursors.set(item.id, player.currentTime));
  }
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
      if (active === "transcript" || active === "insights") {
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

function renderFront(item) {
  const face = $("tool-face");
  const key = [active, item?.id, item?.exists, clipPreview, ...state.artifacts.map(output => output.id),
    capabilities.length, voices.length, previews.size, JSON.stringify(item?.media),
    active === "settings" ? JSON.stringify(state.settings) : "", ["transcript", "insights"].includes(active) ? selectedOutput : ""].join(":");
  // Keep the native media element stable when a command returns the same view.
  if (key === stageKey) return;
  stageKey = key;
  pauseMedia();
  const list = (...kinds) => outputList(outputs(...kinds));
  let contents;
  switch (active) {
    case "video": {
      const clips = outputs("clip");
      contents = [videoSurface(item)];
      if (clips.length) {
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
      if (!capabilities.length) contents = [empty("Native capabilities", "Load Yumlog’s local agent, workflow, model, and extension catalog.")];
      else {
        const catalog = element("div", "item-list");
        for (const item of capabilities) {
          const row = element("article", "capability");
          row.append(element("strong", "", item.displayName), element("small", "", item.kind + " · " + item.status), element("p", "hint", item.description));
          catalog.append(row);
        }
        contents = [catalog];
      }
      break;
    }
    case "voices": {
      const narration = outputs("voice")[0];
      contents = narration ? [audioPlayer(narration), list("voice")] :
        [empty("Local voice rendering", "Use a Windows voice to turn a script into a WAV narration.")];
      break;
    }
    case "insights": {
      const info = item?.media;
      contents = !info ? [empty("Recording insights", "Inspect track metadata, analyze a frame, or build a runbook from MDKG.")] :
        [facts([["Duration", clock(info.duration)], ["Video", info.videoCodec.toUpperCase()], ["Frame size", info.width + " × " + info.height],
          ["Audio", info.audioCodec || "No audio track"], ["Subtitles", info.subtitleStreams], ["Size", bytes(info.bytes)]])];
      if (outputs("ocr", "runbook").length) {
        contents.push(list("ocr", "runbook"));
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
  const layer = card.flipcard.tabs.find(tab => tab.key === active);
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
      actions.push(label("Screenshot tools"), stack(act("Capture desktop screenshot", "capture", {}, { primary: !hasSource })));
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
          act("Transcribe with Whisper", "transcribe", {}, { enabled: hasAudio && configured })));
      if (!configured) actions.push(hint("Add a whisper.cpp executable and model in Settings."));
      break;
    }
    case "agents":
      actions.push(label("Yumlog agents"), stack(act("Load capability catalog", "catalog", {}, { primary: true })),
        section("Windows AI OCR"), stack(act("Check OCR readiness", "ocrStatus"), act("Load OCR model", "loadOcr")));
      break;
    case "voices": {
      const voice = field("Installed Windows voice", "voice", voices[0]?.name, { options: voices.map(item => item.name) });
      const script = field("Narration script", "script", "", { type: "textarea" });
      script.input.maxLength = 10000;
      actions.push(label("Voice tools"), stack(act("List installed voices", "voices", {}, { primary: !voices.length })));
      if (voices.length) actions.push(voice.wrapper, script.wrapper, stack(act("Render WAV narration", "speak",
        () => ({ voice: voice.value(), script: script.value() }), { primary: true, enabled: !!item })));
      if (!item) actions.push(hint("Open a recording to save narration to its card."));
      break;
    }
    case "insights": {
      const frames = outputs("frame", "screenshot");
      const frame = field("Frame for OCR", "ocrFrame", frames[0]?.name, { options: frames.map((image, index) => (index + 1) + ". " + image.name) });
      actions.push(label("Analysis tools"), stack(act("Refresh media metadata", "probe", {}, { primary: true, enabled: hasSource }),
        act("Export MDKG runbook", "runbook", {}, { enabled: state.hasTimedMetadata })));
      if (!state.hasTimedMetadata) actions.push(hint("Runbook export needs a matching .mdkg sidecar."));
      if (frames.length) actions.push(section("Frame OCR"), frame.wrapper, stack(act("Analyze selected frame", "ocr",
        () => ({ artifactId: frames[frame.input.selectedIndex].id }))));
      if (current) actions.push(act("View analysis", "readArtifact", { artifactId: current.id }));
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
        whisperPath: "whisper.cpp executable", whisperModel: "Whisper model file" };
      const fields = Object.entries(labels).map(([key, title]) => [key, field(title, key, state.settings[key] || "")]);
      actions.push(label("Local tools"), ...fields.map(([, value]) => value.wrapper),
        hint("Leave FFmpeg blank to use Yumlog’s existing configuration or PATH."),
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
  $("workspace-title").textContent = item?.title || "Capture workspace";
  $("workspace-meta").textContent = item?.media
    ? [item.name, clock(item.media.duration), item.media.width + " × " + item.media.height].join("  ·  ")
    : item?.name || "Local recording library";
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
$("flip").addEventListener("click", () => setFlip(!flipped));
render();
run("snapshot");
