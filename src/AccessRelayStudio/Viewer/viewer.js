import {
  ACESFilmicToneMapping, Box3, Color, DirectionalLight, Euler, HemisphereLight, MathUtils,
  OrthographicCamera, PMREMGenerator, Quaternion, Raycaster, Scene, SRGBColorSpace,
  Vector2, Vector3, WebGLRenderer
} from "three";
import { RoomEnvironment } from "three/addons/environments/RoomEnvironment.js";
import { GLTFLoader } from "three/addons/loaders/GLTFLoader.js";

const controls = Object.freeze({
  DPadUp: 0x0001, DPadDown: 0x0002, DPadLeft: 0x0004, DPadRight: 0x0008,
  Menu: 0x0010, View: 0x0020, LeftStick: 0x0040, RightStick: 0x0080,
  LeftBumper: 0x0100, RightBumper: 0x0200, A: 0x1000, B: 0x2000, X: 0x4000, Y: 0x8000,
  LeftTrigger: 0x10000, RightTrigger: 0x20000,
  SmallLeft: 0x40000, SmallRight: 0x80000, LargeLeft: 0x100000, LargeRight: 0x200000
});
const controlNames = Object.keys(controls);
// The rear camera shows the controller's right paddles on the left of the screen.
const paddleCallouts = [
  ["left", 0.40, "SmallRight", "Small Right Paddle", "P2"],
  ["right", 0.40, "SmallLeft", "Small Left Paddle", "P1"],
  ["left", 0.65, "LargeRight", "Large Right Paddle", "P4"],
  ["right", 0.65, "LargeLeft", "Large Left Paddle", "P3"]
];
const optionalPaddles = new Set(paddleCallouts.map(([, , id]) => id));
const stickAxes = Object.freeze({
  LeftStick: ["leftX", "leftY"], RightStick: ["rightX", "rightY"]
});
const models = Object.freeze({
  "xbox-one": { file: "XboxOne.glb", name: "Xbox One 3D layout" },
  "elite-series-2": { file: "EliteSeries2Articulated.glb", name: "Elite Series 2 3D layout" },
  dualsense: { file: "DualShock4.glb", name: "DualSense controls on a DualShock 4 model", playStation: true, view: "Create" },
  "dualshock-4": { file: "DualShock4.glb", name: "DualShock 4 3D layout", playStation: true, view: "Share" },
  "switch-pro": { file: "SwitchPro.glb", name: "Switch Pro 3D layout", nintendo: true, triggerAngle: -0.13 }
});
const requestedModel = new URLSearchParams(location.search).get("model");
const modelKey = Object.hasOwn(models, requestedModel) ? requestedModel : "xbox-one";
const modelInfo = models[modelKey];
const frontLabels = modelInfo.playStation ? {
  A: "Cross", B: "Circle", X: "Square", Y: "Triangle", View: modelInfo.view, Menu: "Options",
  LeftTrigger: "Left Trigger (L2)", RightTrigger: "Right Trigger (R2)",
  LeftBumper: "Left Bumper (L1)", RightBumper: "Right Bumper (R1)"
} : modelInfo.nintendo ? {
  A: "B Button", B: "A Button", X: "Y Button", Y: "X Button", View: "Minus (−)", Menu: "Plus (+)",
  LeftTrigger: "Left Trigger (ZL)", RightTrigger: "Right Trigger (ZR)",
  LeftBumper: "Left Bumper (L)", RightBumper: "Right Bumper (R)"
} : {};
const controlLabels = {
  DPadUp: "D-pad up", DPadDown: "D-pad down", DPadLeft: "D-pad left", DPadRight: "D-pad right",
  Menu: "Menu button", View: "View button", LeftStick: "Left stick (L3)", RightStick: "Right stick (R3)",
  LeftBumper: "Left bumper", RightBumper: "Right bumper", A: "A button", B: "B button",
  X: "X button", Y: "Y button", LeftTrigger: "Left trigger", RightTrigger: "Right trigger",
  SmallLeft: "Small left paddle (P1)", SmallRight: "Small right paddle (P2)",
  LargeLeft: "Large left paddle (P3)", LargeRight: "Large right paddle (P4)",
  ...frontLabels
};

