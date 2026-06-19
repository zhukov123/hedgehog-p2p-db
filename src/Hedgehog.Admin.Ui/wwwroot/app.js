const defaultApiBase = `${location.protocol}//${location.hostname}:5081/admin/v1`;
const state = {
  apiBase: localStorage.getItem("hedgehog.admin.apiBase") || defaultApiBase,
  objectFilter: {},
  repairFilter: {},
  auditFilter: {}
};

const apiInput = document.querySelector("#api-base");
apiInput.value = state.apiBase;

document.querySelector("#api-form").addEventListener("submit", event => {
  event.preventDefault();
  state.apiBase = apiInput.value.replace(/\/$/, "");
  localStorage.setItem("hedgehog.admin.apiBase", state.apiBase);
  loadAll();
});

document.querySelector("#object-filter").addEventListener("submit", event => {
  event.preventDefault();
  state.objectFilter = formValues(event.currentTarget);
  loadObjects();
});

document.querySelector("#repair-filter").addEventListener("submit", event => {
  event.preventDefault();
  state.repairFilter = formValues(event.currentTarget);
  loadRepair();
});

document.querySelector("#audit-filter").addEventListener("submit", event => {
  event.preventDefault();
  state.auditFilter = formValues(event.currentTarget);
  loadAudit();
});

document.body.addEventListener("click", async event => {
  const button = event.target.closest("button");
  if (!button) {
    return;
  }

  if (button.hasAttribute("data-reload")) {
    await loadAll();
    return;
  }

  const action = button.dataset.action;
  if (!action) {
    return;
  }

  const [target, verb, id] = action.split(":");
  button.disabled = true;
  try {
    await postAction(target, id || target, verb);
    await loadAll();
  } catch (error) {
    showToast(error.message);
  } finally {
    button.disabled = false;
  }
});

async function loadAll() {
  await Promise.all([
    loadCluster(),
    loadNodes(),
    loadCapacity(),
    loadObjects(),
    loadRepair(),
    loadGates(),
    loadAudit()
  ]);
}

async function loadCluster() {
  const status = await getJson("/cluster/status");
  document.querySelector("#cluster-line").textContent =
    `${status.clusterId} | head ${status.headHealth} | metadata ${status.metadataHealth} | writes ${status.writeMode} | capacity ${status.capacityPressure}`;

  document.querySelector("#cluster-summary").innerHTML = [
    metric("Write mode", status.writeMode),
    metric("Capacity", status.capacityPressure),
    metric("Unavailable nodes", status.unavailableNodeCount),
    metric("Repair backlog", status.repairBacklogCount),
    metric("Repair bytes", bytes(status.repairBytesPending))
  ].join("");

  renderRows("#cluster-signals", status.signals, signal => `
    <tr>
      <td>${escapeHtml(signal.name)}</td>
      <td>${pill(signal.state)}</td>
      <td class="mono">${escapeHtml(signal.value)}</td>
      <td>${escapeHtml(signal.detail)}</td>
    </tr>`);
}

async function loadNodes() {
  const nodes = await getJson("/nodes");
  renderRows("#nodes-body", nodes, node => `
    <tr>
      <td class="mono">${escapeHtml(node.nodeId)}</td>
      <td>${escapeHtml(node.region)}</td>
      <td>${pill(node.state)}</td>
      <td>${node.heartbeatAgeSeconds}s<br><span class="muted">${time(node.lastSeenAt)}</span></td>
      <td>${node.acceptingWrites ? "yes" : "no"}</td>
      <td>${escapeHtml(node.drainState)}</td>
      <td>${bytes(node.usedBytes)} / ${bytes(node.usableBytes)}</td>
      <td>${node.healthyReplicas} healthy<br>${node.suspectReplicas} suspect<br>${node.pendingReplicas} pending</td>
      <td>${actions([
        ["Drain", `node:drain:${node.nodeId}`],
        ["Cancel drain", `node:cancel-drain:${node.nodeId}`],
        ["Quarantine", `node:quarantine:${node.nodeId}`],
        ["Verify", `node:force-verify:${node.nodeId}`]
      ])}</td>
    </tr>`);
}

