// device-icons.js
// SPEC-DEMO-02 Task 2 — Per-device emoji map.
// Loaded as a plain global script BEFORE dashboard.js (no module system).
// Exposes window.DEVICE_EMOJI and window.getDeviceEmoji(id).

(function (global) {
    "use strict";

    // Exact-match map: full device ID -> emoji.
    var DEVICE_EMOJI = {
        "PLC-PRESS-001": "\uD83D\uDD28", // 🔨 Hydraulic press
        "PLC-CONV-002":  "\uD83E\uDEA2", // 🪢 Conveyor belt
        "PLC-WELD-003":  "\u26A1",       // ⚡ Welding (sparks)
        "PLC-PACK-004":  "\uD83D\uDCE6", // 📦 Packaging
        "PLC-OVEN-005":  "\uD83D\uDD25", // 🔥 Oven heat
        "PLC-CNC-006":   "\uD83D\uDD29", // 🔩 CNC mill
        "PLC-PAINT-007": "\uD83C\uDFA8"  // 🎨 Paint booth
    };

    // Secondary match: middle token (e.g. "PRESS" in "PLC-PRESS-008") -> emoji.
    // Lets future PLC-PRESS-N or PLC-CNC-N get the right emoji without code change.
    var DEVICE_TYPE_EMOJI = {
        PRESS: "\uD83D\uDD28", // 🔨
        CONV:  "\uD83E\uDEA2", // 🪢
        WELD:  "\u26A1",       // ⚡
        PACK:  "\uD83D\uDCE6", // 📦
        OVEN:  "\uD83D\uDD25", // 🔥
        CNC:   "\uD83D\uDD29", // 🔩
        PAINT: "\uD83C\uDFA8"  // 🎨
    };

    var FALLBACK_EMOJI = "\uD83C\uDFED"; // 🏭

    function getDeviceEmoji(id) {
        if (id && Object.prototype.hasOwnProperty.call(DEVICE_EMOJI, id)) {
            return DEVICE_EMOJI[id];
        }
        var m = /^PLC-([A-Z]+)-/.exec(id || "");
        if (m && Object.prototype.hasOwnProperty.call(DEVICE_TYPE_EMOJI, m[1])) {
            return DEVICE_TYPE_EMOJI[m[1]];
        }
        return FALLBACK_EMOJI;
    }

    global.DEVICE_EMOJI = DEVICE_EMOJI;
    global.DEVICE_TYPE_EMOJI = DEVICE_TYPE_EMOJI;
    global.getDeviceEmoji = getDeviceEmoji;
})(typeof window !== "undefined" ? window : this);
