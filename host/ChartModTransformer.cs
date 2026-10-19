using System.Globalization;

namespace MalodyV.ReworkOverlay;

internal static class ChartModTransformer
{
    private const uint Flip = 1u << 2;
    private const uint Invert = 1u << 11;
    private const uint Thumb = 1u << 12;
    private const uint Less = 1u << 13;
    private const uint Taps = 1u << 14;
    private const uint Holds = 1u << 15;

    private sealed record TempoPoint(double Time, double BeatLength, double BeatAtStart);

    private sealed class HitObject
    {
        public required int LineIndex { get; init; }
        public required string OriginalLine { get; init; }
        public required int OriginalType { get; init; }
        public required int X { get; init; }
        public required int Column { get; set; }
        public required double StartTime { get; init; }
        public required double StartBeat { get; set; }
        public required string SampleTail { get; init; }
        public double EndTime { get; set; }
        public double EndBeat { get; set; }
        public int Type { get; set; }
        public bool Removed { get; set; }
        public bool IsHold => (Type & 128) != 0;
    }

    public static string Apply(string osuText, uint mods, int playMode, int randomSeed, bool supportsHoldConversion)
    {
        const uint contentMods = 2u | Flip | Invert | Thumb | Less | Taps | Holds;
        if ((mods & contentMods) == 0)
            return osuText;

        var lines = osuText.Split('\n');
        var section = string.Empty;
        var mode = -1;
        var columns = 0;
        var timingLines = new List<string>();
        var hitObjects = new List<HitObject>();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (section.Equals("General", StringComparison.OrdinalIgnoreCase) && line.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out mode);
            else if (section.Equals("Difficulty", StringComparison.OrdinalIgnoreCase) && line.StartsWith("CircleSize:", StringComparison.OrdinalIgnoreCase))
                columns = ParseInt(line[(line.IndexOf(':') + 1)..], 0);
            else if (section.Equals("TimingPoints", StringComparison.OrdinalIgnoreCase) && line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                timingLines.Add(line);
            else if (section.Equals("HitObjects", StringComparison.OrdinalIgnoreCase) && line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            {
                var note = ParseHitObject(lines[index], index, columns);
                if (note is not null)
                    hitObjects.Add(note);
            }
        }

        if (mode != 3 || columns < 2 || hitObjects.Count == 0)
            return osuText;

        var tempo = BuildTempo(timingLines);
        foreach (var note in hitObjects)
        {
            note.StartBeat = BeatAt(tempo, note.StartTime);
            note.EndBeat = BeatAt(tempo, note.EndTime);
        }

        if ((mods & Less) != 0)
            ApplyLess(hitObjects, tempo);
        if ((mods & Thumb) != 0)
            ApplyThumb(hitObjects, columns);
        if ((mods & Taps) != 0)
            ApplyTaps(hitObjects);
        else if ((mods & Holds) != 0 && supportsHoldConversion)
            ApplyHolds(hitObjects, tempo);

        ApplyLaneMapping(hitObjects, columns, playMode, mods, randomSeed);
        foreach (var note in hitObjects.Where(note => !note.Removed))
            lines[note.LineIndex] = FormatHitObject(note, columns);
        foreach (var note in hitObjects.Where(note => note.Removed))
            lines[note.LineIndex] = string.Empty;

        return string.Join('\n', lines);
    }

    private static HitObject? ParseHitObject(string line, int lineIndex, int columns)
    {
        var fields = line.Trim().Split(',');
        if (fields.Length < 5 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var startTime)
            || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type)
            || (type & (1 | 128)) == 0)
            return null;

        var isHold = (type & 128) != 0;
        var sample = fields.Length > 5 ? fields[5] : "0:0:0:0:";
        var colon = sample.IndexOf(':');
        var sampleTail = isHold && colon >= 0 ? sample[(colon + 1)..] : sample;
        var endTime = startTime;
        if (isHold && colon > 0 && double.TryParse(sample[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEnd))
            endTime = parsedEnd;
        return new HitObject
        {
            LineIndex = lineIndex,
            OriginalLine = line,
            OriginalType = type,
            X = x,
            Column = Math.Clamp((int)Math.Floor(x * columns / 512d), 0, columns - 1),
            StartTime = startTime,
            EndTime = endTime,
            StartBeat = 0,
            EndBeat = 0,
            Type = type,
            SampleTail = string.IsNullOrEmpty(sampleTail) ? "0:0:0:0:" : sampleTail,
        };
    }