const stage = document.querySelector(".stage");
const canvas = document.querySelector(".scene");
const message = document.querySelector(".message");
const resetButton = document.querySelector(".reset-view");
const viewerHint = document.querySelector(".viewer-hint");
const layoutButtons = [...document.querySelectorAll("[data-layout]")];
document.title = modelInfo.name;
document.querySelector(".viewer-name").textContent = modelInfo.name;
stage.setAttribute("aria-label", `Interactive ${modelInfo.name}`);
const embeddedDesktop = window.parent !== window && location.origin === "https://scene.accessrelay.test";
const bridge = embeddedDesktop ? {
  postMessage(payload) { window.parent.postMessage({ channel: "accessrelay-controller", payload }, "https://studio.local"); },
  addEventListener(type, callback) {
    if (type !== "message") return;
    window.addEventListener("message", event => {
      if (event.source === window.parent && event.origin === "https://studio.local" && event.data?.channel === "accessrelay-controller")
        callback({ data: event.data.payload });
    });
  }
} : location.origin === "https://studio.local" && location.pathname === "/Viewer/index.html" ? window.chrome?.webview : null;
const requestedTheme = new URLSearchParams(location.search).get("theme");
document.documentElement.dataset.theme = requestedTheme === "light" || requestedTheme === "sage" ? "light" : "dark";
const scene = new Scene();
const parts = new Map();
const pickable = new Map();
const orbitDistance = 4;
const defaultPitch = 0.07;
const raycaster = new Raycaster();
const cursor = new Vector2();
const projected = new Vector3();
const focusGlow = new Color();
const activeGlow = new Color("#98f9c3");
const dimGlow = new Color();
function syncTheme() {
  const palette = getComputedStyle(document.documentElement);
  focusGlow.set(palette.getPropertyValue("--viewer-glow").trim());
  dimGlow.set(palette.getPropertyValue("--accent").trim());
}
syncTheme();
const poseRotation = new Quaternion();
const poseEuler = new Euler(0, 0, 0, "YXZ");
const screenDirection = new Vector3();
const stickDeflection = new Vector2();
const modelCenter = new Vector3();
const modelSize = new Vector3();
const input = {
  leftX: 0, leftY: 0, rightX: 0, rightY: 0, leftTrigger: 0, rightTrigger: 0,
  controls: 0, assigned: 0, selected: "", connected: false
};
const animatedAxes = { leftX: 0, leftY: 0, rightX: 0, rightY: 0 };
let bindings = {};
let renderer;
let model;
let dirty = true;
let settling = false;
let hovered = "";
let frameAt = 0;
let drag;

function view(name, yaw) {
  return {
    name, yaw, pitch: defaultPitch, zoom: 1, camera: new OrthographicCamera(),
    pane: document.querySelector(`[data-view="${name}"]`), callouts: [], rect: null
  };
}

const front = view("front", 0);
const rear = view("rear", Math.PI);
const views = [front, rear];

function addCallout(view, side, row, id, title, subtitle = "") {
  title = frontLabels[id] ?? title;
  const button = document.createElement("button");
  button.type = "button";
  button.className = "callout";
  button.dataset.control = id;
  button.style.top = `${row * 100}%`;
  button.setAttribute("aria-label", controlLabels[id]);
  button.setAttribute("aria-pressed", "false");
  if (/^[ABXY] Button$/.test(title)) {
    const letter = document.createElement("b");
    letter.textContent = title[0];
    button.append(letter, title.slice(1));
  } else {
    button.textContent = title;
  }
  if (subtitle) {
    const detail = document.createElement("small");
    detail.textContent = subtitle;
    button.append(detail);
  }
  button.addEventListener("click", () => select(id));
  button.addEventListener("pointerenter", () => highlight(id));
  button.addEventListener("pointerleave", () => highlight(""));
  view.pane.querySelector(`.labels-${side}`).append(button);

  const leader = document.createElementNS("http://www.w3.org/2000/svg", "polyline");
  leader.classList.add("leader");
  const endpoint = document.createElementNS("http://www.w3.org/2000/svg", "circle");
  endpoint.classList.add("endpoint");
  endpoint.dataset.control = id;
  endpoint.setAttribute("r", "3.3");
  view.pane.querySelector(".leaders").append(leader, endpoint);
  view.callouts.push({ id, side, button, leader, endpoint });
}

