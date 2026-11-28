using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Editing;

/// <summary>Min/max envelope of a mono track at a fixed bucket size, for fast waveform drawing at any zoom.</summary>
public sealed class Peaks
{
    public const int Bucket = 128;
    public readonly float[] Min, Max;
    public readonly float Scale;

    public Peaks(float[] mono)
    {
        int n = (mono.Length + Bucket - 1) / Bucket;
        Min = new float[n];
        Max = new float[n];
        float abs = 1e-4f;
        for (int b = 0; b < n; b++)
        {
            float mn = 0, mx = 0;
            int end = Math.Min(mono.Length, (b + 1) * Bucket);
            for (int i = b * Bucket; i < end; i++)
            {
                float v = mono[i];
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            Min[b] = mn; Max[b] = mx;
            abs = Math.Max(abs, Math.Max(mx, -mn));
        }
        // Normalise display so quiet vocals are still readable.
        Scale = 0.92f / abs;
    }

    public int Length => Min.Length;
}

/// <summary>
/// Timeline with vocal and backing waveforms, trim handles and a playhead.
/// Click/drag = seek, drag the yellow handles = trim, Ctrl+wheel = zoom, wheel = scroll.
/// </summary>
public sealed class TimelineControl : FrameworkElement
{
    private const double RulerH = 22;
    private const double HandleHit = 7;
    private const int Rate = SessionAudio.Rate;

    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x1E)));
    private static readonly Brush LaneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x26)));
    private static readonly Brush RulerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x30)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9B, 0xA3, 0xB0)));
    private static readonly Brush OutBrush = Frozen(new SolidColorBrush(Color.FromArgb(175, 10, 12, 16)));
    private static readonly Pen VocalPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0xA3, 0xF0))), 1));
    private static readonly Pen VocalDimPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x4A, 0x4A, 0xA3, 0xF0))), 1));
    private static readonly Pen BackPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x96, 0x7A, 0xE6))), 1));
    private static readonly Pen PitchGridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF))), 0.75));
    private static readonly Pen PitchInTunePen = MakeRoundPen(Color.FromRgb(0x38, 0xC6, 0x88), 1.8);
    private static readonly Pen PitchNearPen = MakeRoundPen(Color.FromRgb(0xE8, 0xAE, 0x38), 1.8);
    private static readonly Pen PitchOffPen = MakeRoundPen(Color.FromRgb(0xE4, 0x5F, 0x64), 1.8);
    private static readonly Brush PitchInTuneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x38, 0xC6, 0x88)));
    private static readonly Brush PitchNearBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xAE, 0x38)));
    private static readonly Brush PitchOffBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0x5F, 0x64)));
    private static readonly Typeface MonoSemiBold = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Pen TickPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x59, 0x60))), 1));

    private static Pen MakeRoundPen(Color c, double w)
    {
        var p = new Pen(Frozen(new SolidColorBrush(c)), w)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        p.Freeze();
        return p;
    }
    private static readonly Pen HandlePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07))), 2));
    private static readonly Brush HandleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)));
    private static readonly Pen PlayheadPen = Frozen(new Pen(Brushes.White, 1.5));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static readonly Brush SelBrush = Frozen(new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)));
    private static readonly Pen SelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255))), 1));
    private static readonly Brush CutBrush = Frozen(MakeCutBrush());
    private static readonly Pen CutEdgePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D))), 1.5));

    /// <summary>Dark red with diagonal hatching: reads as "removed" even in a small strip.</summary>
    private static Brush MakeCutBrush()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 0x3A, 0x10, 0x14)), null, new Rect(0, 0, 10, 10));
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 0xE5, 0x48, 0x4D)), 1.5), new Point(0, 10), new Point(10, 0));
        }
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 10, 10), ViewportUnits = BrushMappingMode.Absolute };
    }

    private SessionInfo? _s;
    private Peaks? _vocal, _backing;
    private VocalPitchTrack? _pitch;
    private bool _showPitchOverlay;
    private double _viewStart, _viewEnd = 1;
    private double _playhead;
    private DrawingGroup? _cache;
    private enum Drag { None, Seek, In, Out, Select }
    private Drag _drag;
    private double _anchor;
    private double _downX;
    private TimeRange? _selection;

    public event Action<double>? SeekRequested;
    /// <summary>Raised when a trim drag starts (for undo snapshots).</summary>
    public event Action? EditStarting;
    public event Action? TrimChanged;
    public event Action? SelectionChanged;
    /// <summary>Right-click on a deleted section: restore it.</summary>
    public event Action<double>? RestoreCutRequested;

    /// <summary>Current range selection on the timeline (drag on the waveforms), or null.</summary>
    public TimeRange? Selection
    {
        get => _selection;
        set { _selection = value; InvalidateVisual(); SelectionChanged?.Invoke(); }
    }

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
    }

    public void Load(SessionInfo s, Peaks vocal, Peaks backing)
    {
        _s = s;
        _vocal = vocal;
        _backing = backing;
        _viewStart = 0;
        _viewEnd = Math.Max(1, s.Duration);
        Refresh();
    }

    public void SetVocalPeaks(Peaks vocal) { _vocal = vocal; Refresh(); }

    public void SetVocalPitch(VocalPitchTrack? pitch) { _pitch = pitch; Refresh(); }

    public bool ShowPitchOverlay
    {
        get => _showPitchOverlay;
        set { if (_showPitchOverlay != value) { _showPitchOverlay = value; Refresh(); } }
    }

    public double Playhead
    {
        get => _playhead;
        set { _playhead = value; InvalidateVisual(); }
    }

    /// <summary>Rebuilds cached waveform drawing (call after edit-state changes).</summary>
    public void Refresh()
    {
        _cache = null;
        InvalidateVisual();
    }

    public void ZoomToFit()
    {
        if (_s == null) return;
        _viewStart = 0;
        _viewEnd = Math.Max(1, _s.Duration);
        Refresh();
    }

    private double Duration => _s?.Duration ?? 1;
    private double SecPerPx => (_viewEnd - _viewStart) / Math.Max(1, ActualWidth);
    private double X(double t) => (t - _viewStart) / SecPerPx;
    private double T(double x) => _viewStart + x * SecPerPx;

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Refresh();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        if (_s == null || w < 2) return;
        _cache ??= BuildCache(w, h);
        dc.DrawDrawing(_cache);

        // Selection
        if (_selection is { } sel)
        {
            double x0 = Math.Max(0, X(sel.Start)), x1 = Math.Min(w, X(sel.End));
            if (x1 > x0)
            {
                dc.DrawRectangle(SelBrush, null, new Rect(x0, RulerH, x1 - x0, h - RulerH));
                dc.DrawLine(SelPen, new Point(x0, RulerH), new Point(x0, h));
                dc.DrawLine(SelPen, new Point(x1, RulerH), new Point(x1, h));
            }
        }

        // Playhead
        double px = X(_playhead);
        if (px >= 0 && px <= w) dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, h));

        // Live pitch readout badge & playhead dot when pitch overlay is active
        if (_showPitchOverlay && _pitch != null && _pitch.Count > 0)
        {
            var reading = _pitch.ReadingAtTimeline(_playhead, _s.VocalPlacement);
            if (reading.IsVoiced)
            {
                double laneH = (h - RulerH) / 2;
                var vocalLane = new Rect(0, RulerH, w, laneH - 1);
                var (minM, maxM) = PitchRange();
                double py = MidiToLaneY(reading.MidiNote, minM, maxM, vocalLane);
                var brush = reading.Zone switch
                {
                    PitchZone.InTune => PitchInTuneBrush,
                    PitchZone.SlightlyOff => PitchNearBrush,
                    _ => PitchOffBrush,
                };
                if (px >= 0 && px <= w && py >= vocalLane.Top && py <= vocalLane.Bottom)
                {
                    dc.DrawEllipse(brush, null, new Point(px, py), 4.0, 4.0);
                    dc.DrawEllipse(Brushes.White, null, new Point(px, py), 1.6, 1.6);
                }
                double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var ft = new FormattedText($"{reading.NoteName} {reading.CentsText}", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, MonoSemiBold, 11, brush, dpi);
                DrawLaneLabel(dc, ft, new Point(Math.Max(46, w - ft.Width - 10), RulerH + 3));
            }
        }
    }

    private (double Min, double Max) PitchRange()
    {
        if (_pitch == null) return (54, 74);
        return (_pitch.MinMidi - 1.5, _pitch.MaxMidi + 1.5);
    }

    private static double MidiToLaneY(double midi, double minM, double maxM, Rect lane)
    {
        double pad = 5;
        double usable = Math.Max(10, lane.Height - pad * 2);
        double frac = (midi - minM) / Math.Max(1.0, maxM - minM);
        return lane.Bottom - pad - Math.Clamp(frac, 0, 1) * usable;
    }

    private void DrawPitchOverlay(DrawingContext dc, VocalPitchTrack pitch, double placement, Rect lane, double w, double dpi)
    {
        var (minM, maxM) = PitchRange();
        // Subtle C-note reference grid lines inside the vocal lane
        int cStart = (int)Math.Ceiling(minM / 12.0) * 12;
        for (int c = cStart; c <= maxM; c += 12)
        {
            double y = MidiToLaneY(c, minM, maxM, lane);
            dc.DrawLine(PitchGridPen, new Point(0, y), new Point(w, y));
            var lbl = new FormattedText(PitchMath.MidiToNoteName(c), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, MonoSemiBold, 9.5, TextBrush, dpi);
            if (y - lbl.Height * 0.5 > lane.Top + 16 && y + lbl.Height * 0.5 < lane.Bottom - 2)
                dc.DrawText(lbl, new Point(w - lbl.Width - 6, y - lbl.Height * 0.5));
        }

        double step = pitch.StepSeconds;
        int i0 = Math.Max(1, (int)Math.Floor((_viewStart - placement) / step));
        int i1 = Math.Min(pitch.Count - 1, (int)Math.Ceiling((_viewEnd - placement) / step));
        // Decimate step slightly when zoomed way out (e.g. 5-min full view) so drawing stays instant & crisp.
        int stride = Math.Max(1, (int)Math.Floor((SecPerPx * 1.5) / step));

        for (int i = i0; i <= i1; i += stride)
        {
            int prev = i - stride;
            if (prev < 0) continue;
            float m0 = pitch.Midi[prev], m1 = pitch.Midi[i];
            if (m0 <= 0 || m1 <= 0 || Math.Abs(m1 - m0) > 7f) continue;

            double x0 = X(prev * step + placement), x1 = X(i * step + placement);
            if (x1 < 0 || x0 > w) continue;
            double y0 = MidiToLaneY(m0, minM, maxM, lane), y1 = MidiToLaneY(m1, minM, maxM, lane);
            double cents = (m1 - Math.Round(m1)) * 100.0;
            var pen = Math.Abs(cents) <= 12 ? PitchInTunePen : Math.Abs(cents) <= 28 ? PitchNearPen : PitchOffPen;
            dc.DrawLine(pen, new Point(x0, y0), new Point(x1, y1));
        }
    }

    private DrawingGroup BuildCache(double w, double h)
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            var s = _s!;
            double laneH = (h - RulerH) / 2;
            var vocalLane = new Rect(0, RulerH, w, laneH - 1);
            var backLane = new Rect(0, RulerH + laneH, w, laneH - 1);
            dc.DrawRectangle(RulerBrush, null, new Rect(0, 0, w, RulerH));
            dc.DrawRectangle(LaneBrush, null, vocalLane);
            dc.DrawRectangle(LaneBrush, null, backLane);
            DrawRuler(dc, w);

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            bool showPitch = _showPitchOverlay && _pitch != null && _pitch.Count > 0;
            if (_vocal != null) DrawWave(dc, _vocal, s.VocalPlacement, vocalLane, showPitch ? VocalDimPen : VocalPen, w);
            if (showPitch) DrawPitchOverlay(dc, _pitch!, s.VocalPlacement, vocalLane, w, dpi);
            if (_backing != null) DrawWave(dc, _backing, s.BackingPlacement, backLane, BackPen, w);

            // Deleted sections: hatched red over both lanes, with a label when wide enough.
            foreach (var c in s.Edit.Cuts)
            {
                double x0 = Math.Max(0, X(c.Start)), x1 = Math.Min(w, X(c.End));
                if (x1 <= x0) continue;
                dc.DrawRectangle(CutBrush, null, new Rect(x0, RulerH, x1 - x0, h - RulerH));
                dc.DrawLine(CutEdgePen, new Point(x0, RulerH), new Point(x0, h));
                dc.DrawLine(CutEdgePen, new Point(x1, RulerH), new Point(x1, h));
                var label = Label("已删除", dpi);
                if (x1 - x0 > label.Width + 10)
                    DrawLaneLabel(dc, label, new Point((x0 + x1 - label.Width) / 2, RulerH + (h - RulerH - label.Height) / 2));
            }

            // Trim shading + handles
            double a = X(s.Edit.TrimStart), b = X(s.EffectiveTrimEnd);
            if (a > 0) dc.DrawRectangle(OutBrush, null, new Rect(0, RulerH, Math.Min(w, a), h - RulerH));
            if (b < w) dc.DrawRectangle(OutBrush, null, new Rect(Math.Max(0, b), RulerH, w - Math.Max(0, b), h - RulerH));
            DrawHandle(dc, a, h, true);
            DrawHandle(dc, b, h, false);

            // Lane labels last so no overlay ever covers them.
            DrawLaneLabel(dc, Label("人声", dpi), new Point(6, RulerH + 3));
            DrawLaneLabel(dc, Label(s.Edit.BackingMuted ? "伴奏（静音）" : "伴奏", dpi), new Point(6, RulerH + laneH + 3));
        }
        g.Freeze();
        return g;
    }

    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(200, 0x1C, 0x1D, 0x21)));

    /// <summary>Lane label on an opaque plate so waveforms never run through the text.</summary>
    private static void DrawLaneLabel(DrawingContext dc, FormattedText text, Point at)
    {
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(at.X - 4, at.Y - 1, text.Width + 8, text.Height + 2), 3, 3);
        dc.DrawText(text, at);
    }

    private static FormattedText Label(string text, double dpi) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 11, TextBrush, dpi);

    private static void DrawHandle(DrawingContext dc, double x, double h, bool isIn)
    {
        dc.DrawLine(HandlePen, new Point(x, RulerH - 6), new Point(x, h));
        var tri = new StreamGeometry();
        using (var c = tri.Open())
        {
            c.BeginFigure(new Point(x, RulerH - 8), true, true);
            c.LineTo(new Point(isIn ? x + 9 : x - 9, RulerH - 8), false, false);
            c.LineTo(new Point(x, RulerH + 2), false, false);
        }
        tri.Freeze();
        dc.DrawGeometry(HandleBrush, null, tri);
    }

    private void DrawRuler(DrawingContext dc, double w)
    {
        double spp = SecPerPx;
        double[] steps = [0.1, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(st => st / spp >= 70, 600);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double first = Math.Floor(_viewStart / step) * step;
        for (double t = first; t <= _viewEnd; t += step)
        {
            double x = X(t);
            if (x < -1) continue;
            dc.DrawLine(TickPen, new Point(x, RulerH - 7), new Point(x, RulerH));
            string label = step < 1 ? Library.FormatTimecode(t) : Library.FormatDuration(t);
            dc.DrawText(Label(label, dpi), new Point(x + 3, 3));
        }
    }

    private void DrawWave(DrawingContext dc, Peaks p, double placement, Rect lane, Pen pen, double w)
    {
        double mid = lane.Top + lane.Height / 2;
        double amp = lane.Height / 2 * p.Scale;
        double spp = SecPerPx;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            for (int x = 0; x < (int)w; x++)
            {
                double t0 = _viewStart + x * spp - placement;
                double t1 = t0 + spp;
                if (t1 <= 0) continue;
                int b0 = (int)Math.Floor(Math.Max(0, t0) * Rate / Peaks.Bucket);
                int b1 = (int)Math.Ceiling(t1 * Rate / Peaks.Bucket);
                if (b0 >= p.Length) break;
                b1 = Math.Min(Math.Max(b1, b0 + 1), p.Length);
                float mn = 0, mx = 0;
                for (int b = b0; b < b1; b++)
                {
                    if (p.Min[b] < mn) mn = p.Min[b];
                    if (p.Max[b] > mx) mx = p.Max[b];
                }
                double y0 = mid - mx * amp, y1 = mid - mn * amp;
                if (y1 - y0 < 1) { y0 = mid - 0.5; y1 = mid + 0.5; }
                c.BeginFigure(new Point(x + 0.5, y0), false, false);
                c.LineTo(new Point(x + 0.5, y1), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_s == null) return;
        Focus();
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Right)
        {
            double tr = T(p.X);
            if (p.Y > RulerH && EditTimeline.CutAt(_s.Edit.Cuts, tr) != null) RestoreCutRequested?.Invoke(tr);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        double a = X(_s.Edit.TrimStart), b = X(_s.EffectiveTrimEnd);
        if (Math.Abs(p.X - a) <= HandleHit) _drag = Drag.In;
        else if (Math.Abs(p.X - b) <= HandleHit) _drag = Drag.Out;
        else if (p.Y > RulerH) _drag = Drag.Select; // waveform area: click = seek, drag = select
        else _drag = Drag.Seek;                    // ruler: scrub
        if (_drag is Drag.In or Drag.Out) EditStarting?.Invoke();
        _anchor = Math.Clamp(T(p.X), 0, Duration);
        _downX = p.X;
        CaptureMouse();
        if (_drag == Drag.Select)
        {
            if (_selection != null) Selection = null;
            SeekRequested?.Invoke(_anchor);
        }
        else ApplyDrag(p.X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_s == null) return;
        var p = e.GetPosition(this);
        if (_drag != Drag.None)
        {
            ApplyDrag(p.X);
            return;
        }
        double a = X(_s.Edit.TrimStart), b = X(_s.EffectiveTrimEnd);
        Cursor = Math.Abs(p.X - a) <= HandleHit || Math.Abs(p.X - b) <= HandleHit ? Cursors.SizeWE : Cursors.IBeam;
        ToolTip = p.Y > RulerH && EditTimeline.CutAt(_s.Edit.Cuts, T(p.X)) != null ? "已删除的片段 · 右键恢复" : HelpTip;
    }

    private const string HelpTip = "点击定位 · 在波形上拖动选区（Delete 删除）· 拖黄色把手裁剪头尾 · Ctrl+滚轮缩放 · 滚轮平移 · 右键红色片段恢复";

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    private void ApplyDrag(double x)
    {
        var s = _s!;
        double t = Math.Clamp(T(x), 0, Duration);
        switch (_drag)
        {
            case Drag.Select:
                if (Math.Abs(x - _downX) >= 4)
                {
                    var r = new TimeRange(EditTimeline.Snap(Math.Min(_anchor, t), s.Video.Fps), EditTimeline.Snap(Math.Max(_anchor, t), s.Video.Fps));
                    Selection = r.Length > 0 ? r : null;
                }
                break;
            case Drag.Seek:
                SeekRequested?.Invoke(t);
                break;
            case Drag.In:
                s.Edit.TrimStart = Math.Min(t, s.EffectiveTrimEnd - 0.5);
                if (s.Edit.TrimStart < 0) s.Edit.TrimStart = 0;
                TrimChanged?.Invoke();
                Refresh();
                break;
            case Drag.Out:
                double end = Math.Max(t, s.Edit.TrimStart + 0.5);
                s.Edit.TrimEnd = end >= Duration - 0.01 ? 0 : end;
                TrimChanged?.Invoke();
                Refresh();
                break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_s == null) return;
        double span = _viewEnd - _viewStart;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double anchor = T(e.GetPosition(this).X);
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double newSpan = Math.Clamp(span * factor, 0.5, Math.Max(1, Duration));
            double ratio = (anchor - _viewStart) / span;
            _viewStart = anchor - ratio * newSpan;
            _viewEnd = _viewStart + newSpan;
        }
        else
        {
            double shift = -Math.Sign(e.Delta) * span * 0.15;
            _viewStart += shift;
            _viewEnd += shift;
        }
        ClampView();
        Refresh();
        e.Handled = true;
    }

    private void ClampView()
    {
        double span = _viewEnd - _viewStart;
        if (_viewStart < 0) { _viewStart = 0; _viewEnd = span; }
        if (_viewEnd > Duration) { _viewEnd = Math.Max(Duration, span); _viewStart = _viewEnd - span; }
        if (_viewStart < 0) _viewStart = 0;
    }

    /// <summary>Scrolls so the playhead stays visible during playback.</summary>
    public void EnsureVisible(double t)
    {
        double span = _viewEnd - _viewStart;
        if (t < _viewStart || t > _viewEnd)
        {
            _viewStart = t - span * 0.1;
            _viewEnd = _viewStart + span;
            ClampView();
            Refresh();
        }
    }
}