async function loadCapacity() {
  const capacity = await getJson("/capacity");
  renderRows("#capacity-body", capacity, scope => `
    <tr>
      <td><span class="mono">${escapeHtml(scope.scopeType)}</span><br>${escapeHtml(scope.scopeId)}</td>
      <td>${pill(scope.pressure)}</td>
      <td>${bytes(scope.usableBytes)}</td>
      <td>${bytes(scope.committedBytes)}</td>
      <td>${bytes(scope.reservedBytes)}</td>
      <td>${bytes(scope.effectiveFreeBytes)}</td>
      <td>${bytes(scope.emergencyReserveBytes)}</td>
      <td>${scope.writesFrozen ? "yes" : "no"}</td>
      <td>${actions([
        ["Freeze writes", `capacity:freeze-writes:${scope.scopeId}`],
        ["Resume writes", `capacity:resume-writes:${scope.scopeId}`],
        ["Cleanup", `capacity:trigger-cleanup:${scope.scopeId}`]
      ])}</td>
    </tr>`);
}

async function loadObjects() {
  const objects = await getJson(`/objects${query(state.objectFilter)}`);
  renderRows("#objects-body", objects, item => `
    <tr>
      <td>${escapeHtml(item.tenantId)}</td>
      <td>${escapeHtml(item.datasetId)}</td>
      <td>${escapeHtml(item.objectId)}<br><span class="mono muted">${escapeHtml(item.objectLookupHashPrefix)}</span></td>
      <td class="mono">${escapeHtml(item.versionId)}${item.isCurrent ? "<br><span class=\"muted\">current</span>" : ""}</td>
      <td>${pill(item.state)}${item.isTombstone ? "<br><span class=\"muted\">tombstone</span>" : ""}</td>
      <td>${item.healthyReplicas}/${item.requiredReplicas} healthy<br>${item.suspectReplicas} suspect</td>
      <td>placement ${item.placementEpoch}<br>delete ${item.deleteEpoch || "-"}</td>
      <td>${item.gcBlocked ? "blocked" : "open"}</td>
      <td>${actions([
        ["Repair", `object:force-repair:${item.versionId}`],
        ["Suspect", `object:mark-suspect:${item.versionId}`],
        [item.gcBlocked ? "Unblock GC" : "Block GC", `object:${item.gcBlocked ? "unblock-gc" : "block-gc"}:${item.versionId}`]
      ])}</td>
    </tr>`);
}

async function loadRepair() {
  const jobs = await getJson(`/repair/queue${query(state.repairFilter)}`);
  renderRows("#repair-body", jobs, job => `
    <tr>
      <td class="mono">${escapeHtml(job.jobId)}</td>
      <td>${escapeHtml(job.repairClass)}</td>
      <td>${pill(job.priority)}</td>
      <td>${pill(job.state)}</td>
      <td>${escapeHtml(job.reason)}${job.lastFailureReason ? `<br><span class="muted">${escapeHtml(job.lastFailureReason)}</span>` : ""}</td>
      <td>${escapeHtml(job.tenantId)} / ${escapeHtml(job.datasetId)}<br><span class="mono muted">${escapeHtml(job.versionId)}</span></td>
      <td>${bytes(job.bytesPending)}</td>
      <td>${age(job.enqueuedAt)}<br>${job.attemptCount} attempts</td>
      <td>${actions([
        ["Boost", `repair-job:boost-priority:${job.jobId}`],
        ["Retry", `repair-job:retry:${job.jobId}`],
        ["Cancel duplicate", `repair-job:cancel-duplicate:${job.jobId}`]
      ])}</td>
    </tr>`);
}

async function loadGates() {
  const gates = await getJson("/recovery/gates");
  renderRows("#gates-body", gates, gate => `
    <tr>
      <td>${escapeHtml(gate.name)}<br><span class="mono muted">${escapeHtml(gate.gateId)}</span></td>
      <td>${pill(gate.state)}</td>
      <td>${pill(gate.severity)}</td>
      <td>${escapeHtml(gate.reason)}</td>
      <td>${gate.approvals}/${gate.requiredApprovals}</td>
      <td>${gate.blocks.map(escapeHtml).join("<br>")}</td>
      <td>${gate.allowedActions.map(escapeHtml).join("<br>")}</td>
      <td>${actions([
        ["Approve", `recovery-gate:approve:${gate.gateId}`],
        ["Close", `recovery-gate:close:${gate.gateId}`],
        ["Export", `recovery-gate:export-evidence:${gate.gateId}`]
      ])}</td>
    </tr>`);
}