addCallout(front, "left", 0.08, "LeftTrigger", "Left Trigger (LT)");
addCallout(front, "left", 0.19, "LeftBumper", "Left Bumper (LB)");
addCallout(front, "left", 0.36, "LeftStick", "Left Stick (LS)", "Click: L3");
addCallout(front, "left", 0.52, "View", "View Button");
addCallout(front, "left", 0.66, "DPadUp", "D-pad ↑");
addCallout(front, "left", 0.76, "DPadLeft", "D-pad ←");
addCallout(front, "left", 0.86, "DPadRight", "D-pad →");
addCallout(front, "left", 0.96, "DPadDown", "D-pad ↓");
addCallout(front, "right", 0.08, "RightTrigger", "Right Trigger (RT)");
addCallout(front, "right", 0.19, "RightBumper", "Right Bumper (RB)");
addCallout(front, "right", 0.30, "Y", "Y Button");
addCallout(front, "right", 0.41, "X", "X Button");
addCallout(front, "right", 0.52, "B", "B Button");
addCallout(front, "right", 0.63, "A", "A Button");
addCallout(front, "right", 0.75, "Menu", "Menu Button");
addCallout(front, "right", 0.89, "RightStick", "Right Stick (RS)", "Click: R3");

function select(id) {
  if (!parts.has(id))
    return;
  input.selected = id;
  dirty = true;
  updateCallouts();
  bridge?.postMessage({ type: "select", control: id });
}

function highlight(id) {
  if (id === hovered)
    return;
  hovered = id;
  dirty = true;
}

function finite(value, minimum, maximum) {
  return typeof value === "number" && Number.isFinite(value) ? MathUtils.clamp(value, minimum, maximum) : 0;
}

function axis(value) {
  return finite(value, -32768, 32767) / (value < 0 ? 32768 : 32767);
}

function onHostMessage(data) {
  if (!data || typeof data !== "object")
    return;
  if (data.type === "theme" && (data.theme === "light" || data.theme === "dark")) {
    document.documentElement.dataset.theme = data.theme;
    syncTheme();
  } else if (data.type === "bindings" && data.bindings && typeof data.bindings === "object") {
    bindings = Object.fromEntries(controlNames
      .filter(id => typeof data.bindings[id] === "string")
      .map(id => [id, data.bindings[id]]));
  } else if (data.type === "state") {
    const next = {
      leftX: axis(data.leftX), leftY: axis(data.leftY), rightX: axis(data.rightX), rightY: axis(data.rightY),
      leftTrigger: finite(data.leftTrigger, 0, 255) / 255, rightTrigger: finite(data.rightTrigger, 0, 255) / 255,
      controls: data.controls >>> 0, assigned: data.assigned >>> 0,
      connected: data.connected === true,
      selected: Object.hasOwn(controls, data.selected) ? data.selected : input.selected
    };
    if (Object.keys(next).every(key => input[key] === next[key]))
      return;
    Object.assign(input, next);
    viewerHint.title = input.connected ? "Live controller input connected" : "Live controller input disconnected";
  } else {
    return;
  }
  dirty = true;
  updateCallouts();
}

bridge?.addEventListener("message", event => onHostMessage(event.data));

function isActive(id) {
  if (id === "LeftTrigger" && input.leftTrigger > 0.02)
    return true;
  if (id === "RightTrigger" && input.rightTrigger > 0.02)
    return true;
  return (input.controls & controls[id]) !== 0;
}

function updateCallouts() {
  for (const { callouts } of views) {
    for (const { id, button, leader, endpoint } of callouts) {
      const pressed = isActive(id);
      const assigned = (input.assigned & controls[id]) !== 0;
      const description = bindings[id] ? `${controlLabels[id]} · ${bindings[id]}` : controlLabels[id];
      button.title = description;
      button.setAttribute("aria-pressed", String(input.selected === id));
      button.classList.toggle("active", pressed);
      button.classList.toggle("preview", drag?.control === id);
      leader.classList.toggle("active", pressed);
      endpoint.classList.toggle("active", pressed);
      endpoint.classList.toggle("assigned", assigned);
    }
  }
}

