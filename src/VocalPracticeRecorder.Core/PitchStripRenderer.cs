namespace VocalPracticeRecorder.Core;

/// <summary>
/// Pure-C# software BGRA renderer for the dedicated dark pitch strip stacked below exported MP4 videos.
/// Frame-exact with EditTimeline.KeptSegments (skips deleted sections cleanly) and requires zero UI thread
/// or native graphics dependencies, enabling fast headless export and automated CI verification.
/// </summary>
public sealed class PitchStripRenderer
{
    private const double WindowSeconds = 6.0;
    private const double SpanSemitones = 18.0;
    private const double MinCenterMidi = 38.0;
    private const double MaxCenterMidi = 88.0;

    // BGRA colors matching PitchMonitorControl
    private const uint ColBg = 0xFF181B22;
    private const uint ColBlackRow = 0xFF13161C;
    private const uint ColWhiteRow = 0xFF1B1F27;
    private const uint ColSide = 0xFF14171D;
    private const uint ColBlackKey = 0xFF0D0F13;
    private const uint ColWhiteTick = 0xFF3E4450;
    private const uint ColSep = 0xFF363C47;
    private const uint ColCLine = 0xFF474E5C;
    private const uint ColSemiLine = 0xFF232833;
    private const uint ColTimeGrid = 0xFF212630;
    private const uint ColCLabel = 0xFFD8DFEA;
    private const uint ColNoteLabel = 0xFF7A8290;
    private const uint ColSubtle = 0xFF8A8F98;
    private const uint ColSilent = 0xFF555C68;
    private const uint ColInTune = 0xFF3ECF8E;
    private const uint ColNear = 0xFFF5B73B;
    private const uint ColOff = 0xFFF06469;
    private const uint ColWhite = 0xFFFFFFFF;
    private const uint ColTrackBg = 0xFF333945;
    private const uint ColTrackTick = 0xFF858C99;

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int TotalFrames { get; }

    private readonly int _axisW;
    private readonly int _hudW;
    private readonly double _stepSec;
    private readonly float[] _outMidi; // stitched output-timeline pitch samples at _stepSec
    private readonly byte[] _frame;    // reusable BGRA buffer (Width * Height * 4)

    private double _centerMidi = 64.0;
    private double _targetCenterMidi = 64.0;

    /// <summary>Computes an even strip height proportional to the video height (never obscures video pixels).</summary>
    public static int StripHeightFor(int videoHeight) =>
        Math.Clamp(((int)Math.Round(videoHeight * 0.22)) & ~1, 96, 160);

    /// <summary>Counts the exact number of frames FFmpeg's kept-segment trim+concat produces.</summary>
    public static int CountOutputFrames(SessionInfo s)
    {
        int fps = Math.Max(1, s.Video.Fps);
        long total = 0;
        foreach (var seg in EditTimeline.KeptSegments(s))
        {
            long f0 = (long)Math.Round(seg.Start * fps);
            long f1 = (long)Math.Round(seg.End * fps);
            if (f1 > f0) total += f1 - f0;
        }
        return (int)Math.Max(1, total);
    }

