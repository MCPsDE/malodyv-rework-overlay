import {
    profileGraphDurationEl,
    profileGraphRcFillEl,
    profileGraphRcLowerEl,
    profileGraphLnLineEl,
    profileGraphRcLineEl,
    profileGraphRcUpperEl,
    profileGraphWrapEl,
    profileGraphYLabelsEl,
    profileGraphLegendRcEl,
    profileGraphLegendLnEl,
} from "./appContext.js";

const VIEWBOX_LEFT = 8;
const VIEWBOX_RIGHT = 252;
const VIEWBOX_TOP = 14;
const VIEWBOX_BOTTOM = 104;
const Y_TICK_COUNT = 5;

function formatTime(milliseconds) {
    const totalSeconds = Math.max(0, Math.round(Number(milliseconds) / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = String(totalSeconds % 60).padStart(2, "0");
    return `${minutes}:${seconds}`;
}

function pathForSeries(times, values, maxValue) {
    if (times.length < 2) {
        return "";
    }

    const start = Number(times[0]);
    const end = Number(times[times.length - 1]);
    const duration = Math.max(1, end - start);
    const safeMax = Math.max(1e-6, Number(maxValue) || 0);
    const parts = [];

    for (let i = 0; i < times.length; i += 1) {
        const x = VIEWBOX_LEFT + ((Number(times[i]) - start) / duration) * (VIEWBOX_RIGHT - VIEWBOX_LEFT);
        const value = Math.max(0, Number(values[i]) || 0);
        const y = VIEWBOX_BOTTOM - Math.min(1, value / safeMax) * (VIEWBOX_BOTTOM - VIEWBOX_TOP);
        parts.push(`${i === 0 ? "M" : "L"} ${x.toFixed(2)} ${y.toFixed(2)}`);
    }

    return parts.join(" ");
}

function areaPathForSeries(times, lowerValues, upperValues, maxValue) {
    if (times.length < 2) {
        return "";
    }

    const start = Number(times[0]);
    const end = Number(times[times.length - 1]);
    const duration = Math.max(1, end - start);
    const safeMax = Math.max(1e-6, Number(maxValue) || 0);
    const pointFor = (index, values) => {
        const x = VIEWBOX_LEFT + ((Number(times[index]) - start) / duration) * (VIEWBOX_RIGHT - VIEWBOX_LEFT);
        const value = Math.max(0, Number(values[index]) || 0);
        const y = VIEWBOX_BOTTOM - Math.min(1, value / safeMax) * (VIEWBOX_BOTTOM - VIEWBOX_TOP);
        return `${x.toFixed(2)} ${y.toFixed(2)}`;
    };

    const upperParts = times.map((_, index) => `${index === 0 ? "M" : "L"} ${pointFor(index, upperValues)}`);
    const lowerParts = [];
    for (let index = times.length - 1; index >= 0; index -= 1) {
        lowerParts.push(`L ${pointFor(index, lowerValues)}`);
    }
    return `${upperParts.join(" ")} ${lowerParts.join(" ")} Z`;
}

function renderYAxisLabels(maxValue) {
    const labels = profileGraphYLabelsEl?.querySelectorAll("text") || [];
    labels.forEach((label, index) => {
        const fraction = index / Math.max(1, Y_TICK_COUNT - 1);
        const value = maxValue * (1 - fraction);
        label.setAttribute("y", String(VIEWBOX_TOP + fraction * (VIEWBOX_BOTTOM - VIEWBOX_TOP) + 3));
        label.textContent = value.toFixed(1);
    });
}

function setGraphLegend(source) {
    const isSunny = source === "SunnyRework";
    const rcLabel = profileGraphLegendRcEl?.querySelector("span");
    if (rcLabel) {
        rcLabel.textContent = isSunny ? "SunnyRework" : "RC";
    }
    if (profileGraphLegendLnEl) {
        profileGraphLegendLnEl.hidden = isSunny;
    }
}

export function clearProfileGraph() {
    if (!profileGraphWrapEl) {
        return;
    }
    profileGraphWrapEl.hidden = true;
    if (profileGraphRcLineEl) profileGraphRcLineEl.setAttribute("d", "");
    if (profileGraphRcFillEl) profileGraphRcFillEl.setAttribute("d", "");
    if (profileGraphRcUpperEl) profileGraphRcUpperEl.setAttribute("d", "");
    if (profileGraphRcLowerEl) profileGraphRcLowerEl.setAttribute("d", "");
    if (profileGraphLnLineEl) profileGraphLnLineEl.setAttribute("d", "");
    setGraphLegend("FixedDensity");
}

export function renderProfileGraph(profileGraph) {
    const times = Array.isArray(profileGraph?.times) ? profileGraph.times : [];
    const isSunnyProfile = profileGraph?.source === "SunnyRework";
    const sunnyValues = Array.isArray(profileGraph?.values) ? profileGraph.values : [];
    const rcValues = Array.isArray(profileGraph?.rcCenterValues)
        ? profileGraph.rcCenterValues
        : (Array.isArray(profileGraph?.rcValues) ? profileGraph.rcValues : []);
    const rcLowerValues = Array.isArray(profileGraph?.rcLowerValues)
        ? profileGraph.rcLowerValues
        : rcValues;
    const rcUpperValues = Array.isArray(profileGraph?.rcUpperValues)
        ? profileGraph.rcUpperValues
        : rcValues;
    const lnValues = Array.isArray(profileGraph?.lnValues) ? profileGraph.lnValues : [];
    const length = isSunnyProfile
        ? Math.min(times.length, sunnyValues.length)
        : Math.min(times.length, rcValues.length, rcLowerValues.length, rcUpperValues.length, lnValues.length);

    if (!profileGraphWrapEl || length < 2) {
        clearProfileGraph();
        return false;
    }

    const safeTimes = times.slice(0, length);
    const safeSunnyValues = sunnyValues.slice(0, length);
    const safeRcValues = rcValues.slice(0, length);
    const safeRcLowerValues = rcLowerValues.slice(0, length);
    const safeRcUpperValues = rcUpperValues.slice(0, length);
    const safeLnValues = lnValues.slice(0, length);
    let maxValue = Math.max(0, Number(profileGraph.maxValue) || 0);
    for (const value of (isSunnyProfile ? safeSunnyValues : safeRcValues)) {
        maxValue = Math.max(maxValue, Number(value) || 0);
    }
    for (const value of (isSunnyProfile ? [] : safeRcUpperValues)) {
        maxValue = Math.max(maxValue, Number(value) || 0);
    }
    for (const value of (isSunnyProfile ? [] : safeLnValues)) {
        maxValue = Math.max(maxValue, Number(value) || 0);
    }

    profileGraphRcLineEl?.setAttribute("d", pathForSeries(safeTimes, isSunnyProfile ? safeSunnyValues : safeRcValues, maxValue));
    profileGraphRcFillEl?.setAttribute("d", isSunnyProfile ? "" : areaPathForSeries(safeTimes, safeRcLowerValues, safeRcUpperValues, maxValue));
    profileGraphRcUpperEl?.setAttribute("d", isSunnyProfile ? "" : pathForSeries(safeTimes, safeRcUpperValues, maxValue));
    profileGraphRcLowerEl?.setAttribute("d", isSunnyProfile ? "" : pathForSeries(safeTimes, safeRcLowerValues, maxValue));
    profileGraphLnLineEl?.setAttribute("d", isSunnyProfile ? "" : pathForSeries(safeTimes, safeLnValues, maxValue));
    setGraphLegend(isSunnyProfile ? "SunnyRework" : "FixedDensity");
    if (profileGraphDurationEl) {
        profileGraphDurationEl.textContent = formatTime(safeTimes[safeTimes.length - 1] - safeTimes[0]);
    }
    renderYAxisLabels(maxValue);

    profileGraphWrapEl.hidden = false;
    profileGraphWrapEl.classList.remove("profile-graph-enter");
    void profileGraphWrapEl.offsetWidth;
    profileGraphWrapEl.classList.add("profile-graph-enter");
    return true;
}