async function loadAudit() {
  const events = await getJson(`/audit/events${query(state.auditFilter)}`);
  renderRows("#audit-body", events, item => `
    <tr>
      <td>${time(item.occurredAt)}</td>
      <td>${escapeHtml(item.actorType)}<br><span class="mono muted">${escapeHtml(item.actorId)}</span></td>
      <td>${escapeHtml(item.action)}</td>
      <td>${escapeHtml(item.targetType)}<br><span class="mono muted">${escapeHtml(item.targetId)}</span></td>
      <td>${pill(item.result)}</td>
      <td>${escapeHtml(item.reason)}</td>
      <td class="mono">${escapeHtml(item.requestId)}<br>${escapeHtml(item.eventId)}</td>
    </tr>`);
}

async function postAction(target, id, verb) {
  const path = actionPath(target, id, verb);
  const body = {
    actorId: "local-admin",
    reason: `operator requested ${verb}`,
    requestId: `ui-${crypto.randomUUID()}`
  };
  const result = await fetch(`${state.apiBase}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body)
  });
  if (!result.ok) {
    throw new Error(`${verb} failed with HTTP ${result.status}`);
  }
  const payload = await result.json();
  showToast(`${payload.action} ${payload.result}: ${payload.targetType}/${payload.targetId}`);
}

function actionPath(target, id, verb) {
  if (target === "cluster") {
    return `/cluster/actions/${verb}`;
  }
  if (target === "node") {
    return `/nodes/${encodeURIComponent(id)}/actions/${verb}`;
  }
  if (target === "capacity") {
    return `/capacity/scopes/${encodeURIComponent(id)}/actions/${verb}`;
  }
  if (target === "object") {
    return `/objects/${encodeURIComponent(id)}/actions/${verb}`;
  }
  if (target === "repair-job") {
    return `/repair/jobs/${encodeURIComponent(id)}/actions/${verb}`;
  }
  if (target === "recovery-gate") {
    return `/recovery/gates/${encodeURIComponent(id)}/actions/${verb}`;
  }
  throw new Error(`unknown target ${target}`);
}

async function getJson(path) {
  const response = await fetch(`${state.apiBase}${path}`);
  if (!response.ok) {
    throw new Error(`${path} failed with HTTP ${response.status}`);
  }
  return response.json();
}

function renderRows(selector, rows, template) {
  document.querySelector(selector).innerHTML = rows.length
    ? rows.map(template).join("")
    : "<tr><td colspan=\"12\" class=\"muted\">No rows</td></tr>";
}

function actions(items) {
  return `<div class="row-actions">${items
    .map(([label, action]) => `<button data-action="${escapeHtml(action)}">${escapeHtml(label)}</button>`)
    .join("")}</div>`;
}

function metric(label, value) {
  return `<div class="metric"><span>${escapeHtml(label)}</span><strong>${escapeHtml(String(value))}</strong></div>`;
}

function pill(value) {
  const normalized = String(value).toLowerCase();
  return `<span class="pill ${escapeHtml(normalized)}">${escapeHtml(value)}</span>`;
}

function bytes(value) {
  const number = Number(value);
  if (number >= 1024 ** 4) {
    return `${(number / 1024 ** 4).toFixed(1)} TiB`;
  }
  if (number >= 1024 ** 3) {
    return `${(number / 1024 ** 3).toFixed(1)} GiB`;
  }
  return `${number} B`;
}

function age(iso) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (seconds > 3600) {
    return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
  }
  if (seconds > 60) {
    return `${Math.floor(seconds / 60)}m`;
  }
  return `${seconds}s`;
}

function time(iso) {
  return new Date(iso).toLocaleString();
}

function query(values) {
  const params = new URLSearchParams();
  Object.entries(values)
    .filter(([, value]) => value)
    .forEach(([key, value]) => params.set(key, value));
  return params.size ? `?${params}` : "";
}

function formValues(form) {
  return Object.fromEntries([...new FormData(form).entries()].filter(([, value]) => value));
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#039;");
}

let toastTimer = 0;
function showToast(message) {
  const toast = document.querySelector("#toast");
  toast.textContent = message;
  toast.classList.add("visible");
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove("visible"), 3000);
}

loadAll().catch(error => showToast(error.message));