    public PitchStripRenderer(SessionInfo s, VocalPitchTrack track, int width, int height)
    {
        Width = width & ~1;
        Height = height & ~1;
        Fps = Math.Max(1, s.Video.Fps);
        TotalFrames = CountOutputFrames(s);
        _axisW = Math.Clamp((int)Math.Round(Width * 0.055), 42, 60);
        _hudW = Math.Clamp((int)Math.Round(Width * 0.10), 76, 108);
        _stepSec = track.StepSeconds > 0 ? track.StepSeconds : VocalPitchTrack.DefaultStepSeconds;
        _frame = new byte[Width * Height * 4];

        // Stitch kept segments into contiguous output-timeline pitch samples so deleted cuts vanish seamlessly.
        var segs = EditTimeline.KeptSegments(s);
        double outDur = (double)TotalFrames / Fps;
        int steps = Math.Max(1, (int)Math.Ceiling(outDur / _stepSec) + 2);
        _outMidi = new float[steps];

        double outCursor = 0;
        var voiced = new List<float>();
        foreach (var seg in segs)
        {
            long f0 = (long)Math.Round(seg.Start * Fps);
            long f1 = (long)Math.Round(seg.End * Fps);
            double segLen = (f1 - f0) / (double)Fps;
            if (segLen <= 0) continue;

            int s0 = (int)Math.Floor(outCursor / _stepSec);
            int s1 = Math.Min(steps - 1, (int)Math.Ceiling((outCursor + segLen) / _stepSec));
            for (int i = Math.Max(0, s0); i <= s1; i++)
            {
                double localT = i * _stepSec - outCursor;
                if (localT < 0 || localT > segLen) continue;
                double srcTimelineT = (f0 / (double)Fps) + localT;
                double micT = srcTimelineT - s.VocalPlacement;
                int mi = (int)Math.Round(micT / _stepSec);
                if (mi >= 0 && mi < track.Midi.Length && track.Midi[mi] > 0)
                {
                    _outMidi[i] = track.Midi[mi];
                    voiced.Add(track.Midi[mi]);
                }
            }
            outCursor += segLen;
        }

        // Start centered on the median vocal pitch of the song so frame 0 is already cleanly framed.
        if (voiced.Count > 0)
        {
            voiced.Sort();
            _centerMidi = _targetCenterMidi = Math.Clamp(voiced[voiced.Count / 2], MinCenterMidi, MaxCenterMidi);
        }
    }

