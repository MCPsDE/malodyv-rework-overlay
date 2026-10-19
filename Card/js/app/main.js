import { fetchBeatmapFile } from "./analysis.js";
import { startGraphAnimationLoop } from "./graph.js";
import {
    updateCardPlayVisibility,
    updateModeTagVisibility,
    updatePauseCountVisibility,
} from "./hud.js";
import { setRecomputeHandler, scheduleRecompute } from "./scheduler.js";
import { loadSettings } from "./settings.js";
import { setupSocketListener } from "./socketHandlers.js";
import { initTriangleField } from "./triangles.js";
// Side-effect import: presets module self-initializes (registers the preset
// settings-stream listener) on load; it must be loaded exactly once.
import "./presets/index.js";
import { initTelemetry, startTelemetryHeartbeat } from "./telemetry.js";
import { state } from "./appContext.js";

setRecomputeHandler(fetchBeatmapFile);

async function loadOfflineAnalysisOptions() {
    try {
        const endpoint = new URL("./rework/analysis-options", window.location.href).toString();
        const response = await fetch(endpoint, { cache: "no-store" });
        if (!response.ok) return;
        const options = await response.json();
        const speedRate = Number(options?.speedRate);
        if (Number.isFinite(speedRate) && speedRate > 0) {
            state.speedRate = speedRate;
        }
        state.odFlag = options?.odFlag ?? null;
        state.cvtFlag = options?.cvtFlag ?? null;
        state.modCodes = Array.isArray(options?.modCodes) ? options.modCodes : [];
        state.modSignature = [
            state.speedRate.toFixed(5),
            state.odFlag == null ? "none" : String(state.odFlag),
            state.cvtFlag == null ? "none" : String(state.cvtFlag),
            "1",
        ].join("|");
    } catch {
        // The normal tosu deployment has no offline override endpoint.
    }
}

export async function initialize() {
    initTriangleField();
    await loadSettings();
    await loadOfflineAnalysisOptions();
    initTelemetry();
    startTelemetryHeartbeat();
    updateModeTagVisibility();
    updatePauseCountVisibility();
    updateCardPlayVisibility();
    startGraphAnimationLoop();
    if (!window.REWORK_OFFLINE_MODE) {
        setupSocketListener();
    }
    scheduleRecompute("initial load", false);
}


