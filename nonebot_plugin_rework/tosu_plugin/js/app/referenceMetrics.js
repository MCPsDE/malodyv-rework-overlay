import { estDiffCaptionEl, state } from "./appContext.js";
import { calculateLocalReferenceMetrics } from "./referenceMetricCalculator.js";

function formatMetric(value) {
    const number = Number(value);
    return Number.isFinite(number) ? number.toFixed(2) : "-";
}

function parseMetric(value) {
    if (value === null || value === undefined || value === "") {
        return null;
    }
    const number = Number(value);
    return Number.isFinite(number) ? number : null;
}

export function normalizeReferenceMetrics(payload) {
    if (!payload || typeof payload !== "object") {
        return null;
    }
    return {
        sunnyRework: parseMetric(payload.sunnyRework),
        sunnyEstDiff: typeof payload.sunnyEstDiff === "string" ? payload.sunnyEstDiff : null,
        osuSr: parseMetric(payload.osuSr),
    };
}

export function hasReferenceMetrics(metrics) {
    return Boolean(metrics)
        && [metrics.sunnyRework, metrics.osuSr].some((value) => Number.isFinite(Number(value))
            || typeof metrics.sunnyEstDiff === "string");
}

export async function fetchReferenceMetrics() {
    try {
        const endpoint = new URL("./rework/reference-metrics", window.location.href).toString();
        const response = await fetch(endpoint, { cache: "no-store" });
        if (!response.ok) {
            return null;
        }
        const payload = await response.json();
        return normalizeReferenceMetrics(payload);
    } catch {
        return null;
    }
}

export { calculateLocalReferenceMetrics };

export function setReferenceDifficultyCaption(metrics) {
    const sunnyRework = formatMetric(metrics?.sunnyRework);
    const osuSr = formatMetric(metrics?.osuSr);
    const caption = `SunnyRework:${sunnyRework}|OsuSR:${osuSr}`;
    state.referenceDifficultyCaption = caption;
    if (estDiffCaptionEl) {
        estDiffCaptionEl.textContent = caption;
    }
}
