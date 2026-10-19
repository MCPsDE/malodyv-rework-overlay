using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MalodyV.ReworkOverlay;

internal static class MapLoader
{
    private sealed record ChartSource(byte[] Data, string Name, string ContainerPath, byte[]? Background);
    private sealed record TempoPoint(double Beat, double Bpm, int Meter);
    private sealed record HitObject(double Time, int Column, bool Hold, double EndTime);

    public static async Task<MapPayload> LoadAsync(SelectionMessage selection, OverlaySettings settings)
    {
        var sourcePath = Path.GetFullPath(selection.Path ?? throw new InvalidDataException("Missing chart path."));
        var source = await ReadChartSourceAsync(sourcePath, selection.Index, selection.Version);
        var extension = Path.GetExtension(source.Name).ToLowerInvariant();
        string osuText;
        byte[]? background = source.Background is { Length: > 0 } suppliedBackground
            ? suppliedBackground
            : null;
        string title;

        if (extension == ".osu")
        {
            osuText = DecodeText(source.Data);
            title = ReadOsuTitle(osuText);
            background ??= ReadOsuBackground(osuText, source.ContainerPath);
        }
        else if (extension == ".mc")
        {
            using var document = JsonDocument.Parse(source.Data);
            var chart = ConvertMalody(document.RootElement, source.Name, background);
            osuText = chart.OsuText;
            title = chart.Title;
            background ??= chart.Background;
            if (background is null)
            {
                var backgroundName = ReadMcBackgroundName(source.Data);
                var backgroundPath = Path.Combine(Path.GetDirectoryName(source.ContainerPath)!, Path.GetFileName(backgroundName ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(backgroundName) && File.Exists(backgroundPath))
                    background = await File.ReadAllBytesAsync(backgroundPath);
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported chart format: {extension}");
        }

        osuText = ChartModTransformer.Apply(osuText, selection.Mods, selection.PlayMode, selection.RandomSeed,
            supportsHoldConversion: !extension.Equals(".osu", StringComparison.OrdinalIgnoreCase));
        var speedRate = double.IsFinite(selection.SpeedRate) && selection.SpeedRate > 0 ? selection.SpeedRate : 1d;
            var metrics = await CalculateReferenceMetricsAsync(osuText, speedRate);
        var identity = string.IsNullOrWhiteSpace(selection.Identity)
            ? $"{sourcePath}|{selection.Index}|{selection.Mods}|{speedRate:0.#####}"
            : selection.Identity!;
        var analysisSignature = $"{selection.Mods}|{speedRate.ToString("0.#####", CultureInfo.InvariantCulture)}";
        return new MapPayload(identity, sourcePath, selection.Index, osuText, background, metrics, title,
            selection.Mods, speedRate, analysisSignature);
    }

    private static async Task<ChartSource> ReadChartSourceAsync(string path, uint index, string? selectedVersion)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".osu" or ".mc")
            return new ChartSource(await File.ReadAllBytesAsync(path), Path.GetFileName(path), path, null);

        if (extension is not (".mcz" or ".zip" or ".osz"))
            throw new InvalidDataException($"Unsupported chart container: {extension}");

        await using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var candidates = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                (Path.GetExtension(entry.Name).Equals(".osu", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetExtension(entry.Name).Equals(".mc", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidDataException("No .osu or .mc chart was found in the selected archive.");
        ZipArchiveEntry? selected = null;
        byte[]? chartBytes = null;
        if (!string.IsNullOrWhiteSpace(selectedVersion))
        {
            var wantedVersion = NormalizeVersion(selectedVersion);
            foreach (var candidate in candidates)
            {
                await using var candidateStream = candidate.Open();
                using var candidateBuffer = new MemoryStream();
                await candidateStream.CopyToAsync(candidateBuffer);
                var candidateBytes = candidateBuffer.ToArray();
                var candidateVersion = Path.GetExtension(candidate.Name).Equals(".mc", StringComparison.OrdinalIgnoreCase)
                    ? ReadMcVersion(candidateBytes)
                    : ReadSectionValue(DecodeText(candidateBytes), "Metadata", "Version");
                if (string.Equals(NormalizeVersion(candidateVersion), wantedVersion, StringComparison.OrdinalIgnoreCase))
                {
                    selected = candidate;
                    chartBytes = candidateBytes;
                    break;
                }
            }
            if (selected is null)
                throw new InvalidDataException($"Difficulty '{selectedVersion}' was not found inside the selected archive.");
        }
        else
        {
            if (index >= candidates.Length)
                throw new InvalidDataException($"Difficulty index {index} is outside the archive's {candidates.Length} chart entries.");
            selected = candidates[(int)index];
            await using var chartStream = selected.Open();
            using var buffer = new MemoryStream();
            await chartStream.CopyToAsync(buffer);
            chartBytes = buffer.ToArray();
        }

        byte[]? background = null;
        var preferred = Path.GetExtension(selected.Name).Equals(".osu", StringComparison.OrdinalIgnoreCase)
            ? ReadOsuBackgroundName(DecodeText(chartBytes!))
            : ReadMcBackgroundName(chartBytes!);
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = archive.Entries.FirstOrDefault(entry =>
                Path.GetFileName(entry.FullName).Equals(Path.GetFileName(preferred), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                await using var imageStream = match.Open();
                using var imageBuffer = new MemoryStream();
                await imageStream.CopyToAsync(imageBuffer);
                background = imageBuffer.ToArray();
            }
        }
        return new ChartSource(chartBytes!, selected.Name, path, background);
    }

    private static (string OsuText, string Title, byte[]? Background) ConvertMalody(
        JsonElement root, string filename, byte[]? archiveBackground)
    {
        var meta = GetProperty(root, "meta");
        var song = GetProperty(meta, "song");
        var modeExt = GetProperty(meta, "mode_ext");
        var title = GetString(song, "title") ?? GetString(song, "titleorg") ?? "Unknown Title";
        var artist = GetString(song, "artist") ?? GetString(song, "artistorg") ?? "Unknown Artist";
        var creator = GetString(meta, "creator") ?? "Unknown";
        var version = GetString(meta, "version") ?? "Converted";
        var rawNotes = GetProperty(root, "note");
        if (rawNotes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The selected Malody chart has no note array.");
        if (rawNotes.EnumerateArray().Any(note => note.ValueKind == JsonValueKind.Array))
            throw new InvalidDataException("The selected chart uses an unsupported legacy note format.");

        var mode = GetInt(meta, "mode", 0);
        var columnCount = GetInt(modeExt, "column", 0);
        if (columnCount <= 0)
            columnCount = rawNotes.EnumerateArray()
                .Where(note => GetProperty(note, "column").ValueKind == JsonValueKind.Number)
                .Select(note => GetInt(note, "column", -1) + 1)
                .DefaultIfEmpty(0)
                .Max();
        if (columnCount is < 0 or > 18)
            throw new InvalidDataException($"Unsupported Malody mode/key count ({mode}/{columnCount}).");
        if (columnCount == 0)
            columnCount = 1;
        var osuMode = mode switch
        {
            0 => 3,
            3 => 2,
            5 => 1,
            _ => 0,
        };

        var tempo = BuildTempoMap(GetProperty(root, "time"));
        var audioOffset = rawNotes.EnumerateArray()
            .Where(note => GetInt(note, "type", 0) == 1 && GetProperty(note, "column").ValueKind == JsonValueKind.Undefined)
            .Select(note => GetDouble(note, "offset", 0))
            .FirstOrDefault();
        var rows = new List<HitObject>();
        foreach (var note in rawNotes.EnumerateArray())
        {
            if (GetProperty(note, "column").ValueKind != JsonValueKind.Number)
                continue;
            var column = GetInt(note, "column", -1);
            var beat = ParseBeat(GetProperty(note, "beat"));
            if (column < 0 || column >= columnCount || beat is null)
                continue;
            var time = ToMilliseconds(tempo, beat.Value) - audioOffset;
            var endBeat = ParseBeat(GetProperty(note, "endbeat"));
            var isHold = endBeat is not null && endBeat > beat;
            var endTime = isHold ? ToMilliseconds(tempo, endBeat!.Value) - audioOffset : time;
            rows.Add(new HitObject(time, column, isHold && osuMode == 3, endTime));
        }
        if (rows.Count == 0)
            throw new InvalidDataException("The selected Malody chart has no supported notes.");
        rows.Sort((left, right) => left.Time != right.Time
            ? left.Time.CompareTo(right.Time)
            : left.Column.CompareTo(right.Column));

        var backgroundName = Path.GetFileName(GetString(meta, "background") ?? string.Empty);
        var background = archiveBackground;
        var osu = BuildOsu(title, artist, creator, version, columnCount, osuMode,
            GetString(song, "file") ?? "audio.mp3", backgroundName, tempo,
            GetProperty(root, "effect"), rows, audioOffset);
        return (osu, $"{artist} - {title} [{version}] // {creator}", background);
    }

    private static string BuildOsu(string title, string artist, string creator, string version, int columns, int osuMode,
        string audioName, string backgroundName, IReadOnlyList<TempoPoint> tempo, JsonElement effects,
        IReadOnlyList<HitObject> rows, double audioOffset)
    {
        var lines = new List<string>
        {
            "osu file format v14", "", "[General]", $"AudioFilename: {Path.GetFileName(audioName)}",
            $"AudioLeadIn: {Format(audioOffset)}", "PreviewTime: -1", "Countdown: 0", "SampleSet: Soft",
            "StackLeniency: 0.7", $"Mode: {osuMode}", "LetterboxInBreaks: 0", "SpecialStyle: 0", "WidescreenStoryboard: 0",
            "", "[Editor]", "Bookmarks: ", "DistanceSpacing: 1", "BeatDivisor: 4", "GridSize: 4", "TimelineZoom: 1",
            "", "[Metadata]", $"Title:{title}", $"TitleUnicode:{title}", $"Artist:{artist}", $"ArtistUnicode:{artist}",
            $"Creator:{creator}", $"Version:{version}", "Source:", "Tags:malody converted", "BeatmapID:0", "BeatmapSetID:-1",
            "", "[Difficulty]", "HPDrainRate:8", $"CircleSize:{(osuMode == 3 ? columns : 4)}", "OverallDifficulty:8", "ApproachRate:5",
            "SliderMultiplier:1.4", "SliderTickRate:1", "", "[Events]", "//Background and Video events",
        };
        if (!string.IsNullOrWhiteSpace(backgroundName))
            lines.Add($"0,0,\"{backgroundName}\",0,0");
        lines.AddRange(["//Break Periods", "//Storyboard Layer 0 (Background)", "//Storyboard Layer 1 (Fail)",
            "//Storyboard Layer 2 (Pass)", "//Storyboard Layer 3 (Foreground)", "//Storyboard Sound Samples", "", "[TimingPoints]"]);

        var timingPoints = tempo.Select(point =>
            (Time: ToMilliseconds(tempo, point.Beat) - audioOffset, BeatLength: 60000d / point.Bpm, point.Meter, Inherited: 1)).ToList();
        if (effects.ValueKind == JsonValueKind.Array)
        {
            foreach (var effect in effects.EnumerateArray())
            {
                var scroll = GetDouble(effect, "scroll", 0);
                var beat = ParseBeat(GetProperty(effect, "beat"));
                if (scroll > 0 && beat is not null)
                    timingPoints.Add((ToMilliseconds(tempo, beat.Value) - audioOffset, -100d / scroll, 4, 0));
            }
        }
        foreach (var point in timingPoints.OrderBy(point => point.Time))
            lines.Add(string.Join(',', Format(point.Time), Format(point.BeatLength), point.Meter.ToString(CultureInfo.InvariantCulture),
                "2", "0", "100", point.Inherited.ToString(CultureInfo.InvariantCulture), "0"));

        lines.AddRange(["", "[HitObjects]"]);
        foreach (var row in rows)
        {
            var x = osuMode switch
            {
                1 => 256,
                3 => Math.Clamp((int)Math.Round(((row.Column * 2d + 1) / (columns * 2d)) * 512), 0, 511),
                _ => columns > 1
                    ? Math.Clamp((int)Math.Round(((row.Column * 2d + 1) / (columns * 2d)) * 512), 0, 511)
                    : Math.Clamp(row.Column, 0, 511),
            };
            lines.Add(row.Hold && osuMode == 3
                ? $"{x},192,{Format(row.Time)},128,0,{Format(row.EndTime)}:0:0:0:0:"
                : $"{x},192,{Format(row.Time)},1,0,0:0:0:0:0:");
        }
        return string.Join('\n', lines) + "\n";
    }

    private static IReadOnlyList<TempoPoint> BuildTempoMap(JsonElement timeList)
    {
        var points = new List<TempoPoint>();
        if (timeList.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in timeList.EnumerateArray())
            {
                var beat = ParseBeat(GetProperty(entry, "beat"));
                var bpm = GetDouble(entry, "bpm", 0);
                if (beat is null || bpm <= 0)
                    continue;
                points.Add(new TempoPoint(beat.Value, bpm, Math.Max(1, GetInt(entry, "signature", 4))));
            }
        }
        points.Sort((left, right) => left.Beat.CompareTo(right.Beat));
        var deduped = new List<TempoPoint>();
        foreach (var point in points)
        {
            if (deduped.Count > 0 && Math.Abs(deduped[^1].Beat - point.Beat) < 1e-9)
                deduped[^1] = point;
            else
                deduped.Add(point);
        }
        if (deduped.Count == 0)
            deduped.Add(new TempoPoint(0, 120, 4));
        if (Math.Abs(deduped[0].Beat) > 1e-9)
            deduped.Insert(0, new TempoPoint(0, deduped[0].Bpm, deduped[0].Meter));
        return deduped;
    }

    private static double ToMilliseconds(IReadOnlyList<TempoPoint> points, double beat)
    {
        var elapsed = 0d;
        for (var index = 1; index < points.Count; index++)
        {
            var previous = points[index - 1];
            var next = points[index];
            if (beat <= next.Beat)
                return elapsed + (beat - previous.Beat) * 60000d / previous.Bpm;
            elapsed += (next.Beat - previous.Beat) * 60000d / previous.Bpm;
        }
        var last = points[^1];
        return elapsed + (beat - last.Beat) * 60000d / last.Bpm;
    }

    private static double? ParseBeat(JsonElement beat)
    {
        if (beat.ValueKind != JsonValueKind.Array || beat.GetArrayLength() != 3)
            return null;
        var values = beat.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN).ToArray();
        if (values.Any(value => !double.IsFinite(value)) || values[2] <= 0)
            return null;
        return values[0] + values[1] / values[2];
    }

    private static string ReadOsuTitle(string text) =>
        ReadSectionValue(text, "Metadata", "Title") ?? "Unknown chart";

    private static string? ReadOsuBackgroundName(string text)
    {
        var inEvents = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
            if (!inEvents || !line.StartsWith("0,0,", StringComparison.Ordinal))
                continue;
            var start = line.IndexOf('"');
            var end = start >= 0 ? line.IndexOf('"', start + 1) : -1;
            if (end > start)
                return line[(start + 1)..end];
        }
        return null;
    }

    private static byte[]? ReadOsuBackground(string text, string chartPath)
    {
        var name = ReadOsuBackgroundName(text);
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(chartPath) ?? string.Empty, name));
        var prefix = Path.GetFullPath(Path.GetDirectoryName(chartPath) ?? string.Empty) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
            ? File.ReadAllBytes(path)
            : null;
    }

    private static string? ReadMcBackgroundName(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return GetString(GetProperty(document.RootElement, "meta"), "background");
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadMcVersion(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return GetString(GetProperty(document.RootElement, "meta"), "version");
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeVersion(string? version) =>
        System.Text.RegularExpressions.Regex.Replace(version?.Trim() ?? string.Empty, @"^\d+K\s*-\s*", string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? ReadSectionValue(string text, string sectionName, string key)
    {
        var currentSection = string.Empty;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                currentSection = line[1..^1];
            else if (currentSection.Equals(sectionName, StringComparison.OrdinalIgnoreCase) && line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                return line[(key.Length + 1)..].Trim();
        }
        return null;
    }

    private static async Task<string> CalculateReferenceMetricsAsync(string osuText, double speedRate)
    {
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "Data", "ReferenceMetrics.py");
            var toolkitPath = Path.Combine(AppContext.BaseDirectory, "nonebot_plugin_osumania_toolkit");
            if (!File.Exists(script) || !Directory.Exists(Path.Combine(toolkitPath, "algorithm")))
                return "{\"sunnyRework\":null,\"sunnyEstDiff\":null,\"osuSr\":null}";
            var bundledPython = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
            var startInfo = new ProcessStartInfo(File.Exists(bundledPython) ? bundledPython : "python")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add(toolkitPath);
            startInfo.ArgumentList.Add(speedRate.ToString("0.#####", CultureInfo.InvariantCulture));
            using var process = Process.Start(startInfo);
            if (process is null)
                return "{}";
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(osuText);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                return "{\"sunnyRework\":null,\"sunnyEstDiff\":null,\"osuSr\":null}";
            }
            var output = (await outputTask).Trim();
            _ = await errorTask;
            return process.ExitCode == 0 && output.StartsWith('{') ? output : "{\"sunnyRework\":null,\"sunnyEstDiff\":null,\"osuSr\":null}";
        }
        catch
        {
            return "{\"sunnyRework\":null,\"sunnyEstDiff\":null,\"osuSr\":null}";
        }
    }

    private static string DecodeText(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;
    private static string? GetString(JsonElement element, string name)
    {
        var value = GetProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }
    private static int GetInt(JsonElement element, string name, int fallback)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed :
            value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out parsed) ? parsed : fallback;
    }
    private static double GetDouble(JsonElement element, string name, double fallback)
    {
        var value = GetProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var parsed) ? parsed :
            value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
    }
}