function cloneMaterials(node) {
  const lights = [];
  node.traverse(child => {
    if (!child.isMesh)
      return;
    const original = Array.isArray(child.material) ? child.material : [child.material];
    const cloned = original.map(material => {
      const copy = material.clone();
      if (copy.emissive) {
        lights.push({
          material: copy, emissive: copy.emissive.clone(), intensity: copy.emissiveIntensity
        });
      }
      return copy;
    });
    child.material = Array.isArray(child.material) ? cloned : cloned[0];
  });
  return lights;
}

function initializeParts() {
  model.updateMatrixWorld(true);
  for (const id of controlNames) {
    const node = model.getObjectByName(id);
    const bounds = node ? new Box3().setFromObject(node) : null;
    if (!bounds || bounds.isEmpty()) {
      if (optionalPaddles.has(id))
        continue;
      throw new Error(`Controller control ${id} has no visible mesh.`);
    }
    const anchor = bounds.getCenter(new Vector3());
    if (Object.hasOwn(stickAxes, id))
      anchor.z = bounds.max.z - 0.012;
    parts.set(id, {
      node, position: node.position.clone(), quaternion: node.quaternion.clone(),
      anchor: node.worldToLocal(anchor),
      lights: cloneMaterials(node), amount: 0
    });
    pickable.set(node, id);
  }
  for (const callout of paddleCallouts) {
    if (parts.has(callout[2]))
      addCallout(rear, ...callout);
  }
}

function initializeLighting() {
  const environment = new RoomEnvironment();
  const generator = new PMREMGenerator(renderer);
  scene.environment = generator.fromScene(environment, 0.05).texture;
  scene.environmentIntensity = 0.74;
  generator.dispose();
  environment.dispose();
  scene.add(new HemisphereLight(0xd6e5ff, 0x4c5160, 0.85));
  for (const [x, y, z, color, intensity] of [
    [-3, 5, 6, 0xfff5e9, 1.65], [4, 2, 2, 0xc5dbff, 0.72], [2, 4, -5, 0xf1f6ff, 1.32]
  ]) {
    const light = new DirectionalLight(color, intensity);
    light.position.set(x, y, z);
    scene.add(light);
  }
}

function start() {
  renderer = new WebGLRenderer({ canvas, alpha: true, antialias: true, powerPreference: "high-performance" });
  renderer.setClearColor(0x000000, 0);
  renderer.toneMapping = ACESFilmicToneMapping;
  renderer.toneMappingExposure = 1.0;
  renderer.outputColorSpace = SRGBColorSpace;
  renderer.autoClear = false;
  initializeLighting();
  canvas.addEventListener("webglcontextlost", () => fail(new Error("The graphics context was lost.")));
  return new GLTFLoader().loadAsync(new URL(`../Model/${modelInfo.file}`, import.meta.url).href);
}

function viewportSizes() {
  if (!renderer)
    return;
  const rect = stage.getBoundingClientRect();
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.setSize(Math.max(1, Math.round(rect.width)), Math.max(1, Math.round(rect.height)), false);
  for (const view of views) {
    const pane = view.pane.getBoundingClientRect();
    view.rect = { x: pane.left - rect.left, y: rect.bottom - pane.bottom, w: pane.width, h: pane.height };
    for (const callout of view.callouts) {
      const label = callout.button.getBoundingClientRect();
      callout.startX = (callout.side === "left" ? label.right : label.left) - pane.left;
      callout.startY = label.top + label.height / 2 - pane.top;
    }
  }
  dirty = true;
}