    /// <summary>Renders output frame <paramref name="frameIndex"/> into the reused BGRA byte array (Width * Height * 4).</summary>
    public byte[] RenderFrame(int frameIndex)
    {
        double now = frameIndex / (double)Fps;
        int curStep = Math.Clamp((int)Math.Round(now / _stepSec), 0, _outMidi.Length - 1);
        float curMidi = _outMidi[curStep];
        var live = curMidi > 0 ? PitchMath.FromFrequency(PitchMath.MidiToHz(curMidi), 0.96, 0.2f) : PitchReading.Silent;

        // Smooth auto-follow identical to PitchMonitorControl
        if (live.IsVoiced)
        {
            if (Math.Abs(live.MidiNote - _targetCenterMidi) > SpanSemitones * 0.28)
                _targetCenterMidi = Math.Clamp(live.MidiNote, MinCenterMidi, MaxCenterMidi);
        }
        double alpha = Math.Clamp(6.0 / Fps, 0.08, 0.28);
        _centerMidi += (_targetCenterMidi - _centerMidi) * alpha;

        int plotX = _axisW;
        int plotW = Math.Max(40, Width - _axisW - _hudW);
        int rightEdge = plotX + plotW;
        int liveX = rightEdge - 10;
        int topPad = 4, botPad = 4;
        double plotH = Math.Max(20, Height - topPad - botPad);
        double minMidi = _centerMidi - SpanSemitones * 0.5;
        double maxMidi = _centerMidi + SpanSemitones * 0.5;
        double pxPerSemi = plotH / SpanSemitones;

        double MidiToY(double m) => topPad + (maxMidi - m) * pxPerSemi;
        double TimeToX(double t) => liveX - (now - t) / WindowSeconds * (liveX - plotX - 4);

        FillRect(0, 0, Width, Height, ColBg);
        FillRect(0, 0, _axisW, Height, ColSide);
        FillRect(rightEdge, 0, _hudW, Height, ColSide);

        int loNote = (int)Math.Floor(minMidi) - 1;
        int hiNote = (int)Math.Ceiling(maxMidi) + 1;

        // 1. Semitone bands + piano axis ticks + horizontal guide lines
        for (int note = loNote; note <= hiNote; note++)
        {
            int yTop = (int)Math.Round(MidiToY(note + 0.5));
            int yBot = (int)Math.Round(MidiToY(note - 0.5));
            if (yBot < 0 || yTop >= Height) continue;
            int bt = Math.Max(0, yTop);
            int bb = Math.Min(Height, yBot);
            bool isBlack = PitchMath.IsBlackKey(note);
            FillRect(plotX, bt, plotW, bb - bt, isBlack ? ColBlackRow : ColWhiteRow);

            if (live.IsVoiced && live.NearestMidi == note)
                BlendRect(plotX, bt, plotW, bb - bt, ZoneColor(live.Zone), 28);

            if (isBlack) FillRect(_axisW - 10, bt, 9, Math.Max(1, bb - bt - 1), ColBlackKey);
            else FillRect(_axisW - 3, bt, 2, Math.Max(1, bb - bt - 1), ColWhiteTick);

            int yc = (int)Math.Round(MidiToY(note));
            if (yc >= 0 && yc < Height)
                FillRect(plotX, yc, plotW, 1, PitchMath.IsCNote(note) ? ColCLine : ColSemiLine);
        }

        // 2. Vertical 1-second scrolling grid lines
        for (double t = Math.Floor(now); t >= now - WindowSeconds; t -= 1.0)
        {
            int gx = (int)Math.Round(TimeToX(t));
            if (gx >= plotX && gx < rightEdge) FillRect(gx, 0, 1, Height, ColTimeGrid);
        }

        // 3. Left axis labels (C, E, G, A with guaranteed non-overlap)
        int lastLabelY = -100;
        for (int note = hiNote; note >= loNote; note--)
        {
            int pc = ((note % 12) + 12) % 12;
            bool isC = pc == 0;
            bool show = isC || (pxPerSemi >= 5.5 && (pc == 4 || pc == 7 || pc == 9));
            if (!show) continue;
            int yc = (int)Math.Round(MidiToY(note));
            if (yc < 6 || yc > Height - 7 || Math.Abs(yc - lastLabelY) < 12) continue;
            string name = PitchMath.MidiToNoteName(note);
            int tw = MeasureText(name, 1);
            DrawText(name, _axisW - 13 - tw, yc - 3, 1, isC ? ColCLabel : ColNoteLabel);
            lastLabelY = yc;
        }

        // 4. Live reference dashed line
        if (live.IsVoiced)
        {
            int ly = (int)Math.Round(MidiToY(live.MidiNote));
            if (ly >= 0 && ly < Height)
            {
                for (int x = plotX; x < liveX; x += 6)
                    BlendRect(x, ly, Math.Min(3, liveX - x), 1, ColWhite, 55);
            }
        }

        // 5. Scrolling pitch curve (clipped to [plotX, rightEdge))
        int iStart = Math.Max(1, (int)Math.Floor((now - WindowSeconds) / _stepSec));
        int iEnd = Math.Min(_outMidi.Length - 1, curStep);
        for (int i = iStart; i <= iEnd; i++)
        {
            float m0 = _outMidi[i - 1], m1 = _outMidi[i];
            if (m0 <= 0 || m1 <= 0 || Math.Abs(m1 - m0) > 7f) continue;

            double x0 = TimeToX((i - 1) * _stepSec), y0 = MidiToY(m0);
            double x1 = TimeToX(i * _stepSec), y1 = MidiToY(m1);
            if (x1 < plotX || x0 >= rightEdge) continue;

            double cents = (m1 - Math.Round(m1)) * 100.0;
            uint col = Math.Abs(cents) <= 12 ? ColInTune : Math.Abs(cents) <= 28 ? ColNear : ColOff;
            DrawThickLineClipped(x0, y0, x1, y1, 2.3, col, plotX, rightEdge);
        }

        // 6. Live glowing head dot
        if (live.IsVoiced)
        {
            double ly = MidiToY(live.MidiNote);
            if (ly >= 2 && ly < Height - 2)
            {
                uint zc = ZoneColor(live.Zone);
                DrawCircleBlend(liveX, ly, 6.5, zc, 75, plotX, rightEdge);
                DrawCircleBlend(liveX, ly, 3.6, zc, 255, plotX, rightEdge);
                DrawCircleBlend(liveX, ly, 1.6, ColWhite, 240, plotX, rightEdge);
            }
        }

        // 7. Separators & top border separating from the video frame above
        FillRect(0, 0, Width, 1, ColSep);
        FillRect(_axisW, 0, 1, Height, ColSep);
        FillRect(rightEdge, 0, 1, Height, ColSep);

        // 8. Right-side live HUD (Big note name + cents + Hz + tuning bar)
        int hudCx = rightEdge + _hudW / 2;
        int noteScale = Height >= 120 ? 3 : 2;
        int noteH = 7 * noteScale;
        int totalH = noteH + 6 + 7 + 5 + 7 + 8 + 5;
        int topY = Math.Max(6, (Height - totalH) / 2);

        uint hudCol = live.IsVoiced ? ZoneColor(live.Zone) : ColSilent;
        string noteText = live.NoteName;
        DrawText(noteText, hudCx - MeasureText(noteText, noteScale) / 2, topY, noteScale, hudCol);

        int yCents = topY + noteH + 6;
        int yHz = yCents + 11;
        if (live.IsVoiced)
        {
            string centsStr = $"{(live.Cents >= 0 ? "+" : "")}{(int)Math.Round(live.Cents)}c";
            DrawText(centsStr, hudCx - MeasureText(centsStr, 1) / 2, yCents, 1, hudCol);
            string hzStr = $"{live.FrequencyHz:0}Hz";
            DrawText(hzStr, hudCx - MeasureText(hzStr, 1) / 2, yHz, 1, ColSubtle);
        }

        int barW = _hudW - 18;
        int barX = rightEdge + 9;
        int barY = Math.Min(Height - 10, yHz + 14);
        FillRect(barX, barY, barW, 3, ColTrackBg);
        FillRect(hudCx, barY - 2, 1, 7, ColTrackTick);
        if (live.IsVoiced)
        {
            double frac = Math.Clamp(live.Cents / 50.0, -1.0, 1.0);
            int mx = hudCx + (int)Math.Round(frac * (barW * 0.5 - 4));
            FillRect(mx - 4, barY - 1, 8, 5, hudCol);
        }

        return _frame;
    }

