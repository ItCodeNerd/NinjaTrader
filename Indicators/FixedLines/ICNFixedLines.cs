#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{
	/// <summary>
	/// Lightweight overlay: fixed-interval horizontal lines, session VWAP, day-high/day-low
	/// anchored VWAP, and 50/200 EMA. No custom rendering — plots + Draw calls only, kept lean
	/// for performance. Bar-top menu adds a Show/Hide All toggle plus per-line toggles.
	/// </summary>
	public class ICNFixedLines : Indicator
	{
		private EMA _ema9, _ema50, _ema200;

		private double _cumVolumeSession, _cumTypicalVolumeSession;
		private double _dayHigh, _dayLow;
		private double _cumVolumeDayHigh, _cumTypicalVolumeDayHigh;
		private double _cumVolumeDayLow, _cumTypicalVolumeDayLow;

		private double _lastFixedStep, _lastFixedMin, _lastFixedMax;

		private TimeZoneInfo _cachedNYTimeZone;
		private TimeSpan _tsNYStart;
		private DateTime _lastMidnightNYDate, _lastGlobexNYDate, _lastNYOpenNYDate;
		private DateTime _midnightOpenAnchor, _globexOpenAnchor, _nyOpenAnchor;
		private double _midnightOpenPrice, _globexOpenPrice, _nyOpenPrice;

		private TimeSpan _tsAsiaStart, _tsAsiaEnd, _tsLondonStart, _tsLondonEnd;
		private bool _asiaInSession, _londonInSession;
		private double _asiaHigh, _asiaLow, _londonHigh, _londonLow;
		private DateTime _asiaHighAnchor, _asiaLowAnchor, _londonHighAnchor, _londonLowAnchor;

		private bool _hasPrevDay;
		private double _todayHigh, _todayLow, _pdHigh, _pdLow;
		private DateTime _pdAnchorStart;

		private static readonly DateTime WeekEpoch = new DateTime(2000, 1, 2); // a Sunday
		private int _lastWeekBucket = int.MinValue;
		private bool _hasPrevWeek;
		private double _thisWeekHigh, _thisWeekLow, _pwHigh, _pwLow;
		private DateTime _pwAnchorStart;

		private int _lastM5Bucket = -1, _lastH1Bucket = -1, _lastH4Bucket = -1;
		private double _m5OpenPrice, _h1OpenPrice, _h4OpenPrice;
		private DateTime _m5OpenAnchor, _h1OpenAnchor, _h4OpenAnchor;

		private SharpDX.DirectWrite.TextFormat _textFormat;
		private SharpDX.Direct2D1.Brush _dxMidnightOpen, _dxGlobexOpen, _dxNYOpen;
		private SharpDX.Direct2D1.Brush _dxAsiaHigh, _dxAsiaLow, _dxLondonHigh, _dxLondonLow;
		private SharpDX.Direct2D1.Brush _dxFixedLines;
		private SharpDX.Direct2D1.Brush _dxPDHigh, _dxPDLow;
		private SharpDX.Direct2D1.Brush _dxPWHigh, _dxPWLow;
		private SharpDX.Direct2D1.Brush _dxM5Open, _dxH1Open, _dxH4Open;

		// ══════════════════════════════════════════════════════════════════
		//  WPF menu state
		// ══════════════════════════════════════════════════════════════════
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private bool ntBarActive;
		private Menu ntBarMenu;
		private NTMenuItem ntBartopMenuItem;
		private NTMenuItem ntShowHide;
		private NTMenuItem ntFixedLinesItem, ntSessionVWAPItem, ntDayHighVWAPItem, ntDayLowVWAPItem, ntEma9Item, ntEma50Item, ntEma200Item;
		private NTMenuItem ntMidnightOpenItem, ntGlobexOpenItem, ntNYOpenItem;
		private NTMenuItem ntAsiaHighLowItem, ntLondonHighLowItem;
		private NTMenuItem ntPDHLItem;
		private NTMenuItem ntPWHLItem;
		private NTMenuItem ntM5OpenItem, ntH1OpenItem, ntH4OpenItem;
		private NTMenuItem ntShowLabelsItem;
		private bool _showAll;
		private System.Windows.Style mainMenuItemStyle, systemMenuStyle;
		private System.Windows.Controls.TabItem tabItem;
		private ChartTab chartTab;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "ICN – Fixed lines, Session VWAP, Day-High/Day-Low VWAP, 50/200 EMA.";
				Name = "ICNFixedLines";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = true;
				DrawOnPricePanel = true;
				PaintPriceMarkers = true;
				IsSuspendedWhileInactive = true;

				AddPlot(new Stroke(Brushes.Turquoise, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA50");
				AddPlot(new Stroke(Brushes.Orange, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA200");
				AddPlot(new Stroke(Brushes.White, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotVWAPSESSION");
				AddPlot(new Stroke(Brushes.Magenta, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotVWAPDAYHIGH");
				AddPlot(new Stroke(Brushes.Lime, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotVWAPDAYLOW");
				AddPlot(new Stroke(Brushes.HotPink, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA9");

				ShowFixedLines = true;
				FixedLinesStep = 100;
				FixedLinesRange = 20;
				FixedLinesColor = Brushes.Gray;

				ShowSessionVWAP = true;
				ShowDayHighVWAP = true;
				ShowDayLowVWAP = true;

				ShowEma9 = true;
				Ema9Period = 9;
				ShowEma50 = true;
				Ema50Period = 50;
				ShowEma200 = true;
				Ema200Period = 200;

				NYStartTime = 930;

				ShowMidnightOpen = true;
				MidnightOpenBrush = Brushes.White;
				MidnightOpenLineWidth = 1;
				MidnightOpenDashStyle = DashStyleHelper.Dash;

				ShowGlobexOpen = true;
				GlobexOpenBrush = Brushes.Yellow;
				GlobexOpenLineWidth = 1;
				GlobexOpenDashStyle = DashStyleHelper.Dash;

				ShowNYOpenPrice = true;
				NYOpenBrush = Brushes.Cyan;
				NYOpenLineWidth = 1;
				NYOpenDashStyle = DashStyleHelper.Dash;

				ShowAsiaHighLow = true;
				AsiaStartTime = 2000;
				AsiaEndTime = 300;
				AsiaHighBrush = Brushes.Cyan;
				AsiaLowBrush = Brushes.Cyan;
				AsiaLineWidth = 1;
				AsiaDashStyle = DashStyleHelper.Dash;

				ShowLondonHighLow = true;
				LondonStartTime = 300;
				LondonEndTime = 930;
				LondonHighBrush = Brushes.Gold;
				LondonLowBrush = Brushes.Gold;
				LondonLineWidth = 1;
				LondonDashStyle = DashStyleHelper.Dash;

				ShowPDHL = true;
				PDHighBrush = Brushes.OrangeRed;
				PDLowBrush = Brushes.OrangeRed;
				PDLineWidth = 1;
				PDDashStyle = DashStyleHelper.Dash;

				ShowPWHL = true;
				PWHighBrush = Brushes.DeepPink;
				PWLowBrush = Brushes.DeepPink;
				PWLineWidth = 2;
				PWDashStyle = DashStyleHelper.Dash;

				ShowM5Open = true;
				M5OpenBrush = Brushes.Turquoise;
				M5OpenLineWidth = 1;
				M5OpenDashStyle = DashStyleHelper.Dot;

				ShowH1Open = true;
				H1OpenBrush = Brushes.Orange;
				H1OpenLineWidth = 1;
				H1OpenDashStyle = DashStyleHelper.Dot;

				ShowH4Open = true;
				H4OpenBrush = Brushes.MediumPurple;
				H4OpenLineWidth = 1;
				H4OpenDashStyle = DashStyleHelper.Dot;

				ShowLabels = true;
				LabelAlignment = TextAlignment.Right;
				LabelFontSize = 10;
			}
			else if (State == State.DataLoaded)
			{
				_ema9 = EMA(Ema9Period);
				_ema50 = EMA(Ema50Period);
				_ema200 = EMA(Ema200Period);

				try
				{
					_cachedNYTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				}
				catch
				{
					_cachedNYTimeZone = TimeZoneInfo.Utc;
				}
				_tsNYStart = new TimeSpan(NYStartTime / 100, NYStartTime % 100, 0);
				_tsAsiaStart = HHMMToTimeSpan(AsiaStartTime); _tsAsiaEnd = HHMMToTimeSpan(AsiaEndTime);
				_tsLondonStart = HHMMToTimeSpan(LondonStartTime); _tsLondonEnd = HHMMToTimeSpan(LondonEndTime);
				_lastM5Bucket = _lastH1Bucket = _lastH4Bucket = -1;
				_lastWeekBucket = int.MinValue;

				_textFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory, "Arial",
					SharpDX.DirectWrite.FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal, (float)LabelFontSize);
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

		public override void OnRenderTargetChanged()
		{
			DisposeDxBrushes();
			if (RenderTarget == null) return;

			_dxMidnightOpen = MidnightOpenBrush.ToDxBrush(RenderTarget);
			_dxGlobexOpen = GlobexOpenBrush.ToDxBrush(RenderTarget);
			_dxNYOpen = NYOpenBrush.ToDxBrush(RenderTarget);
			_dxAsiaHigh = AsiaHighBrush.ToDxBrush(RenderTarget);
			_dxAsiaLow = AsiaLowBrush.ToDxBrush(RenderTarget);
			_dxLondonHigh = LondonHighBrush.ToDxBrush(RenderTarget);
			_dxLondonLow = LondonLowBrush.ToDxBrush(RenderTarget);
			_dxFixedLines = FixedLinesColor.ToDxBrush(RenderTarget);
			_dxPDHigh = PDHighBrush.ToDxBrush(RenderTarget);
			_dxPDLow = PDLowBrush.ToDxBrush(RenderTarget);
			_dxPWHigh = PWHighBrush.ToDxBrush(RenderTarget);
			_dxPWLow = PWLowBrush.ToDxBrush(RenderTarget);
			_dxM5Open = M5OpenBrush.ToDxBrush(RenderTarget);
			_dxH1Open = H1OpenBrush.ToDxBrush(RenderTarget);
			_dxH4Open = H4OpenBrush.ToDxBrush(RenderTarget);
		}

		private void DisposeDxBrushes()
		{
			foreach (var b in new[] { _dxMidnightOpen, _dxGlobexOpen, _dxNYOpen, _dxAsiaHigh, _dxAsiaLow, _dxLondonHigh, _dxLondonLow, _dxFixedLines, _dxPDHigh, _dxPDLow, _dxPWHigh, _dxPWLow, _dxM5Open, _dxH1Open, _dxH4Open })
				if (b != null) b.Dispose();
			_dxMidnightOpen = _dxGlobexOpen = _dxNYOpen = _dxAsiaHigh = _dxAsiaLow = _dxLondonHigh = _dxLondonLow = _dxFixedLines = _dxPDHigh = _dxPDLow = _dxPWHigh = _dxPWLow = _dxM5Open = _dxH1Open = _dxH4Open = null;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || ChartBars == null) return;

			float canvasLeft = chartControl.CanvasLeft;
			float canvasRight = chartControl.CanvasRight;

			if (ShowFixedLines && _lastFixedStep > 0 && _dxFixedLines != null)
			{
				for (double level = _lastFixedMin; level <= _lastFixedMax; level += _lastFixedStep)
				{
					float y = chartScale.GetYByValue(Math.Round(level / _lastFixedStep) * _lastFixedStep);
					DrawSharpDXLine(canvasLeft, canvasRight, y, _dxFixedLines, 1, DashStyleHelper.Dot);
				}
			}

			if (ShowMidnightOpen && _midnightOpenAnchor != DateTime.MinValue)
				DrawAnchoredLine(chartControl, chartScale, _midnightOpenAnchor, _midnightOpenPrice, _dxMidnightOpen, MidnightOpenLineWidth, MidnightOpenDashStyle, canvasLeft, canvasRight);
			if (ShowGlobexOpen && _globexOpenAnchor != DateTime.MinValue)
				DrawAnchoredLine(chartControl, chartScale, _globexOpenAnchor, _globexOpenPrice, _dxGlobexOpen, GlobexOpenLineWidth, GlobexOpenDashStyle, canvasLeft, canvasRight);
			if (ShowNYOpenPrice && _nyOpenAnchor != DateTime.MinValue)
				DrawAnchoredLine(chartControl, chartScale, _nyOpenAnchor, _nyOpenPrice, _dxNYOpen, NYOpenLineWidth, NYOpenDashStyle, canvasLeft, canvasRight);
			if (ShowAsiaHighLow && _asiaHighAnchor != DateTime.MinValue)
			{
				DrawAnchoredLine(chartControl, chartScale, _asiaHighAnchor, _asiaHigh, _dxAsiaHigh, AsiaLineWidth, AsiaDashStyle, canvasLeft, canvasRight);
				DrawAnchoredLine(chartControl, chartScale, _asiaLowAnchor, _asiaLow, _dxAsiaLow, AsiaLineWidth, AsiaDashStyle, canvasLeft, canvasRight);
			}
			if (ShowLondonHighLow && _londonHighAnchor != DateTime.MinValue)
			{
				DrawAnchoredLine(chartControl, chartScale, _londonHighAnchor, _londonHigh, _dxLondonHigh, LondonLineWidth, LondonDashStyle, canvasLeft, canvasRight);
				DrawAnchoredLine(chartControl, chartScale, _londonLowAnchor, _londonLow, _dxLondonLow, LondonLineWidth, LondonDashStyle, canvasLeft, canvasRight);
			}
			if (ShowPDHL && _hasPrevDay)
			{
				DrawAnchoredLine(chartControl, chartScale, _pdAnchorStart, _pdHigh, _dxPDHigh, PDLineWidth, PDDashStyle, canvasLeft, canvasRight);
				DrawAnchoredLine(chartControl, chartScale, _pdAnchorStart, _pdLow, _dxPDLow, PDLineWidth, PDDashStyle, canvasLeft, canvasRight);
			}
			if (ShowPWHL && _hasPrevWeek)
			{
				DrawAnchoredLine(chartControl, chartScale, _pwAnchorStart, _pwHigh, _dxPWHigh, PWLineWidth, PWDashStyle, canvasLeft, canvasRight);
				DrawAnchoredLine(chartControl, chartScale, _pwAnchorStart, _pwLow, _dxPWLow, PWLineWidth, PWDashStyle, canvasLeft, canvasRight);
			}
			if (ShowM5Open && _m5OpenAnchor != DateTime.MinValue)
				DrawAnchoredLine(chartControl, chartScale, _m5OpenAnchor, _m5OpenPrice, _dxM5Open, M5OpenLineWidth, M5OpenDashStyle, canvasLeft, canvasRight);
			if (ShowH1Open && _h1OpenAnchor != DateTime.MinValue)
				DrawAnchoredLine(chartControl, chartScale, _h1OpenAnchor, _h1OpenPrice, _dxH1Open, H1OpenLineWidth, H1OpenDashStyle, canvasLeft, canvasRight);
			if (ShowH4Open && _h4OpenAnchor != DateTime.MinValue)
				DrawAnchoredLine(chartControl, chartScale, _h4OpenAnchor, _h4OpenPrice, _dxH4Open, H4OpenLineWidth, H4OpenDashStyle, canvasLeft, canvasRight);

			if (!ShowLabels || _textFormat == null) return;

			float xAnchor = canvasRight - 5f;
			float labelH = _textFormat.FontSize + 4f;

			if (ShowMidnightOpen && _midnightOpenAnchor != DateTime.MinValue) RenderLabelAt("Midnight Open", _midnightOpenPrice, xAnchor, labelH, chartScale, _dxMidnightOpen);
			if (ShowGlobexOpen && _globexOpenAnchor != DateTime.MinValue) RenderLabelAt("Globex Open", _globexOpenPrice, xAnchor, labelH, chartScale, _dxGlobexOpen);
			if (ShowNYOpenPrice && _nyOpenAnchor != DateTime.MinValue) RenderLabelAt("NY Open", _nyOpenPrice, xAnchor, labelH, chartScale, _dxNYOpen);
			if (ShowAsiaHighLow && _asiaHighAnchor != DateTime.MinValue)
			{
				RenderLabelAt("Asia High", _asiaHigh, xAnchor, labelH, chartScale, _dxAsiaHigh);
				RenderLabelAt("Asia Low", _asiaLow, xAnchor, labelH, chartScale, _dxAsiaLow);
			}
			if (ShowLondonHighLow && _londonHighAnchor != DateTime.MinValue)
			{
				RenderLabelAt("London High", _londonHigh, xAnchor, labelH, chartScale, _dxLondonHigh);
				RenderLabelAt("London Low", _londonLow, xAnchor, labelH, chartScale, _dxLondonLow);
			}
			if (ShowPDHL && _hasPrevDay)
			{
				RenderLabelAt("PDH", _pdHigh, xAnchor, labelH, chartScale, _dxPDHigh);
				RenderLabelAt("PDL", _pdLow, xAnchor, labelH, chartScale, _dxPDLow);
			}
			if (ShowPWHL && _hasPrevWeek)
			{
				RenderLabelAt("PWH", _pwHigh, xAnchor, labelH, chartScale, _dxPWHigh);
				RenderLabelAt("PWL", _pwLow, xAnchor, labelH, chartScale, _dxPWLow);
			}
			if (ShowM5Open && _m5OpenAnchor != DateTime.MinValue) RenderLabelAt("5m Open", _m5OpenPrice, xAnchor, labelH, chartScale, _dxM5Open);
			if (ShowH1Open && _h1OpenAnchor != DateTime.MinValue) RenderLabelAt("1H Open", _h1OpenPrice, xAnchor, labelH, chartScale, _dxH1Open);
			if (ShowH4Open && _h4OpenAnchor != DateTime.MinValue) RenderLabelAt("4H Open", _h4OpenPrice, xAnchor, labelH, chartScale, _dxH4Open);
		}

		// Line starts at the bar where the extreme/anchor occurred and runs to the chart's right edge.
		private void DrawAnchoredLine(ChartControl cc, ChartScale cs, DateTime anchor, double price,
			SharpDX.Direct2D1.Brush brush, int width, DashStyleHelper dash, float canvasLeft, float canvasRight)
		{
			if (brush == null) return;
			float x0 = Math.Max(GetXForTime(cc, anchor), canvasLeft);
			if (x0 >= canvasRight) return;
			DrawSharpDXLine(x0, canvasRight, cs.GetYByValue(price), brush, width, dash);
		}

		private float GetXForTime(ChartControl chartControl, DateTime time)
		{
			try
			{
				int barIdx = ChartBars.GetBarIdxByTime(chartControl, time);
				return Math.Max(chartControl.GetXByBarIndex(ChartBars, barIdx), chartControl.CanvasLeft + 5f);
			}
			catch { return chartControl.CanvasLeft + 5f; }
		}

		private void DrawSharpDXLine(float x0, float x1, float y, SharpDX.Direct2D1.Brush brush, int width, DashStyleHelper dash)
		{
			if (dash == DashStyleHelper.Solid)
			{
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, width);
			}
			else
			{
				float dashLen = dash == DashStyleHelper.Dot ? width * 2f : width * 6f;
				float gapLen = width * 3f;
				float x = x0; bool drawing = true;
				while (x < x1)
				{
					float segEnd = Math.Min(x + (drawing ? dashLen : gapLen), x1);
					if (drawing) RenderTarget.DrawLine(new SharpDX.Vector2(x, y), new SharpDX.Vector2(segEnd, y), brush, width);
					x = segEnd; drawing = !drawing;
				}
			}
		}

		private void RenderLabelAt(string text, double price, float xAnchor, float labelH, ChartScale chartScale, SharpDX.Direct2D1.Brush brush)
		{
			if (brush == null) return;
			float yCenter = chartScale.GetYByValue(price);
			float yTop = yCenter - labelH - 2f;
			using (var layout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, _textFormat, 300f, labelH))
			{
				float x;
				switch (LabelAlignment)
				{
					case TextAlignment.Left: x = xAnchor + 4f; break;
					case TextAlignment.Center: x = xAnchor - (float)layout.Metrics.Width / 2f; break;
					default: x = xAnchor - (float)layout.Metrics.Width - 4f; break;
				}
				RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, yTop), layout, brush, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}
		}

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1)
				return;

			// Always compute plot values — visibility is toggled via Plots[i].Brush from the
			// menu so toggling doesn't require a history replay.
			PlotEMA9[0] = _ema9[0];
			PlotEMA50[0] = _ema50[0];
			PlotEMA200[0] = _ema200[0];

			double vol0 = Volume[0];
			double typical = (High[0] + Low[0] + Close[0]) / 3.0;

			PlotVWAPDAYHIGH[0] = double.NaN;
			PlotVWAPDAYLOW[0] = double.NaN;

			if (Bars.IsFirstBarOfSession)
			{
				_cumVolumeSession = vol0;
				_cumTypicalVolumeSession = vol0 * typical;
				_dayHigh = High[0];
				_dayLow = Low[0];
				_cumVolumeDayHigh = 0; _cumTypicalVolumeDayHigh = 0;
				_cumVolumeDayLow = 0; _cumTypicalVolumeDayLow = 0;

				if (_pdAnchorStart != DateTime.MinValue)
				{
					_pdHigh = _todayHigh;
					_pdLow = _todayLow;
					_hasPrevDay = true;
				}
				_todayHigh = High[0];
				_todayLow = Low[0];
				_pdAnchorStart = Time[0];
			}
			else
			{
				if (High[0] > _todayHigh) _todayHigh = High[0];
				if (Low[0] < _todayLow) _todayLow = Low[0];
				_cumVolumeSession += vol0;
				_cumTypicalVolumeSession += vol0 * typical;

				if (High[0] >= _dayHigh)
				{
					_dayHigh = High[0];
					_cumVolumeDayHigh = vol0;
					_cumTypicalVolumeDayHigh = vol0 * typical;
				}
				else
				{
					_cumVolumeDayHigh += vol0;
					_cumTypicalVolumeDayHigh += vol0 * typical;
				}
				if (_cumVolumeDayHigh > 0)
					PlotVWAPDAYHIGH[0] = _cumTypicalVolumeDayHigh / _cumVolumeDayHigh;

				if (Low[0] <= _dayLow)
				{
					_dayLow = Low[0];
					_cumVolumeDayLow = vol0;
					_cumTypicalVolumeDayLow = vol0 * typical;
				}
				else
				{
					_cumVolumeDayLow += vol0;
					_cumTypicalVolumeDayLow += vol0 * typical;
				}
				if (_cumVolumeDayLow > 0)
					PlotVWAPDAYLOW[0] = _cumTypicalVolumeDayLow / _cumVolumeDayLow;
			}

			PlotVWAPSESSION[0] = _cumVolumeSession > 0
				? _cumTypicalVolumeSession / _cumVolumeSession
				: double.NaN;

			if (ShowFixedLines)
			{
				double step = FixedLinesStep;
				if (_lastFixedStep != step || Close[0] <= _lastFixedMin + step || Close[0] >= _lastFixedMax - step)
					RecomputeFixedLineBounds();
			}

			// ── Midnight / Globex / NY open anchors (one reused ray per level, latest day wins) ──
			DateTime chartTZTime = Time[0];
			TimeZoneInfo chartTZ = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
			DateTime nyTime = TimeZoneInfo.ConvertTime(chartTZTime, chartTZ, _cachedNYTimeZone);
			DateTime nyDate = nyTime.Date;
			TimeSpan nyTOD = nyTime.TimeOfDay;

			if (nyDate != _lastMidnightNYDate)
			{
				_lastMidnightNYDate = nyDate;
				_midnightOpenPrice = Open[0];
				_midnightOpenAnchor = Time[0];
			}

			// Fallback to first bar of session when Trading Hours template excludes the 18:00 NY bar (e.g. RTH-only)
			if (nyDate != _lastGlobexNYDate && (nyTOD >= new TimeSpan(18, 0, 0) || Bars.IsFirstBarOfSession))
			{
				_lastGlobexNYDate = nyDate;
				_globexOpenPrice = Open[0];
				_globexOpenAnchor = Time[0];
			}

			if (nyTOD >= _tsNYStart && nyDate != _lastNYOpenNYDate)
			{
				_lastNYOpenNYDate = nyDate;
				_nyOpenPrice = Open[0];
				_nyOpenAnchor = Time[0];
			}

			UpdateSessionHighLow(nyTOD, _tsAsiaStart, _tsAsiaEnd, ref _asiaInSession, ref _asiaHigh, ref _asiaHighAnchor, ref _asiaLow, ref _asiaLowAnchor);
			UpdateSessionHighLow(nyTOD, _tsLondonStart, _tsLondonEnd, ref _londonInSession, ref _londonHigh, ref _londonHighAnchor, ref _londonLow, ref _londonLowAnchor);

			// ── Previous Week High/Low — week bucket flips once every 7 calendar days from a fixed
			// Sunday epoch, grouping Sun-Sat together (the futures trading week) without needing
			// day-of-week special-casing or ISOWeek (unavailable on .NET Framework).
			int weekBucket = (int)(nyDate - WeekEpoch).TotalDays / 7;
			if (weekBucket != _lastWeekBucket)
			{
				_lastWeekBucket = weekBucket;
				if (_pwAnchorStart != DateTime.MinValue)
				{
					_pwHigh = _thisWeekHigh;
					_pwLow = _thisWeekLow;
					_hasPrevWeek = true;
				}
				_thisWeekHigh = High[0];
				_thisWeekLow = Low[0];
				_pwAnchorStart = Time[0];
			}
			else
			{
				if (High[0] > _thisWeekHigh) _thisWeekHigh = High[0];
				if (Low[0] < _thisWeekLow) _thisWeekLow = Low[0];
			}

			// ── 5m / 1H / 4H "candle open" trigger lines — the open of the current intrabar period,
			// independent of the chart's own timeframe. Bucket index rolls over naturally at midnight
			// (e.g. 23:55-00:00 and 00:00-00:05 are different 5m buckets), no date tracking needed.
			int nyMinutes = (int)nyTOD.TotalMinutes;
			UpdateIntrabarOpen(nyMinutes, 5, ref _lastM5Bucket, ref _m5OpenPrice, ref _m5OpenAnchor);
			UpdateIntrabarOpen(nyMinutes, 60, ref _lastH1Bucket, ref _h1OpenPrice, ref _h1OpenAnchor);
			UpdateIntrabarOpen(nyMinutes, 240, ref _lastH4Bucket, ref _h4OpenPrice, ref _h4OpenAnchor);
		}

		private void UpdateIntrabarOpen(int nyMinutes, int periodMinutes, ref int lastBucket, ref double openPrice, ref DateTime anchor)
		{
			int bucket = nyMinutes / periodMinutes;
			if (bucket == lastBucket) return;
			lastBucket = bucket;
			openPrice = Open[0];
			anchor = Time[0];
		}

		// ── wrap-aware "is time-of-day within [start,end)" check ───────────
		private static bool InTimeRange(TimeSpan t, TimeSpan start, TimeSpan end)
		{
			if (start <= end)
				return t >= start && t < end;
			return t >= start || t < end;
		}

		private void UpdateSessionHighLow(TimeSpan nyTOD, TimeSpan start, TimeSpan end, ref bool inSession,
			ref double high, ref DateTime highAnchor, ref double low, ref DateTime lowAnchor)
		{
			bool inRange = InTimeRange(nyTOD, start, end);
			if (!inRange) { inSession = false; return; }

			if (!inSession)
			{
				inSession = true;
				high = High[0]; highAnchor = Time[0];
				low = Low[0]; lowAnchor = Time[0];
			}
			else
			{
				if (High[0] > high) { high = High[0]; highAnchor = Time[0]; }
				if (Low[0] < low) { low = Low[0]; lowAnchor = Time[0]; }
			}
		}

		private static TimeSpan HHMMToTimeSpan(int hhmm) => new TimeSpan(hhmm / 100, hhmm % 100, 0);

		private void RecomputeFixedLineBounds()
		{
			double step = FixedLinesStep;
			double range = step * FixedLinesRange;
			double center = Math.Floor(Close[0] / step) * step;
			_lastFixedStep = step;
			_lastFixedMin = center - range;
			_lastFixedMax = center + range;
		}

		// ══════════════════════════════════════════════════════════════════
		//  WPF Controls
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
					Header = "ICN_FixedLines",
					Margin = new Thickness(0),
					Padding = new Thickness(1),
					Style = mainMenuItemStyle,
					VerticalAlignment = VerticalAlignment.Center,
				};
				ntBarMenu.Items.Add(ntBartopMenuItem);

				_showAll = ShowFixedLines && ShowSessionVWAP && ShowDayHighVWAP && ShowDayLowVWAP && ShowEma9 && ShowEma50 && ShowEma200
					&& ShowMidnightOpen && ShowGlobexOpen && ShowNYOpenPrice && ShowAsiaHighLow && ShowLondonHighLow && ShowPDHL && ShowPWHL && ShowM5Open && ShowH1Open && ShowH4Open && ShowLabels;

				ntShowHide = MakeItem(_showAll ? "Hide All" : "Show All"); ntShowHide.Tag = "ShowAll";
				ntShowHide.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntShowHide);
				ntBartopMenuItem.Items.Add(new Separator());

				ntFixedLinesItem = MakeColoredItem(ShowFixedLines ? "Hide Fixed Lines" : "Show Fixed Lines", FixedLinesColor); ntFixedLinesItem.Tag = "ShowFixedLines";
				ntSessionVWAPItem = MakeColoredItem(ShowSessionVWAP ? "Hide Session VWAP" : "Show Session VWAP", Brushes.White); ntSessionVWAPItem.Tag = "ShowSessionVWAP";
				ntDayHighVWAPItem = MakeColoredItem(ShowDayHighVWAP ? "Hide Day-High VWAP" : "Show Day-High VWAP", Brushes.Magenta); ntDayHighVWAPItem.Tag = "ShowDayHighVWAP";
				ntDayLowVWAPItem = MakeColoredItem(ShowDayLowVWAP ? "Hide Day-Low VWAP" : "Show Day-Low VWAP", Brushes.Lime); ntDayLowVWAPItem.Tag = "ShowDayLowVWAP";
				ntEma9Item = MakeColoredItem(ShowEma9 ? "Hide EMA " + Ema9Period : "Show EMA " + Ema9Period, Brushes.HotPink); ntEma9Item.Tag = "ShowEma9";
				ntEma50Item = MakeColoredItem(ShowEma50 ? "Hide EMA " + Ema50Period : "Show EMA " + Ema50Period, Brushes.Turquoise); ntEma50Item.Tag = "ShowEma50";
				ntEma200Item = MakeColoredItem(ShowEma200 ? "Hide EMA " + Ema200Period : "Show EMA " + Ema200Period, Brushes.Orange); ntEma200Item.Tag = "ShowEma200";
				ntMidnightOpenItem = MakeColoredItem(ShowMidnightOpen ? "Hide Midnight Open" : "Show Midnight Open", MidnightOpenBrush); ntMidnightOpenItem.Tag = "ShowMidnightOpen";
				ntGlobexOpenItem = MakeColoredItem(ShowGlobexOpen ? "Hide Globex Open" : "Show Globex Open", GlobexOpenBrush); ntGlobexOpenItem.Tag = "ShowGlobexOpen";
				ntNYOpenItem = MakeColoredItem(ShowNYOpenPrice ? "Hide NY Open" : "Show NY Open", NYOpenBrush); ntNYOpenItem.Tag = "ShowNYOpenPrice";
				ntAsiaHighLowItem = MakeColoredItem(ShowAsiaHighLow ? "Hide Asia High/Low" : "Show Asia High/Low", AsiaHighBrush); ntAsiaHighLowItem.Tag = "ShowAsiaHighLow";
				ntLondonHighLowItem = MakeColoredItem(ShowLondonHighLow ? "Hide London High/Low" : "Show London High/Low", LondonHighBrush); ntLondonHighLowItem.Tag = "ShowLondonHighLow";
				ntPDHLItem = MakeColoredItem(ShowPDHL ? "Hide Prev Day High/Low" : "Show Prev Day High/Low", PDHighBrush); ntPDHLItem.Tag = "ShowPDHL";
				ntPWHLItem = MakeColoredItem(ShowPWHL ? "Hide Prev Week High/Low" : "Show Prev Week High/Low", PWHighBrush); ntPWHLItem.Tag = "ShowPWHL";
				ntM5OpenItem = MakeColoredItem(ShowM5Open ? "Hide 5m Open" : "Show 5m Open", M5OpenBrush); ntM5OpenItem.Tag = "ShowM5Open";
				ntH1OpenItem = MakeColoredItem(ShowH1Open ? "Hide 1H Open" : "Show 1H Open", H1OpenBrush); ntH1OpenItem.Tag = "ShowH1Open";
				ntH4OpenItem = MakeColoredItem(ShowH4Open ? "Hide 4H Open" : "Show 4H Open", H4OpenBrush); ntH4OpenItem.Tag = "ShowH4Open";
				ntShowLabelsItem = MakeItem(ShowLabels ? "Hide Labels" : "Show Labels"); ntShowLabelsItem.Tag = "ShowLabels";

				ntFixedLinesItem.Click += NTBarMenu_Click;
				ntSessionVWAPItem.Click += NTBarMenu_Click;
				ntDayHighVWAPItem.Click += NTBarMenu_Click;
				ntDayLowVWAPItem.Click += NTBarMenu_Click;
				ntEma9Item.Click += NTBarMenu_Click;
				ntEma50Item.Click += NTBarMenu_Click;
				ntEma200Item.Click += NTBarMenu_Click;
				ntMidnightOpenItem.Click += NTBarMenu_Click;
				ntGlobexOpenItem.Click += NTBarMenu_Click;
				ntNYOpenItem.Click += NTBarMenu_Click;
				ntAsiaHighLowItem.Click += NTBarMenu_Click;
				ntLondonHighLowItem.Click += NTBarMenu_Click;
				ntPDHLItem.Click += NTBarMenu_Click;
				ntPWHLItem.Click += NTBarMenu_Click;
				ntM5OpenItem.Click += NTBarMenu_Click;
				ntH1OpenItem.Click += NTBarMenu_Click;
				ntH4OpenItem.Click += NTBarMenu_Click;
				ntShowLabelsItem.Click += NTBarMenu_Click;

				ntBartopMenuItem.Items.Add(ntFixedLinesItem);
				ntBartopMenuItem.Items.Add(ntSessionVWAPItem);
				ntBartopMenuItem.Items.Add(ntDayHighVWAPItem);
				ntBartopMenuItem.Items.Add(ntDayLowVWAPItem);
				ntBartopMenuItem.Items.Add(ntEma9Item);
				ntBartopMenuItem.Items.Add(ntEma50Item);
				ntBartopMenuItem.Items.Add(ntEma200Item);
				ntBartopMenuItem.Items.Add(ntMidnightOpenItem);
				ntBartopMenuItem.Items.Add(ntGlobexOpenItem);
				ntBartopMenuItem.Items.Add(ntNYOpenItem);
				ntBartopMenuItem.Items.Add(ntAsiaHighLowItem);
				ntBartopMenuItem.Items.Add(ntLondonHighLowItem);
				ntBartopMenuItem.Items.Add(ntPDHLItem);
				ntBartopMenuItem.Items.Add(ntPWHLItem);
				ntBartopMenuItem.Items.Add(ntM5OpenItem);
				ntBartopMenuItem.Items.Add(ntH1OpenItem);
				ntBartopMenuItem.Items.Add(ntH4OpenItem);
				ntBartopMenuItem.Items.Add(new Separator());
				ntBartopMenuItem.Items.Add(ntShowLabelsItem);

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
						ShowFixedLines = ShowSessionVWAP = ShowDayHighVWAP = ShowDayLowVWAP = ShowEma9 = ShowEma50 = ShowEma200
							= ShowMidnightOpen = ShowGlobexOpen = ShowNYOpenPrice = ShowAsiaHighLow = ShowLondonHighLow = ShowPDHL = ShowPWHL = ShowM5Open = ShowH1Open = ShowH4Open = ShowLabels = _showAll;
						break;
					case "ShowFixedLines": ShowFixedLines = !ShowFixedLines; break;
					case "ShowSessionVWAP": ShowSessionVWAP = !ShowSessionVWAP; break;
					case "ShowDayHighVWAP": ShowDayHighVWAP = !ShowDayHighVWAP; break;
					case "ShowDayLowVWAP": ShowDayLowVWAP = !ShowDayLowVWAP; break;
					case "ShowEma9": ShowEma9 = !ShowEma9; break;
					case "ShowEma50": ShowEma50 = !ShowEma50; break;
					case "ShowEma200": ShowEma200 = !ShowEma200; break;
					case "ShowMidnightOpen": ShowMidnightOpen = !ShowMidnightOpen; break;
					case "ShowGlobexOpen": ShowGlobexOpen = !ShowGlobexOpen; break;
					case "ShowNYOpenPrice": ShowNYOpenPrice = !ShowNYOpenPrice; break;
					case "ShowAsiaHighLow": ShowAsiaHighLow = !ShowAsiaHighLow; break;
					case "ShowLondonHighLow": ShowLondonHighLow = !ShowLondonHighLow; break;
					case "ShowPDHL": ShowPDHL = !ShowPDHL; break;
					case "ShowPWHL": ShowPWHL = !ShowPWHL; break;
					case "ShowM5Open": ShowM5Open = !ShowM5Open; break;
					case "ShowH1Open": ShowH1Open = !ShowH1Open; break;
					case "ShowH4Open": ShowH4Open = !ShowH4Open; break;
					case "ShowLabels": ShowLabels = !ShowLabels; break;
				}
			}
			catch (Exception ex) { Print("ICNFixedLines menu error: " + ex.Message); }
			finally
			{
				try
				{
					_showAll = ShowFixedLines && ShowSessionVWAP && ShowDayHighVWAP && ShowDayLowVWAP && ShowEma9 && ShowEma50 && ShowEma200
					&& ShowMidnightOpen && ShowGlobexOpen && ShowNYOpenPrice && ShowAsiaHighLow && ShowLondonHighLow && ShowPDHL && ShowPWHL && ShowM5Open && ShowH1Open && ShowH4Open && ShowLabels;

					ntShowHide.Header = _showAll ? "Hide All" : "Show All";
					ntFixedLinesItem.Header = ShowFixedLines ? "Hide Fixed Lines" : "Show Fixed Lines";
					ntSessionVWAPItem.Header = ShowSessionVWAP ? "Hide Session VWAP" : "Show Session VWAP";
					ntDayHighVWAPItem.Header = ShowDayHighVWAP ? "Hide Day-High VWAP" : "Show Day-High VWAP";
					ntDayLowVWAPItem.Header = ShowDayLowVWAP ? "Hide Day-Low VWAP" : "Show Day-Low VWAP";
					ntEma9Item.Header = ShowEma9 ? "Hide EMA " + Ema9Period : "Show EMA " + Ema9Period;
					ntEma50Item.Header = ShowEma50 ? "Hide EMA " + Ema50Period : "Show EMA " + Ema50Period;
					ntEma200Item.Header = ShowEma200 ? "Hide EMA " + Ema200Period : "Show EMA " + Ema200Period;
					ntMidnightOpenItem.Header = ShowMidnightOpen ? "Hide Midnight Open" : "Show Midnight Open";
					ntGlobexOpenItem.Header = ShowGlobexOpen ? "Hide Globex Open" : "Show Globex Open";
					ntNYOpenItem.Header = ShowNYOpenPrice ? "Hide NY Open" : "Show NY Open";
					ntAsiaHighLowItem.Header = ShowAsiaHighLow ? "Hide Asia High/Low" : "Show Asia High/Low";
					ntLondonHighLowItem.Header = ShowLondonHighLow ? "Hide London High/Low" : "Show London High/Low";
					ntPDHLItem.Header = ShowPDHL ? "Hide Prev Day High/Low" : "Show Prev Day High/Low";
					ntPWHLItem.Header = ShowPWHL ? "Hide Prev Week High/Low" : "Show Prev Week High/Low";
					ntM5OpenItem.Header = ShowM5Open ? "Hide 5m Open" : "Show 5m Open";
					ntH1OpenItem.Header = ShowH1Open ? "Hide 1H Open" : "Show 1H Open";
					ntH4OpenItem.Header = ShowH4Open ? "Hide 4H Open" : "Show 4H Open";
					ntShowLabelsItem.Header = ShowLabels ? "Hide Labels" : "Show Labels";

					Plots[5].Brush = ShowEma9 ? Brushes.HotPink : Brushes.Transparent;
					Plots[0].Brush = ShowEma50 ? Brushes.Turquoise : Brushes.Transparent;
					Plots[1].Brush = ShowEma200 ? Brushes.Orange : Brushes.Transparent;
					Plots[2].Brush = ShowSessionVWAP ? Brushes.White : Brushes.Transparent;
					Plots[3].Brush = ShowDayHighVWAP ? Brushes.Magenta : Brushes.Transparent;
					Plots[4].Brush = ShowDayLowVWAP ? Brushes.Lime : Brushes.Transparent;

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
		[Display(Name = "Show Fixed Lines", GroupName = "Fixed Lines", Order = 1)]
		public bool ShowFixedLines { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Step", GroupName = "Fixed Lines", Order = 2)]
		public double FixedLinesStep { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Range (levels each side)", GroupName = "Fixed Lines", Order = 3)]
		public int FixedLinesRange { get; set; }

		[XmlIgnore]
		[Display(Name = "Color", GroupName = "Fixed Lines", Order = 4)]
		public Brush FixedLinesColor { get; set; }

		[Browsable(false)]
		public string FixedLinesColorSerializable
		{
			get { return Serialize.BrushToString(FixedLinesColor); }
			set { FixedLinesColor = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Show Session VWAP", GroupName = "VWAP", Order = 1)]
		public bool ShowSessionVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Day-High VWAP", GroupName = "VWAP", Order = 2)]
		public bool ShowDayHighVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Day-Low VWAP", GroupName = "VWAP", Order = 3)]
		public bool ShowDayLowVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 9", GroupName = "EMA", Order = -2)]
		public bool ShowEma9 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 9 Period", GroupName = "EMA", Order = -1)]
		public int Ema9Period { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 50", GroupName = "EMA", Order = 1)]
		public bool ShowEma50 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 50 Period", GroupName = "EMA", Order = 2)]
		public int Ema50Period { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 200", GroupName = "EMA", Order = 3)]
		public bool ShowEma200 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 200 Period", GroupName = "EMA", Order = 4)]
		public int Ema200Period { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "NY Start Time (HHmm)", GroupName = "Session Opens", Order = 0)]
		public int NYStartTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Midnight Open", GroupName = "Session Opens", Order = 1)]
		public bool ShowMidnightOpen { get; set; }
		[XmlIgnore]
		[Display(Name = "Midnight Open Color", GroupName = "Session Opens", Order = 2)]
		public Brush MidnightOpenBrush { get; set; }
		[Browsable(false)]
		public string MidnightOpenBrushSerialize { get { return Serialize.BrushToString(MidnightOpenBrush); } set { MidnightOpenBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Midnight Open Line Width", GroupName = "Session Opens", Order = 3)]
		public int MidnightOpenLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "Midnight Open Dash Style", GroupName = "Session Opens", Order = 4)]
		public DashStyleHelper MidnightOpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Globex Open", GroupName = "Session Opens", Order = 5)]
		public bool ShowGlobexOpen { get; set; }
		[XmlIgnore]
		[Display(Name = "Globex Open Color", GroupName = "Session Opens", Order = 6)]
		public Brush GlobexOpenBrush { get; set; }
		[Browsable(false)]
		public string GlobexOpenBrushSerialize { get { return Serialize.BrushToString(GlobexOpenBrush); } set { GlobexOpenBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Globex Open Line Width", GroupName = "Session Opens", Order = 7)]
		public int GlobexOpenLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "Globex Open Dash Style", GroupName = "Session Opens", Order = 8)]
		public DashStyleHelper GlobexOpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show NY Open", GroupName = "Session Opens", Order = 9)]
		public bool ShowNYOpenPrice { get; set; }
		[XmlIgnore]
		[Display(Name = "NY Open Color", GroupName = "Session Opens", Order = 10)]
		public Brush NYOpenBrush { get; set; }
		[Browsable(false)]
		public string NYOpenBrushSerialize { get { return Serialize.BrushToString(NYOpenBrush); } set { NYOpenBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "NY Open Line Width", GroupName = "Session Opens", Order = 11)]
		public int NYOpenLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "NY Open Dash Style", GroupName = "Session Opens", Order = 12)]
		public DashStyleHelper NYOpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Asia High/Low", GroupName = "Asia / London", Order = 0)]
		public bool ShowAsiaHighLow { get; set; }
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia Start Time (HHmm)", GroupName = "Asia / London", Order = 1)]
		public int AsiaStartTime { get; set; }
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia End Time (HHmm)", GroupName = "Asia / London", Order = 2)]
		public int AsiaEndTime { get; set; }
		[XmlIgnore]
		[Display(Name = "Asia High Color", GroupName = "Asia / London", Order = 3)]
		public Brush AsiaHighBrush { get; set; }
		[Browsable(false)]
		public string AsiaHighBrushSerialize { get { return Serialize.BrushToString(AsiaHighBrush); } set { AsiaHighBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore]
		[Display(Name = "Asia Low Color", GroupName = "Asia / London", Order = 4)]
		public Brush AsiaLowBrush { get; set; }
		[Browsable(false)]
		public string AsiaLowBrushSerialize { get { return Serialize.BrushToString(AsiaLowBrush); } set { AsiaLowBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Asia Line Width", GroupName = "Asia / London", Order = 5)]
		public int AsiaLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "Asia Dash Style", GroupName = "Asia / London", Order = 6)]
		public DashStyleHelper AsiaDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show London High/Low", GroupName = "Asia / London", Order = 7)]
		public bool ShowLondonHighLow { get; set; }
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "London Start Time (HHmm)", GroupName = "Asia / London", Order = 8)]
		public int LondonStartTime { get; set; }
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "London End Time (HHmm)", GroupName = "Asia / London", Order = 9)]
		public int LondonEndTime { get; set; }
		[XmlIgnore]
		[Display(Name = "London High Color", GroupName = "Asia / London", Order = 10)]
		public Brush LondonHighBrush { get; set; }
		[Browsable(false)]
		public string LondonHighBrushSerialize { get { return Serialize.BrushToString(LondonHighBrush); } set { LondonHighBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore]
		[Display(Name = "London Low Color", GroupName = "Asia / London", Order = 11)]
		public Brush LondonLowBrush { get; set; }
		[Browsable(false)]
		public string LondonLowBrushSerialize { get { return Serialize.BrushToString(LondonLowBrush); } set { LondonLowBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "London Line Width", GroupName = "Asia / London", Order = 12)]
		public int LondonLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "London Dash Style", GroupName = "Asia / London", Order = 13)]
		public DashStyleHelper LondonDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Prev Day High/Low", GroupName = "Prev Day", Order = 0)]
		public bool ShowPDHL { get; set; }
		[XmlIgnore]
		[Display(Name = "PDH Color", GroupName = "Prev Day", Order = 1)]
		public Brush PDHighBrush { get; set; }
		[Browsable(false)]
		public string PDHighBrushSerialize { get { return Serialize.BrushToString(PDHighBrush); } set { PDHighBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore]
		[Display(Name = "PDL Color", GroupName = "Prev Day", Order = 2)]
		public Brush PDLowBrush { get; set; }
		[Browsable(false)]
		public string PDLowBrushSerialize { get { return Serialize.BrushToString(PDLowBrush); } set { PDLowBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Prev Day Line Width", GroupName = "Prev Day", Order = 3)]
		public int PDLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "Prev Day Dash Style", GroupName = "Prev Day", Order = 4)]
		public DashStyleHelper PDDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Prev Week High/Low", GroupName = "Prev Week", Order = 0)]
		public bool ShowPWHL { get; set; }
		[XmlIgnore]
		[Display(Name = "PWH Color", GroupName = "Prev Week", Order = 1)]
		public Brush PWHighBrush { get; set; }
		[Browsable(false)]
		public string PWHighBrushSerialize { get { return Serialize.BrushToString(PWHighBrush); } set { PWHighBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore]
		[Display(Name = "PWL Color", GroupName = "Prev Week", Order = 2)]
		public Brush PWLowBrush { get; set; }
		[Browsable(false)]
		public string PWLowBrushSerialize { get { return Serialize.BrushToString(PWLowBrush); } set { PWLowBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Prev Week Line Width", GroupName = "Prev Week", Order = 3)]
		public int PWLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "Prev Week Dash Style", GroupName = "Prev Week", Order = 4)]
		public DashStyleHelper PWDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 5m Open", Description = "Horizontal line at the open of the current 5-minute NY-time bucket, independent of chart timeframe.", GroupName = "Intrabar Opens", Order = 0)]
		public bool ShowM5Open { get; set; }
		[XmlIgnore]
		[Display(Name = "5m Open Color", GroupName = "Intrabar Opens", Order = 1)]
		public Brush M5OpenBrush { get; set; }
		[Browsable(false)]
		public string M5OpenBrushSerialize { get { return Serialize.BrushToString(M5OpenBrush); } set { M5OpenBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "5m Open Line Width", GroupName = "Intrabar Opens", Order = 2)]
		public int M5OpenLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "5m Open Dash Style", GroupName = "Intrabar Opens", Order = 3)]
		public DashStyleHelper M5OpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 1H Open", Description = "Horizontal line at the open of the current 1-hour NY-time bucket, independent of chart timeframe.", GroupName = "Intrabar Opens", Order = 4)]
		public bool ShowH1Open { get; set; }
		[XmlIgnore]
		[Display(Name = "1H Open Color", GroupName = "Intrabar Opens", Order = 5)]
		public Brush H1OpenBrush { get; set; }
		[Browsable(false)]
		public string H1OpenBrushSerialize { get { return Serialize.BrushToString(H1OpenBrush); } set { H1OpenBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "1H Open Line Width", GroupName = "Intrabar Opens", Order = 6)]
		public int H1OpenLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "1H Open Dash Style", GroupName = "Intrabar Opens", Order = 7)]
		public DashStyleHelper H1OpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 4H Open", Description = "Horizontal line at the open of the current 4-hour NY-time bucket, independent of chart timeframe.", GroupName = "Intrabar Opens", Order = 8)]
		public bool ShowH4Open { get; set; }
		[XmlIgnore]
		[Display(Name = "4H Open Color", GroupName = "Intrabar Opens", Order = 9)]
		public Brush H4OpenBrush { get; set; }
		[Browsable(false)]
		public string H4OpenBrushSerialize { get { return Serialize.BrushToString(H4OpenBrush); } set { H4OpenBrush = Serialize.StringToBrush(value); } }
		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "4H Open Line Width", GroupName = "Intrabar Opens", Order = 10)]
		public int H4OpenLineWidth { get; set; }
		[NinjaScriptProperty]
		[Display(Name = "4H Open Dash Style", GroupName = "Intrabar Opens", Order = 11)]
		public DashStyleHelper H4OpenDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Labels", Description = "Master toggle for all line labels (Midnight/Globex/NY Open, Asia/London High/Low).", GroupName = "1. General", Order = 0)]
		public bool ShowLabels { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Label Alignment", Description = "Anchors labels at the right edge of the chart; Left/Right/Center controls which side of that anchor the text sits on.", GroupName = "1. General", Order = 1)]
		public TextAlignment LabelAlignment { get; set; }

		[NinjaScriptProperty]
		[Range(6, 24)]
		[Display(Name = "Label Font Size", GroupName = "1. General", Order = 2)]
		public double LabelFontSize { get; set; }

		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA9 => Values[5];
		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA50 => Values[0];
		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA200 => Values[1];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPSESSION => Values[2];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPDAYHIGH => Values[3];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPDAYLOW => Values[4];
		#endregion
	}
}