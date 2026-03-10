(function () {
    "use strict";

    const DEVICE_NAMES = {
        "PLC-PRESS-001": "Hydraulic Press",
        "PLC-CONV-002": "Conveyor Belt",
        "PLC-WELD-003": "Welding Robot",
        "PLC-PACK-004": "Packaging Machine",
        "PLC-OVEN-005": "Industrial Oven"
    };

    const MAX_LOG_ENTRIES = 50;
    const MAX_DOWNTIME_ROWS = 50;

    const deviceData = {};
    let lastSeenTimers = null;

    // --- Downtime History State ---
    let downtimeRecords = [];
    let dtActiveFilter = "all";

    // --- DOM helpers ---

    function $(sel, root) {
        return (root || document).querySelector(sel);
    }

    function getCard(deviceId) {
        return document.getElementById("card-" + deviceId);
    }

    // --- Formatting ---

    function relativeTime(isoOrDate) {
        if (!isoOrDate) return "never";
        const date = typeof isoOrDate === "string" ? new Date(isoOrDate) : isoOrDate;
        const seconds = Math.floor((Date.now() - date.getTime()) / 1000);
        if (seconds < 0) return "just now";
        if (seconds < 60) return seconds + "s ago";
        const minutes = Math.floor(seconds / 60);
        if (minutes < 60) return minutes + "m ago";
        const hours = Math.floor(minutes / 60);
        return hours + "h ago";
    }

    function formatDuration(seconds) {
        if (seconds == null || seconds < 0) return "--";
        if (seconds < 60) return Math.floor(seconds) + "s";
        const m = Math.floor(seconds / 60);
        const s = Math.floor(seconds % 60);
        if (m < 60) return m + "m " + s + "s";
        const h = Math.floor(m / 60);
        return h + "h " + (m % 60) + "m";
    }

    function formatTime(isoOrDate) {
        if (!isoOrDate) return "--";
        const d = typeof isoOrDate === "string" ? new Date(isoOrDate) : isoOrDate;
        return d.toLocaleTimeString();
    }

    function timestamp() {
        return new Date().toLocaleTimeString();
    }

    // --- Card Updates ---

    function updateCard(deviceId, status) {
        const card = getCard(deviceId);
        if (!card) return;

        const isUp = !!status.isUp;
        const stateClass = isUp ? "up" : "down";

        $(".card-status-bar", card).className = "card-status-bar " + stateClass;

        const badge = $(".status-badge", card);
        badge.className = "status-badge " + stateClass;
        badge.textContent = isUp ? "ONLINE" : "OFFLINE";

        const temp = status.temperature;
        $(".temp-value", card).textContent = temp != null ? temp.toFixed(1) : "--";

        const psi = status.pressure;
        $(".pressure-value", card).textContent = psi != null ? psi.toFixed(1) : "--";

        const runVal = $(".running-value", card);
        if (status.isRunning != null) {
            runVal.textContent = status.isRunning ? "Running" : "Stopped";
            runVal.className = "running-value " + (status.isRunning ? "running" : "stopped");
        } else {
            runVal.textContent = "--";
            runVal.className = "running-value";
        }

        // Last seen
        deviceData[deviceId] = deviceData[deviceId] || {};
        if (status.lastSeen) {
            deviceData[deviceId].lastSeen = new Date(status.lastSeen);
        }
        $(".last-seen-value", card).textContent = relativeTime(deviceData[deviceId].lastSeen);

        // Downtime
        const downtimeInfo = $(".downtime-info", card);
        if (!isUp && status.downSince) {
            deviceData[deviceId].downSince = new Date(status.downSince);
            $(".down-since-value", card).textContent = formatTime(deviceData[deviceId].downSince);
            const elapsed = (Date.now() - deviceData[deviceId].downSince.getTime()) / 1000;
            $(".downtime-value", card).textContent = formatDuration(elapsed);
            downtimeInfo.classList.remove("hidden");
        } else {
            deviceData[deviceId].downSince = null;
            downtimeInfo.classList.add("hidden");
        }

        deviceData[deviceId].isUp = isUp;
    }

    function triggerPulse(deviceId) {
        const card = getCard(deviceId);
        if (!card) return;
        card.classList.remove("pulse");
        // Force reflow to restart animation
        void card.offsetWidth;
        card.classList.add("pulse");
    }

    function updateStats() {
        let online = 0;
        let offline = 0;
        for (const id in deviceData) {
            if (deviceData[id].isUp) online++;
            else offline++;
        }
        const total = Object.keys(DEVICE_NAMES).length;
        document.getElementById("stat-total").textContent = total;
        document.getElementById("stat-online").textContent = online;
        document.getElementById("stat-offline").textContent = total - online;
    }

    // --- Last-seen & downtime ticker ---

    function startTimers() {
        if (lastSeenTimers) return;
        lastSeenTimers = setInterval(function () {
            for (const deviceId in deviceData) {
                const card = getCard(deviceId);
                if (!card) continue;
                const d = deviceData[deviceId];
                if (d.lastSeen) {
                    $(".last-seen-value", card).textContent = relativeTime(d.lastSeen);
                }
                if (d.downSince && !d.isUp) {
                    const elapsed = (Date.now() - d.downSince.getTime()) / 1000;
                    $(".downtime-value", card).textContent = formatDuration(elapsed);
                }
            }
            // Tick active downtime rows
            tickActiveDts();
        }, 1000);
    }

    // --- Event Log ---

    function addLogEntry(message, cls) {
        const log = document.getElementById("event-log");
        const entry = document.createElement("div");
        entry.className = "event-entry " + (cls || "info");
        entry.textContent = "[" + timestamp() + "] " + message;

        // Insert at top
        if (log.firstChild) {
            log.insertBefore(entry, log.firstChild);
        } else {
            log.appendChild(entry);
        }

        // Cap entries
        while (log.children.length > MAX_LOG_ENTRIES) {
            log.removeChild(log.lastChild);
        }
    }

    // --- Downtime History ---

    function buildFilterButtons() {
        var filtersEl = document.getElementById("downtime-filters");
        if (!filtersEl) return;
        // Collect unique device IDs
        var seen = {};
        filtersEl.innerHTML = '<button class="dt-filter-btn' + (dtActiveFilter === "all" ? ' active' : '') + '" data-filter="all">All Devices</button>';
        for (var i = 0; i < downtimeRecords.length; i++) {
            var rec = downtimeRecords[i];
            if (!seen[rec.deviceId]) {
                seen[rec.deviceId] = true;
                var name = rec.deviceName || DEVICE_NAMES[rec.deviceId] || rec.deviceId;
                var btn = document.createElement("button");
                btn.className = "dt-filter-btn" + (dtActiveFilter === rec.deviceId ? " active" : "");
                btn.setAttribute("data-filter", rec.deviceId);
                btn.textContent = name;
                filtersEl.appendChild(btn);
            }
        }
    }

    function getDtFilteredRecords() {
        if (dtActiveFilter === "all") return downtimeRecords;
        return downtimeRecords.filter(function (r) { return r.deviceId === dtActiveFilter; });
    }

    function updateDtStats() {
        var records = getDtFilteredRecords();
        var total = records.length;
        var activeCount = 0;
        var totalSeconds = 0;
        var resolvedCount = 0;

        for (var i = 0; i < records.length; i++) {
            var r = records[i];
            if (!r.isResolved) {
                activeCount++;
                totalSeconds += (Date.now() - new Date(r.startedAt).getTime()) / 1000;
            } else if (r.durationSeconds != null) {
                resolvedCount++;
                totalSeconds += r.durationSeconds;
            }
        }

        document.getElementById("dt-stat-incidents").textContent = total;
        document.getElementById("dt-stat-total-time").textContent = formatDuration(totalSeconds);
        document.getElementById("dt-stat-avg").textContent = total > 0 ? formatDuration(totalSeconds / total) : "0s";
        document.getElementById("dt-stat-active").textContent = activeCount;
    }

    function formatDtTimestamp(iso) {
        if (!iso) return "--";
        var d = new Date(iso);
        var month = String(d.getMonth() + 1).padStart(2, "0");
        var day = String(d.getDate()).padStart(2, "0");
        var h = String(d.getHours()).padStart(2, "0");
        var m = String(d.getMinutes()).padStart(2, "0");
        var s = String(d.getSeconds()).padStart(2, "0");
        return month + "/" + day + " " + h + ":" + m + ":" + s;
    }

    function buildDtRow(rec) {
        var tr = document.createElement("tr");
        tr.setAttribute("data-dt-id", rec.id);
        tr.setAttribute("data-dt-device", rec.deviceId);

        if (!rec.isResolved) {
            tr.className = "dt-row-active dt-row-new";
        } else {
            tr.className = "dt-row-new";
        }

        var name = rec.deviceName || DEVICE_NAMES[rec.deviceId] || rec.deviceId;

        // Device cell
        var tdDevice = document.createElement("td");
        tdDevice.innerHTML = '<span class="dt-device-name">' + escapeHtml(name) + '</span><span class="dt-device-id">' + escapeHtml(rec.deviceId) + '</span>';
        tr.appendChild(tdDevice);

        // Reason cell
        var tdReason = document.createElement("td");
        tdReason.className = "dt-reason";
        tdReason.textContent = rec.reason || "Unknown";
        tr.appendChild(tdReason);

        // Started cell
        var tdStarted = document.createElement("td");
        tdStarted.className = "dt-time";
        tdStarted.textContent = formatDtTimestamp(rec.startedAt);
        tr.appendChild(tdStarted);

        // Ended cell
        var tdEnded = document.createElement("td");
        tdEnded.className = "dt-time dt-ended";
        tdEnded.textContent = rec.isResolved ? formatDtTimestamp(rec.endedAt) : "--";
        tr.appendChild(tdEnded);

        // Duration cell
        var tdDur = document.createElement("td");
        tdDur.className = "dt-duration";
        if (rec.isResolved && rec.durationSeconds != null) {
            tdDur.textContent = formatDuration(rec.durationSeconds);
        } else if (!rec.isResolved) {
            var elapsed = (Date.now() - new Date(rec.startedAt).getTime()) / 1000;
            tdDur.textContent = formatDuration(elapsed);
            tdDur.style.color = "var(--accent-red)";
        } else {
            tdDur.textContent = "--";
        }
        tr.appendChild(tdDur);

        // Status badge cell
        var tdStatus = document.createElement("td");
        if (rec.isResolved) {
            tdStatus.innerHTML = '<span class="dt-badge dt-badge-resolved">Resolved</span>';
        } else {
            tdStatus.innerHTML = '<span class="dt-badge dt-badge-active"><span class="dt-pulse-dot"></span>ACTIVE</span>';
        }
        tr.appendChild(tdStatus);

        return tr;
    }

    function escapeHtml(str) {
        var div = document.createElement("div");
        div.appendChild(document.createTextNode(str));
        return div.innerHTML;
    }

    function renderDtTable() {
        var tbody = document.getElementById("downtime-tbody");
        var emptyEl = document.getElementById("downtime-empty");
        if (!tbody) return;

        var filtered = getDtFilteredRecords();
        var visible = filtered.slice(0, MAX_DOWNTIME_ROWS);

        tbody.innerHTML = "";
        for (var i = 0; i < visible.length; i++) {
            tbody.appendChild(buildDtRow(visible[i]));
        }

        if (visible.length === 0) {
            emptyEl.classList.remove("hidden");
        } else {
            emptyEl.classList.add("hidden");
        }

        updateDtStats();
    }

    function handleDtFilter(e) {
        if (!e.target.classList.contains("dt-filter-btn")) return;
        dtActiveFilter = e.target.getAttribute("data-filter");
        // Update active class
        var btns = document.querySelectorAll(".dt-filter-btn");
        for (var i = 0; i < btns.length; i++) {
            btns[i].classList.toggle("active", btns[i].getAttribute("data-filter") === dtActiveFilter);
        }
        renderDtTable();
    }

    function tickActiveDts() {
        var rows = document.querySelectorAll(".dt-row-active");
        for (var i = 0; i < rows.length; i++) {
            var id = Number(rows[i].getAttribute("data-dt-id"));
            var rec = findDtRecordById(id);
            if (rec && !rec.isResolved) {
                var durCell = rows[i].querySelectorAll("td")[4];
                if (durCell) {
                    var elapsed = (Date.now() - new Date(rec.startedAt).getTime()) / 1000;
                    durCell.textContent = formatDuration(elapsed);
                }
            }
        }
        // Also refresh stats for active counters
        updateDtStats();
    }

    function findDtRecordById(id) {
        for (var i = 0; i < downtimeRecords.length; i++) {
            if (downtimeRecords[i].id === id) return downtimeRecords[i];
        }
        return null;
    }

    function onReceiveDowntimeHistory(records) {
        if (!records || !records.length) {
            downtimeRecords = [];
        } else {
            // Sort newest first
            downtimeRecords = records.slice().sort(function (a, b) {
                return new Date(b.startedAt) - new Date(a.startedAt);
            });
        }
        buildFilterButtons();
        renderDtTable();
        addLogEntry("Loaded " + downtimeRecords.length + " downtime record(s).", "info");
    }

    function onReceiveDowntimeStarted(rec) {
        // Add to front of array
        downtimeRecords.unshift(rec);
        buildFilterButtons();

        // If this device is filtered out, just update stats
        if (dtActiveFilter !== "all" && dtActiveFilter !== rec.deviceId) {
            updateDtStats();
            return;
        }

        var tbody = document.getElementById("downtime-tbody");
        var emptyEl = document.getElementById("downtime-empty");
        if (!tbody) return;

        var tr = buildDtRow(rec);
        if (tbody.firstChild) {
            tbody.insertBefore(tr, tbody.firstChild);
        } else {
            tbody.appendChild(tr);
        }
        emptyEl.classList.add("hidden");

        // Trim excess
        while (tbody.children.length > MAX_DOWNTIME_ROWS) {
            tbody.removeChild(tbody.lastChild);
        }

        updateDtStats();
        var name = rec.deviceName || DEVICE_NAMES[rec.deviceId] || rec.deviceId;
        addLogEntry("Downtime started: " + name + " (" + rec.deviceId + ")", "status-down");
    }

    function onReceiveDowntimeResolved(rec) {
        // Update local record
        for (var i = 0; i < downtimeRecords.length; i++) {
            if (downtimeRecords[i].id === rec.id) {
                downtimeRecords[i] = rec;
                break;
            }
        }

        // Update the DOM row in place
        var row = document.querySelector('tr[data-dt-id="' + rec.id + '"]');
        if (row) {
            row.classList.remove("dt-row-active");

            var cells = row.querySelectorAll("td");
            // Ended cell (index 3)
            if (cells[3]) cells[3].textContent = formatDtTimestamp(rec.endedAt);
            // Duration cell (index 4)
            if (cells[4]) {
                cells[4].textContent = formatDuration(rec.durationSeconds);
                cells[4].style.color = "";
            }
            // Status cell (index 5)
            if (cells[5]) {
                cells[5].innerHTML = '<span class="dt-badge dt-badge-resolved">Resolved</span>';
            }
        }

        updateDtStats();
        var name = rec.deviceName || DEVICE_NAMES[rec.deviceId] || rec.deviceId;
        addLogEntry("Downtime resolved: " + name + " (" + formatDuration(rec.durationSeconds) + ")", "status-up");
    }

    // --- OEE State ---
    var oeeData = {};

    function getOeeColor(pct) {
        if (pct >= 85) return 'var(--accent-green)';
        if (pct >= 60) return 'var(--accent-amber)';
        return 'var(--accent-red)';
    }

    function getOeeLabel(pct) {
        if (pct >= 85) return 'World Class';
        if (pct >= 60) return 'Typical';
        return 'Needs Improvement';
    }

    function createGauge(containerId, percentage) {
        var el = document.getElementById(containerId);
        if (!el) return;

        var ringEl = el.querySelector('.oee-gauge-ring');
        if (!ringEl) return;

        var pct = Math.max(0, Math.min(100, percentage));
        var r = 85;
        var circumference = 2 * Math.PI * r;
        var offset = circumference * (1 - pct / 100);
        var color = getOeeColor(pct);

        var svg = ringEl.querySelector('svg');
        if (!svg) {
            ringEl.innerHTML =
                '<svg viewBox="0 0 200 200">' +
                '<circle class="oee-track" cx="100" cy="100" r="' + r + '" />' +
                '<circle class="oee-fill" cx="100" cy="100" r="' + r + '" />' +
                '</svg>' +
                '<div class="oee-gauge-center">' +
                '<span class="oee-pct">--</span>' +
                '<span class="oee-gauge-label"></span>' +
                '</div>';
            svg = ringEl.querySelector('svg');
        }

        var fill = svg.querySelector('.oee-fill');
        fill.style.strokeDasharray = circumference;
        fill.style.strokeDashoffset = offset;
        fill.style.stroke = color;

        var pctEl = ringEl.querySelector('.oee-pct');
        pctEl.textContent = pct.toFixed(1) + '%';
        pctEl.style.color = color;

        var labelEl = ringEl.querySelector('.oee-gauge-label');
        labelEl.textContent = getOeeLabel(pct);
        labelEl.style.color = color;
    }

    function updateOeeDisplay(data) {
        if (!data || typeof data !== 'object') return;
        oeeData = data;

        var plantOee = 0;
        var deviceIds = Object.keys(data);
        if (deviceIds.length > 0) {
            plantOee = (data[deviceIds[0]].plantOee || 0) * 100;
        }

        createGauge('plant-oee-gauge', plantOee);

        for (var i = 0; i < deviceIds.length; i++) {
            var deviceId = deviceIds[i];
            var d = data[deviceId];
            var oeePct = (d.oee || 0) * 100;

            createGauge('device-oee-' + deviceId, oeePct);

            var container = document.getElementById('device-oee-' + deviceId);
            if (container) {
                var aEl = container.querySelector('.oee-val-a');
                var pEl = container.querySelector('.oee-val-p');
                var qEl = container.querySelector('.oee-val-q');
                var partsEl = container.querySelector('.oee-val-parts');
                var goodEl = container.querySelector('.oee-val-good');

                if (aEl) aEl.textContent = ((d.availability || 0) * 100).toFixed(1) + '%';
                if (pEl) pEl.textContent = ((d.performance || 0) * 100).toFixed(1) + '%';
                if (qEl) qEl.textContent = ((d.quality || 0) * 100).toFixed(1) + '%';
                if (partsEl) partsEl.textContent = (d.totalParts || 0).toLocaleString();
                if (goodEl) goodEl.textContent = (d.goodParts || 0).toLocaleString();
            }
        }
    }

    // --- Connection Status ---

    function setConnectionStatus(state) {
        const dot = $(".status-dot");
        const text = $(".status-text");
        dot.className = "status-dot " + state;
        const labels = { connected: "Connected", disconnected: "Disconnected", reconnecting: "Reconnecting..." };
        text.textContent = labels[state] || state;
    }

    // --- SignalR ---

    function buildConnection() {
        return new signalR.HubConnectionBuilder()
            .withUrl("/hubs/dashboard")
            .withAutomaticReconnect({
                nextRetryDelayInMilliseconds: function (ctx) {
                    // Exponential backoff: 0, 1s, 2s, 4s, 8s, 16s, max 30s
                    const delay = Math.min(1000 * Math.pow(2, ctx.previousRetryCount), 30000);
                    return delay;
                }
            })
            .configureLogging(signalR.LogLevel.Warning)
            .build();
    }

    function start() {
        const connection = buildConnection();

        connection.onreconnecting(function () {
            setConnectionStatus("reconnecting");
            addLogEntry("Connection lost. Reconnecting...", "warning");
        });

        connection.onreconnected(function () {
            setConnectionStatus("connected");
            addLogEntry("Reconnected to server.", "info");
        });

        connection.onclose(function () {
            setConnectionStatus("disconnected");
            addLogEntry("Disconnected from server.", "warning");
            // Attempt manual reconnect after a delay
            setTimeout(function () {
                addLogEntry("Attempting reconnect...", "info");
                startConnection(connection);
            }, 5000);
        });

        // --- Hub handlers ---

        connection.on("ReceiveAllStatuses", function (statuses) {
            addLogEntry("Received initial device statuses.", "info");
            if (statuses && typeof statuses === "object") {
                for (const deviceId in statuses) {
                    const s = statuses[deviceId];
                    updateCard(deviceId, s);
                }
            }
            updateStats();
            startTimers();
        });

        // --- Downtime History hub handlers ---

        connection.on("ReceiveDowntimeHistory", function (records) {
            onReceiveDowntimeHistory(records);
        });

        connection.on("ReceiveDowntimeStarted", function (rec) {
            onReceiveDowntimeStarted(rec);
        });

        connection.on("ReceiveDowntimeResolved", function (rec) {
            onReceiveDowntimeResolved(rec);
        });

        connection.on("ReceiveHeartbeat", function (deviceId, isUp, lastSeen, downSince, temperature, pressure, isRunning) {
            const prev = deviceData[deviceId];
            const statusChanged = prev && prev.isUp !== undefined && prev.isUp !== isUp;

            updateCard(deviceId, {
                isUp: isUp,
                lastSeen: lastSeen,
                downSince: downSince,
                temperature: temperature,
                pressure: pressure,
                isRunning: isRunning
            });

            triggerPulse(deviceId);
            updateStats();

            const name = DEVICE_NAMES[deviceId] || deviceId;
            if (statusChanged) {
                if (isUp) {
                    addLogEntry(name + " (" + deviceId + ") is now ONLINE", "status-up");
                } else {
                    addLogEntry(name + " (" + deviceId + ") went OFFLINE", "status-down");
                }
            } else {
                addLogEntry(name + " heartbeat — " +
                    (temperature != null ? temperature.toFixed(1) + "°C" : "--") + ", " +
                    (pressure != null ? pressure.toFixed(1) + " PSI" : "--") + ", " +
                    (isRunning ? "Running" : "Stopped"), "heartbeat");
            }
        });

        // --- OEE hub handler ---

        connection.on("ReceiveOeeUpdate", function (data) {
            updateOeeDisplay(data);
            addLogEntry("OEE updated — Plant: " +
                (data && Object.keys(data).length > 0
                    ? ((data[Object.keys(data)[0]].plantOee || 0) * 100).toFixed(1) + "%"
                    : "--%"), "info");
        });

        startConnection(connection);
    }

    function startConnection(connection) {
        setConnectionStatus("reconnecting");
        connection.start()
            .then(function () {
                setConnectionStatus("connected");
                addLogEntry("Connected to SignalR hub.", "info");
            })
            .catch(function (err) {
                setConnectionStatus("disconnected");
                addLogEntry("Connection failed: " + err.toString(), "warning");
                setTimeout(function () {
                    startConnection(connection);
                }, 5000);
            });
    }

    // --- Init ---
    document.addEventListener("DOMContentLoaded", function () {
        // Wire up downtime filter clicks
        var filtersEl = document.getElementById("downtime-filters");
        if (filtersEl) {
            filtersEl.addEventListener("click", handleDtFilter);
        }
        start();
    });
})();
