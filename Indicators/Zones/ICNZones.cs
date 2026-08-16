#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
#endregion

// ---------------------------------------------------------------------------------------------
// ICNZones
//
// Automates the thin horizontal "manual box" levels that get drawn by hand on a chart. Four
// independent detectors feed one shared zone list; overlapping zones from different detectors are
// optionally merged into a single confluence zone whose opacity scales with how many detectors
// agree.
//
//   1. Displacement candle anatomy
//        A candle whose body (or total range) exceeds DisplacementAtrMult x ATR(AtrPeriod) is a
//        displacement. Its anatomy is emitted as thin bands: wick high, wick low, body top,
//        body bottom, body CE (consequent encroachment = body midpoint) and range CE.
//
//   2. Fair value gaps (3-candle imbalance)
//        Bullish: Low[0] > High[2]      -> zone [High[2], Low[0]]
//        Bearish: High[0] < Low[2]      -> zone [High[0], Low[2]]
//        Optionally dropped once price trades back through them.
//
//   3. Equal highs / equal lows (repeat touch)
//        Swing pivots of strength SwingStrength are collected, then clustered: any pivots within
//        EqualToleranceTicks of each other form one band. A cluster is published only once it has
//        at least MinTouches members. Band = [min, max] of the cluster.
//
//   4. Higher timeframe OHLC
//        Open / High / Low / Close of the last CLOSED higher-timeframe candle, each drawn as a
//        thin band across the chart.
//
// Rendering is done in OnRender (SharpDX) rather than with drawing objects, so zones can span the
// full canvas width cheaply and redraw instantly when toggled from the chart menu.
// ---------------------------------------------------------------------------------------------

public enum ICNMZDisplacementBasis
{
	Body,
	Range
}

// ── Zone model ──────────────────────────────────────────────────────
[Flags]
public enum ZoneSource
{
	None = 0,
	Displacement = 1,
	Fvg = 2,
	EqualLevels = 4,
	Htf = 8
}

namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{
	public class ICNZones : Indicator
	{



		private class Zone
		{
			public double Top;
			public double Btm;
			public int OriginBar;
			public ZoneSource Sources;
			public int Touches;      // only meaningful for EqualLevels
			public string Label;

			public double Mid { get { return (Top + Btm) / 2.0; } }

			public bool Overlaps(Zone other, double pad)
			{
				return Top + pad >= other.Btm && other.Top + pad >= Btm;
			}
		}

		// Raw detector output, rebuilt/maintained on the primary series.
		private readonly List<Zone> _displacementZones = new List<Zone>();
		private readonly List<Zone> _fvgZones = new List<Zone>();
		private readonly List<Zone> _equalZones = new List<Zone>();
		private readonly List<Zone> _htfZones = new List<Zone>();

		// Final list consumed by OnRender. Swapped atomically so OnRender never sees a half-built list.
		private volatile List<Zone> _renderZones = new List<Zone>();

		// Guards the detector lists against concurrent access from the UI thread (menu clicks).
		private readonly object _zoneLock = new object();

		// Swing pivot cache for the equal-highs/lows detector.
		private class Pivot { public double Price; public int Bar; public bool IsHigh; }
		private readonly List<Pivot> _pivots = new List<Pivot>();
		private const int MaxPivots = 400;

		private ATR _atr;

		// Last CLOSED higher-timeframe bar.
		private bool _hasHtf;
		private double _htfO, _htfH, _htfL, _htfC;
		private int _htfOriginBar;

		// ── DirectX ─────────────────────────────────────────────────────────
		private SharpDX.DirectWrite.TextFormat _textFormat;
		// Palette slots, indexed by PaletteSlot below.
		private const int SlotDisplacement = 0;
		private const int SlotFvg          = 1;
		private const int SlotEqual        = 2;
		private const int SlotHtf          = 3;
		private const int SlotConfluence   = 4;
		private const int SlotCount        = 5;

		private readonly SharpDX.Direct2D1.Brush[] _dxFills   = new SharpDX.Direct2D1.Brush[SlotCount];
		private readonly SharpDX.Direct2D1.Brush[] _dxBorders = new SharpDX.Direct2D1.Brush[SlotCount];
		private SharpDX.Direct2D1.Brush _dxLabel;

		// ── WPF menu state ──────────────────────────────────────────────────
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private bool ntBarActive;
		private Menu ntBarMenu;
		private NTMenuItem ntBartopMenuItem;
		private NTMenuItem ntShowHide, ntDispItem, ntFvgItem, ntEqualItem, ntHtfItem, ntLabelItem;
		private bool _showAll;
		private System.Windows.Style mainMenuItemStyle, systemMenuStyle;
		private System.Windows.Controls.TabItem tabItem;
		private ChartTab chartTab;

		private static SolidColorBrush MakeBrush(byte a, byte r, byte g, byte b)
		{
			var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
			brush.Freeze();
			return brush;
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Automates hand-drawn horizontal level bands: displacement candle anatomy, fair value gaps, equal highs/lows and higher-timeframe OHLC, merged into confluence zones.";
				Name = "ICNZones";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = true;

				// General
				ZoneThicknessTicks = 4;
				ExtendLeft = true;
				ShowLabels = true;
				MergeConfluence = true;
				MaxZonesPerSource = 6;

				// Displacement
				ShowDisplacement = true;
				DisplacementBasis = ICNMZDisplacementBasis.Body;
				DisplacementAtrMult = 2.5;
				AtrPeriod = 14;
				DisplacementLookback = 300;
				ShowWickHighLow = true;
				ShowBodyTopBottom = true;
				ShowBodyCe = true;
				ShowRangeCe = false;

				// FVG
				ShowFvg = true;
				FvgMinTicks = 2;
				RemoveFilledFvg = true;

				// Equal highs / lows
				ShowEqualLevels = true;
				SwingStrength = 3;
				EqualToleranceTicks = 6;
				MinTouches = 2;
				ShowSwingExtremes = true;
				ExtremeLookback = 40;

				// HTF OHLC
				ShowHtf = false;
				HtfPeriodType = BarsPeriodType.Minute;
				HtfPeriodValue = 240;
				ShowHtfOpen = true;
				ShowHtfHigh = true;
				ShowHtfLow = true;
				ShowHtfClose = true;

				// Colors
				// Palette deliberately excludes red/green so a zone never reads as candle colour,
				// and keeps five separable hues: blue = displacement, aqua = imbalance,
				// violet = repeat-touch, slate = higher timeframe, magenta = confluence (loudest,
				// since a multi-detector level is the one worth reacting to).
				DisplacementFillColor   = MakeBrush(55,  66, 150, 255);
				DisplacementBorderColor = MakeBrush(220,  66, 150, 255);
				FvgFillColor            = MakeBrush(55,   0, 210, 190);
				FvgBorderColor          = MakeBrush(215,   0, 210, 190);
				EqualFillColor          = MakeBrush(55, 168, 120, 255);
				EqualBorderColor        = MakeBrush(215, 168, 120, 255);
				HtfFillColor            = MakeBrush(50, 150, 165, 185);
				HtfBorderColor          = MakeBrush(210, 150, 165, 185);
				ConfluenceFillColor     = MakeBrush(90, 255,  64, 160);
				ConfluenceBorderColor   = MakeBrush(255, 255,  64, 160);
				LabelColor = Brushes.WhiteSmoke;
				ColorLabelsBySource = true;
				BorderWidth = 1;
			}
			else if (State == State.Configure)
			{
				// Always added so the HTF menu toggle can turn the levels back ON at runtime.
				AddDataSeries(HtfPeriodType, HtfPeriodValue);
			}
			else if (State == State.DataLoaded)
			{
				_atr = ATR(AtrPeriod);

				_textFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.Bold,
					SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal,
					10f);
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => CreateWPFControls());
			}
			else if (State == State.Terminated)
			{
				DisposeDxBrushes();
				if (_textFormat != null) { _textFormat.Dispose(); _textFormat = null; }

				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => DisposeWPFControls());
			}
		}

		// ══════════════════════════════════════════════════════════════════
		//  Bar processing
		// ══════════════════════════════════════════════════════════════════
		protected override void OnBarUpdate()
		{
			// Higher-timeframe series: capture the last CLOSED HTF candle.
			if (BarsInProgress == 1)
			{
				if (CurrentBars[1] < 1) return;

				_htfO = Opens[1][1];
				_htfH = Highs[1][1];
				_htfL = Lows[1][1];
				_htfC = Closes[1][1];
				_htfOriginBar = CurrentBars[0];
				_hasHtf = true;
				return;
			}

			if (BarsInProgress != 0) return;
			if (CurrentBar < Math.Max(AtrPeriod, 2 * SwingStrength + 1)) return;

			// Detectors always run regardless of the Show* flags: the flags filter at compose time
			// only, so a menu toggle can reveal zones instantly without waiting for new bars.
			lock (_zoneLock)
			{
				DetectDisplacement();
				DetectFvg();
				DetectEqualLevels();
				BuildHtfZones();

				MaintainFvg();

				_renderZones = ComposeRenderZones();
			}
		}

		// ── 1. Displacement candle anatomy ─────────────────────────────────
		private void DetectDisplacement()
		{
			// Age out old anatomy every bar, not only on bars that produce a new displacement.
			_displacementZones.RemoveAll(z => CurrentBar - z.OriginBar > DisplacementLookback);

			double atr = _atr[0];
			if (atr <= 0) return;

			double body = Math.Abs(Close[0] - Open[0]);
			double range = High[0] - Low[0];
			double basis = DisplacementBasis == ICNMZDisplacementBasis.Body ? body : range;

			if (basis < DisplacementAtrMult * atr) return;

			double bodyTop = Math.Max(Open[0], Close[0]);
			double bodyBtm = Math.Min(Open[0], Close[0]);
			int bar = CurrentBar;

			var fresh = new List<Zone>();
			if (ShowWickHighLow)
			{
				fresh.Add(MakeLevelZone(High[0], bar, ZoneSource.Displacement, "DISP H"));
				fresh.Add(MakeLevelZone(Low[0], bar, ZoneSource.Displacement, "DISP L"));
			}
			if (ShowBodyTopBottom)
			{
				fresh.Add(MakeLevelZone(bodyTop, bar, ZoneSource.Displacement, "DISP OB top"));
				fresh.Add(MakeLevelZone(bodyBtm, bar, ZoneSource.Displacement, "DISP OB btm"));
			}
			if (ShowBodyCe)
				fresh.Add(MakeLevelZone((bodyTop + bodyBtm) / 2.0, bar, ZoneSource.Displacement, "DISP body CE"));
			if (ShowRangeCe)
				fresh.Add(MakeLevelZone((High[0] + Low[0]) / 2.0, bar, ZoneSource.Displacement, "DISP range CE"));

			// Newest displacement first; cap the list so old anatomy fades out.
			_displacementZones.InsertRange(0, fresh);

			int cap = Math.Max(1, MaxZonesPerSource) * 6;
			if (_displacementZones.Count > cap)
				_displacementZones.RemoveRange(cap, _displacementZones.Count - cap);
		}

		// ── 2. Fair value gaps ─────────────────────────────────────────────
		private void DetectFvg()
		{
			if (CurrentBar < 2) return;

			double minSize = FvgMinTicks * TickSize;

			// Bullish imbalance: gap between the high two bars back and the current low.
			if (Low[0] - High[2] >= minSize)
				_fvgZones.Insert(0, new Zone
				{
					Btm = High[2],
					Top = Low[0],
					OriginBar = CurrentBar - 1,
					Sources = ZoneSource.Fvg,
					Label = "FVG+"
				});

			// Bearish imbalance.
			if (Low[2] - High[0] >= minSize)
				_fvgZones.Insert(0, new Zone
				{
					Btm = High[0],
					Top = Low[2],
					OriginBar = CurrentBar - 1,
					Sources = ZoneSource.Fvg,
					Label = "FVG-"
				});

			int cap = Math.Max(1, MaxZonesPerSource) * 4;
			if (_fvgZones.Count > cap)
				_fvgZones.RemoveRange(cap, _fvgZones.Count - cap);
		}

		private void MaintainFvg()
		{
			if (!RemoveFilledFvg || _fvgZones.Count == 0) return;

			// A gap is considered filled once price trades fully through it.
			_fvgZones.RemoveAll(z => z.OriginBar < CurrentBar && Low[0] <= z.Btm && High[0] >= z.Top);
		}

		// ── 3. Equal highs / lows ──────────────────────────────────────────
		private void DetectEqualLevels()
		{
			int s = SwingStrength;
			if (CurrentBar < 2 * s) return;

			// Confirm the pivot candidate sitting s bars back.
			bool isHigh = true, isLow = true;
			double ph = High[s], pl = Low[s];

			for (int i = 0; i <= 2 * s; i++)
			{
				if (i == s) continue;
				if (High[i] >= ph) isHigh = false;
				if (Low[i] <= pl) isLow = false;
				if (!isHigh && !isLow) break;
			}

			if (isHigh) _pivots.Insert(0, new Pivot { Price = ph, Bar = CurrentBar - s, IsHigh = true });
			if (isLow) _pivots.Insert(0, new Pivot { Price = pl, Bar = CurrentBar - s, IsHigh = false });

			if (_pivots.Count > MaxPivots)
				_pivots.RemoveRange(MaxPivots, _pivots.Count - MaxPivots);

			ClusterPivots();
		}

		private void ClusterPivots()
		{
			_equalZones.Clear();
			if (_pivots.Count == 0) return;

			double tol = EqualToleranceTicks * TickSize;

			foreach (bool side in new[] { true, false })
			{
				var group = _pivots.Where(p => p.IsHigh == side).OrderBy(p => p.Price).ToList();
				if (group.Count < MinTouches) continue;

				int i = 0;
				while (i < group.Count)
				{
					int j = i + 1;
					double lo = group[i].Price, hi = group[i].Price;
					int newest = group[i].Bar;

					// Chain outward while each next pivot stays within tolerance of the cluster top.
					while (j < group.Count && group[j].Price - hi <= tol)
					{
						hi = group[j].Price;
						if (group[j].Bar > newest) newest = group[j].Bar;
						j++;
					}

					int touches = j - i;
					if (touches >= MinTouches)
					{
						double pad = ZoneThicknessTicks * TickSize / 2.0;
						_equalZones.Add(new Zone
						{
							Btm = lo - pad,
							Top = hi + pad,
							OriginBar = group[i].Bar,
							Sources = ZoneSource.EqualLevels,
							Touches = touches,
							Label = (side ? "EQH x" : "EQL x") + touches
						});
					}

					i = j;
				}
			}

			AddSwingExtremes();

			// Keep the most recent clusters.
			_equalZones.Sort((a, b) => b.OriginBar.CompareTo(a.OriginBar));
			int cap = Math.Max(1, MaxZonesPerSource) * 2;
			if (_equalZones.Count > cap)
				_equalZones.RemoveRange(cap, _equalZones.Count - cap);
		}

		// A sharp reaction low/high that price never returned to still deserves a level, even
		// though it can never satisfy MinTouches. A pivot qualifies when no same-side pivot within
		// +/-ExtremeLookback bars is more extreme, and it is not already inside an equal-level band.
		private void AddSwingExtremes()
		{
			if (!ShowSwingExtremes || _pivots.Count == 0) return;

			// Bounded scan: only the most recent pivots matter, and this runs on every bar.
			int scan = Math.Min(_pivots.Count, 80);
			double pad = Math.Max(1, ZoneThicknessTicks) * TickSize / 2.0;

			for (int i = 0; i < scan; i++)
			{
				Pivot p = _pivots[i];

				bool major = true;
				for (int j = 0; j < scan; j++)
				{
					if (j == i) continue;
					Pivot q = _pivots[j];
					if (q.IsHigh != p.IsHigh) continue;
					if (Math.Abs(q.Bar - p.Bar) > ExtremeLookback) continue;

					if (p.IsHigh ? q.Price > p.Price : q.Price < p.Price) { major = false; break; }
				}
				if (!major) continue;

				// An equal-level band already covering this price wins — it carries the touch count.
				bool covered = false;
				for (int k = 0; k < _equalZones.Count; k++)
					if (p.Price >= _equalZones[k].Btm - pad && p.Price <= _equalZones[k].Top + pad) { covered = true; break; }
				if (covered) continue;

				_equalZones.Add(new Zone
				{
					Btm = p.Price - pad,
					Top = p.Price + pad,
					OriginBar = p.Bar,
					Sources = ZoneSource.EqualLevels,
					Touches = 1,
					Label = p.IsHigh ? "SWING H" : "SWING L"
				});
			}
		}

		// ── 4. Higher-timeframe OHLC ───────────────────────────────────────
		private void BuildHtfZones()
		{
			_htfZones.Clear();
			if (!_hasHtf) return;

			if (ShowHtfOpen) _htfZones.Add(MakeLevelZone(_htfO, _htfOriginBar, ZoneSource.Htf, "HTF O"));
			if (ShowHtfHigh) _htfZones.Add(MakeLevelZone(_htfH, _htfOriginBar, ZoneSource.Htf, "HTF H"));
			if (ShowHtfLow) _htfZones.Add(MakeLevelZone(_htfL, _htfOriginBar, ZoneSource.Htf, "HTF L"));
			if (ShowHtfClose) _htfZones.Add(MakeLevelZone(_htfC, _htfOriginBar, ZoneSource.Htf, "HTF C"));
		}

		private Zone MakeLevelZone(double price, int bar, ZoneSource src, string label)
		{
			double half = Math.Max(1, ZoneThicknessTicks) * TickSize / 2.0;
			return new Zone { Top = price + half, Btm = price - half, OriginBar = bar, Sources = src, Label = label };
		}

		// ── Compose + confluence merge ─────────────────────────────────────
		private List<Zone> ComposeRenderZones()
		{
			var all = new List<Zone>();

			// mult scales the cap for sources that emit several levels per event (displacement
			// anatomy is up to 6 bands from one candle, HTF is 4 from one candle).
			Action<List<Zone>, bool, int> take = (src, enabled, mult) =>
			{
				if (!enabled) return;
				int n = Math.Min(src.Count, Math.Max(1, MaxZonesPerSource) * mult);
				for (int i = 0; i < n; i++) all.Add(src[i]);
			};

			take(_displacementZones, ShowDisplacement, 6);
			take(_fvgZones, ShowFvg, 1);
			take(_equalZones, ShowEqualLevels, 2);
			take(_htfZones, ShowHtf, 4);

			if (!MergeConfluence || all.Count < 2)
				return all;

			// Merge overlapping zones into a single band carrying the union of their sources.
			var merged = new List<Zone>();
			foreach (Zone z in all.OrderBy(z => z.Btm))
			{
				Zone target = merged.FirstOrDefault(m => m.Overlaps(z, 0));
				if (target == null)
				{
					merged.Add(new Zone
					{
						Top = z.Top,
						Btm = z.Btm,
						OriginBar = z.OriginBar,
						Sources = z.Sources,
						Touches = z.Touches,
						Label = z.Label
					});
				}
				else
				{
					target.Top = Math.Max(target.Top, z.Top);
					target.Btm = Math.Min(target.Btm, z.Btm);
					target.OriginBar = Math.Min(target.OriginBar, z.OriginBar);
					target.Touches = Math.Max(target.Touches, z.Touches);
					if ((target.Sources & z.Sources) == 0)
						target.Label = target.Label + " + " + z.Label;
					target.Sources |= z.Sources;
				}
			}

			return merged;
		}

		private static int SourceCount(ZoneSource s)
		{
			int n = 0;
			if ((s & ZoneSource.Displacement) != 0) n++;
			if ((s & ZoneSource.Fvg) != 0) n++;
			if ((s & ZoneSource.EqualLevels) != 0) n++;
			if ((s & ZoneSource.Htf) != 0) n++;
			return n;
		}

		// ══════════════════════════════════════════════════════════════════
		//  Rendering
		// ══════════════════════════════════════════════════════════════════
		public override void OnRenderTargetChanged()
		{
			DisposeDxBrushes();
			if (RenderTarget == null) return;

			_dxFills[SlotDisplacement]   = DisplacementFillColor.ToDxBrush(RenderTarget);
			_dxBorders[SlotDisplacement] = DisplacementBorderColor.ToDxBrush(RenderTarget);
			_dxFills[SlotFvg]            = FvgFillColor.ToDxBrush(RenderTarget);
			_dxBorders[SlotFvg]          = FvgBorderColor.ToDxBrush(RenderTarget);
			_dxFills[SlotEqual]          = EqualFillColor.ToDxBrush(RenderTarget);
			_dxBorders[SlotEqual]        = EqualBorderColor.ToDxBrush(RenderTarget);
			_dxFills[SlotHtf]            = HtfFillColor.ToDxBrush(RenderTarget);
			_dxBorders[SlotHtf]          = HtfBorderColor.ToDxBrush(RenderTarget);
			_dxFills[SlotConfluence]     = ConfluenceFillColor.ToDxBrush(RenderTarget);
			_dxBorders[SlotConfluence]   = ConfluenceBorderColor.ToDxBrush(RenderTarget);

			_dxLabel = LabelColor.ToDxBrush(RenderTarget);
		}

		private void DisposeDxBrushes()
		{
			for (int i = 0; i < SlotCount; i++)
			{
				if (_dxFills[i]   != null) { _dxFills[i].Dispose();   _dxFills[i]   = null; }
				if (_dxBorders[i] != null) { _dxBorders[i].Dispose(); _dxBorders[i] = null; }
			}
			if (_dxLabel != null) { _dxLabel.Dispose(); _dxLabel = null; }
		}

		// A merged zone uses the confluence palette; a single-source zone uses its own.
		private static int SlotFor(ZoneSource s, int confluence)
		{
			if (confluence > 1) return SlotConfluence;
			if ((s & ZoneSource.Displacement) != 0) return SlotDisplacement;
			if ((s & ZoneSource.Fvg)          != 0) return SlotFvg;
			if ((s & ZoneSource.EqualLevels)  != 0) return SlotEqual;
			if ((s & ZoneSource.Htf)          != 0) return SlotHtf;
			return SlotDisplacement;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || _dxFills[SlotDisplacement] == null || ChartBars == null) return;

			List<Zone> zones = _renderZones;
			if (zones == null || zones.Count == 0) return;

			float canvasLeft = chartControl.CanvasLeft;
			float canvasRight = chartControl.CanvasRight;

			SharpDX.Direct2D1.AntialiasMode prevAa = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			foreach (Zone z in zones)
			{
				float yTop = chartScale.GetYByValue(z.Top);
				float yBtm = chartScale.GetYByValue(z.Btm);
				if (yBtm < chartScale.Height * -1 || yTop > chartScale.Height * 2) continue;

				float x0 = canvasLeft;
				if (!ExtendLeft)
				{
					int barsAgo = ChartBars.ToIndex - z.OriginBar;
					int idx = ChartBars.ToIndex - Math.Max(0, barsAgo);
					x0 = chartControl.GetXByBarIndex(ChartBars, idx);
					if (x0 < canvasLeft) x0 = canvasLeft;
				}
				if (x0 >= canvasRight) continue;

				int confluence = SourceCount(z.Sources);
				int slot = SlotFor(z.Sources, confluence);

				SharpDX.Direct2D1.Brush fill   = _dxFills[slot];
				SharpDX.Direct2D1.Brush border = _dxBorders[slot];
				if (fill == null || border == null) continue;

				// Single-source zones render at their configured alpha; confluence zones get denser
				// the more detectors agree.
				fill.Opacity = confluence > 1
					? (float)Math.Min(1.0, 0.6 + 0.2 * (confluence - 1))
					: 1f;

				var rect = new SharpDX.RectangleF(x0, yTop, canvasRight - x0, Math.Max(1f, yBtm - yTop));
				RenderTarget.FillRectangle(rect, fill);

				RenderTarget.DrawLine(new SharpDX.Vector2(x0, yTop), new SharpDX.Vector2(canvasRight, yTop), border, BorderWidth);
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, yBtm), new SharpDX.Vector2(canvasRight, yBtm), border, BorderWidth);

				if (ShowLabels && _textFormat != null && _dxLabel != null && !string.IsNullOrEmpty(z.Label))
				{
					// A band no taller than a single-level zone reads as one price; anything
					// wider (an FVG, or a merged cluster) reads as a range.
					double levelHeight = Math.Max(1, ZoneThicknessTicks) * TickSize;
					string price = z.Top - z.Btm <= levelHeight * 1.5
						? Instrument.MasterInstrument.FormatPrice(z.Mid)
						: Instrument.MasterInstrument.FormatPrice(z.Btm) + "-" + Instrument.MasterInstrument.FormatPrice(z.Top);

					string text = z.Label + "  " + price;
					if (confluence > 1) text += "  (" + confluence + "x)";

					using (var layout = new SharpDX.DirectWrite.TextLayout(
						NinjaTrader.Core.Globals.DirectWriteFactory, text, _textFormat, 400f, 14f))
					{
						float lx = canvasRight - (float)layout.Metrics.Width - 6f;
						RenderTarget.DrawTextLayout(
							new SharpDX.Vector2(lx, (yTop + yBtm) / 2f - 7f),
							layout, ColorLabelsBySource ? border : _dxLabel, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
					}
				}
			}

			RenderTarget.AntialiasMode = prevAa;
		}

		// ══════════════════════════════════════════════════════════════════
		//  WPF Controls (bar-top menu)
		// ══════════════════════════════════════════════════════════════════
		private static System.Windows.Shapes.Rectangle MakeGutterIcon() =>
			new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Fill = Brushes.Black };

		private NTMenuItem MakeItem(string header) =>
			new NTMenuItem { Header = header, StaysOpenOnClick = true, Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };

		protected void CreateWPFControls()
		{
			try
			{
				chartWindow = System.Windows.Window.GetWindow(ChartControl.Parent) as NinjaTrader.Gui.Chart.Chart;
				if (chartWindow == null) return;

				mainMenuItemStyle = Application.Current.TryFindResource("MainMenuItem") as Style;
				systemMenuStyle = Application.Current.TryFindResource("SystemMenuStyle") as Style;
				if (mainMenuItemStyle == null || systemMenuStyle == null) return;

				ntBarMenu = new Menu
				{
					VerticalAlignment = VerticalAlignment.Top,
					VerticalContentAlignment = VerticalAlignment.Top,
					Style = systemMenuStyle
				};

				ntBartopMenuItem = new NTMenuItem
				{
					Header = "ICN_Zones",
					Margin = new Thickness(0),
					Padding = new Thickness(1),
					Style = mainMenuItemStyle,
					VerticalAlignment = VerticalAlignment.Center,
				};
				ntBarMenu.Items.Add(ntBartopMenuItem);

				_showAll = ShowDisplacement && ShowFvg && ShowEqualLevels && ShowHtf;

				ntShowHide = MakeItem(_showAll ? "Hide All" : "Show All"); ntShowHide.Tag = "ShowAll";
				ntShowHide.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntShowHide);
				ntBartopMenuItem.Items.Add(new Separator());

				ntDispItem = MakeItem(""); ntDispItem.Tag = "ShowDisplacement";
				ntFvgItem = MakeItem(""); ntFvgItem.Tag = "ShowFvg";
				ntEqualItem = MakeItem(""); ntEqualItem.Tag = "ShowEqualLevels";
				ntHtfItem = MakeItem(""); ntHtfItem.Tag = "ShowHtf";
				ntLabelItem = MakeItem(""); ntLabelItem.Tag = "ShowLabels";

				// Tint each menu entry with its zone color so the menu doubles as a legend.
				ntDispItem.Foreground  = DisplacementBorderColor;
				ntFvgItem.Foreground   = FvgBorderColor;
				ntEqualItem.Foreground = EqualBorderColor;
				ntHtfItem.Foreground   = HtfBorderColor;

				ntDispItem.Click += NTBarMenu_Click;
				ntFvgItem.Click += NTBarMenu_Click;
				ntEqualItem.Click += NTBarMenu_Click;
				ntHtfItem.Click += NTBarMenu_Click;
				ntLabelItem.Click += NTBarMenu_Click;

				ntBartopMenuItem.Items.Add(ntDispItem);
				ntBartopMenuItem.Items.Add(ntFvgItem);
				ntBartopMenuItem.Items.Add(ntEqualItem);
				ntBartopMenuItem.Items.Add(ntHtfItem);
				ntBartopMenuItem.Items.Add(new Separator());
				ntBartopMenuItem.Items.Add(ntLabelItem);

				UpdateMenuHeaders();

				if (TabSelected()) ShowWPFControls();
				chartWindow.MainTabControl.SelectionChanged += TabChangedHandler;
			}
			catch (Exception ex) { Print(ex); }
		}

		private void UpdateMenuHeaders()
		{
			try
			{
				_showAll = ShowDisplacement && ShowFvg && ShowEqualLevels && ShowHtf;

				if (ntShowHide != null) ntShowHide.Header = _showAll ? "Hide All" : "Show All";
				if (ntDispItem != null) ntDispItem.Header = (ShowDisplacement ? "Hide" : "Show") + " Displacement Anatomy";
				if (ntFvgItem != null) ntFvgItem.Header = (ShowFvg ? "Hide" : "Show") + " Fair Value Gaps";
				if (ntEqualItem != null) ntEqualItem.Header = (ShowEqualLevels ? "Hide" : "Show") + " Equal Highs/Lows";
				if (ntHtfItem != null) ntHtfItem.Header = (ShowHtf ? "Hide" : "Show") + " HTF OHLC";
				if (ntLabelItem != null) ntLabelItem.Header = (ShowLabels ? "Hide" : "Show") + " Labels";
			}
			catch { }
		}

		protected void NTBarMenu_Click(object sender, RoutedEventArgs e)
		{
			MenuItem item = sender as MenuItem;
			if (item == null) return;
			string tag = item.Tag as string;
			if (tag == null) return;

			try
			{
				switch (tag)
				{
					case "ShowAll":
						_showAll = !_showAll;
						ShowDisplacement = ShowFvg = ShowEqualLevels = ShowHtf = _showAll;
						break;
					case "ShowDisplacement": ShowDisplacement = !ShowDisplacement; break;
					case "ShowFvg": ShowFvg = !ShowFvg; break;
					case "ShowEqualLevels": ShowEqualLevels = !ShowEqualLevels; break;
					case "ShowHtf": ShowHtf = !ShowHtf; break;
					case "ShowLabels": ShowLabels = !ShowLabels; break;
				}

				// Detector lists are left intact — only the filter changes, so zones reappear
				// immediately instead of waiting for the next bar to rebuild them.
				lock (_zoneLock)
					_renderZones = ComposeRenderZones();
			}
			catch (Exception ex) { Print("ICNZones menu error: " + ex.Message); }
			finally
			{
				UpdateMenuHeaders();
				if (ChartControl != null) ChartControl.InvalidateVisual();
			}
		}

		private void DisposeWPFControls()
		{
			try
			{
				if (chartWindow != null)
					chartWindow.MainTabControl.SelectionChanged -= TabChangedHandler;
				HideWPFControls();
			}
			catch (Exception ex) { Print(ex); }
		}

		private void HideWPFControls()
		{
			if (ntBarActive) { chartWindow.MainMenu.Remove(ntBarMenu); ntBarActive = false; }
		}

		private void ShowWPFControls()
		{
			try
			{
				if (!ntBarActive) { chartWindow.MainMenu.Add(ntBarMenu); ntBarActive = true; }
			}
			catch (Exception ex) { Print(ex); }
		}

		private void TabChangedHandler(object sender, SelectionChangedEventArgs e)
		{
			try
			{
				if (e.AddedItems.Count <= 0) return;
				tabItem = e.AddedItems[0] as System.Windows.Controls.TabItem;
				if (tabItem == null) return;
				chartTab = tabItem.Content as ChartTab;
				if (chartTab == null) return;
				if (TabSelected()) ShowWPFControls(); else HideWPFControls();
			}
			catch (Exception ex) { Print(ex); }
		}

		private bool TabSelected()
		{
			try
			{
				if (ChartControl == null || chartWindow == null || chartWindow.MainTabControl == null)
					return false;
				return ChartControl.ChartTab ==
					((chartWindow.MainTabControl.Items.GetItemAt(chartWindow.MainTabControl.SelectedIndex)
					  as System.Windows.Controls.TabItem).Content as ChartTab);
			}
			catch (Exception ex) { Print(ex); return false; }
		}

		#region Properties

		// ── General ────────────────────────────────────────────────────────
		[Range(1, 200)]
		[Display(Name = "Zone Thickness (ticks)", Description = "Height of a band drawn around a single price level.", Order = 0, GroupName = "01 General")]
		public int ZoneThicknessTicks { get; set; }

		[Display(Name = "Extend Left", Description = "Draw zones across the full chart width instead of starting at their origin bar.", Order = 1, GroupName = "01 General")]
		public bool ExtendLeft { get; set; }

		[Display(Name = "Show Labels", Order = 2, GroupName = "01 General")]
		public bool ShowLabels { get; set; }

		[Display(Name = "Merge Confluence", Description = "Merge overlapping zones from different detectors into one band; fill opacity scales with how many detectors agree.", Order = 3, GroupName = "01 General")]
		public bool MergeConfluence { get; set; }

		[Range(1, 50)]
		[Display(Name = "Max Zones Per Source", Order = 4, GroupName = "01 General")]
		public int MaxZonesPerSource { get; set; }

		// ── Displacement ───────────────────────────────────────────────────
		[Display(Name = "Show Displacement Anatomy", Order = 0, GroupName = "02 Displacement")]
		public bool ShowDisplacement { get; set; }

		[Display(Name = "Displacement Basis", Description = "Measure the candle by its body or its full high-low range.", Order = 1, GroupName = "02 Displacement")]
		public ICNMZDisplacementBasis DisplacementBasis { get; set; }

		[Range(0.1, 20.0)]
		[Display(Name = "Displacement ATR Multiple", Description = "Candle qualifies when basis > multiple x ATR.", Order = 2, GroupName = "02 Displacement")]
		public double DisplacementAtrMult { get; set; }

		[Range(1, 200)]
		[Display(Name = "ATR Period", Order = 3, GroupName = "02 Displacement")]
		public int AtrPeriod { get; set; }

		[Range(10, 5000)]
		[Display(Name = "Displacement Lookback (bars)", Description = "Drop displacement levels older than this many bars.", Order = 4, GroupName = "02 Displacement")]
		public int DisplacementLookback { get; set; }

		[Display(Name = "Level: Wick High/Low", Order = 5, GroupName = "02 Displacement")]
		public bool ShowWickHighLow { get; set; }

		[Display(Name = "Level: Body Top/Bottom", Order = 6, GroupName = "02 Displacement")]
		public bool ShowBodyTopBottom { get; set; }

		[Display(Name = "Level: Body CE (midpoint)", Order = 7, GroupName = "02 Displacement")]
		public bool ShowBodyCe { get; set; }

		[Display(Name = "Level: Range CE (midpoint)", Order = 8, GroupName = "02 Displacement")]
		public bool ShowRangeCe { get; set; }

		// ── FVG ────────────────────────────────────────────────────────────
		[Display(Name = "Show Fair Value Gaps", Order = 0, GroupName = "03 Fair Value Gaps")]
		public bool ShowFvg { get; set; }

		[Range(0, 1000)]
		[Display(Name = "Min Gap Size (ticks)", Order = 1, GroupName = "03 Fair Value Gaps")]
		public int FvgMinTicks { get; set; }

		[Display(Name = "Remove Filled Gaps", Order = 2, GroupName = "03 Fair Value Gaps")]
		public bool RemoveFilledFvg { get; set; }

		// ── Equal levels ───────────────────────────────────────────────────
		[Display(Name = "Show Equal Highs/Lows", Order = 0, GroupName = "04 Equal Highs-Lows")]
		public bool ShowEqualLevels { get; set; }

		[Range(1, 50)]
		[Display(Name = "Swing Strength", Description = "Bars required on each side of a pivot.", Order = 1, GroupName = "04 Equal Highs-Lows")]
		public int SwingStrength { get; set; }

		[Range(0, 1000)]
		[Display(Name = "Equal Tolerance (ticks)", Description = "Pivots within this distance are treated as the same level.", Order = 2, GroupName = "04 Equal Highs-Lows")]
		public int EqualToleranceTicks { get; set; }

		[Range(2, 20)]
		[Display(Name = "Min Touches", Order = 3, GroupName = "04 Equal Highs-Lows")]
		public int MinTouches { get; set; }

		[Display(Name = "Show Single-Touch Swing Extremes", Description = "Also mark sharp reaction highs/lows that price never returned to, which can never reach Min Touches.", Order = 4, GroupName = "04 Equal Highs-Lows")]
		public bool ShowSwingExtremes { get; set; }

		[Range(5, 2000)]
		[Display(Name = "Swing Extreme Lookback (bars)", Description = "A pivot is a swing extreme when no same-side pivot within this many bars either side is more extreme. Higher = fewer, more significant levels.", Order = 5, GroupName = "04 Equal Highs-Lows")]
		public int ExtremeLookback { get; set; }

		// ── HTF ────────────────────────────────────────────────────────────
		[Display(Name = "Show HTF OHLC", Description = "Toggles the higher-timeframe levels. The HTF series is always loaded; changing HTF Period Type/Value requires reloading the indicator.", Order = 0, GroupName = "05 HTF OHLC")]
		public bool ShowHtf { get; set; }

		[Display(Name = "HTF Period Type", Order = 1, GroupName = "05 HTF OHLC")]
		public BarsPeriodType HtfPeriodType { get; set; }

		[Range(1, int.MaxValue)]
		[Display(Name = "HTF Period Value", Order = 2, GroupName = "05 HTF OHLC")]
		public int HtfPeriodValue { get; set; }

		[Display(Name = "HTF Open", Order = 3, GroupName = "05 HTF OHLC")]
		public bool ShowHtfOpen { get; set; }

		[Display(Name = "HTF High", Order = 4, GroupName = "05 HTF OHLC")]
		public bool ShowHtfHigh { get; set; }

		[Display(Name = "HTF Low", Order = 5, GroupName = "05 HTF OHLC")]
		public bool ShowHtfLow { get; set; }

		[Display(Name = "HTF Close", Order = 6, GroupName = "05 HTF OHLC")]
		public bool ShowHtfClose { get; set; }

		// ── Colors ─────────────────────────────────────────────────────────
		[XmlIgnore]
		[Display(Name = "Displacement Fill", Order = 0, GroupName = "06 Colors")]
		public Brush DisplacementFillColor { get; set; }
		[Browsable(false)]
		public string DisplacementFillColorSerialize { get { return Serialize.BrushToString(DisplacementFillColor); } set { DisplacementFillColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Displacement Border", Order = 1, GroupName = "06 Colors")]
		public Brush DisplacementBorderColor { get; set; }
		[Browsable(false)]
		public string DisplacementBorderColorSerialize { get { return Serialize.BrushToString(DisplacementBorderColor); } set { DisplacementBorderColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "FVG Fill", Order = 2, GroupName = "06 Colors")]
		public Brush FvgFillColor { get; set; }
		[Browsable(false)]
		public string FvgFillColorSerialize { get { return Serialize.BrushToString(FvgFillColor); } set { FvgFillColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "FVG Border", Order = 3, GroupName = "06 Colors")]
		public Brush FvgBorderColor { get; set; }
		[Browsable(false)]
		public string FvgBorderColorSerialize { get { return Serialize.BrushToString(FvgBorderColor); } set { FvgBorderColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Equal Highs/Lows Fill", Order = 4, GroupName = "06 Colors")]
		public Brush EqualFillColor { get; set; }
		[Browsable(false)]
		public string EqualFillColorSerialize { get { return Serialize.BrushToString(EqualFillColor); } set { EqualFillColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Equal Highs/Lows Border", Order = 5, GroupName = "06 Colors")]
		public Brush EqualBorderColor { get; set; }
		[Browsable(false)]
		public string EqualBorderColorSerialize { get { return Serialize.BrushToString(EqualBorderColor); } set { EqualBorderColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "HTF OHLC Fill", Order = 6, GroupName = "06 Colors")]
		public Brush HtfFillColor { get; set; }
		[Browsable(false)]
		public string HtfFillColorSerialize { get { return Serialize.BrushToString(HtfFillColor); } set { HtfFillColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "HTF OHLC Border", Order = 7, GroupName = "06 Colors")]
		public Brush HtfBorderColor { get; set; }
		[Browsable(false)]
		public string HtfBorderColorSerialize { get { return Serialize.BrushToString(HtfBorderColor); } set { HtfBorderColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Confluence Fill", Order = 8, GroupName = "06 Colors")]
		public Brush ConfluenceFillColor { get; set; }
		[Browsable(false)]
		public string ConfluenceFillColorSerialize { get { return Serialize.BrushToString(ConfluenceFillColor); } set { ConfluenceFillColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Confluence Border", Order = 9, GroupName = "06 Colors")]
		public Brush ConfluenceBorderColor { get; set; }
		[Browsable(false)]
		public string ConfluenceBorderColorSerialize { get { return Serialize.BrushToString(ConfluenceBorderColor); } set { ConfluenceBorderColor = Serialize.StringToBrush(value); } }

		[XmlIgnore]
		[Display(Name = "Label", Description = "Used when Color Labels By Source is off.", Order = 10, GroupName = "06 Colors")]
		public Brush LabelColor { get; set; }
		[Browsable(false)]
		public string LabelColorSerialize { get { return Serialize.BrushToString(LabelColor); } set { LabelColor = Serialize.StringToBrush(value); } }

		[Display(Name = "Color Labels By Source", Description = "Draw each label in its zone's border color instead of the single Label color.", Order = 11, GroupName = "06 Colors")]
		public bool ColorLabelsBySource { get; set; }

		[Range(1, 10)]
		[Display(Name = "Border Width", Order = 12, GroupName = "06 Colors")]
		public int BorderWidth { get; set; }

		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private ItCodeNerd.ICNZones[] cacheICNZones;
		public ItCodeNerd.ICNZones ICNZones()
		{
			return ICNZones(Input);
		}

		public ItCodeNerd.ICNZones ICNZones(ISeries<double> input)
		{
			if (cacheICNZones != null)
				for (int idx = 0; idx < cacheICNZones.Length; idx++)
					if (cacheICNZones[idx] != null &&  cacheICNZones[idx].EqualsInput(input))
						return cacheICNZones[idx];
			return CacheIndicator<ItCodeNerd.ICNZones>(new ItCodeNerd.ICNZones(), input, ref cacheICNZones);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.ItCodeNerd.ICNZones ICNZones()
		{
			return indicator.ICNZones(Input);
		}

		public Indicators.ItCodeNerd.ICNZones ICNZones(ISeries<double> input )
		{
			return indicator.ICNZones(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.ItCodeNerd.ICNZones ICNZones()
		{
			return indicator.ICNZones(Input);
		}

		public Indicators.ItCodeNerd.ICNZones ICNZones(ISeries<double> input )
		{
			return indicator.ICNZones(input);
		}
	}
}

#endregion