function fitCamera(view) {
  const { camera, rect, yaw, pitch, zoom, name } = view;
  const aspect = rect.w / rect.h;
  const single = stage.dataset.layout !== "both";
  const widthFraction = rect.w < 480 ? 0.69 : name === "rear" && !single ? 0.63 : 0.69;
  const heightFraction = 0.95;
  const minimumHeight = (modelSize.y + modelSize.z * Math.abs(Math.sin(pitch))) / heightFraction;
  const width = Math.max(modelSize.x / widthFraction, minimumHeight * aspect) / zoom;
  const height = width / aspect;
  camera.left = -width / 2;
  camera.right = width / 2;
  camera.top = height / 2;
  camera.bottom = -height / 2;
  camera.near = 0.1;
  camera.far = orbitDistance * 3;
  camera.position.set(Math.sin(yaw) * Math.cos(pitch), Math.sin(pitch), Math.cos(yaw) * Math.cos(pitch))
    .multiplyScalar(orbitDistance).add(modelCenter);
  camera.lookAt(modelCenter);
  camera.updateProjectionMatrix();
  camera.updateMatrixWorld();
}

function redrawCalloutLines(view) {
  const { w, h } = view.rect;
  const initialYaw = view.name === "front" ? 0 : Math.PI;
  const showLines = Math.cos(view.yaw - initialYaw) > 0.48;
  for (const { id, side, leader, endpoint, startX, startY } of view.callouts) {
    const { node, anchor } = parts.get(id);
    projected.copy(anchor).applyMatrix4(node.matrixWorld).project(view.camera);
    const x = (projected.x * 0.5 + 0.5) * w;
    const y = (-projected.y * 0.5 + 0.5) * h;
    const inView = x > 0 && y > 0 && x < w && y < h;
    leader.style.display = endpoint.style.display = showLines && inView ? "" : "none";
    if (!showLines || !inView)
      continue;
    const lead = Math.min(Math.abs(x - startX) * 0.55, 12 + Math.abs(y - startY) * 0.65);
    const elbow = x - (side === "left" ? 1 : -1) * lead;
    leader.setAttribute("points", `${startX},${startY} ${elbow},${startY} ${x},${y}`);
    endpoint.setAttribute("cx", String(x));
    endpoint.setAttribute("cy", String(y));
  }
}

function smooth(current, target, delta) {
  const next = MathUtils.damp(current, target, 35, delta);
  return Math.abs(target - next) < 0.001 ? target : next;
}

function targetAmount(id) {
  const previewing = drag?.control === id;
  if (id === "LeftTrigger")
    return previewing ? drag.travel : input.leftTrigger;
  if (id === "RightTrigger")
    return previewing ? drag.travel : input.rightTrigger;
  return isActive(id) || (previewing && !Object.hasOwn(stickAxes, id)) ? 1 : 0;
}

function targetAxis(key) {
  if (drag?.axes?.[0] === key)
    return drag.axisX;
  if (drag?.axes?.[1] === key)
    return drag.axisY;
  return input[key];
}

function updatePose(delta) {
  let moving = false;
  for (const key of Object.keys(animatedAxes)) {
    const next = smooth(animatedAxes[key], targetAxis(key), delta);
    moving ||= next !== animatedAxes[key];
    animatedAxes[key] = next;
  }
  for (const [id, part] of parts) {
    const next = smooth(part.amount, targetAmount(id), delta);
    moving ||= part.amount !== next;
    part.amount = next;
    part.node.position.copy(part.position);
    part.node.quaternion.copy(part.quaternion);
    if (id.endsWith("Stick")) {
      const prefix = id === "LeftStick" ? "left" : "right";
      poseEuler.set(-animatedAxes[`${prefix}Y`] * 0.31, animatedAxes[`${prefix}X`] * 0.31, 0, "YXZ");
      part.node.quaternion.multiply(poseRotation.setFromEuler(poseEuler));
      part.node.position.z -= 0.015 * next;
    } else if (id.endsWith("Trigger")) {
      // Negative X rotates the exposed surface inward toward the grip.
      part.node.rotateX(next * (modelInfo.triggerAngle ?? -0.34));
    } else if (id.startsWith("Small") || id.startsWith("Large")) {
      part.node.position.z += 0.017 * next;
    } else if (id.endsWith("Bumper")) {
      part.node.position.y -= 0.009 * next;
      part.node.position.z -= 0.008 * next;
    } else {
      part.node.position.z -= 0.018 * next;
    }

    const stickMoving = id === "LeftStick" && Math.hypot(animatedAxes.leftX, animatedAxes.leftY) > 0.07
      || id === "RightStick" && Math.hypot(animatedAxes.rightX, animatedAxes.rightY) > 0.07;
    const previewing = drag?.control === id;
    const active = isActive(id) || (stickMoving && !previewing);
    const glow = active ? activeGlow
      : previewing || input.selected === id ? focusGlow : hovered === id ? dimGlow : null;
    const intensity = active ? 0.46 : previewing ? 0.36 : input.selected === id ? 0.29 : 0.17;
    for (const { material, emissive, intensity: baseline } of part.lights) {
      material.emissive.copy(glow ?? emissive);
      material.emissiveIntensity = glow ? intensity : baseline;
    }
  }
  return moving;
}

