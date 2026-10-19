import { runSunnyEstimatorFromText } from "../estimator/sunnyEstimator.js";

function readSections(osuText) {
    const sections = new Map();
    let section = "";
    for (const rawLine of String(osuText || "").split(/\r?\n/)) {
        const line = rawLine.trim();
        if (line.startsWith("[") && line.endsWith("]")) {
            section = line.slice(1, -1);
            if (!sections.has(section)) sections.set(section, []);
        } else if (section) {
            sections.get(section)?.push(line);
        }
    }
    return sections;
}

function parseOfficialObjects(osuText, speedRate) {
    const sections = readSections(osuText);
    const difficulty = sections.get("Difficulty") || [];
    const general = sections.get("General") || [];
    const mode = Number(general.find((line) => line.startsWith("Mode:"))?.split(":")[1] ?? 0);
    const keys = Math.max(1, Math.floor(Number(difficulty.find((line) => line.startsWith("CircleSize:"))?.split(":")[1] ?? 4)));
    if (mode !== 3) return { mode, keys, objects: [] };

    const objects = [];
    for (const line of sections.get("HitObjects") || []) {
        const parts = line.split(",");
        if (parts.length < 5) continue;
        const x = Number(parts[0]);
        const start = Number(parts[2]);
        const type = Number(parts[3]);
        if (![x, start, type].every(Number.isFinite)) continue;
        const column = Math.max(0, Math.min(keys - 1, Math.floor(x * keys / 512)));
        let end = start;
        if ((type & 128) !== 0 && parts[5]) {
            const holdEnd = Number(parts[5].split(":")[0]);
            if (Number.isFinite(holdEnd)) end = holdEnd;
        }
        objects.push({
            start: start / speedRate,
            end: end / speedRate,
            column,
        });
    }
    objects.sort((left, right) => left.start - right.start);
    return { mode, keys, objects };
}

function logistic(value, midpointOffset, multiplier, maxValue = 1) {
    return maxValue / (1 + Math.exp(multiplier * (midpointOffset - value)));
}

function bigger(left, right, tolerance = 1) {
    return left > right + tolerance;
}

function calculateOfficialStar(osuText, speedRate = 1) {
    const parsed = parseOfficialObjects(osuText, Math.max(0.001, Number(speedRate) || 1));
    if (parsed.mode !== 3 || parsed.objects.length === 0) return null;

    const previousByColumn = Array(parsed.keys).fill(null);
    const difficultyObjects = parsed.objects.map((object, index) => {
        const previous = index > 0 ? parsed.objects[index - 1] : null;
        const value = {
            ...object,
            delta: previous ? object.start - previous.start : 0,
            previousByColumn: [...previousByColumn],
        };
        previousByColumn[object.column] = value;
        return value;
    });

    const individual = Array(parsed.keys).fill(0);
    let overall = 1;
    const sectionLength = 400;
    const peaks = [];
    let sectionPeak = 0;
    let sectionEnd = Math.ceil(difficultyObjects[0].start / sectionLength) * sectionLength;

    for (const current of difficultyObjects) {
        while (current.start > sectionEnd) {
            peaks.push(sectionPeak);
            sectionPeak = 0;
            sectionEnd += sectionLength;
        }

        const elapsed = current.delta / 1000;
        for (let column = 0; column < individual.length; column += 1) {
            individual[column] *= 0.125 ** elapsed;
        }
        overall *= 0.30 ** elapsed;

        let holdFactor = 1;
        let closestEnd = Math.abs(current.end - current.start);
        let overlapping = false;
        for (const previous of current.previousByColumn) {
            if (!previous) continue;
            if (bigger(previous.end, current.end) && bigger(current.start, previous.start)) {
                holdFactor = 1.25;
            }
            if (bigger(previous.end, current.start)
                && bigger(current.end, previous.end)
                && bigger(current.start, previous.start)) {
                overlapping = true;
            }
            closestEnd = Math.min(closestEnd, Math.abs(current.end - previous.end));
        }

        const holdAddition = overlapping ? logistic(closestEnd, 30, 0.27) : 0;
        individual[current.column] += 2 * holdFactor;
        overall += (1 + holdAddition) * holdFactor;
        sectionPeak = Math.max(sectionPeak, Math.max(...individual) + overall);
    }
    peaks.push(sectionPeak);
    peaks.sort((left, right) => right - left);

    let result = 0;
    let weight = 1;
    for (const peak of peaks) {
        result += peak * weight;
        weight *= 0.9;
    }
    return result * 0.018;
}

export function calculateLocalReferenceMetrics(osuText, pipelineResult, speedRate = 1) {
    let sunny = null;
    try {
        sunny = runSunnyEstimatorFromText(osuText, { speedRate });
    } catch {
        sunny = null;
    }

    const sunnyRework = Number.isFinite(Number(sunny?.star))
        ? Number(sunny.star)
        : Number.isFinite(Number(pipelineResult?.sunnyStar))
            ? Number(pipelineResult.sunnyStar)
            : Number.isFinite(Number(pipelineResult?.rework?.star))
                ? Number(pipelineResult.rework.star)
                : null;
    const osuSr = calculateOfficialStar(osuText, speedRate);
    return {
        sunnyRework,
        sunnyEstDiff: typeof sunny?.estDiff === "string" ? sunny.estDiff : null,
        osuSr,
    };
}

