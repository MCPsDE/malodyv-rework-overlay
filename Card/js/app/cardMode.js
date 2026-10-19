export function isFourKeyMania(parsedInfo) {
    return String(parsedInfo?.gameMode ?? "0") === "3"
        && Number(parsedInfo?.columnCount) === 4;
}

export function resolveDifficultyText({
    supportsFixedDensity,
    srText,
    fixedDensityDifficultyText,
    resolvedNumericDifficulty,
    resolvedEstDiff,
    sunnyEstDiff,
}) {
    if (!supportsFixedDensity) {
        return sunnyEstDiff || "";
    }

    if (srText === "Fixed Density v1" && fixedDensityDifficultyText) {
        return fixedDensityDifficultyText;
    }

    if (Number.isFinite(resolvedNumericDifficulty) && resolvedNumericDifficulty >= 18.5) {
        if (!resolvedEstDiff) {
            return "> Cloverwisp Theta high";
        }
        const parts = resolvedEstDiff.split("||");
        parts[0] = "> Cloverwisp Theta high";
        return parts.join("||");
    }

    return resolvedEstDiff || "";
}