function render(now) {
  const delta = Math.min(0.05, (now - (frameAt || now - 16)) / 1000);
  frameAt = now;
  if (dirty || settling) {
    settling = updatePose(delta);
    model.updateMatrixWorld(true);
    renderer.setScissorTest(false);
    renderer.clear(true, true, false);
    renderer.setScissorTest(true);
    for (const view of views) {
      const { rect } = view;
      if (view.pane.hidden || !rect || rect.w <= 0 || rect.h <= 0)
        continue;
      fitCamera(view);
      renderer.setViewport(rect.x, rect.y, rect.w, rect.h);
      renderer.setScissor(rect.x, rect.y, rect.w, rect.h);
      renderer.render(scene, view.camera);
      redrawCalloutLines(view);
    }
    dirty = false;
  }
  requestAnimationFrame(render);
}

function pickAt(view, rect, x, y) {
  cursor.set((x - rect.left) / rect.width * 2 - 1, -(y - rect.top) / rect.height * 2 + 1);
  raycaster.setFromCamera(cursor, view.camera);
  const first = raycaster.intersectObject(model, true)[0];
  for (let candidate = first?.object; candidate && candidate !== model; candidate = candidate.parent) {
    if (pickable.has(candidate))
      return pickable.get(candidate);
  }
  return "";
}

function pick(view, event, tolerateSeam = false) {
  if (!model)
    return "";
  const rect = view.pane.getBoundingClientRect();
  const { clientX: x, clientY: y } = event;
  const control = pickAt(view, rect, x, y);
  if (control || !tolerateSeam)
    return control;

  // A narrow seam in the mesh may fall under the center of a pixel.
  for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
    const adjacent = pickAt(view, rect, x + dx, y + dy);
    if (adjacent)
      return adjacent;
  }
  return "";
}

function beginDrag(view, event) {
  if (drag || !model || event.target.closest("button") || event.button !== 0)
    return;
  event.preventDefault();
  view.pane.setPointerCapture(event.pointerId);
  const control = pick(view, event, true);
  const start = { view, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY };
  if (!control) {
    drag = { ...start, yaw: view.yaw, pitch: view.pitch, moved: false };
    return;
  }

  const axes = stickAxes[control];
  const originX = axes ? input[axes[0]] : 0;
  const originY = axes ? input[axes[1]] : 0;
  stickDeflection.set(originX, originY).clampLength(0, 1);
  const triggerStart = control === "LeftTrigger" ? input.leftTrigger : input.rightTrigger;
  drag = {
    ...start, control, axes, originX: stickDeflection.x, originY: stickDeflection.y,
    axisX: stickDeflection.x, axisY: stickDeflection.y,
    initialTravel: Math.max(triggerStart, 0.2), travel: Math.max(triggerStart, 0.2),
    cameraOrientation: view.camera.quaternion.clone(),
    travelPixels: MathUtils.clamp(view.rect.w * 0.075, 24, 56)
  };
  view.pane.classList.add("manipulating");
  select(control);
}