    private static uint ZoneColor(PitchZone z) => z switch
    {
        PitchZone.InTune => ColInTune,
        PitchZone.SlightlyOff => ColNear,
        PitchZone.OffPitch => ColOff,
        _ => ColSilent,
    };

    private void FillRect(int x, int y, int w, int h, uint bgra)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) return;
        byte b = (byte)(bgra & 0xFF), g = (byte)((bgra >> 8) & 0xFF), r = (byte)((bgra >> 16) & 0xFF);
        for (int py = y0; py < y1; py++)
        {
            int row = (py * Width + x0) * 4;
            for (int px = x0; px < x1; px++, row += 4)
            {
                _frame[row] = b; _frame[row + 1] = g; _frame[row + 2] = r; _frame[row + 3] = 255;
            }
        }
    }

    private void BlendRect(int x, int y, int w, int h, uint bgra, int alpha)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) return;
        byte b = (byte)(bgra & 0xFF), g = (byte)((bgra >> 8) & 0xFF), r = (byte)((bgra >> 16) & 0xFF);
        int inv = 255 - alpha;
        for (int py = y0; py < y1; py++)
        {
            int row = (py * Width + x0) * 4;
            for (int px = x0; px < x1; px++, row += 4)
            {
                _frame[row] = (byte)((_frame[row] * inv + b * alpha) / 255);
                _frame[row + 1] = (byte)((_frame[row + 1] * inv + g * alpha) / 255);
                _frame[row + 2] = (byte)((_frame[row + 2] * inv + r * alpha) / 255);
            }
        }
    }

    private void BlendPixel(int x, int y, byte r, byte g, byte b, int alpha)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height || alpha <= 0) return;
        int idx = (y * Width + x) * 4;
        if (alpha >= 255)
        {
            _frame[idx] = b; _frame[idx + 1] = g; _frame[idx + 2] = r; _frame[idx + 3] = 255;
            return;
        }
        int inv = 255 - alpha;
        _frame[idx] = (byte)((_frame[idx] * inv + b * alpha) / 255);
        _frame[idx + 1] = (byte)((_frame[idx + 1] * inv + g * alpha) / 255);
        _frame[idx + 2] = (byte)((_frame[idx + 2] * inv + r * alpha) / 255);
    }

    private void DrawThickLineClipped(double x0, double y0, double x1, double y1, double thickness, uint col, int clipL, int clipR)
    {
        double rad = thickness * 0.5;
        double glowRad = rad + 1.4;
        int minX = Math.Max(clipL, (int)Math.Floor(Math.Min(x0, x1) - glowRad - 1));
        int maxX = Math.Min(clipR - 1, (int)Math.Ceiling(Math.Max(x0, x1) + glowRad + 1));
        int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - glowRad - 1));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + glowRad + 1));
        if (minX > maxX || minY > maxY) return;

        double dx = x1 - x0, dy = y1 - y0;
        double lenSq = dx * dx + dy * dy;
        byte b = (byte)(col & 0xFF), g = (byte)((col >> 8) & 0xFF), r = (byte)((col >> 16) & 0xFF);

        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                double t = lenSq > 1e-6 ? Math.Clamp(((px - x0) * dx + (py - y0) * dy) / lenSq, 0, 1) : 0;
                double cx = x0 + t * dx, cy = y0 + t * dy;
                double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                if (dist > glowRad) continue;
                int a = dist <= rad ? 255 : (int)Math.Round(255 * Math.Clamp(1.0 - (dist - rad) / (glowRad - rad), 0, 1));
                BlendPixel(px, py, r, g, b, a);
            }
        }
    }

    private void DrawCircleBlend(double cx, double cy, double radius, uint col, int maxAlpha, int clipL, int clipR)
    {
        int minX = Math.Max(clipL, (int)Math.Floor(cx - radius - 1));
        int maxX = Math.Min(clipR - 1, (int)Math.Ceiling(cx + radius + 1));
        int minY = Math.Max(0, (int)Math.Floor(cy - radius - 1));
        int maxY = Math.Min(Height - 1, (int)Math.Ceiling(cy + radius + 1));
        byte b = (byte)(col & 0xFF), g = (byte)((col >> 8) & 0xFF), r = (byte)((col >> 16) & 0xFF);
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                double d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                if (d > radius + 0.8) continue;
                double cov = d <= radius - 0.5 ? 1.0 : Math.Clamp((radius + 0.5 - d), 0, 1);
                BlendPixel(px, py, r, g, b, (int)Math.Round(maxAlpha * cov));
            }
        }
    }

    // Compact crisp 5x7 pixel font for note names, digits, Hz, +, -, #, c (zero OS font dependency).
    private static int MeasureText(string s, int scale) => s.Length == 0 ? 0 : (s.Length * 6 - 1) * scale;

    private void DrawText(string s, int x, int y, int scale, uint col)
    {
        int cx = x;
        foreach (char ch in s)
        {
            var glyph = GlyphOf(ch);
            if (glyph != null)
            {
                for (int row = 0; row < 7; row++)
                {
                    byte bits = glyph[row];
                    for (int colBit = 0; colBit < 5; colBit++)
                    {
                        if ((bits & (1 << (4 - colBit))) != 0)
                            FillRect(cx + colBit * scale, y + row * scale, scale, scale, col);
                    }
                }
            }
            cx += 6 * scale;
        }
    }

    private static byte[]? GlyphOf(char ch) => ch switch
    {
        '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
        '1' => [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
        '2' => [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
        '3' => [0x0E, 0x11, 0x01, 0x06, 0x01, 0x11, 0x0E],
        '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
        '5' => [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
        '6' => [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
        '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
        '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
        '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
        'A' => [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        'B' => [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
        'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
        'D' => [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
        'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
        'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
        'G' => [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
        'H' => [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
        'z' => [0x00, 0x00, 0x1F, 0x02, 0x04, 0x08, 0x1F],
        'c' => [0x00, 0x00, 0x0E, 0x10, 0x10, 0x10, 0x0E],
        '#' => [0x0A, 0x0A, 0x1F, 0x0A, 0x1F, 0x0A, 0x0A],
        '+' => [0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00],
        '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
        _ => null,
    };
}
