using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VocalPracticeRecorder.Core;

namespace VocalPracticeRecorder.Views;

/// <summary>
/// VocalPitchMonitor-style scrolling real-time pitch curve with piano semitone grid,
/// smooth vertical auto-follow (plus mouse-wheel zoom / vertical drag pan / double-click reset),
/// and a crisp right-side live note + cents + Hz HUD.
/// </summary>
public sealed class PitchMonitorControl : FrameworkElement
{
    private const double AxisWidth = 44;
    private const double HudWidth = 82;
    private const double WindowSeconds = 6.0;
    private const double DefaultSpanSemitones = 18.0;
    private const double MinSpanSemitones = 12.0;
    private const double MaxSpanSemitones = 36.0;
    private const double DefaultCenterMidi = 64.0; // E4 (~G3..C#5)
    private const double MinCenterMidi = 38.0;     // D2
    private const double MaxCenterMidi = 88.0;     // E6
    private const double TopPad = 6;
    private const double BottomPad = 8; // keep clear of the 6px native bottom resize edge

    private static readonly Typeface UiTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiSemiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface MonoTypeface = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface MonoBold = new(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly SolidColorBrush BgBrush = Freeze(Color.FromRgb(0x18, 0x1B, 0x22));
    private static readonly SolidColorBrush BlackKeyRowBrush = Freeze(Color.FromRgb(0x13, 0x16, 0x1C));
    private static readonly SolidColorBrush WhiteKeyRowBrush = Freeze(Color.FromRgb(0x1B, 0x1F, 0x27));
    private static readonly SolidColorBrush SidePanelBrush = Freeze(Color.FromRgb(0x14, 0x17, 0x1D));
    private static readonly SolidColorBrush BlackKeyPillBrush = Freeze(Color.FromRgb(0x0D, 0x0F, 0x13));
    private static readonly SolidColorBrush WhiteKeyTickBrush = Freeze(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CLabelBrush = Freeze(Color.FromRgb(0xD8, 0xDF, 0xEA));
    private static readonly SolidColorBrush NoteLabelBrush = Freeze(Color.FromRgb(0x7A, 0x82, 0x90));
    private static readonly SolidColorBrush SubtleBrush = Freeze(Color.FromRgb(0x8A, 0x8F, 0x98));
    private static readonly SolidColorBrush SilentBrush = Freeze(Color.FromRgb(0x55, 0x5C, 0x68));
    private static readonly SolidColorBrush WhiteCoreBrush = Freeze(Color.FromArgb(0xEC, 0xFF, 0xFF, 0xFF));

    private static readonly Color InTuneColor = Color.FromRgb(0x3E, 0xCF, 0x8E);
    private static readonly Color NearColor = Color.FromRgb(0xF5, 0xB7, 0x3B);
    private static readonly Color OffColor = Color.FromRgb(0xF0, 0x64, 0x69);

    private static readonly SolidColorBrush InTuneBrush = Freeze(InTuneColor);
    private static readonly SolidColorBrush NearBrush = Freeze(NearColor);
    private static readonly SolidColorBrush OffBrush = Freeze(OffColor);

    private static readonly SolidColorBrush InTuneBandBrush = Freeze(Color.FromArgb(0x1C, 0x3E, 0xCF, 0x8E));
    private static readonly SolidColorBrush NearBandBrush = Freeze(Color.FromArgb(0x18, 0xF5, 0xB7, 0x3B));
    private static readonly SolidColorBrush OffBandBrush = Freeze(Color.FromArgb(0x18, 0xF0, 0x64, 0x69));
    private static readonly SolidColorBrush InTuneHaloBrush = Freeze(Color.FromArgb(0x45, 0x3E, 0xCF, 0x8E));
    private static readonly SolidColorBrush NearHaloBrush = Freeze(Color.FromArgb(0x45, 0xF5, 0xB7, 0x3B));
    private static readonly SolidColorBrush OffHaloBrush = Freeze(Color.FromArgb(0x45, 0xF0, 0x64, 0x69));
    private static readonly SolidColorBrush TunerTrackBrush = Freeze(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TunerTickBrush = Freeze(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));

    private static readonly Pen SeparatorPen = Freeze(new Pen(Freeze(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen CLinePen = Freeze(new Pen(Freeze(Color.FromArgb(0x36, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen SemiLinePen = Freeze(new Pen(Freeze(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), 0.75));
    private static readonly Pen TimeGridPen = Freeze(new Pen(Freeze(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen CursorLinePen = Freeze(new Pen(Freeze(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), 1) { DashStyle = DashStyles.Dash });

    private static readonly Pen InTunePen = MakeCurvePen(InTuneColor, 2.2);
    private static readonly Pen NearPen = MakeCurvePen(NearColor, 2.2);
    private static readonly Pen OffPen = MakeCurvePen(OffColor, 2.2);
    private static readonly Pen InTuneGlowPen = MakeCurvePen(Color.FromArgb(0x38, InTuneColor.R, InTuneColor.G, InTuneColor.B), 5.5);
    private static readonly Pen NearGlowPen = MakeCurvePen(Color.FromArgb(0x34, NearColor.R, NearColor.G, NearColor.B), 5.5);
    private static readonly Pen OffGlowPen = MakeCurvePen(Color.FromArgb(0x34, OffColor.R, OffColor.G, OffColor.B), 5.5);

    private readonly List<PitchSample> _samples = new(512);
    private PitchReading _live = PitchReading.Silent;
    private double _nowSeconds;

    private double _centerMidi = DefaultCenterMidi;
    private double _targetCenterMidi = DefaultCenterMidi;
    private double _spanSemitones = DefaultSpanSemitones;
    private bool _autoFollow = true;

    private bool _panning;
    private double _panStartY;
    private double _panStartCenter;

    public PitchMonitorControl()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    public static SolidColorBrush BrushForZone(PitchZone zone) => zone switch
    {
        PitchZone.InTune => InTuneBrush,
        PitchZone.SlightlyOff => NearBrush,
        PitchZone.OffPitch => OffBrush,
        _ => SilentBrush,
    };

    /// <summary>Pulls the latest readings from <paramref name="tracker"/> and queues a redraw.</summary>
    public void UpdateFrom(PitchTracker tracker, bool snapAutoFollow = false)
    {
        tracker.CopyHistory(_samples, WindowSeconds + 0.2, out _nowSeconds);
        _live = tracker.GetLiveReading();

        if (_autoFollow)
        {
            double? followMidi = _live.IsVoiced ? _live.MidiNote : null;
            if (followMidi == null && snapAutoFollow)
            {
                // For deterministic snapshot rendering, center on the median of visible voiced samples.
                var voiced = _samples.Where(s => s.Reading.IsVoiced).Select(s => s.Reading.MidiNote).OrderBy(x => x).ToList();
                if (voiced.Count > 0) followMidi = voiced[voiced.Count / 2];
            }

            if (followMidi is { } m)
            {
                if (snapAutoFollow)
                {
                    _targetCenterMidi = Math.Clamp(m, MinCenterMidi, MaxCenterMidi);
                    _centerMidi = _targetCenterMidi;
                }
                else
                {
                    double margin = _spanSemitones * 0.28;
                    if (Math.Abs(m - _targetCenterMidi) > margin)
                        _targetCenterMidi = Math.Clamp(m, MinCenterMidi, MaxCenterMidi);
                }
            }

            if (!snapAutoFollow)
                _centerMidi += (_targetCenterMidi - _centerMidi) * 0.16;
        }

        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.88 : 1.14;
        _spanSemitones = Math.Clamp(_spanSemitones * factor, MinSpanSemitones, MaxSpanSemitones);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _autoFollow = true;
            _spanSemitones = DefaultSpanSemitones;
            if (_live.IsVoiced) _targetCenterMidi = Math.Clamp(_live.MidiNote, MinCenterMidi, MaxCenterMidi);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _panning = true;
        _panStartY = e.GetPosition(this).Y;
        _panStartCenter = _centerMidi;
        _autoFollow = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_panning) return;
        double plotH = Math.Max(40, ActualHeight - TopPad - BottomPad);
        double dy = e.GetPosition(this).Y - _panStartY;
        double dMidi = (dy / plotH) * _spanSemitones;
        _centerMidi = Math.Clamp(_panStartCenter + dMidi, MinCenterMidi, MaxCenterMidi);
        _targetCenterMidi = _centerMidi;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 140 || h < 40) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double plotX = AxisWidth;
        double plotW = Math.Max(40, w - AxisWidth - HudWidth);
        double plotY = TopPad;
        double plotH = Math.Max(20, h - TopPad - BottomPad);
        double rightEdge = plotX + plotW;
        double liveX = rightEdge - 10; // leave a tiny margin so the glowing head dot never clips

        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        dc.DrawRectangle(SidePanelBrush, null, new Rect(0, 0, AxisWidth, h));
        dc.DrawRectangle(SidePanelBrush, null, new Rect(rightEdge, 0, HudWidth, h));

        double minMidi = _centerMidi - _spanSemitones * 0.5;
        double maxMidi = _centerMidi + _spanSemitones * 0.5;
        double pxPerSemi = plotH / _spanSemitones;

        double MidiToY(double midi) => plotY + (maxMidi - midi) * pxPerSemi;
        double TimeToX(double t) => liveX - (_nowSeconds - t) / WindowSeconds * (liveX - plotX - 4);

        // 1. Semitone bands + horizontal guide lines + piano axis ticks
        int loNote = (int)Math.Floor(minMidi) - 1;
        int hiNote = (int)Math.Ceiling(maxMidi) + 1;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, rightEdge, h)));

        for (int note = loNote; note <= hiNote; note++)
        {
            double yTop = MidiToY(note + 0.5);
            double yBot = MidiToY(note - 0.5);
            if (yBot < 0 || yTop > h) continue;

            bool isBlack = PitchMath.IsBlackKey(note);
            double bandTop = Math.Max(0, yTop);
            double bandBot = Math.Min(h, yBot);
            dc.DrawRectangle(isBlack ? BlackKeyRowBrush : WhiteKeyRowBrush, null,
                new Rect(plotX, bandTop, plotW, Math.Max(0, bandBot - bandTop)));

            // Highlight the target semitone lane when actively voiced.
            if (_live.IsVoiced && _live.NearestMidi == note)
            {
                var band = _live.Zone switch
                {
                    PitchZone.InTune => InTuneBandBrush,
                    PitchZone.SlightlyOff => NearBandBrush,
                    _ => OffBandBrush,
                };
                dc.DrawRectangle(band, null, new Rect(plotX, bandTop, plotW, Math.Max(0, bandBot - bandTop)));
            }

            // Piano-roll key hint inside the left axis
            if (isBlack)
            {
                dc.DrawRectangle(BlackKeyPillBrush, null, new Rect(AxisWidth - 10, bandTop + 0.5, 9, Math.Max(1, bandBot - bandTop - 1)));
            }
            else
            {
                dc.DrawRectangle(WhiteKeyTickBrush, null, new Rect(AxisWidth - 3, bandTop + 0.5, 2, Math.Max(1, bandBot - bandTop - 1)));
            }

            // Semitone center line (represents exact 0-cent pitch for this note)
            double yCenter = MidiToY(note);
            if (yCenter >= 1 && yCenter <= h - 1)
            {
                bool isC = PitchMath.IsCNote(note);
                dc.DrawLine(isC ? CLinePen : SemiLinePen, new Point(plotX, yCenter), new Point(rightEdge, yCenter));
            }
        }

        // 2. Vertical 1-second scrolling time grid lines
        double firstGridSec = Math.Floor(_nowSeconds);
        for (double t = firstGridSec; t >= _nowSeconds - WindowSeconds; t -= 1.0)
        {
            double gx = TimeToX(t);
            if (gx >= plotX && gx <= rightEdge)
                dc.DrawLine(TimeGridPen, new Point(gx, 0), new Point(gx, h));
        }

        // 3. Axis note labels (density adapts strictly to pxPerSemi so labels NEVER overlap)
        double lastLabelY = double.NegativeInfinity;
        for (int note = hiNote; note >= loNote; note--)
        {
            int pc = ((note % 12) + 12) % 12;
            bool isC = pc == 0;
            bool show = isC
                || (pxPerSemi >= 11.5 && !PitchMath.IsBlackKey(note))
                || (pxPerSemi >= 5.5 && (pc == 4 || pc == 7 || pc == 9)); // E, G, A
            if (!show) continue;

            double yCenter = MidiToY(note);
            if (yCenter < 7 || yCenter > h - 8) continue;
            if (Math.Abs(yCenter - lastLabelY) < 12.5) continue;

            var ft = MakeText(PitchMath.MidiToNoteName(note), isC ? 10.5 : 9.5,
                isC ? UiSemiBold : UiTypeface, isC ? CLabelBrush : NoteLabelBrush, dpi);
            dc.DrawText(ft, new Point(AxisWidth - 13 - ft.Width, yCenter - ft.Height * 0.5));
            lastLabelY = yCenter;
        }

        dc.Pop(); // release plot+axis clip

        // 4. Scrolling pitch curve (clipped strictly to plot area)
        dc.PushClip(new RectangleGeometry(new Rect(plotX, 0, plotW, h)));

        if (_live.IsVoiced)
        {
            double ly = MidiToY(_live.MidiNote);
            if (ly >= 0 && ly <= h)
                dc.DrawLine(CursorLinePen, new Point(plotX, ly), new Point(liveX, ly));
        }

        // Pass 1: soft glow under-stroke; Pass 2: crisp colored curve core
        DrawCurvePass(dc, glow: true, MidiToY, TimeToX);
        DrawCurvePass(dc, glow: false, MidiToY, TimeToX);

        // Live head dot at the current pitch
        if (_live.IsVoiced)
        {
            double ly = MidiToY(_live.MidiNote);
            if (ly >= 2 && ly <= h - 2)
            {
                var halo = _live.Zone switch
                {
                    PitchZone.InTune => InTuneHaloBrush,
                    PitchZone.SlightlyOff => NearHaloBrush,
                    _ => OffHaloBrush,
                };
                dc.DrawEllipse(halo, null, new Point(liveX, ly), 7, 7);
                dc.DrawEllipse(BrushForZone(_live.Zone), null, new Point(liveX, ly), 4, 4);
                dc.DrawEllipse(WhiteCoreBrush, null, new Point(liveX, ly), 1.8, 1.8);
            }
        }

        dc.Pop();

        // 5. Crisp vertical separators
        dc.DrawLine(SeparatorPen, new Point(AxisWidth, 0), new Point(AxisWidth, h));
        dc.DrawLine(SeparatorPen, new Point(rightEdge, 0), new Point(rightEdge, h));

        // 6. Right-side live HUD (Note name + cents + Hz + horizontal cent bar)
        DrawRightHud(dc, rightEdge, HudWidth, h, dpi);
    }

    private void DrawCurvePass(DrawingContext dc, bool glow, Func<double, double> midiToY, Func<double, double> timeToX)
    {
        for (int i = 1; i < _samples.Count; i++)
        {
            var a = _samples[i - 1];
            var b = _samples[i];
            if (!a.Reading.IsVoiced || !b.Reading.IsVoiced) continue;
            if (b.TimeSeconds - a.TimeSeconds > 0.095) continue;
            if (Math.Abs(b.Reading.MidiNote - a.Reading.MidiNote) > 7.0) continue;

            var p1 = new Point(timeToX(a.TimeSeconds), midiToY(a.Reading.MidiNote));
            var p2 = new Point(timeToX(b.TimeSeconds), midiToY(b.Reading.MidiNote));
            var pen = b.Reading.Zone switch
            {
                PitchZone.InTune => glow ? InTuneGlowPen : InTunePen,
                PitchZone.SlightlyOff => glow ? NearGlowPen : NearPen,
                _ => glow ? OffGlowPen : OffPen,
            };
            dc.DrawLine(pen, p1, p2);
        }
    }

    private void DrawRightHud(DrawingContext dc, double x, double w, double h, double dpi)
    {
        double cx = x + w * 0.5;
        var zoneBrush = BrushForZone(_live.Zone);
        var noteFt = MakeText(_live.NoteName, 21, MonoBold, zoneBrush, dpi);
        var centsFt = MakeText(_live.IsVoiced ? _live.CentsText : " ", 11.5, MonoTypeface, zoneBrush, dpi);
        var hzFt = MakeText(_live.IsVoiced ? $"{_live.FrequencyHz:0}Hz" : " ", 10, UiTypeface, SubtleBrush, dpi);

        double barGap = 7;
        double barH = 5;
        double totalH = noteFt.Height + 1 + centsFt.Height + 1 + hzFt.Height + barGap + barH;
        double topY = Math.Max(8, (h - BottomPad - totalH) * 0.5);

        // Big note name
        dc.DrawText(noteFt, new Point(cx - noteFt.Width * 0.5, topY));

        double yCents = topY + noteFt.Height + 1;
        double yHz = yCents + centsFt.Height + 1;
        if (_live.IsVoiced)
        {
            dc.DrawText(centsFt, new Point(cx - centsFt.Width * 0.5, yCents));
            dc.DrawText(hzFt, new Point(cx - hzFt.Width * 0.5, yHz));
        }

        // Mini horizontal tuning bar (-50¢ .. +50¢) derived from actual text heights
        double barW = w - 18;
        double barX = x + 9;
        double barY = Math.Min(h - BottomPad - 8, yHz + hzFt.Height + barGap);
        dc.DrawRoundedRectangle(TunerTrackBrush, null, new Rect(barX, barY, barW, 3), 1.5, 1.5);
        // Center 0¢ tick
        dc.DrawRectangle(TunerTickBrush, null, new Rect(cx - 0.5, barY - 2, 1, 7));

        if (_live.IsVoiced)
        {
            double frac = Math.Clamp(_live.Cents / 50.0, -1.0, 1.0);
            double mx = cx + frac * (barW * 0.5 - 3);
            dc.DrawRoundedRectangle(zoneBrush, null, new Rect(mx - 4, barY - 1, 8, 5), 2, 2);
        }
    }

    private static FormattedText MakeText(string text, double size, Typeface face, Brush brush, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen Freeze(Pen p)
    {
        p.Freeze();
        return p;
    }

    private static Pen MakeCurvePen(Color c, double thickness)
    {
        var p = new Pen(Freeze(c), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        p.Freeze();
        return p;
    }
}