function movePointer(view, event) {
  if (!drag || drag.view !== view || drag.pointerId !== event.pointerId) {
    if (!drag && !event.target.closest("button"))
      hoverPart(view, pick(view, event));
    return;
  }
  const dx = event.clientX - drag.startX;
  const dy = event.clientY - drag.startY;
  if (drag.control) {
    if (drag.axes) {
      // Convert the screen gesture into the model's axes, even when seen from the side.
      screenDirection.set(dx, -dy, 0).applyQuaternion(drag.cameraOrientation)
        .multiplyScalar(1 / drag.travelPixels);
      stickDeflection.set(drag.originX + screenDirection.x, drag.originY + screenDirection.y)
        .clampLength(0, 1);
      drag.axisX = stickDeflection.x;
      drag.axisY = stickDeflection.y;
    } else if (drag.control.endsWith("Trigger")) {
      drag.travel = Math.min(1, drag.initialTravel + Math.hypot(dx, dy) / drag.travelPixels);
    }
    dirty = true;
    return;
  }

  if (!drag.moved && Math.hypot(dx, dy) < 5)
    return;
  drag.moved = true;
  view.pane.classList.add("dragging");
  view.yaw = drag.yaw - dx * 0.009;
  view.pitch = MathUtils.clamp(drag.pitch + dy * 0.007, -1.10, 1.10);
  dirty = true;
}

function hoverPart(view, control) {
  view.pane.classList.toggle("over-part", Boolean(control));
  view.pane.classList.toggle("over-stick", Object.hasOwn(stickAxes, control));
  highlight(control);
}

function releaseDrag() {
  const gesture = drag;
  if (!gesture)
    return;
  drag = undefined;
  const { view, pointerId } = gesture;
  view.pane.classList.remove("dragging", "manipulating");
  if (view.pane.hasPointerCapture(pointerId))
    view.pane.releasePointerCapture(pointerId);
  hoverPart(view, "");
  dirty = true;
  updateCallouts();
}

function finishDrag(view, event) {
  if (!drag || drag.view !== view || drag.pointerId !== event.pointerId)
    return;
  releaseDrag();
}

for (const view of views) {
  view.pane.addEventListener("pointerdown", event => beginDrag(view, event));
  view.pane.addEventListener("pointermove", event => movePointer(view, event));
  view.pane.addEventListener("pointerup", event => finishDrag(view, event));
  view.pane.addEventListener("pointercancel", event => finishDrag(view, event));
  view.pane.addEventListener("lostpointercapture", event => finishDrag(view, event));
  view.pane.addEventListener("dragstart", event => event.preventDefault());
  view.pane.addEventListener("pointerleave", () => hoverPart(view, ""));
  view.pane.addEventListener("wheel", event => {
    if (!model || drag?.control)
      return;
    view.zoom = MathUtils.clamp(view.zoom * Math.exp(-event.deltaY * 0.0012), 0.7, 1.65);
    event.preventDefault();
    dirty = true;
  }, { passive: false });
}

function setLayout(layout) {
  releaseDrag();
  stage.dataset.layout = layout;
  front.pane.hidden = layout === "rear";
  rear.pane.hidden = layout === "front";
  for (const button of layoutButtons)
    button.setAttribute("aria-pressed", String(button.dataset.layout === layout));
  viewportSizes();
}

for (const button of layoutButtons)
  button.addEventListener("click", () => setLayout(button.dataset.layout));

resetButton.addEventListener("click", () => {
  releaseDrag();
  front.yaw = 0;
  rear.yaw = Math.PI;
  for (const view of views) {
    view.pitch = defaultPitch;
    view.zoom = 1;
  }
  dirty = true;
});

window.addEventListener("blur", releaseDrag);

function fail(error) {
  const detail = error instanceof Error ? error.message : String(error);
  message.hidden = false;
  message.textContent = "The 3D controller could not open.";
  message.title = detail;
  bridge?.postMessage({ type: "error", message: detail, model: modelKey });
  console.error(error);
}

try {
  const gltf = await start();
  model = gltf.scene;
  const bounds = new Box3().setFromObject(model);
  bounds.getCenter(modelCenter);
  bounds.getSize(modelSize);
  initializeParts();
  scene.add(model);
  message.hidden = true;
  resetButton.disabled = false;
  setLayout("front");
  updateCallouts();
  new ResizeObserver(viewportSizes).observe(stage);
  requestAnimationFrame(render);
  bridge?.postMessage({ type: "ready", model: modelKey });
} catch (error) {
  fail(error);
}