    private static List<TempoPoint> BuildTempo(IReadOnlyList<string> rows)
    {
        var points = new List<(double Time, double BeatLength)>();
        foreach (var row in rows)
        {
            var fields = row.Split(',');
            if (fields.Length < 7
                || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                || !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var beatLength)
                || beatLength <= 0
                || !int.TryParse(fields[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uninherited)
                || uninherited != 1)
                continue;
            points.Add((time, beatLength));
        }
        points.Sort((left, right) => left.Time.CompareTo(right.Time));
        if (points.Count == 0)
            points.Add((0, 500));

        var result = new List<TempoPoint>(points.Count);
        var beat = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            if (index > 0)
                beat += (points[index].Time - points[index - 1].Time) / points[index - 1].BeatLength;
            result.Add(new TempoPoint(points[index].Time, points[index].BeatLength, beat));
        }
        return result;
    }

    private static double BeatAt(IReadOnlyList<TempoPoint> tempo, double time)
    {
        var point = tempo[0];
        for (var index = 1; index < tempo.Count; index++)
        {
            if (tempo[index].Time > time)
                break;
            point = tempo[index];
        }
        return point.BeatAtStart + (time - point.Time) / point.BeatLength;
    }

    private static double TimeAt(IReadOnlyList<TempoPoint> tempo, double beat)
    {
        var point = tempo[0];
        for (var index = 1; index < tempo.Count; index++)
        {
            if (tempo[index].BeatAtStart > beat)
                break;
            point = tempo[index];
        }
        return point.Time + (beat - point.BeatAtStart) * point.BeatLength;
    }

    private static void ApplyLess(IReadOnlyList<HitObject> notes, IReadOnlyList<TempoPoint> tempo)
    {
        var bpm = 60000d / tempo[0].BeatLength;
        var denominatorLimit = bpm < 150 ? 4 : bpm < 200 ? 3 : 2;
        foreach (var note in notes)
        {
            if (ReducedBeatDenominator(note.StartBeat) > denominatorLimit)
                note.Removed = true;
        }
    }

    private static int ReducedBeatDenominator(double beat)
    {
        var fraction = beat - Math.Floor(beat);
        if (fraction < 1e-5 || 1 - fraction < 1e-5)
            return 1;
        for (var denominator = 2; denominator <= 288; denominator++)
        {
            var numerator = (int)Math.Round(fraction * denominator);
            if (Math.Abs(fraction * denominator - numerator) <= 1e-3)
                return denominator / GreatestCommonDivisor(numerator, denominator);
        }
        return 289;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
            (left, right) = (right, left % right);
        return Math.Max(1, left);
    }

    private static void ApplyThumb(IReadOnlyList<HitObject> notes, int columns)
    {
        var random = new Random(columns);
        var pending = new HitObject?[columns];
        var currentTime = int.MinValue;
        foreach (var note in notes.Where(note => !note.Removed).OrderBy(note => note.StartTime).ThenBy(note => note.Column))
        {
            var time = (int)Math.Round(note.StartTime);
            if (currentTime == int.MinValue)
                currentTime = time;
            if (time == currentTime)
            {
                pending[note.Column] = note;
                continue;
            }
            RemoveThumbExcess(pending, random);
            for (var column = 0; column < columns; column++)
            {
                if (pending[column] is { } active && active.EndTime <= time)
                    pending[column] = null;
            }
            currentTime = time;
            pending[note.Column] = note;
        }
        RemoveThumbExcess(pending, random);
    }

    private static void RemoveThumbExcess(HitObject?[] pending, Random random)
    {
        var taps = pending.Count(note => note is not null && !note.IsHold);
        var holds = pending.Count(note => note is not null && note.IsHold);
        if (taps + holds <= 2)
            return;
        if (holds == 0)
        {
            while (taps > 2)
                RemoveRandomPending(pending, random, note => !note.IsHold, ref taps);
        }
        else if (holds == 1)
        {
            while (taps > 1)
                RemoveRandomPending(pending, random, note => !note.IsHold, ref taps);
        }
        else
        {
            while (taps > 1)
                RemoveRandomPending(pending, random, note => !note.IsHold, ref taps);
            while (taps + holds > 2)
            {
                var column = random.Next(pending.Length);
                if (pending[column] is not { IsHold: true } note || random.NextDouble() > 0.5)
                    continue;
                note.Removed = true;
                pending[column] = null;
                holds--;
            }
        }
    }

    private static void RemoveRandomPending(HitObject?[] pending, Random random, Func<HitObject, bool> predicate, ref int count)
    {
        while (true)
        {
            var column = random.Next(pending.Length);
            if (pending[column] is not { } note || !predicate(note))
                continue;
            note.Removed = true;
            pending[column] = null;
            count--;
            return;
        }
    }

    private static void ApplyTaps(IEnumerable<HitObject> notes)
    {
        foreach (var note in notes.Where(note => !note.Removed && note.IsHold))
        {
            note.Type = (note.Type & ~128) | 1;
            note.EndTime = note.StartTime;
        }
    }

    private static void ApplyHolds(IReadOnlyList<HitObject> notes, IReadOnlyList<TempoPoint> tempo)
    {
        var active = new HitObject?[notes.Select(note => note.Column).DefaultIfEmpty(0).Max() + 1];
        var lastBeat = 0d;
        foreach (var note in notes.Where(note => !note.Removed).OrderBy(note => note.StartTime).ThenBy(note => note.Column))
        {
            lastBeat = Math.Max(lastBeat, note.EndBeat);
            var previous = active[note.Column];
            if (previous is null)
            {
                active[note.Column] = note;
                continue;
            }
            if (note.StartTime - previous.EndTime < 100 || note.StartBeat - previous.EndBeat <= 0.25)
            {
                active[note.Column] = note;
                continue;
            }
            SetHold(previous, TimeAt(tempo, note.StartBeat - 0.25), note.StartBeat - 0.25);
            active[note.Column] = note;
        }

        foreach (var note in active.Where(note => note is not null).Cast<HitObject>())
        {
            if (Math.Abs(note.EndBeat - lastBeat) > 1e-8)
                SetHold(note, TimeAt(tempo, lastBeat), lastBeat);
        }
    }

    private static void SetHold(HitObject note, double endTime, double endBeat)
    {
        note.Type = (note.Type & ~1) | 128;
        note.EndTime = endTime;
        note.EndBeat = endBeat;
    }

    private static void ApplyLaneMapping(IReadOnlyList<HitObject> notes, int columns, int playMode, uint mods, int randomSeed)
    {
        var mapping = Enumerable.Range(0, columns).ToArray();
        if ((mods & 2) != 0)
        {
            var random = new Random(randomSeed);
            for (var index = 0; index < mapping.Length; index++)
            {
                var swapIndex = random.Next(0, index + 1);
                (mapping[index], mapping[swapIndex]) = (mapping[swapIndex], mapping[index]);
            }
        }
        else if (playMode == 9 && columns == 6)
        {
            if ((mods & Flip) != 0)
                mapping = [5, 4, 3, 0, 1, 2];
            else if ((mods & Invert) != 0)
                mapping = [2, 1, 0, 3, 4, 5];
            else
                mapping = [0, 1, 2, 5, 4, 3];
        }
        else if ((mods & Flip) != 0)
        {
            Array.Reverse(mapping);
        }
        foreach (var note in notes)
            note.Column = mapping[Math.Clamp(note.Column, 0, mapping.Length - 1)];
    }

    private static string FormatHitObject(HitObject note, int columns)
    {
        var fields = note.OriginalLine.Trim().Split(',');
        fields[0] = Math.Clamp((int)Math.Round((note.Column + 0.5) * 512d / columns), 0, 511)
            .ToString(CultureInfo.InvariantCulture);
        fields[3] = note.Type.ToString(CultureInfo.InvariantCulture);
        if (fields.Length > 5)
        {
            fields[5] = note.IsHold
                ? $"{Math.Round(note.EndTime).ToString(CultureInfo.InvariantCulture)}:{note.SampleTail}"
                : note.SampleTail;
        }
        return string.Join(',', fields);
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}
