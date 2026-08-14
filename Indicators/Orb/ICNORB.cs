#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
#endregion

// ---------------------------------------------------------------------------------------------
// ICNORB
//
// Standalone Opening-Range-Breakout indicator, extracted from ICNImportantLines' ORB feature set:
//   - NY / Asia / Europe opening-range high & low lines (first N minutes of each session).
//   - NY / London pre-open boxes (high/low of the N minutes BEFORE session open, +/- Points).
// All lines are drawn via SharpDX in OnRender (bounded to the session window, extending to the
// live edge while the window is still open) to match ICNImportantLines' rendering exactly.
//
// Session windows use Bars.IsFirstBarOfSession to reset each ORB/pre-open box for the new trading
// day; the original's Globex 18:00-roll session tracking was not ported since this indicator only
// needs "new day started", not full session-VWAP bookkeeping.
// ---------------------------------------------------------------------------------------------

namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{
	public class ICNORB : Indicator
	{
		private struct OrbState
		{
			public double High, Low;
			public bool Done, Started;
			public DateTime Start, End;
			public void Reset()
			{
				High = double.MinValue; Low = double.MaxValue;
				Done = false; Started = false;
				Start = DateTime.MinValue; End = DateTime.MinValue;
			}
		}

		private OrbState _orbNY, _orbAsia, _orbEurope;
		private OrbState _preOpenNY, _preOpenLondon;

		private const int MaxHistoryDays = 250;
		private System.Collections.Generic.List<OrbState> _histOrbNY = new System.Collections.Generic.List<OrbState>();
		private System.Collections.Generic.List<OrbState> _histOrbAsia = new System.Collections.Generic.List<OrbState>();
		private System.Collections.Generic.List<OrbState> _histOrbEurope = new System.Collections.Generic.List<OrbState>();
		private System.Collections.Generic.List<OrbState> _histPreOpenNY = new System.Collections.Generic.List<OrbState>();
		private System.Collections.Generic.List<OrbState> _histPreOpenLondon = new System.Collections.Generic.List<OrbState>();

		private TimeSpan _tsAsiaStart, _tsEuropeStart, _tsNYStart;
		private TimeZoneInfo _cachedNYTimeZone;
		private bool _inSession;

		private SharpDX.DirectWrite.TextFormat _textFormat;
		private SharpDX.Direct2D1.Brush _dxOrbHighBrush, _dxOrbLowBrush;
		private SharpDX.Direct2D1.Brush _dxOrbAsiaHighBrush, _dxOrbAsiaLowBrush;
		private SharpDX.Direct2D1.Brush _dxOrbEuropeHighBrush, _dxOrbEuropeLowBrush;
		private SharpDX.Direct2D1.Brush _dxNYPreOpenHighBrush, _dxNYPreOpenLowBrush;
		private SharpDX.Direct2D1.Brush _dxLondonPreOpenHighBrush, _dxLondonPreOpenLowBrush;

		// ── WPF menu state ──────────────────────────────────────────────────
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private bool ntBarActive;
		private Menu ntBarMenu;
		private NTMenuItem ntBartopMenuItem;
		private NTMenuItem ntShowHide;
		private NTMenuItem ntORBItem, ntAsiaORBItem, ntEuropeORBItem, ntNYPreOpenItem, ntLondonPreOpenItem;
		private bool _showAll;
		private System.Windows.Style mainMenuItemStyle, systemMenuStyle;
		private System.Windows.Controls.TabItem tabItem;
		private ChartTab chartTab;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Opening Range Breakout lines (NY/Asia/Europe) plus NY/London pre-open boxes, extracted from ICNImportantLines.";
				Name = "ICNORB";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = true;

				AsiaStartTime = 2000;
				EuropeStartTime = 300;
				NYStartTime = 930;

				ShowLineLabels = true;
				LabelFontSize = 11;
				LabelAlignment = TextAlignment.Right;

				ShowORB = true; ORBMinutes = 15;
				OrbHighBrush = Brushes.Cyan; OrbLowBrush = Brushes.Cyan;
				OrbLineWidth = 2; OrbDashStyle = DashStyleHelper.Dash;

				ShowAsiaORB = true; AsiaORBMinutes = 60;
				AsiaOrbHighBrush = Brushes.Cyan; AsiaOrbLowBrush = Brushes.Cyan;

				ShowEuropeORB = true; EuropeORBMinutes = 60;
				EuropeOrbHighBrush = Brushes.Gold; EuropeOrbLowBrush = Brushes.Gold;

				ShowNYPreOpenBox = true; NYPreOpenMinutes = 5; NYPreOpenPoints = 20;
				NYPreOpenHighBrush = Brushes.Lime; NYPreOpenLowBrush = Brushes.Lime;
				NYPreOpenLineWidth = 2; NYPreOpenDashStyle = DashStyleHelper.Solid;

				ShowLondonPreOpenBox = true; LondonPreOpenMinutes = 5; LondonPreOpenPoints = 20;
				LondonPreOpenHighBrush = Brushes.Gold; LondonPreOpenLowBrush = Brushes.Gold;
				LondonPreOpenLineWidth = 2; LondonPreOpenDashStyle = DashStyleHelper.Solid;
			}
			else if (State == State.Configure)
			{
			}
			else if (State == State.DataLoaded)
			{
				try
				{
					_cachedNYTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				}
				catch
				{
					_cachedNYTimeZone = TimeZoneInfo.Utc;
				}

				_tsAsiaStart = HHMMToTimeSpan(AsiaStartTime);
				_tsEuropeStart = HHMMToTimeSpan(EuropeStartTime);
				_tsNYStart = HHMMToTimeSpan(NYStartTime);

				_textFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.Bold,
					SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal,
					(float)LabelFontSize);

				_orbNY.Reset(); _orbAsia.Reset(); _orbEurope.Reset();
				_preOpenNY.Reset(); _preOpenLondon.Reset();
				_histOrbNY.Clear(); _histOrbAsia.Clear(); _histOrbEurope.Clear();
				_histPreOpenNY.Clear(); _histPreOpenLondon.Clear();
				_inSession = false;
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => CreateWPFControls());
			}
			else if (State == State.Terminated)
			{
				if (_textFormat != null) { _textFormat.Dispose(); _textFormat = null; }
				DisposeDxBrushes();

				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => DisposeWPFControls());
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1)
				return;

			if (Bars.IsFirstBarOfSession)
			{
				DateTime prevSessionEnd = Time[1];
				PushHistory(_histOrbNY, _orbNY, prevSessionEnd);
				PushHistory(_histOrbAsia, _orbAsia, prevSessionEnd);
				PushHistory(_histOrbEurope, _orbEurope, prevSessionEnd);
				PushHistory(_histPreOpenNY, _preOpenNY, null);
				PushHistory(_histPreOpenLondon, _preOpenLondon, null);

				_orbNY.Reset(); _orbAsia.Reset(); _orbEurope.Reset();
				_preOpenNY.Reset(); _preOpenLondon.Reset();
				_inSession = true;
			}

			DateTime barTime = Time[0];
			TimeZoneInfo chartTZ = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
			DateTime nyTime = TimeZoneInfo.ConvertTime(barTime, chartTZ, _cachedNYTimeZone);
			TimeSpan nyTOD = nyTime.TimeOfDay;

			AccumulateOrb(ref _orbNY, _tsNYStart, ORBMinutes, ShowORB, nyTOD);
			AccumulateOrb(ref _orbAsia, _tsAsiaStart, AsiaORBMinutes, ShowAsiaORB, nyTOD);
			AccumulateOrb(ref _orbEurope, _tsEuropeStart, EuropeORBMinutes, ShowEuropeORB, nyTOD);

			AccumulatePreOpen(ref _preOpenNY, _tsNYStart, _tsAsiaStart, NYPreOpenMinutes, ShowNYPreOpenBox, nyTOD);
			AccumulatePreOpen(ref _preOpenLondon, _tsEuropeStart, _tsNYStart, LondonPreOpenMinutes, ShowLondonPreOpenBox, nyTOD);
		}

		// ── wrap-aware "is time-of-day within [start,end)" check ───────────
		private static bool InTimeRange(TimeSpan t, TimeSpan start, TimeSpan end)
		{
			if (start <= end)
				return t >= start && t < end;
			return t >= start || t < end;
		}

		// ── ORB accumulator (N minutes AFTER session open) ─────────────────
		private void AccumulateOrb(ref OrbState orb, TimeSpan sessionOpen, int minutes, bool show, TimeSpan nyTOD)
		{
			if (!show || orb.Done) return;
			TimeSpan orbEnd = sessionOpen.Add(TimeSpan.FromMinutes(minutes));
			if (nyTOD >= sessionOpen && nyTOD < orbEnd)
			{
				if (!orb.Started) { orb.Started = true; orb.Start = Time[0]; }
				if (High[0] > orb.High) orb.High = High[0];
				if (Low[0] < orb.Low) orb.Low = Low[0];
				orb.End = Time[0];
			}
			else if (orb.Started && nyTOD >= orbEnd)
				orb.Done = true;
		}

		// ── Pre-open box accumulator (N minutes BEFORE session open) ───────
		// Box keeps extending (End only, no High/Low growth) through the session
		// itself so the line runs to that session's own close, then stops.
		private void AccumulatePreOpen(ref OrbState orb, TimeSpan sessionOpen, TimeSpan sessionEnd, int minutes, bool show, TimeSpan nyTOD)
		{
			if (!show || orb.Done) return;
			TimeSpan preStart = sessionOpen.Subtract(TimeSpan.FromMinutes(minutes));
			bool inWindow;
			if (preStart < TimeSpan.Zero)
			{
				TimeSpan wrapped = preStart.Add(TimeSpan.FromDays(1));
				inWindow = nyTOD >= wrapped || nyTOD < sessionOpen;
			}
			else
			{
				inWindow = nyTOD >= preStart && nyTOD < sessionOpen;
			}

			if (inWindow)
			{
				if (!orb.Started) { orb.Started = true; orb.Start = Time[0]; }
				if (High[0] > orb.High) orb.High = High[0];
				if (Low[0] < orb.Low) orb.Low = Low[0];
				orb.End = Time[0];
			}
			else if (orb.Started)
			{
				if (InTimeRange(nyTOD, sessionOpen, sessionEnd))
					orb.End = Time[0];
				else
					orb.Done = true;
			}
		}

		// ── Archive a finished session's box into its history list (bounded) ──
		private static void PushHistory(System.Collections.Generic.List<OrbState> hist, OrbState orb, DateTime? extendEndTo)
		{
			if (!orb.Started || orb.High <= double.MinValue) return;
			if (extendEndTo.HasValue && extendEndTo.Value > orb.End)
				orb.End = extendEndTo.Value;
			hist.Add(orb);
			if (hist.Count > MaxHistoryDays) hist.RemoveAt(0);
		}

		public override void OnRenderTargetChanged()
		{
			DisposeDxBrushes();
			if (RenderTarget == null) return;

			_dxOrbHighBrush = OrbHighBrush.ToDxBrush(RenderTarget);
			_dxOrbLowBrush = OrbLowBrush.ToDxBrush(RenderTarget);
			_dxOrbAsiaHighBrush = AsiaOrbHighBrush.ToDxBrush(RenderTarget);
			_dxOrbAsiaLowBrush = AsiaOrbLowBrush.ToDxBrush(RenderTarget);
			_dxOrbEuropeHighBrush = EuropeOrbHighBrush.ToDxBrush(RenderTarget);
			_dxOrbEuropeLowBrush = EuropeOrbLowBrush.ToDxBrush(RenderTarget);
			_dxNYPreOpenHighBrush = NYPreOpenHighBrush.ToDxBrush(RenderTarget);
			_dxNYPreOpenLowBrush = NYPreOpenLowBrush.ToDxBrush(RenderTarget);
			_dxLondonPreOpenHighBrush = LondonPreOpenHighBrush.ToDxBrush(RenderTarget);
			_dxLondonPreOpenLowBrush = LondonPreOpenLowBrush.ToDxBrush(RenderTarget);
		}

		private void DisposeDxBrushes()
		{
			if (_dxOrbHighBrush != null) { _dxOrbHighBrush.Dispose(); _dxOrbHighBrush = null; }
			if (_dxOrbLowBrush != null) { _dxOrbLowBrush.Dispose(); _dxOrbLowBrush = null; }
			if (_dxOrbAsiaHighBrush != null) { _dxOrbAsiaHighBrush.Dispose(); _dxOrbAsiaHighBrush = null; }
			if (_dxOrbAsiaLowBrush != null) { _dxOrbAsiaLowBrush.Dispose(); _dxOrbAsiaLowBrush = null; }
			if (_dxOrbEuropeHighBrush != null) { _dxOrbEuropeHighBrush.Dispose(); _dxOrbEuropeHighBrush = null; }
			if (_dxOrbEuropeLowBrush != null) { _dxOrbEuropeLowBrush.Dispose(); _dxOrbEuropeLowBrush = null; }
			if (_dxNYPreOpenHighBrush != null) { _dxNYPreOpenHighBrush.Dispose(); _dxNYPreOpenHighBrush = null; }
			if (_dxNYPreOpenLowBrush != null) { _dxNYPreOpenLowBrush.Dispose(); _dxNYPreOpenLowBrush = null; }
			if (_dxLondonPreOpenHighBrush != null) { _dxLondonPreOpenHighBrush.Dispose(); _dxLondonPreOpenHighBrush = null; }
			if (_dxLondonPreOpenLowBrush != null) { _dxLondonPreOpenLowBrush.Dispose(); _dxLondonPreOpenLowBrush = null; }
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartBars == null) return;

			float canvasLeft = chartControl.CanvasLeft;
			float canvasRight = chartControl.CanvasRight;
			float labelH = (_textFormat != null) ? _textFormat.FontSize + 4f : 14f;

			if (ShowORB)
			{
				foreach (var h in _histOrbNY) DrawOrbLines(chartControl, chartScale, h, false, false, _dxOrbHighBrush, _dxOrbLowBrush, OrbLineWidth, OrbDashStyle, "ORB", canvasLeft, canvasRight, labelH);
				DrawOrbLines(chartControl, chartScale, _orbNY, _inSession, true, _dxOrbHighBrush, _dxOrbLowBrush, OrbLineWidth, OrbDashStyle, "ORB", canvasLeft, canvasRight, labelH);
			}
			if (ShowAsiaORB)
			{
				foreach (var h in _histOrbAsia) DrawOrbLines(chartControl, chartScale, h, false, false, _dxOrbAsiaHighBrush, _dxOrbAsiaLowBrush, OrbLineWidth, OrbDashStyle, "Asia ORB", canvasLeft, canvasRight, labelH);
				DrawOrbLines(chartControl, chartScale, _orbAsia, _inSession, true, _dxOrbAsiaHighBrush, _dxOrbAsiaLowBrush, OrbLineWidth, OrbDashStyle, "Asia ORB", canvasLeft, canvasRight, labelH);
			}
			if (ShowEuropeORB)
			{
				foreach (var h in _histOrbEurope) DrawOrbLines(chartControl, chartScale, h, false, false, _dxOrbEuropeHighBrush, _dxOrbEuropeLowBrush, OrbLineWidth, OrbDashStyle, "Europe ORB", canvasLeft, canvasRight, labelH);
				DrawOrbLines(chartControl, chartScale, _orbEurope, _inSession, true, _dxOrbEuropeHighBrush, _dxOrbEuropeLowBrush, OrbLineWidth, OrbDashStyle, "Europe ORB", canvasLeft, canvasRight, labelH);
			}

			if (ShowNYPreOpenBox)
			{
				foreach (var h in _histPreOpenNY) DrawPreOpenBox(chartControl, chartScale, h, NYPreOpenPoints, false, false, _dxNYPreOpenHighBrush, _dxNYPreOpenLowBrush, NYPreOpenLineWidth, NYPreOpenDashStyle, "NY PreOpen", canvasLeft, canvasRight, labelH);
				DrawPreOpenBox(chartControl, chartScale, _preOpenNY, NYPreOpenPoints, _inSession && !_preOpenNY.Done, true, _dxNYPreOpenHighBrush, _dxNYPreOpenLowBrush, NYPreOpenLineWidth, NYPreOpenDashStyle, "NY PreOpen", canvasLeft, canvasRight, labelH);
			}
			if (ShowLondonPreOpenBox)
			{
				foreach (var h in _histPreOpenLondon) DrawPreOpenBox(chartControl, chartScale, h, LondonPreOpenPoints, false, false, _dxLondonPreOpenHighBrush, _dxLondonPreOpenLowBrush, LondonPreOpenLineWidth, LondonPreOpenDashStyle, "London PreOpen", canvasLeft, canvasRight, labelH);
				DrawPreOpenBox(chartControl, chartScale, _preOpenLondon, LondonPreOpenPoints, _inSession && !_preOpenLondon.Done, true, _dxLondonPreOpenHighBrush, _dxLondonPreOpenLowBrush, LondonPreOpenLineWidth, LondonPreOpenDashStyle, "London PreOpen", canvasLeft, canvasRight, labelH);
			}
		}

		private void DrawOrbLines(ChartControl cc, ChartScale cs, OrbState orb, bool extendRight, bool showLabel,
			SharpDX.Direct2D1.Brush highBrush, SharpDX.Direct2D1.Brush lowBrush,
			int width, DashStyleHelper dash, string label,
			float canvasLeft, float canvasRight, float labelH)
		{
			if (!orb.Started || orb.High <= double.MinValue || highBrush == null) return;
			if (!extendRight && (orb.End < cc.FirstTimePainted || orb.Start > cc.LastTimePainted)) return;
			float x0 = Math.Max(GetXForTime(cc, orb.Start), canvasLeft);
			float x1 = Math.Min(extendRight ? canvasRight : GetXForTimeInclusive(cc, orb.End), canvasRight);
			if (x1 <= x0) return;
			DrawSharpDXLine(x0, x1, cs.GetYByValue(orb.High), highBrush, width, dash);
			DrawSharpDXLine(x0, x1, cs.GetYByValue(orb.Low), lowBrush, width, dash);
			if (showLabel && ShowLineLabels && _textFormat != null)
			{
				float lx = Math.Min(x1, canvasRight - 5f);
				RenderLabelAt(label + " High " + orb.High.ToString("F2"), orb.High, lx, labelH, cs, highBrush);
				RenderLabelAt(label + " Low " + orb.Low.ToString("F2"), orb.Low, lx, labelH, cs, lowBrush);
			}
		}

		private void DrawPreOpenBox(ChartControl cc, ChartScale cs, OrbState orb, double points, bool extendRight, bool showLabel,
			SharpDX.Direct2D1.Brush highBrush, SharpDX.Direct2D1.Brush lowBrush,
			int width, DashStyleHelper dash, string label,
			float canvasLeft, float canvasRight, float labelH)
		{
			if (!orb.Started || orb.High <= double.MinValue || highBrush == null) return;
			if (!extendRight && (orb.End < cc.FirstTimePainted || orb.Start > cc.LastTimePainted)) return;
			float x0 = Math.Max(GetXForTime(cc, orb.Start), canvasLeft);
			float x1 = Math.Min(extendRight ? canvasRight : GetXForTimeInclusive(cc, orb.End), canvasRight);
			if (x1 <= x0) return;
			double top = orb.High + points;
			double bottom = orb.Low - points;
			DrawSharpDXLine(x0, x1, cs.GetYByValue(top), highBrush, width, dash);
			DrawSharpDXLine(x0, x1, cs.GetYByValue(bottom), lowBrush, width, dash);
			if (showLabel && ShowLineLabels && _textFormat != null)
			{
				float lx = Math.Min(x1, canvasRight - 5f);
				RenderLabelAt(label + " Top " + top.ToString("F2"), top, lx, labelH, cs, highBrush);
				RenderLabelAt(label + " Bottom " + bottom.ToString("F2"), bottom, lx, labelH, cs, lowBrush);
			}
		}

		private void DrawSharpDXLine(float x0, float x1, float y,
			SharpDX.Direct2D1.Brush brush, int width, DashStyleHelper dash)
		{
			if (dash == DashStyleHelper.Solid)
			{
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, width);
			}
			else
			{
				float dashLen = dash == DashStyleHelper.Dot ? width * 2f : width * 6f;
				float gapLen = width * 3f;
				float x = x0;
				bool drawing = true;
				while (x < x1)
				{
					float segEnd = Math.Min(x + (drawing ? dashLen : gapLen), x1);
					if (drawing)
						RenderTarget.DrawLine(new SharpDX.Vector2(x, y), new SharpDX.Vector2(segEnd, y), brush, width);
					x = segEnd;
					drawing = !drawing;
				}
			}
		}

		private float GetXForTime(ChartControl chartControl, DateTime time)
		{
			try
			{
				int barIdx = ChartBars.GetBarIdxByTime(chartControl, time);
				float x = chartControl.GetXByBarIndex(ChartBars, barIdx);
				return Math.Max(x, chartControl.CanvasLeft + 5f);
			}
			catch { return chartControl.CanvasRight - 5f; }
		}

		// Right edge of the bar at 'time' (barIdx+1), so a box ending on a single bar
		// (e.g. a 5-min pre-open window on a coarse chart) still has non-zero width.
		private float GetXForTimeInclusive(ChartControl chartControl, DateTime time)
		{
			try
			{
				int barIdx = ChartBars.GetBarIdxByTime(chartControl, time);
				int nextIdx = Math.Min(barIdx + 1, ChartBars.ToIndex);
				float x = chartControl.GetXByBarIndex(ChartBars, nextIdx);
				return Math.Max(x, chartControl.CanvasLeft + 5f);
			}
			catch { return chartControl.CanvasRight - 5f; }
		}

		private void RenderLabelAt(string text, double price, float xAnchor, float labelH,
			ChartScale chartScale, SharpDX.Direct2D1.Brush brush)
		{
			float yCenter = chartScale.GetYByValue(price);
			float yTop = yCenter - labelH - 2f;
			using (var layout = new SharpDX.DirectWrite.TextLayout(
				NinjaTrader.Core.Globals.DirectWriteFactory, text, _textFormat, 300f, labelH))
			{
				float x;
				switch (LabelAlignment)
				{
					case TextAlignment.Left: x = xAnchor + 4f; break;
					case TextAlignment.Center: x = xAnchor - (float)layout.Metrics.Width / 2f; break;
					default: x = xAnchor - (float)layout.Metrics.Width - 4f; break;
				}
				RenderTarget.DrawTextLayout(
					new SharpDX.Vector2(x, yTop),
					layout, brush, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}
		}

		private static TimeSpan HHMMToTimeSpan(int hhmm)
			=> new TimeSpan(hhmm / 100, hhmm % 100, 0);

		// ══════════════════════════════════════════════════════════════════
		//  WPF Controls (bar-top menu: Show/Hide All + per-ORB toggles)
		// ══════════════════════════════════════════════════════════════════
		private static System.Windows.Shapes.Rectangle MakeGutterIcon() =>
			new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Fill = Brushes.Black };

		private NTMenuItem MakeItem(string header) =>
			new NTMenuItem { Header = header, StaysOpenOnClick = true, Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };

		private NTMenuItem MakeColoredItem(string header, Brush fg)
		{
			var item = new NTMenuItem { Header = header, StaysOpenOnClick = true, Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };
			if (fg != null) item.Foreground = fg;
			return item;
		}

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
					Header = "ICN_ORB",
					Margin = new Thickness(0),
					Padding = new Thickness(1),
					Style = mainMenuItemStyle,
					VerticalAlignment = VerticalAlignment.Center,
				};
				ntBarMenu.Items.Add(ntBartopMenuItem);

				_showAll = ShowORB && ShowAsiaORB && ShowEuropeORB && ShowNYPreOpenBox && ShowLondonPreOpenBox;

				ntShowHide = MakeItem(_showAll ? "Hide All" : "Show All"); ntShowHide.Tag = "ShowAll";
				ntShowHide.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntShowHide);
				ntBartopMenuItem.Items.Add(new Separator());

				ntORBItem = MakeColoredItem(ShowORB ? "Hide NY ORB" : "Show NY ORB", OrbHighBrush); ntORBItem.Tag = "ShowORB";
				ntAsiaORBItem = MakeColoredItem(ShowAsiaORB ? "Hide Asia ORB" : "Show Asia ORB", AsiaOrbHighBrush); ntAsiaORBItem.Tag = "ShowAsiaORB";
				ntEuropeORBItem = MakeColoredItem(ShowEuropeORB ? "Hide Europe ORB" : "Show Europe ORB", EuropeOrbHighBrush); ntEuropeORBItem.Tag = "ShowEuropeORB";
				ntNYPreOpenItem = MakeColoredItem(ShowNYPreOpenBox ? "Hide NY Pre-Open Box" : "Show NY Pre-Open Box", NYPreOpenHighBrush); ntNYPreOpenItem.Tag = "ShowNYPreOpenBox";
				ntLondonPreOpenItem = MakeColoredItem(ShowLondonPreOpenBox ? "Hide London Pre-Open Box" : "Show London Pre-Open Box", LondonPreOpenHighBrush); ntLondonPreOpenItem.Tag = "ShowLondonPreOpenBox";

				ntORBItem.Click += NTBarMenu_Click;
				ntAsiaORBItem.Click += NTBarMenu_Click;
				ntEuropeORBItem.Click += NTBarMenu_Click;
				ntNYPreOpenItem.Click += NTBarMenu_Click;
				ntLondonPreOpenItem.Click += NTBarMenu_Click;

				ntBartopMenuItem.Items.Add(ntORBItem);
				ntBartopMenuItem.Items.Add(ntAsiaORBItem);
				ntBartopMenuItem.Items.Add(ntEuropeORBItem);
				ntBartopMenuItem.Items.Add(ntNYPreOpenItem);
				ntBartopMenuItem.Items.Add(ntLondonPreOpenItem);

				if (TabSelected()) ShowWPFControls();
				chartWindow.MainTabControl.SelectionChanged += TabChangedHandler;
			}
			catch (Exception ex) { Print(ex); }
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
						ShowORB = ShowAsiaORB = ShowEuropeORB = ShowNYPreOpenBox = ShowLondonPreOpenBox = _showAll;
						break;
					case "ShowORB": ShowORB = !ShowORB; break;
					case "ShowAsiaORB": ShowAsiaORB = !ShowAsiaORB; break;
					case "ShowEuropeORB": ShowEuropeORB = !ShowEuropeORB; break;
					case "ShowNYPreOpenBox": ShowNYPreOpenBox = !ShowNYPreOpenBox; break;
					case "ShowLondonPreOpenBox": ShowLondonPreOpenBox = !ShowLondonPreOpenBox; break;
				}
			}
			catch (Exception ex) { Print("ICNORB menu error: " + ex.Message); }
			finally
			{
				try
				{
					_showAll = ShowORB && ShowAsiaORB && ShowEuropeORB && ShowNYPreOpenBox && ShowLondonPreOpenBox;

					ntShowHide.Header = _showAll ? "Hide All" : "Show All";
					ntORBItem.Header = ShowORB ? "Hide NY ORB" : "Show NY ORB";
					ntAsiaORBItem.Header = ShowAsiaORB ? "Hide Asia ORB" : "Show Asia ORB";
					ntEuropeORBItem.Header = ShowEuropeORB ? "Hide Europe ORB" : "Show Europe ORB";
					ntNYPreOpenItem.Header = ShowNYPreOpenBox ? "Hide NY Pre-Open Box" : "Show NY Pre-Open Box";
					ntLondonPreOpenItem.Header = ShowLondonPreOpenBox ? "Hide London Pre-Open Box" : "Show London Pre-Open Box";

					if (ChartControl != null) ChartControl.InvalidateVisual();
				}
				catch { }
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
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia Start Time (HHmm)", Description = "Time-of-day (NY/ET, HHmm) that starts the Asia session. Default 2000 (8:00 PM).", Order = 0, GroupName = "1. Sessions")]
		public int AsiaStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Europe Start Time (HHmm)", Description = "Time-of-day (NY/ET, HHmm) that starts the Europe/London session. Default 300 (3:00 AM).", Order = 1, GroupName = "1. Sessions")]
		public int EuropeStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "NY Start Time (HHmm)", Description = "Time-of-day (NY/ET, HHmm) that starts the NY session. Default 930 (9:30 AM).", Order = 2, GroupName = "1. Sessions")]
		public int NYStartTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Line Labels", Description = "Show price labels at the right edge of each ORB/pre-open line.", Order = 3, GroupName = "1. Sessions")]
		public bool ShowLineLabels { get; set; }

		[NinjaScriptProperty]
		[Range(6, 24)]
		[Display(Name = "Label Font Size", Order = 4, GroupName = "1. Sessions")]
		public double LabelFontSize { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Label Alignment", Description = "Anchors labels at the right edge of the chart; Left/Right/Center controls which side of that anchor the text sits on.", Order = 5, GroupName = "1. Sessions")]
		public TextAlignment LabelAlignment { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show ORB", Description = "Show/hide the NY Opening Range Breakout high and low lines.", Order = 0, GroupName = "2. NY ORB")]
		public bool ShowORB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "ORB Minutes", Description = "Minutes after the NY session open (NYStartTime) that define the opening range.", Order = 1, GroupName = "2. NY ORB")]
		public int ORBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "ORB High Color", Order = 2, GroupName = "2. NY ORB")]
		public Brush OrbHighBrush { get; set; }
		[Browsable(false)]
		public string OrbHighBrushSerialize { get { return Serialize.BrushToString(OrbHighBrush); } set { OrbHighBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "ORB Low Color", Order = 3, GroupName = "2. NY ORB")]
		public Brush OrbLowBrush { get; set; }
		[Browsable(false)]
		public string OrbLowBrushSerialize { get { return Serialize.BrushToString(OrbLowBrush); } set { OrbLowBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "ORB Line Width", Description = "Pixel width shared by NY/Asia/Europe ORB lines.", Order = 4, GroupName = "2. NY ORB")]
		public int OrbLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ORB Dash Style", Description = "Dash style shared by NY/Asia/Europe ORB lines.", Order = 5, GroupName = "2. NY ORB")]
		public DashStyleHelper OrbDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Asia ORB", Description = "Show/hide the Asia Opening Range Breakout lines.", Order = 0, GroupName = "3. Asia ORB")]
		public bool ShowAsiaORB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Asia ORB Minutes", Description = "Minutes after Asia open (AsiaStartTime) for the opening range.", Order = 1, GroupName = "3. Asia ORB")]
		public int AsiaORBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia ORB High Color", Order = 2, GroupName = "3. Asia ORB")]
		public Brush AsiaOrbHighBrush { get; set; }
		[Browsable(false)]
		public string AsiaOrbHighBrushSerialize { get { return Serialize.BrushToString(AsiaOrbHighBrush); } set { AsiaOrbHighBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia ORB Low Color", Order = 3, GroupName = "3. Asia ORB")]
		public Brush AsiaOrbLowBrush { get; set; }
		[Browsable(false)]
		public string AsiaOrbLowBrushSerialize { get { return Serialize.BrushToString(AsiaOrbLowBrush); } set { AsiaOrbLowBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Display(Name = "Show Europe ORB", Description = "Show/hide the Europe Opening Range Breakout lines.", Order = 0, GroupName = "4. Europe ORB")]
		public bool ShowEuropeORB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Europe ORB Minutes", Description = "Minutes after Europe open (EuropeStartTime) for the opening range.", Order = 1, GroupName = "4. Europe ORB")]
		public int EuropeORBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Europe ORB High Color", Order = 2, GroupName = "4. Europe ORB")]
		public Brush EuropeOrbHighBrush { get; set; }
		[Browsable(false)]
		public string EuropeOrbHighBrushSerialize { get { return Serialize.BrushToString(EuropeOrbHighBrush); } set { EuropeOrbHighBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Europe ORB Low Color", Order = 3, GroupName = "4. Europe ORB")]
		public Brush EuropeOrbLowBrush { get; set; }
		[Browsable(false)]
		public string EuropeOrbLowBrushSerialize { get { return Serialize.BrushToString(EuropeOrbLowBrush); } set { EuropeOrbLowBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Display(Name = "Show NY Pre-Open Box", Description = "Show/hide the NY pre-open box: high/low of the N minutes before NY open, offset by +/- Points.", Order = 0, GroupName = "5. NY Pre-Open Box")]
		public bool ShowNYPreOpenBox { get; set; }

		[NinjaScriptProperty]
		[Range(1, 120)]
		[Display(Name = "NY Pre-Open Minutes", Description = "Minutes before NY session open (NYStartTime) that define the pre-open window.", Order = 1, GroupName = "5. NY Pre-Open Box")]
		public int NYPreOpenMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "NY Pre-Open Points", Description = "Points added above the pre-open high and subtracted below the pre-open low.", Order = 2, GroupName = "5. NY Pre-Open Box")]
		public double NYPreOpenPoints { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "NY Pre-Open High Color", Order = 3, GroupName = "5. NY Pre-Open Box")]
		public Brush NYPreOpenHighBrush { get; set; }
		[Browsable(false)]
		public string NYPreOpenHighBrushSerialize { get { return Serialize.BrushToString(NYPreOpenHighBrush); } set { NYPreOpenHighBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "NY Pre-Open Low Color", Order = 4, GroupName = "5. NY Pre-Open Box")]
		public Brush NYPreOpenLowBrush { get; set; }
		[Browsable(false)]
		public string NYPreOpenLowBrushSerialize { get { return Serialize.BrushToString(NYPreOpenLowBrush); } set { NYPreOpenLowBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "NY Pre-Open Line Width", Order = 5, GroupName = "5. NY Pre-Open Box")]
		public int NYPreOpenLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "NY Pre-Open Dash Style", Order = 6, GroupName = "5. NY Pre-Open Box")]
		public DashStyleHelper NYPreOpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show London Pre-Open Box", Description = "Show/hide the London pre-open box: high/low of the N minutes before London (Europe) open, offset by +/- Points.", Order = 0, GroupName = "6. London Pre-Open Box")]
		public bool ShowLondonPreOpenBox { get; set; }

		[NinjaScriptProperty]
		[Range(1, 120)]
		[Display(Name = "London Pre-Open Minutes", Description = "Minutes before London session open (EuropeStartTime) that define the pre-open window.", Order = 1, GroupName = "6. London Pre-Open Box")]
		public int LondonPreOpenMinutes { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "London Pre-Open Points", Description = "Points added above the pre-open high and subtracted below the pre-open low.", Order = 2, GroupName = "6. London Pre-Open Box")]
		public double LondonPreOpenPoints { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "London Pre-Open High Color", Order = 3, GroupName = "6. London Pre-Open Box")]
		public Brush LondonPreOpenHighBrush { get; set; }
		[Browsable(false)]
		public string LondonPreOpenHighBrushSerialize { get { return Serialize.BrushToString(LondonPreOpenHighBrush); } set { LondonPreOpenHighBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "London Pre-Open Low Color", Order = 4, GroupName = "6. London Pre-Open Box")]
		public Brush LondonPreOpenLowBrush { get; set; }
		[Browsable(false)]
		public string LondonPreOpenLowBrushSerialize { get { return Serialize.BrushToString(LondonPreOpenLowBrush); } set { LondonPreOpenLowBrush = Serialize.StringToBrush(value); } }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "London Pre-Open Line Width", Order = 5, GroupName = "6. London Pre-Open Box")]
		public int LondonPreOpenLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "London Pre-Open Dash Style", Order = 6, GroupName = "6. London Pre-Open Box")]
		public DashStyleHelper LondonPreOpenDashStyle { get; set; }
		#endregion
	}
}