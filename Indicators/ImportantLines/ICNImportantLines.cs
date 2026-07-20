#region Using declarations
using System;
using System.Collections.Generic;
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

public enum ValueAreaMethod { VolumeProfile, Uniform, LinearWeighted, CloseWeighted, TPO }
public enum ICNCorner { TopLeft, TopRight, BottomLeft, BottomRight }

namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{

	public class ICNImportantLines : Indicator
	{
		// ══════════════════════════════════════════════════════════════════
		//  VWAP state
		// ══════════════════════════════════════════════════════════════════
		private double _cumVolumeSession, _cumTypicalVolumeSession, _cumSqSession;
		private double _lastVwapSession, _lastVwapNY;         // last known values for status panel
		private double _lastVwapAsia, _lastVwapEurope;     // last known values for status panel
		private double _lastVwapDayHigh, _lastVwapDayLow;    // last known HOD/LOD VWAP values
		private bool _isAsiaActive, _isEuropeActive, _isNYActive; // session windows active at last bar
		private readonly double _pdVwapNY;      // previous session NY VWAP endpoint
		private readonly double _pdVwapSession; // previous session Session VWAP endpoint
		private SharpDX.Direct2D1.Brush _dxPdVwapNYBrush;
		private SharpDX.Direct2D1.Brush _dxPdVwapSessionBrush;

		// TimeZone cache — avoid expensive FindSystemTimeZoneById on every bar
		private TimeZoneInfo _cachedNYTimeZone;

		// WPF colors cached for panel rows (safe to use any time)
		private System.Windows.Media.Color _panelColorPDVwapNY;
		private System.Windows.Media.Color _panelColorPDVwapSession;

		// ── ORB state ────────────────────────────────────────────────────────

		// ── ORB state (one per session) ────────────────────────────────────
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
		private OrbState _orbNY, _orbAsia, _orbEurope, _ib;
		private SharpDX.Direct2D1.Brush _dxIBHighBrush, _dxIBLowBrush, _dxIBExtBrush;
		private SharpDX.Direct2D1.Brush _dxOrbHighBrush, _dxOrbLowBrush;
		private SharpDX.Direct2D1.Brush _dxOrbAsiaHighBrush, _dxOrbAsiaLowBrush;
		private SharpDX.Direct2D1.Brush _dxOrbEuropeHighBrush, _dxOrbEuropeLowBrush;
		private SharpDX.Direct2D1.Brush _dxAsiaHighLineBrush, _dxAsiaLowLineBrush;
		private SharpDX.Direct2D1.Brush _dxEuropeHighLineBrush, _dxEuropeLowLineBrush;
		private SharpDX.Direct2D1.Brush _dxGlobexOpenBrush;
		private SharpDX.Direct2D1.Brush _dxMidnightOpenBrush;
		private SharpDX.Direct2D1.Brush _dxPwhBrush, _dxPwlBrush;
		private double _asiaSessionHigh, _asiaSessionLow;
		private bool _asiaSessionStarted;
		private DateTime _asiaSessionStart, _asiaSessionEnd;
		private double _europeSessionHigh, _europeSessionLow;
		private bool _europeSessionStarted;
		private DateTime _europeSessionStart, _europeSessionEnd;
		private bool _nySessionStarted;
		private DateTime _nySessionStart;
		private DateTime _nySessionEnd;
		// Session-open vertical line markers
		private List<DateTime> _dayOpenTimes, _asiaOpenTimes, _londonOpenTimes, _nyOpenTimes;
		private SharpDX.Direct2D1.Brush _dxDayOpenLineBrush, _dxAsiaOpenLineBrush, _dxLondonOpenLineBrush, _dxNYOpenLineBrush;
		private double _cumVolumeAsia, _cumTypicalVolumeAsia;
		private double _cumVolumeEurope, _cumTypicalVolumeEurope;
		private double _cumVolumeNY, _cumTypicalVolumeNY;
		private double _cumVolumeDayHigh, _cumTypicalVolumeDayHigh;
		private double _cumVolumeDayLow, _cumTypicalVolumeDayLow;
		// Weekly VWAP — anchored at Sunday 18:00 ET (CME futures week start)
		private double _cumVolumeWeek, _cumTypicalVolumeWeek;
		private double _lastVwapWeek;
		// Rolling 24h VWAP — sliding window, not anchored to any session boundary
		private Queue<(DateTime Time, double Vol, double TypVol)> _vwap24hQueue;
		private double _cumVol24h, _cumTypVol24h;
		private double _lastVwap24h;

		private DateTime _currentWeekStart;
		// Previous Week High/Low
		private double _weekHigh, _weekLow, _prevWeekHigh, _prevWeekLow;
		private bool _hasPrevWeek;
		private DateTime _weekStartBar;
		private double _dayHigh, _dayLow;
		private bool _dayHighStarted, _dayLowStarted;
		private double _currentPrice;

		// ══════════════════════════════════════════════════════════════════
		//  PrevDayLevels state
		// ══════════════════════════════════════════════════════════════════

		// A completed NQ session (18:00 → 17:00 ET next day)
		private struct FinishedSession
		{
			public DateTime SessionStart;    // first bar of session (at or after 18:00)
			public DateTime SessionEnd;      // last bar of session
			public DateTime NextSessionStart; // first bar of the NEXT session (for extension line)
			public DateTime NextSessionEnd;   // last bar of the next session (DateTime.MinValue = still live)
			public double PDH, PDL, PVAH, PVAL, POC;
			public double VwapNY;      // NY VWAP value at session end
			public double VwapSession; // Session VWAP value at session end
		}

		// All finished sessions, keyed by SessionStart so we can redraw on menu toggle
		private List<FinishedSession> _finishedSessions;

		// Accumulator for the session currently in progress
		private DateTime _currentSessionStart; // first bar at or after 18:00 ET of this session
		private bool _inSession;           // true once 18:00 bar has been seen today
		private double _sessionHigh, _sessionLow;
		private double _pocPrice;
		private long _pocVolume;
		private Dictionary<long, long> _bucketVolume;
		private Dictionary<long, long> _bucketTPO;         // TPO bar-count per bucket
		private Dictionary<long, long> _bucketUniform;     // volume spread uniformly across range
		private Dictionary<long, long> _bucketLinear;      // volume weighted toward midpoint
		private Dictionary<long, long> _bucketClose;       // volume assigned to close bucket

		// Previous (most recent finished) session – for label rendering
		private FinishedSession _prevSession;
		private bool _hasPreviousDay;

		// Today live tracking (from 18:00 onwards)
		// Perf caches — fixed after configuration, computed once at DataLoaded
		private EMA _ema1, _ema2, _ema3, _ema4;
		private TimeSpan _tsAsiaStart, _tsAsiaEnd, _tsEuropeStart, _tsEuropeEnd, _tsNYStart, _tsNYEnd;
		private List<long> _vaSortBuffer; // reused key buffer for value-area sort (avoids per-bar alloc)

		private double _tpvah, _tpval;
		private double _todayPoc;
		private DateTime _marketOpenStart; // first bar at or after 18:00 NY – visual start of today lines
		private double _globexOpenPrice; // open price of today's Globex session (18:00 ET bar)
		private double _midnightOpenPrice; // open price of the 00:00 ET bar
		private DateTime _midnightOpenStart;
		private DateTime _lastMidnightNYDate;
		private DateTime _lastGlobexOpenNYDate;     // NY date for which the 1-min series captured the Globex open
		private DateTime _lastMidnightMinuteNYDate; // NY date for which the 1-min series captured the midnight open

		// Track the last NY date we saw an 18:00 bar to detect session roll
		private DateTime _lastSessionRollNYDate;

		// SharpDX brushes for PrevDayLevels labels
		private SharpDX.Direct2D1.Brush _dxPdhBrush, _dxPdlBrush, _dxPvahBrush, _dxPvalBrush;
		private SharpDX.Direct2D1.Brush _dxTdhBrush, _dxTdlBrush, _dxTpvahBrush, _dxTpvalBrush;
		private SharpDX.Direct2D1.Brush _dxVABackgroundBrush;
		private SharpDX.Direct2D1.Brush _dxPocBrush;
		private SharpDX.DirectWrite.TextFormat _textFormat;

		// ══════════════════════════════════════════════════════════════════
		//  WPF menu state
		// ══════════════════════════════════════════════════════════════════
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private bool ntBarActive;
		private Menu ntBarMenu;
		private NTMenuItem ntBartopMenuItem;

		// Show/Hide All
		private NTMenuItem ntShowHide;
		private bool _showAll;

		// EMA menu items
		private NTMenuItem ntEMAShowHide;
		private NTMenuItem ntEMA1, ntEMA2, ntEMA3, ntEMA4;
		private bool _showAllEMA;

		// VWAP menu items
		private NTMenuItem ntVWAPShowHide;
		private NTMenuItem ntVWAPSession, ntVWAPAsia, ntVWAPEurope, ntVWAPNY, ntVWAPDayHigh, ntVWAPDayLow, ntVWAPWeek, ntVWAP24h;
		private NTMenuItem ntVWAPBands;
		private NTMenuItem ntPWH, ntPWL;
		private bool _showAllVWAP;

		// PrevDayLevels menu items
		private readonly NTMenuItem ntPDLShowHide;
		private NTMenuItem ntHistoricShowHide;
		private NTMenuItem ntHistoricLines;
		private NTMenuItem ntPDH, ntPDL, ntPVAH, ntPVAL, ntPrevPOC;
		private NTMenuItem ntTodayShowHide;
		private NTMenuItem ntTodayVABackground;
		private NTMenuItem ntTDH, ntTDL, ntTPVAH, ntTPVAL, ntPOC;
		private NTMenuItem ntGlobexOpen;
		private NTMenuItem ntMidnightOpen;
		private NTMenuItem ntTodayPDH, ntTodayPDL;
		private NTMenuItem ntDayOpenLine, ntAsiaOpenLine, ntLondonOpenLine, ntNYOpenLine;
		private bool _showAllPDL;
		private bool _showAllHistoric;
		private bool _showAllToday;

		// FixedLines menu items
		private NTMenuItem ntFixedLinesShowHide;
		private NTMenuItem ntIBShowHide, ntIBExtShowHide;
		private NTMenuItem ntORBShowHide;
		private NTMenuItem ntAsiaORBShowHide;
		private NTMenuItem ntEuropeORBShowHide;
		private NTMenuItem ntAsiaHighLowShowHide;
		private NTMenuItem ntEuropeHighLowShowHide;
		private NTMenuItem ntPDVwapNY;
		private NTMenuItem ntPDVwapSession;
		private bool _showAllFixed;

		private System.Windows.Style mainMenuItemStyle, systemMenuStyle;
		private System.Windows.Controls.TabItem tabItem;
		private ChartTab chartTab;

		// ══════════════════════════════════════════════════════════════════
		//  OnStateChange
		// ══════════════════════════════════════════════════════════════════
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "ICN – Combined: Session VWAP, Previous Day Levels, Fixed Number Lines.";
				Name = "ICNImportantLines";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = true;
				DrawOnPricePanel = true;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = true;

				NinjaTrader.Core.Instrumentation.ChartPerformanceThresholdMilliseconds = 50;

				// VWAP plots
				AddPlot(new Stroke(Brushes.Cyan, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPASIA");
				AddPlot(new Stroke(Brushes.Gold, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPEUROPE");
				AddPlot(new Stroke(Brushes.Lime, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPNY");
				AddPlot(new Stroke(Brushes.White, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPSESSION");
				AddPlot(new Stroke(Brushes.Magenta, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPDAYHIGH");
				AddPlot(new Stroke(Brushes.Green, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPDAYLOW");
				AddPlot(new Stroke(Brushes.DarkOrange, DashStyleHelper.Solid, 3), PlotStyle.Line, "PlotVWAPWEEK");

				// EMA plots
				AddPlot(new Stroke(Brushes.Turquoise, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA1");
				AddPlot(new Stroke(Brushes.Orchid, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA2");
				AddPlot(new Stroke(Brushes.DodgerBlue, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA3");
				AddPlot(new Stroke(Brushes.Red, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotEMA4");

				// VWAP std-dev band plots (Session VWAP)
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 1), PlotStyle.Line, "PlotVWAPUP1");
				AddPlot(new Stroke(Brushes.Gray, DashStyleHelper.Solid, 1), PlotStyle.Line, "PlotVWAPDN1");
				AddPlot(new Stroke(Brushes.DimGray, DashStyleHelper.Solid, 1), PlotStyle.Line, "PlotVWAPUP2");
				AddPlot(new Stroke(Brushes.DimGray, DashStyleHelper.Solid, 1), PlotStyle.Line, "PlotVWAPDN2");

				// Rolling 24h VWAP
				AddPlot(new Stroke(Brushes.HotPink, DashStyleHelper.Solid, 2), PlotStyle.Line, "PlotVWAP24H");

				// ── EMA defaults ──
				ShowEma1 = true; Ema1Period = 9; Ema1Brush = Brushes.Turquoise;
				ShowEma2 = true; Ema2Period = 14; Ema2Brush = Brushes.Orchid;
				ShowEma3 = true; Ema3Period = 50; Ema3Brush = Brushes.DodgerBlue;
				ShowEma4 = true; Ema4Period = 200; Ema4Brush = Brushes.Red;

				// ── VWAP band defaults ──
				ShowVwapBands = true; Vwap1SDBrush = Brushes.Gray; Vwap2SDBrush = Brushes.DimGray;

				// ── Previous Week defaults ──
				ShowPrevWeekHigh = true; ShowPrevWeekLow = true;
				PrevWeekHighBrush = Brushes.Violet; PrevWeekLowBrush = Brushes.Violet;

				// ── Midnight Open defaults ──
				ShowMidnightOpen = true; MidnightOpenBrush = Brushes.White;
				MidnightLineWidth = 2; MidnightDashStyle = DashStyleHelper.Dash;

				// ── Session Open vertical line defaults ──
				ShowDayOpenLine = true; DayOpenLineBrush = Brushes.White;
				ShowAsiaOpenLine = true; AsiaOpenLineBrush = Brushes.DodgerBlue;
				ShowLondonOpenLine = true; LondonOpenLineBrush = Brushes.Orange;
				ShowNYOpenLine = true; NYOpenLineBrush = Brushes.Yellow;
				VerticalLineWidth = 1; VerticalLineDashStyle = DashStyleHelper.Dot;

				// ── Initial Balance defaults ──
				ShowIB = true; IBMinutes = 60; IBHighBrush = Brushes.Orange; IBLowBrush = Brushes.Orange;
				IBLineWidth = 2; IBDashStyle = DashStyleHelper.Solid;
				ShowIBExtensions = true; IBExtBrush = Brushes.OrangeRed;

				// ── VWAP defaults ──
				ShowSessionVWAP = true;
				ShowAsiaVWAP = true;
				ShowEuropeVWAP = true;
				ShowNYVWAP = true;

				// VWAP session times (ET)
				AsiaStartTime = 2000;
				AsiaEndTime = 500;
				EuropeStartTime = 300;
				EuropeEndTime = 1200;
				NYStartTime = 930;
				NYEndTime = 1700;
				ShowDayHighVWAP = true;
				ShowDayLowVWAP = true;
				ShowWeeklyVWAP = true;
				ShowVwap24h = true;

				// ── PrevDayLevels defaults ──
				ValueAreaPercent = 70;
				TicksPerBucket = 2;
				VAMethod = ValueAreaMethod.VolumeProfile;

				PdhBrush = Brushes.DodgerBlue;
				PdlBrush = Brushes.DodgerBlue;
				PvahBrush = Brushes.Orange;
				PvalBrush = Brushes.Orange;
				PdLineWidth = 2;
				PvaLineWidth = 1;
				PdDashStyle = DashStyleHelper.Solid;
				PvaDashStyle = DashStyleHelper.Dash;

				TdhBrush = Brushes.LimeGreen;
				TdlBrush = Brushes.LimeGreen;
				TpvahBrush = Brushes.Yellow;
				TpvalBrush = Brushes.Yellow;
				TdLineWidth = 2;
				TpvaLineWidth = 1;
				TdDashStyle = DashStyleHelper.Solid;
				TpvaDashStyle = DashStyleHelper.Dash;

				ShowLineLabels = true;
				LabelFontSize = 9;

				ShowHistoricLines = true;
				ShowPDH = true;
				ShowPDL = true;
				ShowPVAH = true;
				ShowPVAL = true;
				ShowTDH = true;
				ShowTDL = true;
				ShowTPVAH = true;
				ShowTPVAL = true;
				ShowTodayVABackground = true;
				TodayVABackgroundColor = System.Windows.Media.Color.FromArgb(30, 255, 255, 0); // very light yellow
				ShowPOC = true;
				ShowPrevPOC = true;

				PocBrush = Brushes.OrangeRed;
				PocLineWidth = 2;
				PocDashStyle = DashStyleHelper.Dot;

				// ── FixedLines defaults ──
				FixedLinesColor = Brushes.Lime;
				FixedLinesStep = 100;
				FixedLinesRange = 20;
				ShowFixedLines = true;

				// Panel label colors — initialized here so they're valid before OnRenderTargetChanged fires
				_panelColorPDVwapNY = System.Windows.Media.Color.FromRgb(0, 200, 255);
				_panelColorPDVwapSession = System.Windows.Media.Color.FromRgb(200, 200, 200);

				// PD VWAP defaults
				ShowPDVwapNY = true;
				ShowPDVwapSession = true;
				PDVwapNYColor = System.Windows.Media.Color.FromRgb(0, 200, 255);   // cyan-ish
				PDVwapSessionColor = System.Windows.Media.Color.FromRgb(200, 200, 200); // light grey

				// ORB defaults
				ShowORB = true; ORBMinutes = 15;
				OrbHighBrush = Brushes.Cyan; OrbLowBrush = Brushes.Cyan;
				OrbLineWidth = 2; OrbDashStyle = DashStyleHelper.Dash;
				// Asia ORB
				ShowAsiaORB = true; AsiaORBMinutes = 60;
				AsiaOrbHighBrush = Brushes.Cyan; AsiaOrbLowBrush = Brushes.Cyan;
				// Europe ORB
				ShowEuropeORB = true; EuropeORBMinutes = 60;
				EuropeOrbHighBrush = Brushes.Gold; EuropeOrbLowBrush = Brushes.Gold;
				// Asia/Europe session high/low
				ShowAsiaHighLow = true; AsiaHighBrush = Brushes.Cyan; AsiaLowBrush = Brushes.Cyan;
				ShowEuropeHighLow = true; EuropeHighBrush = Brushes.Gold; EuropeLowBrush = Brushes.Gold;

				// Globex open
				ShowGlobexOpen = true; GlobexOpenBrush = Brushes.Yellow;
				GlobexLineWidth = 2; GlobexDashStyle = DashStyleHelper.Dash;
			}
			else if (State == State.Configure)
			{
				// Hidden 1-minute series: chart bars can be Renko/Range style whose
				// synthetic Open equals the prior brick's close, so Open[0] of the
				// chart series is useless for session opens. The minute series
				// provides the true first traded price of the session.
				AddDataSeries(BarsPeriodType.Minute, 1);
			}
			else if (State == State.DataLoaded)
			{
				// PrevDayLevels init
				_bucketVolume = new Dictionary<long, long>();
				_bucketTPO = new Dictionary<long, long>();
				_bucketUniform = new Dictionary<long, long>();
				_bucketLinear = new Dictionary<long, long>();
				_bucketClose = new Dictionary<long, long>();
				_vaSortBuffer = new List<long>(512);
				_vwap24hQueue = new Queue<(DateTime, double, double)>(2048);
				_cumVol24h = 0; _cumTypVol24h = 0;
				_finishedSessions = new List<FinishedSession>();
				_dayOpenTimes = new List<DateTime>();
				_asiaOpenTimes = new List<DateTime>();
				_londonOpenTimes = new List<DateTime>();
				_nyOpenTimes = new List<DateTime>();
				_hasPreviousDay = false;
				_inSession = false;
				_currentSessionStart = DateTime.MinValue;
				_lastSessionRollNYDate = DateTime.MinValue;
				_marketOpenStart = DateTime.MinValue;
				_todayPoc = 0;
				_weekHigh = double.MinValue;
				_weekLow = double.MaxValue;
				_hasPrevWeek = false;
				_lastMidnightNYDate = DateTime.MinValue;
				_lastGlobexOpenNYDate = DateTime.MinValue;
				_lastMidnightMinuteNYDate = DateTime.MinValue;
				_ib.Reset();

				// Cache TimeZoneInfo to avoid expensive FindSystemTimeZoneById on every bar
				try
				{
					_cachedNYTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
				}
				catch
				{
					_cachedNYTimeZone = TimeZoneInfo.Utc;
				}

				// Cache EMA indicator instances — EMA(period) does a cache lookup on every call
				_ema1 = EMA(Ema1Period);
				_ema2 = EMA(Ema2Period);
				_ema3 = EMA(Ema3Period);
				_ema4 = EMA(Ema4Period);

				// Cache session-window TimeSpans — HHMM properties are fixed after configuration
				_tsAsiaStart = HHMMToTimeSpan(AsiaStartTime);
				_tsAsiaEnd = HHMMToTimeSpan(AsiaEndTime);
				_tsEuropeStart = HHMMToTimeSpan(EuropeStartTime);
				_tsEuropeEnd = HHMMToTimeSpan(EuropeEndTime);
				_tsNYStart = HHMMToTimeSpan(NYStartTime);
				_tsNYEnd = HHMMToTimeSpan(NYEndTime);

				_textFormat = new SharpDX.DirectWrite.TextFormat(
					NinjaTrader.Core.Globals.DirectWriteFactory,
					"Arial",
					SharpDX.DirectWrite.FontWeight.Bold,
					SharpDX.DirectWrite.FontStyle.Normal,
					SharpDX.DirectWrite.FontStretch.Normal,
					(float)LabelFontSize);

				ResetPreviousSession();
				ResetTodaySession();

				// FixedLines: draw horizontal lines at every 100-point interval
				if (ShowFixedLines)
					DrawFixedLines();

				// WPF menu
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

		// ══════════════════════════════════════════════════════════════════
		//  OnBarUpdate
		// ══════════════════════════════════════════════════════════════════
		protected override void OnBarUpdate()
		{
			if (CurrentBar < 1) return;

			// ── 1-minute series: capture the TRUE traded session opens ─────
			if (BarsInProgress == 1)
			{
				DateTime nyMin = TimeZoneInfo.ConvertTime(Time[0], Core.Globals.GeneralOptions.TimeZoneInfo, _cachedNYTimeZone);

				// Globex open: first minute bar ending after 18:00 ET covers
				// 18:00→18:01, so its Open is the first trade of the new session.
				if (nyMin.TimeOfDay > new TimeSpan(18, 0, 0) && nyMin.Date != _lastGlobexOpenNYDate)
				{
					_lastGlobexOpenNYDate = nyMin.Date;
					_globexOpenPrice = Open[0];
				}

				// Midnight open: first minute bar of the new NY date ending after
				// 00:00 (a bar stamped exactly 00:00 covers 23:59→00:00 = yesterday).
				if (nyMin.Date != _lastMidnightMinuteNYDate && nyMin.TimeOfDay > TimeSpan.Zero)
				{
					_lastMidnightMinuteNYDate = nyMin.Date;
					_midnightOpenPrice = Open[0];
				}
				return;
			}

			if (BarsInProgress != 0) return;

			// ── Main instrument only from here ─────────────────────────────

			// ── VWAP ──────────────────────────────────────────────────────
			DateTime barTime = Time[0];
			TimeZoneInfo chartTZ = Core.Globals.GeneralOptions.TimeZoneInfo;
			var nyTime = TimeZoneInfo.ConvertTime(barTime, chartTZ, _cachedNYTimeZone);

			_currentPrice = (High[0] + Low[0] + Close[0]) / 3.0;
			double vol0 = Volume[0]; // cache once — avoids repeated indicator/series lookups below

			// ── Rolling 24h VWAP — sliding window, no session anchor ────────
			if (ShowVwap24h)
			{
				double typVol0 = vol0 * _currentPrice;
				_vwap24hQueue.Enqueue((Time[0], vol0, typVol0));
				_cumVol24h += vol0;
				_cumTypVol24h += typVol0;

				DateTime cutoff = Time[0].AddHours(-24);
				while (_vwap24hQueue.Count > 0 && _vwap24hQueue.Peek().Time < cutoff)
				{
					var old = _vwap24hQueue.Dequeue();
					_cumVol24h -= old.Vol;
					_cumTypVol24h -= old.TypVol;
				}

				PlotVWAP24H[0] = _cumVol24h > 0 ? _cumTypVol24h / _cumVol24h : double.NaN;
				if (!double.IsNaN(PlotVWAP24H[0])) _lastVwap24h = PlotVWAP24H[0];
			}
			else
			{
				PlotVWAP24H[0] = double.NaN;
			}

			// ── EMA levels ────────────────────────────────────────────────
			PlotEMA1[0] = ShowEma1 ? _ema1[0] : double.NaN;
			PlotEMA2[0] = ShowEma2 ? _ema2[0] : double.NaN;
			PlotEMA3[0] = ShowEma3 ? _ema3[0] : double.NaN;
			PlotEMA4[0] = ShowEma4 ? _ema4[0] : double.NaN;

			if (Bars.IsFirstBarOfSession)
			{
				_cumVolumeSession = vol0;
				_cumTypicalVolumeSession = vol0 * _currentPrice;
				_cumSqSession = vol0 * _currentPrice * _currentPrice;
				_dayHigh = High[0];
				_dayLow = Low[0];
				_dayHighStarted = true;
				_dayLowStarted = true;
				_cumVolumeDayHigh = 0; _cumTypicalVolumeDayHigh = 0;
				_cumVolumeDayLow = 0; _cumTypicalVolumeDayLow = 0;
			}
			else
			{
				_cumVolumeSession += vol0;
				_cumTypicalVolumeSession += vol0 * _currentPrice;
				_cumSqSession += vol0 * _currentPrice * _currentPrice;

				if (High[0] >= _dayHigh)
				{
					_dayHigh = High[0];
					_dayHighStarted = true;
					_cumVolumeDayHigh = vol0;
					_cumTypicalVolumeDayHigh = vol0 * _currentPrice;
				}
				else if (_dayHighStarted)
				{
					_cumVolumeDayHigh += vol0;
					_cumTypicalVolumeDayHigh += vol0 * _currentPrice;
					PlotVWAPDAYHIGH[0] = _cumVolumeDayHigh > 0
						? _cumTypicalVolumeDayHigh / _cumVolumeDayHigh : double.NaN;
					if (!double.IsNaN(PlotVWAPDAYHIGH[0])) _lastVwapDayHigh = PlotVWAPDAYHIGH[0];
				}

				if (Low[0] <= _dayLow)
				{
					_dayLow = Low[0];
					_dayLowStarted = true;
					_cumVolumeDayLow = vol0;
					_cumTypicalVolumeDayLow = vol0 * _currentPrice;
				}
				else if (_dayHighStarted)
				{
					_cumVolumeDayLow += vol0;
					_cumTypicalVolumeDayLow += vol0 * _currentPrice;
					PlotVWAPDAYLOW[0] = _cumVolumeDayLow > 0
						? _cumTypicalVolumeDayLow / _cumVolumeDayLow : double.NaN;
					if (!double.IsNaN(PlotVWAPDAYLOW[0])) _lastVwapDayLow = PlotVWAPDAYLOW[0];
				}
			}

			PlotVWAPSESSION[0] = _cumTypicalVolumeSession / _cumVolumeSession;
			_lastVwapSession = PlotVWAPSESSION[0];

			// ── VWAP std-dev bands ────────────────────────────────────────
			if (ShowVwapBands && _cumVolumeSession > 0)
			{
				double variance = (_cumSqSession / _cumVolumeSession) - (PlotVWAPSESSION[0] * PlotVWAPSESSION[0]);
				double stdDev = variance > 0 ? Math.Sqrt(variance) : 0;
				PlotVWAPUP1[0] = PlotVWAPSESSION[0] + stdDev;
				PlotVWAPDN1[0] = PlotVWAPSESSION[0] - stdDev;
				PlotVWAPUP2[0] = PlotVWAPSESSION[0] + stdDev * 2;
				PlotVWAPDN2[0] = PlotVWAPSESSION[0] - stdDev * 2;
			}
			else
			{
				PlotVWAPUP1[0] = PlotVWAPDN1[0] = PlotVWAPUP2[0] = PlotVWAPDN2[0] = double.NaN;
			}

			TimeSpan nyTOD = nyTime.TimeOfDay;

			// Asia VWAP – supports midnight-crossing sessions (start > end)
			bool inAsia = _tsAsiaStart > _tsAsiaEnd
				? (nyTOD >= _tsAsiaStart || nyTOD < _tsAsiaEnd)
				: (nyTOD >= _tsAsiaStart && nyTOD < _tsAsiaEnd);
			if (inAsia)
			{
				if (_cumVolumeAsia == 0)
				{
					_cumVolumeAsia = vol0;
					_cumTypicalVolumeAsia = vol0 * _currentPrice;
				}
				else
				{
					_cumVolumeAsia += vol0;
					_cumTypicalVolumeAsia += vol0 * _currentPrice;
				}
				PlotVWAPASIA[0] = _cumTypicalVolumeAsia / _cumVolumeAsia;
				_lastVwapAsia = PlotVWAPASIA[0];
				// Track Asia session high/low (always, regardless of ShowAsiaHighLow — render gate handles visibility)
				if (!_asiaSessionStarted) { _asiaSessionStarted = true; _asiaSessionStart = Time[0]; _asiaSessionEnd = DateTime.MinValue; _asiaSessionHigh = High[0]; _asiaSessionLow = Low[0]; _asiaOpenTimes.Add(Time[0]); }
				else { if (High[0] > _asiaSessionHigh) _asiaSessionHigh = High[0]; if (Low[0] < _asiaSessionLow) _asiaSessionLow = Low[0]; }
			}
			else { _cumVolumeAsia = 0; _cumTypicalVolumeAsia = 0; if (_asiaSessionStarted && _asiaSessionEnd == DateTime.MinValue) _asiaSessionEnd = Time[0]; }

			// Europe VWAP
			bool inEurope = _tsEuropeStart > _tsEuropeEnd
				? (nyTOD >= _tsEuropeStart || nyTOD < _tsEuropeEnd)
				: (nyTOD >= _tsEuropeStart && nyTOD < _tsEuropeEnd);
			if (inEurope)
			{
				// Reset on first bar of Europe session
				if (_cumVolumeEurope == 0)
				{
					_cumVolumeEurope = vol0;
					_cumTypicalVolumeEurope = vol0 * _currentPrice;
				}
				else
				{
					_cumVolumeEurope += vol0;
					_cumTypicalVolumeEurope += vol0 * _currentPrice;
				}
				PlotVWAPEUROPE[0] = _cumTypicalVolumeEurope / _cumVolumeEurope;
				_lastVwapEurope = PlotVWAPEUROPE[0];
				// Track Europe session high/low (always, regardless of ShowEuropeHighLow — render gate handles visibility)
				if (!_europeSessionStarted) { _europeSessionStarted = true; _europeSessionStart = Time[0]; _europeSessionEnd = DateTime.MinValue; _europeSessionHigh = High[0]; _europeSessionLow = Low[0]; _londonOpenTimes.Add(Time[0]); }
				else { if (High[0] > _europeSessionHigh) _europeSessionHigh = High[0]; if (Low[0] < _europeSessionLow) _europeSessionLow = Low[0]; }
			}
			else { _cumVolumeEurope = 0; _cumTypicalVolumeEurope = 0; if (_europeSessionStarted && _europeSessionEnd == DateTime.MinValue) _europeSessionEnd = Time[0]; }

			// NY VWAP
			bool inNY = _tsNYStart > _tsNYEnd
				? (nyTOD >= _tsNYStart || nyTOD < _tsNYEnd)
				: (nyTOD >= _tsNYStart && nyTOD < _tsNYEnd);
			if (inNY)
			{
				if (_cumVolumeNY == 0)
				{
					_cumVolumeNY = vol0;
					_cumTypicalVolumeNY = vol0 * _currentPrice;
				}
				else
				{
					_cumVolumeNY += vol0;
					_cumTypicalVolumeNY += vol0 * _currentPrice;
				}
				PlotVWAPNY[0] = _cumTypicalVolumeNY / _cumVolumeNY;
				_lastVwapNY = PlotVWAPNY[0];
				if (!_nySessionStarted) { _nySessionStarted = true; _nySessionStart = Time[0]; _nySessionEnd = DateTime.MinValue; _nyOpenTimes.Add(Time[0]); }
			}
			else { _cumVolumeNY = 0; _cumTypicalVolumeNY = 0; if (_nySessionStarted && _nySessionEnd == DateTime.MinValue) _nySessionEnd = Time[0]; }

			// ── Weekly VWAP ───────────────────────────────────────────────
			// Anchored at the most recent Sunday 18:00 ET (CME futures week start).
			// Accumulates across ALL session bars (full 24/5 inclusion) until the
			// next Sunday 18:00 ET boundary, which triggers a reset.
			DateTime weekAnchor = GetWeeklyAnchor(nyTime);
			if (weekAnchor != _currentWeekStart)
			{
				// ── Previous Week High/Low ──────────────────────────────────
				if (_weekHigh > double.MinValue)
				{
					_prevWeekHigh = _weekHigh;
					_prevWeekLow = _weekLow;
					_hasPrevWeek = true;
				}
				_weekStartBar = Time[0];
				_weekHigh = High[0];
				_weekLow = Low[0];

				_currentWeekStart = weekAnchor;
				_cumVolumeWeek = vol0;
				_cumTypicalVolumeWeek = vol0 * _currentPrice;
			}
			else
			{
				if (High[0] > _weekHigh) _weekHigh = High[0];
				if (Low[0] < _weekLow) _weekLow = Low[0];

				_cumVolumeWeek += vol0;
				_cumTypicalVolumeWeek += vol0 * _currentPrice;
			}
			if (_cumVolumeWeek > 0)
			{
				PlotVWAPWEEK[0] = _cumTypicalVolumeWeek / _cumVolumeWeek;
				_lastVwapWeek = PlotVWAPWEEK[0];
			}

			// Cache session-active flags for panel
			_isAsiaActive = inAsia;
			_isEuropeActive = inEurope;
			_isNYActive = inNY;

			// ── ORB accumulation ──────────────────────────────────────────────
			AccumulateOrb(ref _orbNY, _tsNYStart, ORBMinutes, ShowORB, nyTOD);
			AccumulateOrb(ref _orbAsia, _tsAsiaStart, AsiaORBMinutes, ShowAsiaORB, nyTOD);
			AccumulateOrb(ref _orbEurope, _tsEuropeStart, EuropeORBMinutes, ShowEuropeORB, nyTOD);
			AccumulateOrb(ref _ib, _tsNYStart, IBMinutes, ShowIB, nyTOD);

			// ── PrevDayLevels ─────────────────────────────────────────────
			// NQ session boundary: rolls at 18:00 ET each day
			DateTime nyTimePD = nyTime; // same Time[0] conversion as computed above
			DateTime nyDatePD = nyTimePD.Date;
			// Bars are END-stamped: a bar stamped exactly 18:00 covers 17:59→18:00 and
			// still belongs to the OLD session. Use strictly-greater so the roll fires
			// on the first bar of the NEW session (18:01 on a 1-min chart), making
			// Open[0] the true Globex open instead of the prior session's last price.
			bool at1800 = nyTimePD.TimeOfDay > new TimeSpan(18, 0, 0);

			// ── Midnight Open (00:00 ET) ────────────────────────────────────
			if (nyDatePD != _lastMidnightNYDate)
			{
				_lastMidnightNYDate = nyDatePD;
				_midnightOpenStart = Time[0];
				// Fallback only — the 1-min series capture is authoritative
				if (_lastMidnightMinuteNYDate != nyDatePD)
					_midnightOpenPrice = Open[0];
			}

			bool sessionRoll = at1800 && nyDatePD != _lastSessionRollNYDate;

			if (sessionRoll)
			{
				// Close out the previous running session first
				if (_inSession && _bucketVolume.Count > 0)
				{
					double vah, val;
					CalculateValueArea(_bucketVolume, _bucketUniform, _bucketLinear, _bucketClose, _bucketTPO, _pocPrice, out vah, out val);

					var finished = new FinishedSession
					{
						SessionStart = _currentSessionStart,
						SessionEnd = Time[1].AddMinutes(-1), // end just before the new 18:00 bar
						PDH = _sessionHigh,
						PDL = _sessionLow,
						PVAH = vah,
						PVAL = val,
						POC = GetPocForMethod(_bucketVolume, _bucketUniform, _bucketLinear, _bucketClose, _bucketTPO, _pocPrice),
						VwapNY = _lastVwapNY,
						VwapSession = _lastVwapSession
					};
					_finishedSessions.Add(finished);
					_prevSession = finished;
					_hasPreviousDay = true;
				}

				// Backfill NextSessionStart on the session that just ended
				// (the one we just added, index Count-1; and also the one before it if it exists)
				if (_finishedSessions.Count >= 1)
				{
					// The session we just closed gets the current bar as its next-session start
					var prev = _finishedSessions[_finishedSessions.Count - 1];
					prev.NextSessionStart = Time[0];
					prev.NextSessionEnd = DateTime.MinValue; // still live – filled when THAT session closes
					_finishedSessions[_finishedSessions.Count - 1] = prev;
				}
				if (_finishedSessions.Count >= 2)
				{
					// The session before that has its next-session-end = the bar just before NOW
					var older = _finishedSessions[_finishedSessions.Count - 2];
					if (older.NextSessionEnd == DateTime.MinValue)
					{
						older.NextSessionEnd = Time[1].AddMinutes(-1);
						_finishedSessions[_finishedSessions.Count - 2] = older;
					}
				}

				// Start new session
				_lastSessionRollNYDate = nyDatePD;
				_inSession = true;
				_currentSessionStart = Time[0];
				_marketOpenStart = Time[0];
				_dayOpenTimes.Add(Time[0]);
				// Fallback only — the 1-min series capture is authoritative
				if (_lastGlobexOpenNYDate != nyDatePD)
					_globexOpenPrice = Open[0];
				ResetPreviousSession();
				ResetTodaySession();
				// Reset all ORBs for new session
				_orbNY.Reset(); _orbAsia.Reset(); _orbEurope.Reset(); _ib.Reset();
				_asiaSessionStarted = false; _asiaSessionHigh = double.MinValue; _asiaSessionLow = double.MaxValue; _asiaSessionStart = DateTime.MinValue; _asiaSessionEnd = DateTime.MinValue;
				_europeSessionStarted = false; _europeSessionHigh = double.MinValue; _europeSessionLow = double.MaxValue; _europeSessionStart = DateTime.MinValue; _europeSessionEnd = DateTime.MinValue;
				_nySessionStarted = false; _nySessionEnd = DateTime.MinValue;
			}

			// First bar ever – bootstrap session tracking
			if (!_inSession && at1800)
			{
				_inSession = true;
				_currentSessionStart = Time[0];
				_marketOpenStart = Time[0];
				_dayOpenTimes.Add(Time[0]);
				if (_lastGlobexOpenNYDate != nyDatePD)
					_globexOpenPrice = Open[0];
				_lastSessionRollNYDate = nyDatePD;
			}

			// Accumulate into running session buckets.
			// The "today" bucket set was an exact duplicate of the running session set
			// (both reset at the 18:00 roll, both fed every bar) — one set serves both
			// the live today-VA and the finished-session snapshot. Only the bucket
			// family the configured VAMethod actually reads is accumulated; VolumeProfile
			// buckets are always kept since they drive the POC fallback.
			if (_inSession)
			{
				if (High[0] > _sessionHigh) _sessionHigh = High[0];
				if (Low[0] < _sessionLow) _sessionLow = Low[0];
				AccumulateBucket(_bucketVolume, ref _pocPrice, ref _pocVolume);
				switch (VAMethod)
				{
					case ValueAreaMethod.TPO: AccumulateTPO(_bucketTPO); break;
					case ValueAreaMethod.Uniform: AccumulateUniform(_bucketUniform); break;
					case ValueAreaMethod.LinearWeighted: AccumulateLinear(_bucketLinear); break;
					case ValueAreaMethod.CloseWeighted: AccumulateClose(_bucketClose); break;
				}

				if (_bucketVolume.Count > 0)
				{
					CalculateValueArea(_bucketVolume, _bucketUniform, _bucketLinear, _bucketClose, _bucketTPO, _pocPrice, out _tpvah, out _tpval);
					_todayPoc = GetPocForMethod(_bucketVolume, _bucketUniform, _bucketLinear, _bucketClose, _bucketTPO, _pocPrice);
				}
			}



			// Redraw fixed lines if price drifts outside the covered range
			if (ShowFixedLines && _lastFixedStep > 0 &&
				(Close[0] < _lastFixedMin + _lastFixedStep * 3 ||
				 Close[0] > _lastFixedMax - _lastFixedStep * 3))
			{
				DrawFixedLines();
			}
		}

		// ══════════════════════════════════════════════════════════════════
		//  OnRenderTargetChanged  (PrevDayLevels SharpDX brushes)
		// ══════════════════════════════════════════════════════════════════
		public override void OnRenderTargetChanged()
		{
			DisposeDxBrushes();
			if (RenderTarget != null)
			{
				_dxPdhBrush = PdhBrush.ToDxBrush(RenderTarget);
				_dxPdlBrush = PdlBrush.ToDxBrush(RenderTarget);
				_dxPvahBrush = PvahBrush.ToDxBrush(RenderTarget);
				_dxPvalBrush = PvalBrush.ToDxBrush(RenderTarget);
				_dxTdhBrush = TdhBrush.ToDxBrush(RenderTarget);
				_dxTdlBrush = TdlBrush.ToDxBrush(RenderTarget);
				_dxTpvahBrush = TpvahBrush.ToDxBrush(RenderTarget);
				_dxTpvalBrush = TpvalBrush.ToDxBrush(RenderTarget);
				_dxPocBrush = PocBrush.ToDxBrush(RenderTarget);
				_dxOrbHighBrush = OrbHighBrush.ToDxBrush(RenderTarget);
				_dxOrbLowBrush = OrbLowBrush.ToDxBrush(RenderTarget);
				_dxOrbAsiaHighBrush = AsiaOrbHighBrush.ToDxBrush(RenderTarget);
				_dxOrbAsiaLowBrush = AsiaOrbLowBrush.ToDxBrush(RenderTarget);
				_dxOrbEuropeHighBrush = EuropeOrbHighBrush.ToDxBrush(RenderTarget);
				_dxOrbEuropeLowBrush = EuropeOrbLowBrush.ToDxBrush(RenderTarget);
				_dxAsiaHighLineBrush = AsiaHighBrush.ToDxBrush(RenderTarget);
				_dxAsiaLowLineBrush = AsiaLowBrush.ToDxBrush(RenderTarget);
				_dxEuropeHighLineBrush = EuropeHighBrush.ToDxBrush(RenderTarget);
				_dxEuropeLowLineBrush = EuropeLowBrush.ToDxBrush(RenderTarget);
				_dxGlobexOpenBrush = GlobexOpenBrush.ToDxBrush(RenderTarget);
				_dxMidnightOpenBrush = MidnightOpenBrush.ToDxBrush(RenderTarget);
				_dxDayOpenLineBrush = DayOpenLineBrush.ToDxBrush(RenderTarget);
				_dxAsiaOpenLineBrush = AsiaOpenLineBrush.ToDxBrush(RenderTarget);
				_dxLondonOpenLineBrush = LondonOpenLineBrush.ToDxBrush(RenderTarget);
				_dxNYOpenLineBrush = NYOpenLineBrush.ToDxBrush(RenderTarget);
				_dxPwhBrush = PrevWeekHighBrush.ToDxBrush(RenderTarget);
				_dxPwlBrush = PrevWeekLowBrush.ToDxBrush(RenderTarget);
				_dxIBHighBrush = IBHighBrush.ToDxBrush(RenderTarget);
				_dxIBLowBrush = IBLowBrush.ToDxBrush(RenderTarget);
				_dxIBExtBrush = IBExtBrush.ToDxBrush(RenderTarget);
				// Cache WPF colors for panel rows
				_panelColorPDVwapNY = PDVwapNYColor;
				_panelColorPDVwapSession = PDVwapSessionColor;
				var nyC = PDVwapNYColor;
				var sesC = PDVwapSessionColor;
				_dxPdVwapNYBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(nyC.R / 255f, nyC.G / 255f, nyC.B / 255f, 1f));
				_dxPdVwapSessionBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(sesC.R / 255f, sesC.G / 255f, sesC.B / 255f, 1f));
				// VA background brush — use TodayVABackgroundColor directly with alpha
				var vaCol = TodayVABackgroundColor;
				_dxVABackgroundBrush = new SharpDX.Direct2D1.SolidColorBrush(
					RenderTarget,
					new SharpDX.Color4(vaCol.R / 255f, vaCol.G / 255f, vaCol.B / 255f, vaCol.A / 255f));
			}
		}

		private void DisposeDxBrushes()
		{
			if (_dxPdhBrush != null) { _dxPdhBrush.Dispose(); _dxPdhBrush = null; }
			if (_dxPdlBrush != null) { _dxPdlBrush.Dispose(); _dxPdlBrush = null; }
			if (_dxPvahBrush != null) { _dxPvahBrush.Dispose(); _dxPvahBrush = null; }
			if (_dxPvalBrush != null) { _dxPvalBrush.Dispose(); _dxPvalBrush = null; }
			if (_dxTdhBrush != null) { _dxTdhBrush.Dispose(); _dxTdhBrush = null; }
			if (_dxTdlBrush != null) { _dxTdlBrush.Dispose(); _dxTdlBrush = null; }
			if (_dxTpvahBrush != null) { _dxTpvahBrush.Dispose(); _dxTpvahBrush = null; }
			if (_dxTpvalBrush != null) { _dxTpvalBrush.Dispose(); _dxTpvalBrush = null; }
			if (_dxPocBrush != null) { _dxPocBrush.Dispose(); _dxPocBrush = null; }
			if (_dxOrbHighBrush != null) { _dxOrbHighBrush.Dispose(); _dxOrbHighBrush = null; }
			if (_dxOrbLowBrush != null) { _dxOrbLowBrush.Dispose(); _dxOrbLowBrush = null; }
			if (_dxOrbAsiaHighBrush != null) { _dxOrbAsiaHighBrush.Dispose(); _dxOrbAsiaHighBrush = null; }
			if (_dxOrbAsiaLowBrush != null) { _dxOrbAsiaLowBrush.Dispose(); _dxOrbAsiaLowBrush = null; }
			if (_dxOrbEuropeHighBrush != null) { _dxOrbEuropeHighBrush.Dispose(); _dxOrbEuropeHighBrush = null; }
			if (_dxOrbEuropeLowBrush != null) { _dxOrbEuropeLowBrush.Dispose(); _dxOrbEuropeLowBrush = null; }
			if (_dxAsiaHighLineBrush != null) { _dxAsiaHighLineBrush.Dispose(); _dxAsiaHighLineBrush = null; }
			if (_dxAsiaLowLineBrush != null) { _dxAsiaLowLineBrush.Dispose(); _dxAsiaLowLineBrush = null; }
			if (_dxEuropeHighLineBrush != null) { _dxEuropeHighLineBrush.Dispose(); _dxEuropeHighLineBrush = null; }
			if (_dxEuropeLowLineBrush != null) { _dxEuropeLowLineBrush.Dispose(); _dxEuropeLowLineBrush = null; }
			if (_dxGlobexOpenBrush != null) { _dxGlobexOpenBrush.Dispose(); _dxGlobexOpenBrush = null; }
			if (_dxMidnightOpenBrush != null) { _dxMidnightOpenBrush.Dispose(); _dxMidnightOpenBrush = null; }
			if (_dxDayOpenLineBrush != null) { _dxDayOpenLineBrush.Dispose(); _dxDayOpenLineBrush = null; }
			if (_dxAsiaOpenLineBrush != null) { _dxAsiaOpenLineBrush.Dispose(); _dxAsiaOpenLineBrush = null; }
			if (_dxLondonOpenLineBrush != null) { _dxLondonOpenLineBrush.Dispose(); _dxLondonOpenLineBrush = null; }
			if (_dxNYOpenLineBrush != null) { _dxNYOpenLineBrush.Dispose(); _dxNYOpenLineBrush = null; }
			if (_dxPwhBrush != null) { _dxPwhBrush.Dispose(); _dxPwhBrush = null; }
			if (_dxPwlBrush != null) { _dxPwlBrush.Dispose(); _dxPwlBrush = null; }
			if (_dxIBHighBrush != null) { _dxIBHighBrush.Dispose(); _dxIBHighBrush = null; }
			if (_dxIBLowBrush != null) { _dxIBLowBrush.Dispose(); _dxIBLowBrush = null; }
			if (_dxIBExtBrush != null) { _dxIBExtBrush.Dispose(); _dxIBExtBrush = null; }
			if (_dxPdVwapNYBrush != null) { _dxPdVwapNYBrush.Dispose(); _dxPdVwapNYBrush = null; }
			if (_dxPdVwapSessionBrush != null) { _dxPdVwapSessionBrush.Dispose(); _dxPdVwapSessionBrush = null; }
			if (_dxVABackgroundBrush != null) { _dxVABackgroundBrush.Dispose(); _dxVABackgroundBrush = null; }
		}

		// ══════════════════════════════════════════════════════════════════
		//  OnRender – all prev-day & today lines + optional labels
		//  All drawn via SharpDX for pixel-perfect bounded segments
		// ══════════════════════════════════════════════════════════════════
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (RenderTarget == null || _dxPdhBrush == null) return;

			float canvasLeft = chartControl.CanvasLeft;
			float canvasRight = chartControl.CanvasRight;
			float labelH = (_textFormat != null) ? _textFormat.FontSize + 4f : 14f;

			// ── Session-open vertical lines — drawn first, sit behind everything ──
			{
				float vTop = 0f;
				float vBottom = (float)RenderTarget.Size.Height;
				if (ShowDayOpenLine) DrawSessionOpenLines(chartControl, _dayOpenTimes, _dxDayOpenLineBrush, canvasLeft, canvasRight, vTop, vBottom);
				if (ShowAsiaOpenLine) DrawSessionOpenLines(chartControl, _asiaOpenTimes, _dxAsiaOpenLineBrush, canvasLeft, canvasRight, vTop, vBottom);
				if (ShowLondonOpenLine) DrawSessionOpenLines(chartControl, _londonOpenTimes, _dxLondonOpenLineBrush, canvasLeft, canvasRight, vTop, vBottom);
				if (ShowNYOpenLine) DrawSessionOpenLines(chartControl, _nyOpenTimes, _dxNYOpenLineBrush, canvasLeft, canvasRight, vTop, vBottom);
			}

			// ── Finished (historical) sessions ──────────────────────────────
			if (_finishedSessions != null)
			{
				for (int si = 0; si < _finishedSessions.Count; si++)
				{
					var s = _finishedSessions[si];

					// ── Solid segment: own session ──────────────────────────
					float xStart = GetXForTime(chartControl, s.SessionStart);
					// Early exit: if session starts past canvas right, older sessions won't be visible either
					if (xStart > canvasRight) break;
					float xEnd = GetXForTime(chartControl, s.SessionEnd);

					bool solidVisible = xEnd >= canvasLeft && xStart <= canvasRight;
					if (solidVisible && ShowHistoricLines)
					{
						float x0 = Math.Max(xStart, canvasLeft);
						float x1 = Math.Min(xEnd, canvasRight);
						if (x1 > x0)
						{
							DrawSharpDXLine(x0, x1, chartScale.GetYByValue(s.PDH), _dxPdhBrush, PdLineWidth, DashStyleHelper.Solid);
							DrawSharpDXLine(x0, x1, chartScale.GetYByValue(s.PDL), _dxPdlBrush, PdLineWidth, DashStyleHelper.Solid);
							DrawSharpDXLine(x0, x1, chartScale.GetYByValue(s.PVAH), _dxPvahBrush, PvaLineWidth, DashStyleHelper.Solid);
							DrawSharpDXLine(x0, x1, chartScale.GetYByValue(s.PVAL), _dxPvalBrush, PvaLineWidth, DashStyleHelper.Solid);
							if (s.POC > 0) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(s.POC), _dxPocBrush, PocLineWidth, DashStyleHelper.Solid);

							// Label at right end of solid segment — always show when this day is on screen
							if (ShowLineLabels && _textFormat != null)
							{
								float labelX = Math.Min(x1, canvasRight - 5f);
								RenderLabelAt("Day High " + s.PDH.ToString("F2"), s.PDH, labelX, labelH, chartScale, _dxPdhBrush);
								RenderLabelAt("Day Low " + s.PDL.ToString("F2"), s.PDL, labelX, labelH, chartScale, _dxPdlBrush);
								RenderLabelAt("Value Area High " + s.PVAH.ToString("F2"), s.PVAH, labelX, labelH, chartScale, _dxPvahBrush);
								RenderLabelAt("Value Area Low " + s.PVAL.ToString("F2"), s.PVAL, labelX, labelH, chartScale, _dxPvalBrush);
								if (s.POC > 0) RenderLabelAt("Point of Control " + s.POC.ToString("F2"), s.POC, labelX, labelH, chartScale, _dxPocBrush);
							}
						}
					}

					// ── Dashed extension: into the next session ─────────────
					if (s.NextSessionStart != DateTime.MinValue)
					{
						// End of extension = next session's end, or canvas right if still live
						DateTime extEnd = (s.NextSessionEnd != DateTime.MinValue)
							? s.NextSessionEnd
							: (_inSession ? Time[0] : s.NextSessionStart); // fallback

						// If this is the most recent finished session and next session is live, extend to canvas right
						bool nextIsLive = si == _finishedSessions.Count - 1 && _inSession;

						float xExtStart = GetXForTime(chartControl, s.NextSessionStart);
						float xExtEnd = nextIsLive ? canvasRight : GetXForTime(chartControl, extEnd);

						bool extVisible = xExtEnd >= canvasLeft && xExtStart <= canvasRight;
						if (extVisible)
						{
							float xe0 = Math.Max(xExtStart, canvasLeft);
							float xe1 = Math.Min(xExtEnd, canvasRight);
							if (xe1 > xe0)
							{
								if (ShowPDH) DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(s.PDH), _dxPdhBrush, PdLineWidth, DashStyleHelper.Dash);
								if (ShowPDL) DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(s.PDL), _dxPdlBrush, PdLineWidth, DashStyleHelper.Dash);
								if (ShowPVAH) DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(s.PVAH), _dxPvahBrush, PvaLineWidth, DashStyleHelper.Dash);
								if (ShowPVAL) DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(s.PVAL), _dxPvalBrush, PvaLineWidth, DashStyleHelper.Dash);
								if (ShowPrevPOC && s.POC > 0) DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(s.POC), _dxPocBrush, PocLineWidth, DashStyleHelper.Dash);

								// Label at right end of extension
								if (ShowLineLabels && _textFormat != null)
								{
									float labelX = Math.Min(xe1, canvasRight - 5f);
									if (ShowPDH) RenderLabelAt("Prev Day High " + s.PDH.ToString("F2"), s.PDH, labelX, labelH, chartScale, _dxPdhBrush);
									if (ShowPDL) RenderLabelAt("Prev Day Low " + s.PDL.ToString("F2"), s.PDL, labelX, labelH, chartScale, _dxPdlBrush);
									if (ShowPVAH) RenderLabelAt("Prev Value Area High " + s.PVAH.ToString("F2"), s.PVAH, labelX, labelH, chartScale, _dxPvahBrush);
									if (ShowPVAL) RenderLabelAt("Prev Value Area Low " + s.PVAL.ToString("F2"), s.PVAL, labelX, labelH, chartScale, _dxPvalBrush);
									if (ShowPrevPOC && s.POC > 0) RenderLabelAt("Prev Point of Control " + s.POC.ToString("F2"), s.POC, labelX, labelH, chartScale, _dxPocBrush);
								}
							}
						}
					}
				}
			}

			// ── PD VWAP lines (endpoint of prev session VWAP, extended dashed into today) ──
			if (_hasPreviousDay && _finishedSessions.Count > 0)
			{
				var ps = _finishedSessions[_finishedSessions.Count - 1];
				float xExtStart = GetXForTime(chartControl, ps.NextSessionStart);
				float xExtEnd = _inSession ? canvasRight : GetXForTime(chartControl, ps.NextSessionEnd == DateTime.MinValue ? ps.SessionEnd : ps.NextSessionEnd);
				float xe0 = Math.Max(xExtStart, canvasLeft);
				float xe1 = Math.Min(xExtEnd, canvasRight);
				if (xe1 > xe0)
				{
					if (ShowPDVwapNY && ps.VwapNY > 0 && _dxPdVwapNYBrush != null)
					{
						DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(ps.VwapNY), _dxPdVwapNYBrush, 1, DashStyleHelper.Dash);
						if (ShowLineLabels && _textFormat != null)
							RenderLabelAt("PD NY VWAP " + ps.VwapNY.ToString("F2"), ps.VwapNY, xe1, labelH, chartScale, _dxPdVwapNYBrush);
					}
					if (ShowPDVwapSession && ps.VwapSession > 0 && _dxPdVwapSessionBrush != null)
					{
						DrawSharpDXLine(xe0, xe1, chartScale.GetYByValue(ps.VwapSession), _dxPdVwapSessionBrush, 1, DashStyleHelper.Dash);
						if (ShowLineLabels && _textFormat != null)
							RenderLabelAt("PD Session VWAP " + ps.VwapSession.ToString("F2"), ps.VwapSession, xe1, labelH, chartScale, _dxPdVwapSessionBrush);
					}
				}
			}

			// ── ORB lines ────────────────────────────────────────────────────
			// ── ORB lines ────────────────────────────────────────────────────
			if (ShowORB) DrawOrbLines(chartControl, chartScale, _orbNY, _dxOrbHighBrush, _dxOrbLowBrush, OrbLineWidth, OrbDashStyle, "ORB", canvasLeft, canvasRight, labelH);
			if (ShowAsiaORB) DrawOrbLines(chartControl, chartScale, _orbAsia, _dxOrbAsiaHighBrush, _dxOrbAsiaLowBrush, OrbLineWidth, OrbDashStyle, "Asia ORB", canvasLeft, canvasRight, labelH);
			if (ShowEuropeORB) DrawOrbLines(chartControl, chartScale, _orbEurope, _dxOrbEuropeHighBrush, _dxOrbEuropeLowBrush, OrbLineWidth, OrbDashStyle, "Europe ORB", canvasLeft, canvasRight, labelH);

			// ── Initial Balance (box + 1x/2x extensions) ──────────────────────
			if (ShowIB) DrawOrbLines(chartControl, chartScale, _ib, _dxIBHighBrush, _dxIBLowBrush, IBLineWidth, IBDashStyle, "IB", canvasLeft, canvasRight, labelH);
			if (ShowIB && ShowIBExtensions && _ib.Done && _ib.High > double.MinValue && _dxIBExtBrush != null)
			{
				double ibRange = _ib.High - _ib.Low;
				float xie0 = Math.Max(GetXForTime(chartControl, _ib.End), canvasLeft);
				float xie1 = canvasRight;
				if (xie1 > xie0 && ibRange > 0)
				{
					double up1 = _ib.High + ibRange, up2 = _ib.High + ibRange * 2;
					double dn1 = _ib.Low - ibRange, dn2 = _ib.Low - ibRange * 2;
					DrawSharpDXLine(xie0, xie1, chartScale.GetYByValue(up1), _dxIBExtBrush, 1, DashStyleHelper.Dash);
					DrawSharpDXLine(xie0, xie1, chartScale.GetYByValue(up2), _dxIBExtBrush, 1, DashStyleHelper.Dash);
					DrawSharpDXLine(xie0, xie1, chartScale.GetYByValue(dn1), _dxIBExtBrush, 1, DashStyleHelper.Dash);
					DrawSharpDXLine(xie0, xie1, chartScale.GetYByValue(dn2), _dxIBExtBrush, 1, DashStyleHelper.Dash);
					if (ShowLineLabels && _textFormat != null)
					{
						float lxie = Math.Min(xie1, canvasRight - 5f);
						RenderLabelAt("IB +1x " + up1.ToString("F2"), up1, lxie, labelH, chartScale, _dxIBExtBrush);
						RenderLabelAt("IB +2x " + up2.ToString("F2"), up2, lxie, labelH, chartScale, _dxIBExtBrush);
						RenderLabelAt("IB -1x " + dn1.ToString("F2"), dn1, lxie, labelH, chartScale, _dxIBExtBrush);
						RenderLabelAt("IB -2x " + dn2.ToString("F2"), dn2, lxie, labelH, chartScale, _dxIBExtBrush);
					}
				}
			}

			// ── Previous Week High/Low ─────────────────────────────────────────
			if (_hasPrevWeek && _dxPwhBrush != null && _dxPwlBrush != null)
			{
				float xw0 = Math.Max(GetXForTime(chartControl, _weekStartBar), canvasLeft);
				float xw1 = canvasRight;
				if (xw1 > xw0)
				{
					if (ShowPrevWeekHigh) DrawSharpDXLine(xw0, xw1, chartScale.GetYByValue(_prevWeekHigh), _dxPwhBrush, 2, DashStyleHelper.Solid);
					if (ShowPrevWeekLow) DrawSharpDXLine(xw0, xw1, chartScale.GetYByValue(_prevWeekLow), _dxPwlBrush, 2, DashStyleHelper.Solid);
					if (ShowLineLabels && _textFormat != null)
					{
						float lxw = Math.Min(xw1, canvasRight - 5f);
						if (ShowPrevWeekHigh) RenderLabelAt("Prev Week High " + _prevWeekHigh.ToString("F2"), _prevWeekHigh, lxw, labelH, chartScale, _dxPwhBrush);
						if (ShowPrevWeekLow) RenderLabelAt("Prev Week Low " + _prevWeekLow.ToString("F2"), _prevWeekLow, lxw, labelH, chartScale, _dxPwlBrush);
					}
				}
			}

			// ── Midnight Open (00:00 ET) ───────────────────────────────────────
			if (ShowMidnightOpen && _midnightOpenPrice > 0 && _dxMidnightOpenBrush != null && _midnightOpenStart != DateTime.MinValue)
			{
				float xm0 = Math.Max(GetXForTime(chartControl, _midnightOpenStart) - chartControl.GetBarPaintWidth(ChartBars) / 2f, canvasLeft);
				float xm1 = canvasRight;
				if (xm1 > xm0)
				{
					DrawSharpDXLine(xm0, xm1, chartScale.GetYByValue(_midnightOpenPrice), _dxMidnightOpenBrush, MidnightLineWidth, MidnightDashStyle);
					if (ShowLineLabels && _textFormat != null)
						RenderLabelAt("Midnight Open " + _midnightOpenPrice.ToString("F2"), _midnightOpenPrice, xm1 - 5f, labelH, chartScale, _dxMidnightOpenBrush);
				}
			}

			// ── Asia session High/Low ─────────────────────────────────────────
			// xA1 extends to NY session end (or canvasRight while NY still active)
			float nyEndX = _nySessionEnd != DateTime.MinValue ? Math.Min(GetXForTime(chartControl, _nySessionEnd), canvasRight) : canvasRight;
			if (ShowAsiaHighLow && _asiaSessionStarted && _asiaSessionHigh > double.MinValue && _dxAsiaHighLineBrush != null)
			{
				float xA0 = Math.Max(GetXForTime(chartControl, _asiaSessionStart), canvasLeft);
				float xA1 = nyEndX;
				if (xA1 > xA0)
				{
					DrawSharpDXLine(xA0, xA1, chartScale.GetYByValue(_asiaSessionHigh), _dxAsiaHighLineBrush, OrbLineWidth, OrbDashStyle);
					DrawSharpDXLine(xA0, xA1, chartScale.GetYByValue(_asiaSessionLow), _dxAsiaLowLineBrush, OrbLineWidth, OrbDashStyle);
					if (ShowLineLabels && _textFormat != null)
					{
						float lxA = Math.Min(xA1, canvasRight - 5f);
						RenderLabelAt("Asia High " + _asiaSessionHigh.ToString("F2"), _asiaSessionHigh, lxA, labelH, chartScale, _dxAsiaHighLineBrush);
						RenderLabelAt("Asia Low " + _asiaSessionLow.ToString("F2"), _asiaSessionLow, lxA, labelH, chartScale, _dxAsiaLowLineBrush);
					}
				}
			}

			// ── Europe session High/Low ───────────────────────────────────────
			// xE1 extends to NY session end (reuse nyEndX computed above)
			if (ShowEuropeHighLow && _europeSessionStarted && _europeSessionHigh > double.MinValue && _dxEuropeHighLineBrush != null)
			{
				float xE0 = Math.Max(GetXForTime(chartControl, _europeSessionStart), canvasLeft);
				float xE1 = nyEndX;
				if (xE1 > xE0)
				{
					DrawSharpDXLine(xE0, xE1, chartScale.GetYByValue(_europeSessionHigh), _dxEuropeHighLineBrush, OrbLineWidth, OrbDashStyle);
					DrawSharpDXLine(xE0, xE1, chartScale.GetYByValue(_europeSessionLow), _dxEuropeLowLineBrush, OrbLineWidth, OrbDashStyle);
					if (ShowLineLabels && _textFormat != null)
					{
						float lxE = Math.Min(xE1, canvasRight - 5f);
						RenderLabelAt("Europe High " + _europeSessionHigh.ToString("F2"), _europeSessionHigh, lxE, labelH, chartScale, _dxEuropeHighLineBrush);
						RenderLabelAt("Europe Low " + _europeSessionLow.ToString("F2"), _europeSessionLow, lxE, labelH, chartScale, _dxEuropeLowLineBrush);
					}
				}
			}

			// ── Live today session ───────────────────────────────────────────
			if (_inSession && _sessionHigh > double.MinValue && _marketOpenStart != DateTime.MinValue)
			{
				// Start at left edge of the opening bar (GetXByBarIndex gives bar center)
				float xStart = GetXForTime(chartControl, _marketOpenStart) - chartControl.GetBarPaintWidth(ChartBars) / 2f;
				float xEnd = canvasRight; // live — extend to right edge

				float x0 = Math.Max(xStart, canvasLeft);
				float x1 = xEnd;
				if (x1 > x0)
				{
					// ── Value Area background fill ──────────────────────────
					if (ShowTodayVABackground && _tpvah > 0 && _tpval > 0 && _dxVABackgroundBrush != null)
					{
						float yVAH = chartScale.GetYByValue(_tpvah);
						float yVAL = chartScale.GetYByValue(_tpval);
						if (yVAL > yVAH) // screen y is inverted
							RenderTarget.FillRectangle(
								new SharpDX.RectangleF(x0, yVAH, x1 - x0, yVAL - yVAH),
								_dxVABackgroundBrush);
					}

					if (ShowTDH) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(_sessionHigh), _dxTdhBrush, TdLineWidth, TdDashStyle);
					if (ShowTDL) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(_sessionLow), _dxTdlBrush, TdLineWidth, TdDashStyle);
					if (ShowTPVAH) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(_tpvah), _dxTpvahBrush, TpvaLineWidth, TpvaDashStyle);
					if (ShowTPVAL) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(_tpval), _dxTpvalBrush, TpvaLineWidth, TpvaDashStyle);
					if (ShowPOC && _todayPoc > 0) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(_todayPoc), _dxPocBrush, PocLineWidth, PocDashStyle);
					if (ShowGlobexOpen && _globexOpenPrice > 0 && _dxGlobexOpenBrush != null) DrawSharpDXLine(x0, x1, chartScale.GetYByValue(_globexOpenPrice), _dxGlobexOpenBrush, GlobexLineWidth, GlobexDashStyle);

					if (ShowLineLabels && _textFormat != null)
					{
						if (ShowTDH) RenderLabelAt("Day High " + _sessionHigh.ToString("F2"), _sessionHigh, x1 - 5f, labelH, chartScale, _dxTdhBrush);
						if (ShowTDL) RenderLabelAt("Day Low " + _sessionLow.ToString("F2"), _sessionLow, x1 - 5f, labelH, chartScale, _dxTdlBrush);
						if (ShowTPVAH) RenderLabelAt("Value Area High " + _tpvah.ToString("F2"), _tpvah, x1 - 5f, labelH, chartScale, _dxTpvahBrush);
						if (ShowTPVAL) RenderLabelAt("Value Area Low " + _tpval.ToString("F2"), _tpval, x1 - 5f, labelH, chartScale, _dxTpvalBrush);
						if (ShowPOC && _todayPoc > 0) RenderLabelAt("Point of Control " + _todayPoc.ToString("F2"), _todayPoc, x1 - 5f, labelH, chartScale, _dxPocBrush);
						if (ShowGlobexOpen && _globexOpenPrice > 0 && _dxGlobexOpenBrush != null) RenderLabelAt("Globex Open " + _globexOpenPrice.ToString("F2"), _globexOpenPrice, x1 - 5f, labelH, chartScale, _dxGlobexOpenBrush);
					}

				}
			}

			// ── VWAP line labels at right edge ───────────────────────────────
			// Labels are drawn right-aligned just inside the canvas right edge,
			// at the y-position of the current VWAP value.
			if (ShowLineLabels && _textFormat != null && RenderTarget != null)
			{
				float lx = canvasRight - 5f;
				Action<string, double, SharpDX.Color4> drawVwapLabel =
					(text, val, col) =>
					{
						if (val <= 0) return;
						using (var br = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, col))
							RenderLabelAt(text, val, lx, labelH, chartScale, br);
					};

				if (ShowSessionVWAP) drawVwapLabel("Sess VWAP", _lastVwapSession, new SharpDX.Color4(1f, 1f, 1f, 1f));
				if (ShowNYVWAP) drawVwapLabel("NY VWAP", _lastVwapNY, new SharpDX.Color4(0f, 1f, 0f, 1f));
				if (ShowAsiaVWAP) drawVwapLabel("Asia VWAP", _lastVwapAsia, new SharpDX.Color4(0f, 1f, 1f, 1f));
				if (ShowEuropeVWAP) drawVwapLabel("Europe VWAP", _lastVwapEurope, new SharpDX.Color4(1f, 0.84f, 0f, 1f));
				if (ShowDayHighVWAP) drawVwapLabel("HOD VWAP", _lastVwapDayHigh, new SharpDX.Color4(1f, 0f, 1f, 1f));
				if (ShowDayLowVWAP) drawVwapLabel("LOD VWAP", _lastVwapDayLow, new SharpDX.Color4(0f, 0.5f, 0f, 1f));
				if (ShowWeeklyVWAP) drawVwapLabel("Week VWAP", _lastVwapWeek, new SharpDX.Color4(1f, 0.549f, 0f, 1f));
				if (ShowVwap24h) drawVwapLabel("24h VWAP", _lastVwap24h, new SharpDX.Color4(1f, 0.412f, 0.706f, 1f));
				if (ShowPDVwapNY) drawVwapLabel("PD NY VWAP", _pdVwapNY > 0 ? _pdVwapNY : (_hasPreviousDay ? _prevSession.VwapNY : 0), new SharpDX.Color4(_panelColorPDVwapNY.R / 255f, _panelColorPDVwapNY.G / 255f, _panelColorPDVwapNY.B / 255f, 1f));
				if (ShowPDVwapSession) drawVwapLabel("PD Sess VWAP", _hasPreviousDay ? _prevSession.VwapSession : 0, new SharpDX.Color4(_panelColorPDVwapSession.R / 255f, _panelColorPDVwapSession.G / 255f, _panelColorPDVwapSession.B / 255f, 1f));
			}

		}

		// Draw a SharpDX horizontal line with dash support
		private void DrawSharpDXLine(float x0, float x1, float y,
									  SharpDX.Direct2D1.Brush brush, int width, DashStyleHelper dash)
		{
			if (dash == DashStyleHelper.Solid)
			{
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, width);
			}
			else
			{
				// Build a simple dash pattern for non-solid styles
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

		// Draw a SharpDX vertical line with dash support
		private void DrawSharpDXVLine(float x, float y0, float y1,
									   SharpDX.Direct2D1.Brush brush, int width, DashStyleHelper dash)
		{
			if (dash == DashStyleHelper.Solid)
			{
				RenderTarget.DrawLine(new SharpDX.Vector2(x, y0), new SharpDX.Vector2(x, y1), brush, width);
			}
			else
			{
				float dashLen = dash == DashStyleHelper.Dot ? width * 2f : width * 6f;
				float gapLen = width * 3f;
				float y = y0;
				bool drawing = true;
				while (y < y1)
				{
					float segEnd = Math.Min(y + (drawing ? dashLen : gapLen), y1);
					if (drawing)
						RenderTarget.DrawLine(new SharpDX.Vector2(x, y), new SharpDX.Vector2(x, segEnd), brush, width);
					y = segEnd;
					drawing = !drawing;
				}
			}
		}

		// Draw a vertical marker at every occurrence of a session-open time
		private void DrawSessionOpenLines(ChartControl cc, List<DateTime> times, SharpDX.Direct2D1.Brush brush,
										   float canvasLeft, float canvasRight, float yTop, float yBottom)
		{
			if (brush == null) return;
			for (int i = 0; i < times.Count; i++)
			{
				float x = GetXForTime(cc, times[i]);
				if (x > canvasRight) break;
				if (x >= canvasLeft)
					DrawSharpDXVLine(x, yTop, yBottom, brush, VerticalLineWidth, VerticalLineDashStyle);
			}
		}

		// Returns the pixel X coordinate for a given bar time, clamped to the visible canvas
		private float GetXForTime(ChartControl chartControl, DateTime time)
		{
			try
			{
				int barIdx = ChartBars.GetBarIdxByTime(chartControl, time);
				float x = chartControl.GetXByBarIndex(ChartBars, barIdx);
				// Clamp so label never goes off the left edge
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
				// Draw text so its right edge aligns with xAnchor
				float x = xAnchor - (float)layout.Metrics.Width - 4f;
				RenderTarget.DrawTextLayout(
					new SharpDX.Vector2(x, yTop),
					layout, brush, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
			}
		}


		// Convert HHMM int (e.g. 930 = 09:30, 1700 = 17:00) to TimeSpan
		private static TimeSpan HHMMToTimeSpan(int hhmm)
			=> new TimeSpan(hhmm / 100, hhmm % 100, 0);

		// Returns the most recent Sunday 18:00 ET preceding (or equal to) the given NY time.
		// This is the CME futures "week start" used for weekly VWAP anchoring.
		private static DateTime GetWeeklyAnchor(DateTime nyTime)
		{
			// .NET: Sunday=0, Monday=1, ..., Saturday=6
			int daysSinceSunday = (int)nyTime.DayOfWeek;
			DateTime sundayMidnight = nyTime.Date.AddDays(-daysSinceSunday);
			DateTime anchor = sundayMidnight + new TimeSpan(18, 0, 0);
			// If we're earlier than this Sunday's 18:00, the active anchor is last Sunday's 18:00
			if (nyTime < anchor)
				anchor = anchor.AddDays(-7);
			return anchor;
		}

		// ══════════════════════════════════════════════════════════════════
		//  FixedLines helpers
		// ══════════════════════════════════════════════════════════════════
		private double _lastFixedStep = 0;
		private double _lastFixedMin = 0;
		private double _lastFixedMax = 0;

		// Called from OnBarUpdate — uses configurable step, centers on current price
		private void DrawFixedLines()
		{
			double step = FixedLinesStep;
			if (step <= 0) step = TickSize;

			if (_lastFixedStep > 0)
				RemoveFixedLinesInternal(_lastFixedStep, _lastFixedMin, _lastFixedMax);

			double mid = Close[0];
			double range = step * FixedLinesRange;
			double start = Math.Floor((mid - range) / step) * step;
			double end = Math.Ceiling((mid + range) / step) * step;

			DrawFixedLinesInRange(step, start, end);
		}

		// Called from menu toggle — reuses cached step/range, safe on UI thread
		private void ShowFixedLinesFromCache()
		{
			if (_lastFixedStep <= 0) return; // nothing drawn yet, OnBarUpdate will handle it
			DrawFixedLinesInRange(_lastFixedStep, _lastFixedMin, _lastFixedMax);
		}

		private void DrawFixedLinesInRange(double step, double start, double end)
		{
			for (double p = start; p <= end; p += step)
			{
				double rounded = Math.Round(p, 8);
				string tag = "FixedLine_" + rounded.ToString("F8").Replace(".", "_");
				Draw.HorizontalLine(this, tag, rounded, FixedLinesColor, DashStyleHelper.Dot, 1);
			}
			_lastFixedStep = step;
			_lastFixedMin = start;
			_lastFixedMax = end;
		}

		private void RemoveFixedLines()
		{
			if (_lastFixedStep > 0)
				RemoveFixedLinesInternal(_lastFixedStep, _lastFixedMin, _lastFixedMax);
		}

		private void RemoveFixedLinesInternal(double step, double start, double end)
		{
			for (double p = start; p <= end; p += step)
			{
				double rounded = Math.Round(p, 8);
				string tag = "FixedLine_" + rounded.ToString("F8").Replace(".", "_");
				RemoveDrawObject(tag);
			}
		}

		// ══════════════════════════════════════════════════════════════════
		//  PrevDayLevels calculation helpers
		// ══════════════════════════════════════════════════════════════════
		private void ResetPreviousSession()
		{
			_sessionHigh = double.MinValue;
			_sessionLow = double.MaxValue;
			_pocPrice = 0;
			_pocVolume = 0;
			_bucketVolume.Clear();
			_bucketTPO.Clear();
			_bucketUniform.Clear();
			_bucketLinear.Clear();
			_bucketClose.Clear();
		}

		private void ResetTodaySession()
		{
			_tpvah = 0;
			_tpval = 0;
		}

		// ── ORB draw helper ────────────────────────────────────────────────
		private void DrawOrbLines(ChartControl cc, ChartScale cs, OrbState orb,
			SharpDX.Direct2D1.Brush highBrush, SharpDX.Direct2D1.Brush lowBrush,
			int width, DashStyleHelper dash, string label,
			float canvasLeft, float canvasRight, float labelH)
		{
			if (!orb.Started || orb.High <= double.MinValue || highBrush == null) return;
			float x0 = Math.Max(GetXForTime(cc, orb.Start), canvasLeft);
			float x1 = Math.Min(_inSession ? canvasRight : GetXForTime(cc, orb.End), canvasRight);
			if (x1 <= x0) return;
			DrawSharpDXLine(x0, x1, cs.GetYByValue(orb.High), highBrush, width, dash);
			DrawSharpDXLine(x0, x1, cs.GetYByValue(orb.Low), lowBrush, width, dash);
			if (ShowLineLabels && _textFormat != null)
			{
				float lx = Math.Min(x1, canvasRight - 5f);
				RenderLabelAt(label + " High " + orb.High.ToString("F2"), orb.High, lx, labelH, cs, highBrush);
				RenderLabelAt(label + " Low " + orb.Low.ToString("F2"), orb.Low, lx, labelH, cs, lowBrush);
			}
		}

		// ── ORB accumulator helper ──────────────────────────────────────────
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

		// ── Bucket helpers ─────────────────────────────────────────────────
		private long PriceToBucket(double price) => (long)Math.Floor(price / (TicksPerBucket * TickSize));
		private double BucketToPrice(long bucket) => bucket * TicksPerBucket * TickSize;

		// 1. VolumeProfile – all volume to midpoint bucket
		private void AccumulateBucket(Dictionary<long, long> buckets,
									  ref double pocPrice, ref long pocVolume)
		{
			long barVol = (long)Volume[0];
			long bucketIdx = PriceToBucket((High[0] + Low[0]) / 2.0);

			if (buckets.ContainsKey(bucketIdx)) buckets[bucketIdx] += barVol;
			else buckets[bucketIdx] = barVol;

			if (buckets[bucketIdx] > pocVolume)
			{
				pocVolume = buckets[bucketIdx];
				pocPrice = BucketToPrice(bucketIdx);
			}
		}

		// 2. Uniform – volume spread evenly over all buckets in High–Low range
		private void AccumulateUniform(Dictionary<long, long> buckets)
		{
			long loBucket = PriceToBucket(Low[0]);
			long hiBucket = PriceToBucket(High[0]);
			long numBuckets = hiBucket - loBucket + 1;
			long barVol = (long)Volume[0];
			long volPerBucket = barVol / numBuckets;
			long remainder = barVol % numBuckets;

			for (long b = loBucket; b <= hiBucket; b++)
			{
				long v = volPerBucket + (b == loBucket ? remainder : 0);
				if (buckets.ContainsKey(b)) buckets[b] += v;
				else buckets[b] = v;
			}
		}

		// 3. LinearWeighted – volume weighted by triangular distribution peaking at midpoint
		private void AccumulateLinear(Dictionary<long, long> buckets)
		{
			long loBucket = PriceToBucket(Low[0]);
			long hiBucket = PriceToBucket(High[0]);
			long barVol = (long)Volume[0];
			double midBucket = (loBucket + hiBucket) / 2.0;
			long numBuckets = hiBucket - loBucket + 1;

			if (numBuckets == 1)
			{
				if (buckets.ContainsKey(loBucket)) buckets[loBucket] += barVol;
				else buckets[loBucket] = barVol;
				return;
			}

			// Calculate triangle weights: peak at midpoint, taper to edges
			double[] weights = new double[numBuckets];
			double wSum = 0;
			for (long b = loBucket; b <= hiBucket; b++)
			{
				int idx = (int)(b - loBucket);
				double dist = Math.Abs(b - midBucket);
				double w = (numBuckets / 2.0) - dist + 1.0;
				if (w < 1.0) w = 1.0;
				weights[idx] = w;
				wSum += w;
			}

			long assigned = 0;
			for (long b = loBucket; b <= hiBucket; b++)
			{
				int idx = (int)(b - loBucket);
				long v = (b == hiBucket)
					? barVol - assigned  // assign remainder to last bucket
					: (long)(barVol * weights[idx] / wSum);
				assigned += v;
				if (buckets.ContainsKey(b)) buckets[b] += v;
				else buckets[b] = v;
			}
		}

		// 4. CloseWeighted – all volume assigned to the close-price bucket
		private void AccumulateClose(Dictionary<long, long> buckets)
		{
			long bucketIdx = PriceToBucket(Close[0]);
			long barVol = (long)Volume[0];

			if (buckets.ContainsKey(bucketIdx)) buckets[bucketIdx] += barVol;
			else buckets[bucketIdx] = barVol;
		}

		// 5. TPO – each bucket in High–Low range gets +1 (bar touch count)
		private void AccumulateTPO(Dictionary<long, long> tpoBuckets)
		{
			long loBucket = PriceToBucket(Low[0]);
			long hiBucket = PriceToBucket(High[0]);
			for (long b = loBucket; b <= hiBucket; b++)
			{
				if (tpoBuckets.ContainsKey(b)) tpoBuckets[b]++;
				else tpoBuckets[b] = 1;
			}
		}

		// Return POC for a given bucket set (highest count/volume bucket)
		private double GetBucketPoc(Dictionary<long, long> buckets)
		{
			if (buckets.Count == 0) return 0;
			// Manual max scan — called every bar, avoid LINQ sort + allocations
			long best = 0, bestVal = long.MinValue;
			foreach (var kv in buckets)
				if (kv.Value > bestVal) { bestVal = kv.Value; best = kv.Key; }
			return BucketToPrice(best);
		}

		// Select correct POC based on active method
		private double GetPocForMethod(
			Dictionary<long, long> volB, Dictionary<long, long> uniformB,
			Dictionary<long, long> linearB, Dictionary<long, long> closeB,
			Dictionary<long, long> tpoB, double volPocPrice)
		{
			switch (VAMethod)
			{
				case ValueAreaMethod.Uniform: return GetBucketPoc(uniformB);
				case ValueAreaMethod.LinearWeighted: return GetBucketPoc(linearB);
				case ValueAreaMethod.CloseWeighted: return GetBucketPoc(closeB);
				case ValueAreaMethod.TPO: return GetBucketPoc(tpoB);
				default: return volPocPrice; // VolumeProfile
			}
		}

		// Unified value area calculation: selects active bucket set based on VAMethod
		private void CalculateValueArea(
			Dictionary<long, long> volBuckets, Dictionary<long, long> uniformBuckets,
			Dictionary<long, long> linearBuckets, Dictionary<long, long> closeBuckets,
			Dictionary<long, long> tpoBuckets, double volPocPrice,
			out double vah, out double val)
		{
			vah = 0; val = 0;

			Dictionary<long, long> buckets;
			double pocPrice;

			switch (VAMethod)
			{
				case ValueAreaMethod.Uniform:
					if (uniformBuckets.Count == 0) return;
					buckets = uniformBuckets;
					pocPrice = GetBucketPoc(uniformBuckets);
					break;
				case ValueAreaMethod.LinearWeighted:
					if (linearBuckets.Count == 0) return;
					buckets = linearBuckets;
					pocPrice = GetBucketPoc(linearBuckets);
					break;
				case ValueAreaMethod.CloseWeighted:
					if (closeBuckets.Count == 0) return;
					buckets = closeBuckets;
					pocPrice = GetBucketPoc(closeBuckets);
					break;
				case ValueAreaMethod.TPO:
					if (tpoBuckets.Count == 0) return;
					buckets = tpoBuckets;
					pocPrice = GetBucketPoc(tpoBuckets);
					break;
				default: // VolumeProfile
					if (volBuckets.Count == 0) return;
					buckets = volBuckets;
					pocPrice = volPocPrice;
					break;
			}

			// Manual sum + reused sort buffer — this runs every bar; LINQ here
			// allocated a fresh list and sorted via IEnumerable each time.
			long totalCount = 0;
			foreach (var v in buckets.Values) totalCount += v;
			long targetCount = (long)(totalCount * ValueAreaPercent / 100.0);
			long pocBucket = PriceToBucket(pocPrice);

			var sortedKeys = _vaSortBuffer;
			sortedKeys.Clear();
			foreach (var k in buckets.Keys) sortedKeys.Add(k);
			sortedKeys.Sort();
			int pocIdx = sortedKeys.BinarySearch(pocBucket);
			if (pocIdx < 0)
			{
				pocIdx = 0;
				long minDist = long.MaxValue;
				for (int i = 0; i < sortedKeys.Count; i++)
				{
					long dist = Math.Abs(sortedKeys[i] - pocBucket);
					if (dist < minDist) { minDist = dist; pocIdx = i; }
				}
			}

			long accumulated = buckets.ContainsKey(sortedKeys[pocIdx]) ? buckets[sortedKeys[pocIdx]] : 0;
			int loIdx = pocIdx, hiIdx = pocIdx;

			while (accumulated < targetCount)
			{
				bool canUp = hiIdx + 1 < sortedKeys.Count;
				bool canDown = loIdx - 1 >= 0;
				if (!canUp && !canDown) break;

				long countUp = canUp ? buckets[sortedKeys[hiIdx + 1]] : 0;
				long countDown = canDown ? buckets[sortedKeys[loIdx - 1]] : 0;

				if (!canDown || (canUp && countUp >= countDown))
					accumulated += buckets[sortedKeys[++hiIdx]];
				else
					accumulated += buckets[sortedKeys[--loIdx]];
			}

			vah = BucketToPrice(sortedKeys[hiIdx]) + (TicksPerBucket * TickSize / 2.0);
			val = BucketToPrice(sortedKeys[loIdx]) - (TicksPerBucket * TickSize / 2.0);
		}

		// ══════════════════════════════════════════════════════════════════
		//  WPF Controls
		// ══════════════════════════════════════════════════════════════════
		// NT's menu template reserves an icon/checkbox gutter column to the left of the
		// header text, painted from the template's own theme brush (not our Background).
		// A blank black icon covers that gutter so it doesn't show through as grey.
		private static System.Windows.Shapes.Rectangle MakeGutterIcon() =>
			new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Fill = Brushes.Black };

		private NTMenuItem MakeItem(string header) =>
			new NTMenuItem { Header = header, StaysOpenOnClick = true, Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };

		private NTMenuItem MakeColoredItem(string header, System.Windows.Media.Brush fg)
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
				if (chartWindow == null) { Print("ICNImportantLines: chart window null"); return; }

				mainMenuItemStyle = Application.Current.TryFindResource("MainMenuItem") as Style;
				systemMenuStyle = Application.Current.TryFindResource("SystemMenuStyle") as Style;
				if (mainMenuItemStyle == null || systemMenuStyle == null) { Print("ICNImportantLines: styles null"); return; }

				ntBarMenu = new Menu
				{
					VerticalAlignment = VerticalAlignment.Top,
					VerticalContentAlignment = VerticalAlignment.Top,
					Style = systemMenuStyle
				};

				ntBartopMenuItem = new NTMenuItem
				{
					Header = "ICN_Lines",
					Margin = new Thickness(0),
					Padding = new Thickness(1),
					Style = mainMenuItemStyle,
					VerticalAlignment = VerticalAlignment.Center,
				};
				ntBarMenu.Items.Add(ntBartopMenuItem);

				_showAllEMA = ShowEma1 && ShowEma2 && ShowEma3 && ShowEma4;
				_showAllVWAP = ShowSessionVWAP && ShowAsiaVWAP && ShowEuropeVWAP && ShowNYVWAP &&
								   ShowDayHighVWAP && ShowDayLowVWAP && ShowWeeklyVWAP && ShowVwap24h &&
								   ShowPDVwapNY && ShowPDVwapSession && ShowVwapBands;
				_showAllHistoric = ShowHistoricLines && ShowPDH && ShowPDL && ShowPVAH && ShowPVAL && ShowPrevPOC;
				_showAllToday = ShowTDH && ShowTDL && ShowTPVAH && ShowTPVAL && ShowPOC && ShowGlobexOpen && ShowMidnightOpen;
				_showAllPDL = _showAllHistoric && _showAllToday;
				_showAllFixed = ShowFixedLines;
				bool _showAllORBInit = ShowORB && ShowAsiaORB && ShowEuropeORB && ShowIB && ShowIBExtensions;
				_showAll = _showAllEMA && _showAllVWAP && _showAllPDL && _showAllORBInit && ShowFixedLines
					&& ShowAsiaHighLow && ShowEuropeHighLow && ShowDayOpenLine && ShowAsiaOpenLine && ShowLondonOpenLine && ShowNYOpenLine;

				// Global Show/Hide All
				ntShowHide = MakeItem(_showAll ? "Hide All" : "Show All"); ntShowHide.Tag = "ShowAll";
				ntShowHide.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntShowHide);
				ntBartopMenuItem.Items.Add(new Separator());

				// ── EMA submenu ───────────────────────────────────────────
				ntEMAShowHide = MakeItem(_showAllEMA ? "Hide All EMA" : "Show All EMA"); ntEMAShowHide.Tag = "ShowAllEMA";
				ntEMA1 = MakeColoredItem(ShowEma1 ? "Hide EMA " + Ema1Period : "Show EMA " + Ema1Period, Ema1Brush as System.Windows.Media.Brush ?? Brushes.Turquoise); ntEMA1.Tag = "ShowEma1";
				ntEMA2 = MakeColoredItem(ShowEma2 ? "Hide EMA " + Ema2Period : "Show EMA " + Ema2Period, Ema2Brush as System.Windows.Media.Brush ?? Brushes.Orchid); ntEMA2.Tag = "ShowEma2";
				ntEMA3 = MakeColoredItem(ShowEma3 ? "Hide EMA " + Ema3Period : "Show EMA " + Ema3Period, Ema3Brush as System.Windows.Media.Brush ?? Brushes.DodgerBlue); ntEMA3.Tag = "ShowEma3";
				ntEMA4 = MakeColoredItem(ShowEma4 ? "Hide EMA " + Ema4Period : "Show EMA " + Ema4Period, Ema4Brush as System.Windows.Media.Brush ?? Brushes.Red); ntEMA4.Tag = "ShowEma4";

				ntEMAShowHide.Click += NTBarMenu_Click;
				ntEMA1.Click += NTBarMenu_Click;
				ntEMA2.Click += NTBarMenu_Click;
				ntEMA3.Click += NTBarMenu_Click;
				ntEMA4.Click += NTBarMenu_Click;

				var ntEMAMenu = new NTMenuItem { Header = "EMA", Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };
				ntEMAMenu.Items.Add(ntEMAShowHide);
				ntEMAMenu.Items.Add(ntEMA1);
				ntEMAMenu.Items.Add(ntEMA2);
				ntEMAMenu.Items.Add(ntEMA3);
				ntEMAMenu.Items.Add(ntEMA4);
				ntBartopMenuItem.Items.Add(ntEMAMenu);

				// ── VWAP submenu ──────────────────────────────────────────
				ntVWAPShowHide = MakeItem(_showAllVWAP ? "Hide All VWAP" : "Show All VWAP"); ntVWAPShowHide.Tag = "ShowAllVWAP";
				ntVWAPSession = MakeColoredItem(ShowSessionVWAP ? "Hide Session VWAP" : "Show Session VWAP", Brushes.White); ntVWAPSession.Tag = "ShowSessionVWAP";
				ntVWAPAsia = MakeColoredItem(ShowAsiaVWAP ? "Hide Asia VWAP" : "Show Asia VWAP", Brushes.Cyan); ntVWAPAsia.Tag = "ShowAsiaVWAP";
				ntVWAPEurope = MakeColoredItem(ShowEuropeVWAP ? "Hide Europe VWAP" : "Show Europe VWAP", Brushes.Gold); ntVWAPEurope.Tag = "ShowEuropeVWAP";
				ntVWAPNY = MakeColoredItem(ShowNYVWAP ? "Hide NY VWAP" : "Show NY VWAP", Brushes.Lime); ntVWAPNY.Tag = "ShowNYVWAP";
				ntVWAPDayHigh = MakeColoredItem(ShowDayHighVWAP ? "Hide Day-High VWAP" : "Show Day-High VWAP", Brushes.Magenta); ntVWAPDayHigh.Tag = "ShowDayHighVWAP";
				ntVWAPDayLow = MakeColoredItem(ShowDayLowVWAP ? "Hide Day-Low VWAP" : "Show Day-Low VWAP", Brushes.Green); ntVWAPDayLow.Tag = "ShowDayLowVWAP";
				ntVWAPWeek = MakeColoredItem(ShowWeeklyVWAP ? "Hide Weekly VWAP" : "Show Weekly VWAP", Brushes.DarkOrange); ntVWAPWeek.Tag = "ShowWeeklyVWAP";
				ntPDVwapNY = MakeColoredItem(ShowPDVwapNY ? "Hide PD NY VWAP" : "Show PD NY VWAP", new System.Windows.Media.SolidColorBrush(PDVwapNYColor)); ntPDVwapNY.Tag = "ShowPDVwapNY";
				ntPDVwapSession = MakeColoredItem(ShowPDVwapSession ? "Hide PD Session VWAP" : "Show PD Session VWAP", new System.Windows.Media.SolidColorBrush(PDVwapSessionColor)); ntPDVwapSession.Tag = "ShowPDVwapSession";
				ntVWAPBands = MakeColoredItem(ShowVwapBands ? "Hide VWAP Bands" : "Show VWAP Bands", Vwap1SDBrush as System.Windows.Media.Brush ?? Brushes.Gray); ntVWAPBands.Tag = "ShowVwapBands";
				ntVWAP24h = MakeColoredItem(ShowVwap24h ? "Hide 24h VWAP" : "Show 24h VWAP", Brushes.HotPink); ntVWAP24h.Tag = "ShowVwap24h";

				ntVWAPShowHide.Click += NTBarMenu_Click;
				ntVWAPSession.Click += NTBarMenu_Click;
				ntVWAPAsia.Click += NTBarMenu_Click;
				ntVWAPEurope.Click += NTBarMenu_Click;
				ntVWAPNY.Click += NTBarMenu_Click;
				ntVWAPDayHigh.Click += NTBarMenu_Click;
				ntVWAPDayLow.Click += NTBarMenu_Click;
				ntVWAPWeek.Click += NTBarMenu_Click;
				ntPDVwapNY.Click += NTBarMenu_Click;
				ntPDVwapSession.Click += NTBarMenu_Click;
				ntVWAPBands.Click += NTBarMenu_Click;
				ntVWAP24h.Click += NTBarMenu_Click;

				var ntVWAPMenu = new NTMenuItem { Header = "VWAP", Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };
				ntVWAPMenu.Items.Add(ntVWAPShowHide);
				ntVWAPMenu.Items.Add(ntVWAPSession);
				ntVWAPMenu.Items.Add(ntVWAPDayHigh);
				ntVWAPMenu.Items.Add(ntVWAPDayLow);
				ntVWAPMenu.Items.Add(ntVWAPWeek);
				ntVWAPMenu.Items.Add(ntPDVwapSession);
				ntVWAPMenu.Items.Add(ntVWAPBands);
				ntVWAPMenu.Items.Add(ntVWAP24h);
				ntBartopMenuItem.Items.Add(ntVWAPMenu);

				// ── Week submenu ──────────────────────────────────────────
				ntPWH = MakeColoredItem(ShowPrevWeekHigh ? "Hide Prev Week High" : "Show Prev Week High", PrevWeekHighBrush as System.Windows.Media.Brush ?? Brushes.Violet); ntPWH.Tag = "ShowPrevWeekHigh";
				ntPWL = MakeColoredItem(ShowPrevWeekLow ? "Hide Prev Week Low" : "Show Prev Week Low", PrevWeekLowBrush as System.Windows.Media.Brush ?? Brushes.Violet); ntPWL.Tag = "ShowPrevWeekLow";
				ntPWH.Click += NTBarMenu_Click;
				ntPWL.Click += NTBarMenu_Click;
				var ntWeekMenu = new NTMenuItem { Header = "Week", Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };
				ntWeekMenu.Items.Add(ntPWH);
				ntWeekMenu.Items.Add(ntPWL);
				ntBartopMenuItem.Items.Add(ntWeekMenu);

				_showAllHistoric = ShowHistoricLines && ShowPDH && ShowPDL && ShowPVAH && ShowPVAL && ShowPrevPOC;
				_showAllToday = ShowTDH && ShowTDL && ShowTPVAH && ShowTPVAL && ShowPOC && ShowGlobexOpen && ShowMidnightOpen;
				_showAllPDL = _showAllHistoric && _showAllToday;

				// ── Historic submenu ──────────────────────────────────────
				ntHistoricShowHide = MakeItem(_showAllHistoric ? "Hide All Historic" : "Show All Historic"); ntHistoricShowHide.Tag = "ShowAllHistoric";
				ntHistoricLines = MakeItem(ShowHistoricLines ? "Hide Historic Lines" : "Show Historic Lines"); ntHistoricLines.Tag = "ShowHistoricLines";
				ntPDH = MakeColoredItem(ShowPDH ? "Hide Prev Day High" : "Show Prev Day High", PdhBrush as System.Windows.Media.Brush ?? Brushes.White); ntPDH.Tag = "ShowPDH";
				ntPDL = MakeColoredItem(ShowPDL ? "Hide Prev Day Low" : "Show Prev Day Low", PdlBrush as System.Windows.Media.Brush ?? Brushes.White); ntPDL.Tag = "ShowPDL";
				ntPVAH = MakeColoredItem(ShowPVAH ? "Hide Prev Value Area High" : "Show Prev Value Area High", PvahBrush as System.Windows.Media.Brush ?? Brushes.White); ntPVAH.Tag = "ShowPVAH";
				ntPVAL = MakeColoredItem(ShowPVAL ? "Hide Prev Value Area Low" : "Show Prev Value Area Low", PvalBrush as System.Windows.Media.Brush ?? Brushes.White); ntPVAL.Tag = "ShowPVAL";
				ntPrevPOC = MakeColoredItem(ShowPrevPOC ? "Hide Prev Point of Control" : "Show Prev Point of Control", PocBrush as System.Windows.Media.Brush ?? Brushes.White); ntPrevPOC.Tag = "ShowPrevPOC";

				ntHistoricShowHide.Click += NTBarMenu_Click;
				ntHistoricLines.Click += NTBarMenu_Click;
				ntPDH.Click += NTBarMenu_Click;
				ntPDL.Click += NTBarMenu_Click;
				ntPVAH.Click += NTBarMenu_Click;
				ntPVAL.Click += NTBarMenu_Click;
				ntPrevPOC.Click += NTBarMenu_Click;

				var ntHistoricMenu = new NTMenuItem { Header = "Historic", Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };
				ntHistoricMenu.Items.Add(ntHistoricShowHide);
				ntHistoricMenu.Items.Add(ntHistoricLines);
				ntHistoricMenu.Items.Add(ntPDH);
				ntHistoricMenu.Items.Add(ntPDL);
				ntHistoricMenu.Items.Add(ntPVAH);
				ntHistoricMenu.Items.Add(ntPVAL);
				ntHistoricMenu.Items.Add(ntPrevPOC);
				ntBartopMenuItem.Items.Add(ntHistoricMenu);

				// ── Today submenu ─────────────────────────────────────────
				ntTodayShowHide = MakeItem(_showAllToday ? "Hide All Today" : "Show All Today"); ntTodayShowHide.Tag = "ShowAllToday";
				ntTodayVABackground = MakeColoredItem(ShowTodayVABackground ? "Hide VA Background" : "Show VA Background", new System.Windows.Media.SolidColorBrush(TodayVABackgroundColor)); ntTodayVABackground.Tag = "ShowTodayVABackground";
				ntTDH = MakeColoredItem(ShowTDH ? "Hide Today High" : "Show Today High", TdhBrush as System.Windows.Media.Brush ?? Brushes.White); ntTDH.Tag = "ShowTDH";
				ntTDL = MakeColoredItem(ShowTDL ? "Hide Today Low" : "Show Today Low", TdlBrush as System.Windows.Media.Brush ?? Brushes.White); ntTDL.Tag = "ShowTDL";
				ntTPVAH = MakeColoredItem(ShowTPVAH ? "Hide Today Value Area High" : "Show Today Value Area High", TpvahBrush as System.Windows.Media.Brush ?? Brushes.White); ntTPVAH.Tag = "ShowTPVAH";
				ntTPVAL = MakeColoredItem(ShowTPVAL ? "Hide Today Value Area Low" : "Show Today Value Area Low", TpvalBrush as System.Windows.Media.Brush ?? Brushes.White); ntTPVAL.Tag = "ShowTPVAL";
				ntPOC = MakeColoredItem(ShowPOC ? "Hide Point of Control" : "Show Point of Control", PocBrush as System.Windows.Media.Brush ?? Brushes.White); ntPOC.Tag = "ShowPOC";
				ntGlobexOpen = MakeColoredItem(ShowGlobexOpen ? "Hide Globex Open" : "Show Globex Open", GlobexOpenBrush as System.Windows.Media.Brush ?? Brushes.Yellow); ntGlobexOpen.Tag = "ShowGlobexOpen";
				ntMidnightOpen = MakeColoredItem(ShowMidnightOpen ? "Hide Midnight Open" : "Show Midnight Open", MidnightOpenBrush as System.Windows.Media.Brush ?? Brushes.White); ntMidnightOpen.Tag = "ShowMidnightOpen";
				ntTodayPDH = MakeColoredItem(ShowPDH ? "Hide Prev Day High" : "Show Prev Day High", PdhBrush as System.Windows.Media.Brush ?? Brushes.DodgerBlue); ntTodayPDH.Tag = "ShowPDH";
				ntTodayPDL = MakeColoredItem(ShowPDL ? "Hide Prev Day Low" : "Show Prev Day Low", PdlBrush as System.Windows.Media.Brush ?? Brushes.DodgerBlue); ntTodayPDL.Tag = "ShowPDL";
				ntDayOpenLine = MakeColoredItem(ShowDayOpenLine ? "Hide Day Open Line" : "Show Day Open Line", DayOpenLineBrush as System.Windows.Media.Brush ?? Brushes.White); ntDayOpenLine.Tag = "ShowDayOpenLine";

				ntTodayShowHide.Click += NTBarMenu_Click;
				ntTodayVABackground.Click += NTBarMenu_Click;
				ntTDH.Click += NTBarMenu_Click;
				ntTDL.Click += NTBarMenu_Click;
				ntTPVAH.Click += NTBarMenu_Click;
				ntTPVAL.Click += NTBarMenu_Click;
				ntPOC.Click += NTBarMenu_Click;
				ntGlobexOpen.Click += NTBarMenu_Click;
				ntMidnightOpen.Click += NTBarMenu_Click;
				ntTodayPDH.Click += NTBarMenu_Click;
				ntTodayPDL.Click += NTBarMenu_Click;
				ntDayOpenLine.Click += NTBarMenu_Click;

				var ntTodayMenu = new NTMenuItem { Header = "Today", Background = Brushes.Black, Foreground = Brushes.WhiteSmoke, Icon = MakeGutterIcon() };
				ntTodayMenu.Items.Add(ntTodayShowHide);
				ntTodayMenu.Items.Add(ntTodayVABackground);
				ntTodayMenu.Items.Add(ntTodayPDH);
				ntTodayMenu.Items.Add(ntTodayPDL);
				ntTodayMenu.Items.Add(ntTDH);
				ntTodayMenu.Items.Add(ntTDL);
				ntTodayMenu.Items.Add(ntTPVAH);
				ntTodayMenu.Items.Add(ntTPVAL);
				ntTodayMenu.Items.Add(ntPOC);
				ntTodayMenu.Items.Add(ntGlobexOpen);
				ntTodayMenu.Items.Add(ntMidnightOpen);
				ntTodayMenu.Items.Add(ntDayOpenLine);
				ntBartopMenuItem.Items.Add(ntTodayMenu);
				ntBartopMenuItem.Items.Add(new Separator());

				// ── NY submenu ────────────────────────────────────────────
				ntORBShowHide = MakeColoredItem(ShowORB ? "Hide NY ORB" : "Show NY ORB", OrbHighBrush as System.Windows.Media.Brush ?? Brushes.Cyan); ntORBShowHide.Tag = "ShowORB";
				ntORBShowHide.Click += NTBarMenu_Click;
				ntIBShowHide = MakeColoredItem(ShowIB ? "Hide Initial Balance" : "Show Initial Balance", IBHighBrush as System.Windows.Media.Brush ?? Brushes.Orange); ntIBShowHide.Tag = "ShowIB";
				ntIBShowHide.Click += NTBarMenu_Click;
				ntIBExtShowHide = MakeColoredItem(ShowIBExtensions ? "Hide IB Extensions" : "Show IB Extensions", IBExtBrush as System.Windows.Media.Brush ?? Brushes.OrangeRed); ntIBExtShowHide.Tag = "ShowIBExtensions";
				ntIBExtShowHide.Click += NTBarMenu_Click;
				ntNYOpenLine = MakeColoredItem(ShowNYOpenLine ? "Hide NY Open Line" : "Show NY Open Line", NYOpenLineBrush as System.Windows.Media.Brush ?? Brushes.Yellow); ntNYOpenLine.Tag = "ShowNYOpenLine";
				ntNYOpenLine.Click += NTBarMenu_Click;
				var ntNYMenu = new NTMenuItem { Header = "NY", Foreground = Brushes.Lime, Background = Brushes.Black, Icon = MakeGutterIcon() };
				ntNYMenu.Items.Add(ntVWAPNY);
				ntNYMenu.Items.Add(ntPDVwapNY);
				ntNYMenu.Items.Add(ntORBShowHide);
				ntNYMenu.Items.Add(ntIBShowHide);
				ntNYMenu.Items.Add(ntIBExtShowHide);
				ntNYMenu.Items.Add(ntNYOpenLine);
				ntBartopMenuItem.Items.Add(ntNYMenu);

				// ── Asia submenu ──────────────────────────────────────────
				ntAsiaORBShowHide = MakeColoredItem(ShowAsiaORB ? "Hide Asia ORB" : "Show Asia ORB", AsiaOrbHighBrush as System.Windows.Media.Brush ?? Brushes.Cyan); ntAsiaORBShowHide.Tag = "ShowAsiaORB";
				ntAsiaORBShowHide.Click += NTBarMenu_Click;
				ntAsiaHighLowShowHide = MakeColoredItem(ShowAsiaHighLow ? "Hide Asia High/Low" : "Show Asia High/Low", AsiaHighBrush as System.Windows.Media.Brush ?? Brushes.Cyan); ntAsiaHighLowShowHide.Tag = "ShowAsiaHighLow";
				ntAsiaHighLowShowHide.Click += NTBarMenu_Click;
				ntAsiaOpenLine = MakeColoredItem(ShowAsiaOpenLine ? "Hide Asia Open Line" : "Show Asia Open Line", AsiaOpenLineBrush as System.Windows.Media.Brush ?? Brushes.DodgerBlue); ntAsiaOpenLine.Tag = "ShowAsiaOpenLine";
				ntAsiaOpenLine.Click += NTBarMenu_Click;
				var ntAsiaMenu = new NTMenuItem { Header = "Asia", Foreground = Brushes.Cyan, Background = Brushes.Black, Icon = MakeGutterIcon() };
				ntAsiaMenu.Items.Add(ntVWAPAsia);
				ntAsiaMenu.Items.Add(ntAsiaORBShowHide);
				ntAsiaMenu.Items.Add(ntAsiaHighLowShowHide);
				ntAsiaMenu.Items.Add(ntAsiaOpenLine);
				ntBartopMenuItem.Items.Add(ntAsiaMenu);

				// ── London submenu ────────────────────────────────────────
				ntEuropeORBShowHide = MakeColoredItem(ShowEuropeORB ? "Hide Europe ORB" : "Show Europe ORB", EuropeOrbHighBrush as System.Windows.Media.Brush ?? Brushes.Gold); ntEuropeORBShowHide.Tag = "ShowEuropeORB";
				ntEuropeORBShowHide.Click += NTBarMenu_Click;
				ntEuropeHighLowShowHide = MakeColoredItem(ShowEuropeHighLow ? "Hide Europe High/Low" : "Show Europe High/Low", EuropeHighBrush as System.Windows.Media.Brush ?? Brushes.Gold); ntEuropeHighLowShowHide.Tag = "ShowEuropeHighLow";
				ntEuropeHighLowShowHide.Click += NTBarMenu_Click;
				ntLondonOpenLine = MakeColoredItem(ShowLondonOpenLine ? "Hide London Open Line" : "Show London Open Line", LondonOpenLineBrush as System.Windows.Media.Brush ?? Brushes.Orange); ntLondonOpenLine.Tag = "ShowLondonOpenLine";
				ntLondonOpenLine.Click += NTBarMenu_Click;
				var ntLondonMenu = new NTMenuItem { Header = "London", Foreground = Brushes.Gold, Background = Brushes.Black, Icon = MakeGutterIcon() };
				ntLondonMenu.Items.Add(ntVWAPEurope);
				ntLondonMenu.Items.Add(ntEuropeORBShowHide);
				ntLondonMenu.Items.Add(ntEuropeHighLowShowHide);
				ntLondonMenu.Items.Add(ntLondonOpenLine);
				ntBartopMenuItem.Items.Add(ntLondonMenu);
				ntBartopMenuItem.Items.Add(new Separator());

				// ── Fixed Lines ────────────────────────────────────────────
				ntFixedLinesShowHide = MakeItem(ShowFixedLines ? "Hide Fixed Lines" : "Show Fixed Lines"); ntFixedLinesShowHide.Tag = "ShowFixedLines";
				ntFixedLinesShowHide.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntFixedLinesShowHide);

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
						// EMA
						ShowEma1 = ShowEma2 = ShowEma3 = ShowEma4 = _showAll;
						// VWAP
						ShowSessionVWAP = ShowAsiaVWAP = ShowEuropeVWAP = ShowNYVWAP =
						ShowDayHighVWAP = ShowDayLowVWAP = ShowWeeklyVWAP = ShowPDVwapNY = ShowPDVwapSession = ShowVwapBands = ShowVwap24h = _showAll;
						// Prev Day Levels
						ShowHistoricLines = ShowPDH = ShowPDL = ShowPVAH = ShowPVAL = ShowPrevPOC = _showAll;
						ShowTDH = ShowTDL = ShowTPVAH = ShowTPVAL = ShowPOC = ShowGlobexOpen = ShowMidnightOpen = _showAll;
						ShowTodayVABackground = _showAll;
						// Week
						ShowPrevWeekHigh = ShowPrevWeekLow = _showAll;
						// ORB / Initial Balance
						ShowORB = ShowAsiaORB = ShowEuropeORB = ShowIB = ShowIBExtensions = _showAll;
						// Session high/low
						ShowAsiaHighLow = ShowEuropeHighLow = _showAll;
						// Session-open vertical lines
						ShowDayOpenLine = ShowAsiaOpenLine = ShowLondonOpenLine = ShowNYOpenLine = _showAll;
						// Fixed lines
						ShowFixedLines = _showAll;
						if (ShowFixedLines) ShowFixedLinesFromCache(); else RemoveFixedLines();
						break;
					case "ShowAllEMA":
						_showAllEMA = !_showAllEMA;
						ShowEma1 = ShowEma2 = ShowEma3 = ShowEma4 = _showAllEMA;
						break;
					case "ShowEma1": ShowEma1 = !ShowEma1; break;
					case "ShowEma2": ShowEma2 = !ShowEma2; break;
					case "ShowEma3": ShowEma3 = !ShowEma3; break;
					case "ShowEma4": ShowEma4 = !ShowEma4; break;
					case "ShowAllVWAP":
						_showAllVWAP = !_showAllVWAP;
						ShowSessionVWAP = ShowAsiaVWAP = ShowEuropeVWAP =
						ShowNYVWAP = ShowDayHighVWAP = ShowDayLowVWAP = ShowWeeklyVWAP = ShowVwap24h =
						ShowPDVwapNY = ShowPDVwapSession = ShowVwapBands = _showAllVWAP;
						break;
					case "ShowSessionVWAP": ShowSessionVWAP = !ShowSessionVWAP; break;
					case "ShowAsiaVWAP": ShowAsiaVWAP = !ShowAsiaVWAP; break;
					case "ShowEuropeVWAP": ShowEuropeVWAP = !ShowEuropeVWAP; break;
					case "ShowNYVWAP": ShowNYVWAP = !ShowNYVWAP; break;
					case "ShowDayHighVWAP": ShowDayHighVWAP = !ShowDayHighVWAP; break;
					case "ShowDayLowVWAP": ShowDayLowVWAP = !ShowDayLowVWAP; break;
					case "ShowWeeklyVWAP": ShowWeeklyVWAP = !ShowWeeklyVWAP; break;
					case "ShowPDVwapNY": ShowPDVwapNY = !ShowPDVwapNY; break;
					case "ShowPDVwapSession": ShowPDVwapSession = !ShowPDVwapSession; break;
					case "ShowVwapBands": ShowVwapBands = !ShowVwapBands; break;
					case "ShowVwap24h": ShowVwap24h = !ShowVwap24h; break;
					case "ShowPrevWeekHigh": ShowPrevWeekHigh = !ShowPrevWeekHigh; break;
					case "ShowPrevWeekLow": ShowPrevWeekLow = !ShowPrevWeekLow; break;
					case "ShowAllPDL":
						_showAllPDL = !_showAllPDL;
						ShowHistoricLines = ShowPDH = ShowPDL = ShowPVAH = ShowPVAL = ShowPrevPOC = _showAllPDL;
						ShowTDH = ShowTDL = ShowTPVAH = ShowTPVAL = ShowPOC = ShowGlobexOpen = ShowMidnightOpen = _showAllPDL;
						break;
					case "ShowAllHistoric":
						_showAllHistoric = !_showAllHistoric;
						ShowHistoricLines = ShowPDH = ShowPDL = ShowPVAH = ShowPVAL = ShowPrevPOC = _showAllHistoric;
						break;
					case "ShowAllToday":
						_showAllToday = !_showAllToday;
						ShowTDH = ShowTDL = ShowTPVAH = ShowTPVAL = ShowPOC = ShowGlobexOpen = ShowMidnightOpen = _showAllToday;
						break;
					case "ShowHistoricLines": ShowHistoricLines = !ShowHistoricLines; break;
					case "ShowTodayVABackground": ShowTodayVABackground = !ShowTodayVABackground; break;
					case "ShowPDH": ShowPDH = !ShowPDH; break;
					case "ShowPDL": ShowPDL = !ShowPDL; break;
					case "ShowPVAH": ShowPVAH = !ShowPVAH; break;
					case "ShowPVAL": ShowPVAL = !ShowPVAL; break;
					case "ShowPrevPOC": ShowPrevPOC = !ShowPrevPOC; break;
					case "ShowTDH": ShowTDH = !ShowTDH; break;
					case "ShowTDL": ShowTDL = !ShowTDL; break;
					case "ShowTPVAH": ShowTPVAH = !ShowTPVAH; break;
					case "ShowTPVAL": ShowTPVAL = !ShowTPVAL; break;
					case "ShowPOC": ShowPOC = !ShowPOC; break;
					case "ShowGlobexOpen": ShowGlobexOpen = !ShowGlobexOpen; break;
					case "ShowMidnightOpen": ShowMidnightOpen = !ShowMidnightOpen; break;
					case "ShowDayOpenLine": ShowDayOpenLine = !ShowDayOpenLine; break;
					case "ShowAsiaOpenLine": ShowAsiaOpenLine = !ShowAsiaOpenLine; break;
					case "ShowLondonOpenLine": ShowLondonOpenLine = !ShowLondonOpenLine; break;
					case "ShowNYOpenLine": ShowNYOpenLine = !ShowNYOpenLine; break;
					case "ShowIB": ShowIB = !ShowIB; break;
					case "ShowIBExtensions": ShowIBExtensions = !ShowIBExtensions; break;
					case "ShowORB": ShowORB = !ShowORB; break;
					case "ShowAsiaORB": ShowAsiaORB = !ShowAsiaORB; break;
					case "ShowEuropeORB": ShowEuropeORB = !ShowEuropeORB; break;
					case "ShowAsiaHighLow": ShowAsiaHighLow = !ShowAsiaHighLow; break;
					case "ShowEuropeHighLow": ShowEuropeHighLow = !ShowEuropeHighLow; break;
					case "ShowFixedLines":
						ShowFixedLines = !ShowFixedLines;
						if (ShowFixedLines) ShowFixedLinesFromCache(); else RemoveFixedLines();
						break;
				}
			}
			catch (Exception ex) { Print("ICNImportantLines menu error: " + ex.Message); }
			finally
			{
				try
				{
					_showAllVWAP = ShowSessionVWAP && ShowAsiaVWAP && ShowEuropeVWAP &&
									   ShowNYVWAP && ShowDayHighVWAP && ShowDayLowVWAP && ShowWeeklyVWAP &&
									   ShowPDVwapNY && ShowPDVwapSession && ShowVwapBands && ShowVwap24h;
					_showAllHistoric = ShowHistoricLines && ShowPDH && ShowPDL && ShowPVAH && ShowPVAL && ShowPrevPOC;
					_showAllToday = ShowTDH && ShowTDL && ShowTPVAH && ShowTPVAL && ShowPOC && ShowGlobexOpen && ShowMidnightOpen;
					_showAllPDL = _showAllHistoric && _showAllToday;
					bool _showAllORB = ShowORB && ShowAsiaORB && ShowEuropeORB && ShowIB && ShowIBExtensions;
					_showAllEMA = ShowEma1 && ShowEma2 && ShowEma3 && ShowEma4;
					_showAll = _showAllEMA && _showAllVWAP && _showAllPDL && _showAllORB && ShowFixedLines
						&& ShowAsiaHighLow && ShowEuropeHighLow && ShowDayOpenLine && ShowAsiaOpenLine && ShowLondonOpenLine && ShowNYOpenLine;

					ntShowHide.Header = _showAll ? "Hide All" : "Show All";
					ntEMAShowHide.Header = _showAllEMA ? "Hide All EMA" : "Show All EMA";
					ntEMA1.Header = ShowEma1 ? "Hide EMA " + Ema1Period : "Show EMA " + Ema1Period;
					ntEMA2.Header = ShowEma2 ? "Hide EMA " + Ema2Period : "Show EMA " + Ema2Period;
					ntEMA3.Header = ShowEma3 ? "Hide EMA " + Ema3Period : "Show EMA " + Ema3Period;
					ntEMA4.Header = ShowEma4 ? "Hide EMA " + Ema4Period : "Show EMA " + Ema4Period;
					ntVWAPShowHide.Header = _showAllVWAP ? "Hide All VWAP" : "Show All VWAP";
					ntHistoricShowHide.Header = _showAllHistoric ? "Hide All Historic" : "Show All Historic";
					ntHistoricLines.Header = ShowHistoricLines ? "Hide Historic Lines" : "Show Historic Lines";
					ntTodayShowHide.Header = _showAllToday ? "Hide All Today" : "Show All Today";
					ntTodayVABackground.Header = ShowTodayVABackground ? "Hide VA Background" : "Show VA Background";
					ntORBShowHide.Header = ShowORB ? "Hide NY ORB" : "Show NY ORB";
					ntIBShowHide.Header = ShowIB ? "Hide Initial Balance" : "Show Initial Balance";
					ntIBExtShowHide.Header = ShowIBExtensions ? "Hide IB Extensions" : "Show IB Extensions";
					ntAsiaORBShowHide.Header = ShowAsiaORB ? "Hide Asia ORB" : "Show Asia ORB";
					ntEuropeORBShowHide.Header = ShowEuropeORB ? "Hide Europe ORB" : "Show Europe ORB";
					ntAsiaHighLowShowHide.Header = ShowAsiaHighLow ? "Hide Asia High/Low" : "Show Asia High/Low";
					ntEuropeHighLowShowHide.Header = ShowEuropeHighLow ? "Hide Europe High/Low" : "Show Europe High/Low";
					ntFixedLinesShowHide.Header = ShowFixedLines ? "Hide Fixed Lines" : "Show Fixed Lines";

					ntVWAPSession.Header = ShowSessionVWAP ? "Hide Session VWAP" : "Show Session VWAP";
					ntVWAPAsia.Header = ShowAsiaVWAP ? "Hide Asia VWAP" : "Show Asia VWAP";
					ntVWAPEurope.Header = ShowEuropeVWAP ? "Hide Europe VWAP" : "Show Europe VWAP";
					ntVWAPNY.Header = ShowNYVWAP ? "Hide NY VWAP" : "Show NY VWAP";
					ntVWAPDayHigh.Header = ShowDayHighVWAP ? "Hide Day-High VWAP" : "Show Day-High VWAP";
					ntVWAPDayLow.Header = ShowDayLowVWAP ? "Hide Day-Low VWAP" : "Show Day-Low VWAP";
					ntVWAPWeek.Header = ShowWeeklyVWAP ? "Hide Weekly VWAP" : "Show Weekly VWAP";
					ntPDVwapNY.Header = ShowPDVwapNY ? "Hide PD NY VWAP" : "Show PD NY VWAP";
					ntPDVwapSession.Header = ShowPDVwapSession ? "Hide PD Session VWAP" : "Show PD Session VWAP";
					ntVWAPBands.Header = ShowVwapBands ? "Hide VWAP Bands" : "Show VWAP Bands";
					ntVWAP24h.Header = ShowVwap24h ? "Hide 24h VWAP" : "Show 24h VWAP";
					ntPWH.Header = ShowPrevWeekHigh ? "Hide Prev Week High" : "Show Prev Week High";
					ntPWL.Header = ShowPrevWeekLow ? "Hide Prev Week Low" : "Show Prev Week Low";

					ntPDH.Header = ShowPDH ? "Hide Prev Day High" : "Show Prev Day High";
					ntPDL.Header = ShowPDL ? "Hide Prev Day Low" : "Show Prev Day Low";
					ntTodayPDH.Header = ShowPDH ? "Hide Prev Day High" : "Show Prev Day High";
					ntTodayPDL.Header = ShowPDL ? "Hide Prev Day Low" : "Show Prev Day Low";
					ntPVAH.Header = ShowPVAH ? "Hide Prev Value Area High" : "Show Prev Value Area High";
					ntPVAL.Header = ShowPVAL ? "Hide Prev Value Area Low" : "Show Prev Value Area Low";
					ntTDH.Header = ShowTDH ? "Hide Today High" : "Show Today High";
					ntTDL.Header = ShowTDL ? "Hide Today Low" : "Show Today Low";
					ntTPVAH.Header = ShowTPVAH ? "Hide Today Value Area High" : "Show Today Value Area High";
					ntTPVAL.Header = ShowTPVAL ? "Hide Today Value Area Low" : "Show Today Value Area Low";
					ntPOC.Header = ShowPOC ? "Hide Point of Control" : "Show Point of Control";
					ntGlobexOpen.Header = ShowGlobexOpen ? "Hide Globex Open" : "Show Globex Open";
					ntMidnightOpen.Header = ShowMidnightOpen ? "Hide Midnight Open" : "Show Midnight Open";
					ntDayOpenLine.Header = ShowDayOpenLine ? "Hide Day Open Line" : "Show Day Open Line";
					ntAsiaOpenLine.Header = ShowAsiaOpenLine ? "Hide Asia Open Line" : "Show Asia Open Line";
					ntLondonOpenLine.Header = ShowLondonOpenLine ? "Hide London Open Line" : "Show London Open Line";
					ntNYOpenLine.Header = ShowNYOpenLine ? "Hide NY Open Line" : "Show NY Open Line";
					ntPrevPOC.Header = ShowPrevPOC ? "Hide Prev Point of Control" : "Show Prev Point of Control";

					Plots[0].Brush = ShowAsiaVWAP ? Brushes.Cyan : Brushes.Transparent;
					Plots[1].Brush = ShowEuropeVWAP ? Brushes.Gold : Brushes.Transparent;
					Plots[2].Brush = ShowNYVWAP ? Brushes.Lime : Brushes.Transparent;
					Plots[3].Brush = ShowSessionVWAP ? Brushes.White : Brushes.Transparent;
					Plots[4].Brush = ShowDayHighVWAP ? Brushes.Magenta : Brushes.Transparent;
					Plots[5].Brush = ShowDayLowVWAP ? Brushes.Green : Brushes.Transparent;
					Plots[6].Brush = ShowWeeklyVWAP ? Brushes.DarkOrange : Brushes.Transparent;
					Plots[7].Brush = ShowEma1 ? Ema1Brush : Brushes.Transparent;
					Plots[8].Brush = ShowEma2 ? Ema2Brush : Brushes.Transparent;
					Plots[9].Brush = ShowEma3 ? Ema3Brush : Brushes.Transparent;
					Plots[10].Brush = ShowEma4 ? Ema4Brush : Brushes.Transparent;
					Plots[11].Brush = ShowVwapBands ? Vwap1SDBrush : Brushes.Transparent;
					Plots[12].Brush = ShowVwapBands ? Vwap1SDBrush : Brushes.Transparent;
					Plots[13].Brush = ShowVwapBands ? Vwap2SDBrush : Brushes.Transparent;
					Plots[14].Brush = ShowVwapBands ? Vwap2SDBrush : Brushes.Transparent;
					Plots[15].Brush = ShowVwap24h ? Brushes.HotPink : Brushes.Transparent;

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

		// ══════════════════════════════════════════════════════════════════
		//  Properties
		// ══════════════════════════════════════════════════════════════════

		#region VWAP Plots
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPASIA => Values[0];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPEUROPE => Values[1];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPNY => Values[2];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPSESSION => Values[3];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPDAYHIGH => Values[4];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPDAYLOW => Values[5];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPWEEK => Values[6];
		#endregion

		#region EMA Plots
		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA1 => Values[7];
		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA2 => Values[8];
		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA3 => Values[9];
		[Browsable(false)][XmlIgnore] public Series<double> PlotEMA4 => Values[10];
		#endregion

		#region VWAP Band Plots
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPUP1 => Values[11];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPDN1 => Values[12];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPUP2 => Values[13];
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAPDN2 => Values[14];
		#endregion

		#region Rolling 24h VWAP Plot
		[Browsable(false)][XmlIgnore] public Series<double> PlotVWAP24H => Values[15];
		#endregion

		#region EMA Levels
		[NinjaScriptProperty]
		[Display(Name = "Show EMA 1", Description = "Show/hide the first EMA line.", Order = 0, GroupName = "0. EMA Levels")]
		public bool ShowEma1 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "EMA 1 Period", Description = "Period for the first EMA. Default 9.", Order = 1, GroupName = "0. EMA Levels")]
		public int Ema1Period { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "EMA 1 Color", Description = "Color of the first EMA line.", Order = 2, GroupName = "0. EMA Levels")]
		public Brush Ema1Brush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 2", Description = "Show/hide the second EMA line.", Order = 3, GroupName = "0. EMA Levels")]
		public bool ShowEma2 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "EMA 2 Period", Description = "Period for the second EMA. Default 14.", Order = 4, GroupName = "0. EMA Levels")]
		public int Ema2Period { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "EMA 2 Color", Description = "Color of the second EMA line.", Order = 5, GroupName = "0. EMA Levels")]
		public Brush Ema2Brush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 3", Description = "Show/hide the third EMA line.", Order = 6, GroupName = "0. EMA Levels")]
		public bool ShowEma3 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "EMA 3 Period", Description = "Period for the third EMA. Default 50.", Order = 7, GroupName = "0. EMA Levels")]
		public int Ema3Period { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "EMA 3 Color", Description = "Color of the third EMA line.", Order = 8, GroupName = "0. EMA Levels")]
		public Brush Ema3Brush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 4", Description = "Show/hide the fourth EMA line.", Order = 9, GroupName = "0. EMA Levels")]
		public bool ShowEma4 { get; set; }

		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "EMA 4 Period", Description = "Period for the fourth EMA. Default 200.", Order = 10, GroupName = "0. EMA Levels")]
		public int Ema4Period { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "EMA 4 Color", Description = "Color of the fourth EMA line.", Order = 11, GroupName = "0. EMA Levels")]
		public Brush Ema4Brush { get; set; }
		#endregion

		#region VWAP Visibility
		#region VWAP Session Times
		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia Start (ET)", Description = "Start time of the Asia session in ET, HHMM format (e.g. 2000 = 20:00). Default 2000. If start > end the session wraps overnight.", Order = 0, GroupName = "1b. VWAP Session Times")]
		public int AsiaStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Asia End (ET)", Description = "End time of the Asia session in ET, HHMM format (e.g. 500 = 05:00). Default 500.", Order = 1, GroupName = "1b. VWAP Session Times")]
		public int AsiaEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Europe Start (ET)", Description = "Start time of the Europe session in ET, HHMM format (e.g. 300 = 03:00). Default 300.", Order = 2, GroupName = "1b. VWAP Session Times")]
		public int EuropeStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Europe End (ET)", Description = "End time of the Europe session in ET, HHMM format (e.g. 1200 = 12:00). Default 1200.", Order = 3, GroupName = "1b. VWAP Session Times")]
		public int EuropeEndTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "NY Start (ET)", Description = "Start time of the NY session in ET, HHMM format (e.g. 930 = 09:30). Default 930.", Order = 4, GroupName = "1b. VWAP Session Times")]
		public int NYStartTime { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "NY End (ET)", Description = "End time of the NY session in ET, HHMM format (e.g. 1700 = 17:00). Default 1700.", Order = 5, GroupName = "1b. VWAP Session Times")]
		public int NYEndTime { get; set; }
		#endregion

		[NinjaScriptProperty]
		[Display(Name = "Show Session VWAP", Description = "Show/hide the full-session VWAP line, anchored at the start of each trading session.", Order = 1, GroupName = "1. VWAP")]
		public bool ShowSessionVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Asia VWAP", Description = "Show/hide the Asia session VWAP (18:00–05:00 ET). Resets every session.", Order = 2, GroupName = "1. VWAP")]
		public bool ShowAsiaVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Europe VWAP", Description = "Show/hide the Europe session VWAP (03:00–12:00 ET). Resets every session.", Order = 3, GroupName = "1. VWAP")]
		public bool ShowEuropeVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show NY VWAP", Description = "Show/hide the New York regular session VWAP (09:30–17:00 ET). Resets every session.", Order = 4, GroupName = "1. VWAP")]
		public bool ShowNYVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Day-High VWAP", Description = "Show/hide a VWAP anchored at the intraday high. Resets each time a new session high is set.", Order = 5, GroupName = "1. VWAP")]
		public bool ShowDayHighVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Day-Low VWAP", Description = "Show/hide a VWAP anchored at the intraday low. Resets each time a new session low is set.", Order = 6, GroupName = "1. VWAP")]
		public bool ShowDayLowVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Weekly VWAP", Description = "Show/hide the weekly VWAP, anchored at Sunday 18:00 ET (CME futures week start) and including all session bars until the next Sunday 18:00 ET roll.", Order = 11, GroupName = "1. VWAP")]
		public bool ShowWeeklyVWAP { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 24h VWAP", Description = "Show/hide a rolling 24-hour VWAP — a sliding window of the last 24 hours of volume, not anchored to any session boundary.", Order = 12, GroupName = "1. VWAP")]
		public bool ShowVwap24h { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show PD NY VWAP", Description = "Show/hide the previous day NY VWAP endpoint as a dashed reference line on the current session.", Order = 7, GroupName = "1. VWAP")]
		public bool ShowPDVwapNY { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show PD Session VWAP", Description = "Show/hide the previous day Session VWAP endpoint as a dashed reference line on the current session.", Order = 8, GroupName = "1. VWAP")]
		public bool ShowPDVwapSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "PD NY VWAP Color", Description = "Color for the previous day NY VWAP endpoint line.", Order = 9, GroupName = "1. VWAP")]
		public System.Windows.Media.Color PDVwapNYColor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "PD Session VWAP Color", Description = "Color for the previous day Session VWAP endpoint line.", Order = 10, GroupName = "1. VWAP")]
		public System.Windows.Media.Color PDVwapSessionColor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show VWAP Bands", Description = "Show/hide ±1 and ±2 standard-deviation bands around the Session VWAP.", Order = 12, GroupName = "1. VWAP")]
		public bool ShowVwapBands { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "VWAP 1st SD Color", Description = "Color for the ±1 standard-deviation VWAP band.", Order = 13, GroupName = "1. VWAP")]
		public Brush Vwap1SDBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "VWAP 2nd SD Color", Description = "Color for the ±2 standard-deviation VWAP band.", Order = 14, GroupName = "1. VWAP")]
		public Brush Vwap2SDBrush { get; set; }
		#endregion

		#region Previous Week
		[NinjaScriptProperty]
		[Display(Name = "Show Prev Week High", Description = "Show/hide the previous week's high, extending across the current week.", Order = 0, GroupName = "1d. Previous Week")]
		public bool ShowPrevWeekHigh { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Prev Week Low", Description = "Show/hide the previous week's low, extending across the current week.", Order = 1, GroupName = "1d. Previous Week")]
		public bool ShowPrevWeekLow { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Prev Week High Color", Description = "Color of the previous week high line.", Order = 2, GroupName = "1d. Previous Week")]
		public Brush PrevWeekHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Prev Week Low Color", Description = "Color of the previous week low line.", Order = 3, GroupName = "1d. Previous Week")]
		public Brush PrevWeekLowBrush { get; set; }
		#endregion

		#region PrevDayLevels – Value Area
		[NinjaScriptProperty]
		[Display(Name = "Value Area Method", Description = "Method used to calculate the value area and POC. VolumeProfile: volume at bar midpoint. Uniform: volume spread evenly over bar range. LinearWeighted: volume weighted toward bar midpoint. CloseWeighted: volume at close price. TPO: counts how many bars touched each price level.", Order = 0, GroupName = "2. Prev Day – Value Area")]
		public ValueAreaMethod VAMethod { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Value Area %", Description = "Percentage of total session volume (or TPO count) that defines the value area. Default is 70%, meaning the area containing 70% of activity around the POC.", Order = 1, GroupName = "2. Prev Day – Value Area")]
		public double ValueAreaPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Price Levels (ticks/bucket)", Description = "Height of each price bucket in ticks. Smaller values give finer resolution but slower calculation. Recommended: 2–4 ticks for NQ.", Order = 1, GroupName = "2. Prev Day – Value Area")]
		public int TicksPerBucket { get; set; }
		#endregion

		#region PrevDayLevels – Previous Day Style
		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "PDH Colour", Description = "Line colour for the Previous Day High.", Order = 0, GroupName = "3. Prev Day – Previous Day")]
		public Brush PdhBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "PDL Colour", Description = "Line colour for the Previous Day Low.", Order = 1, GroupName = "3. Prev Day – Previous Day")]
		public Brush PdlBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "PVAH Colour", Description = "Line colour for the Previous Value Area High.", Order = 2, GroupName = "3. Prev Day – Previous Day")]
		public Brush PvahBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "PVAL Colour", Description = "Line colour for the Previous Value Area Low.", Order = 3, GroupName = "3. Prev Day – Previous Day")]
		public Brush PvalBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "PDH/PDL Line Width", Description = "Pixel width of the Previous Day High and Low lines.", Order = 4, GroupName = "3. Prev Day – Previous Day")]
		public int PdLineWidth { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "PVAH/PVAL Line Width", Description = "Pixel width of the Previous Value Area High and Low lines.", Order = 5, GroupName = "3. Prev Day – Previous Day")]
		public int PvaLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "PDH/PDL Dash Style", Description = "Dash style for the Previous Day High and Low lines on their own session day.", Order = 6, GroupName = "3. Prev Day – Previous Day")]
		public DashStyleHelper PdDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "PVAH/PVAL Dash Style", Description = "Dash style for the Previous Value Area High and Low lines on their own session day.", Order = 7, GroupName = "3. Prev Day – Previous Day")]
		public DashStyleHelper PvaDashStyle { get; set; }
		#endregion

		#region PrevDayLevels – Today Style
		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "TDH Colour", Description = "Line colour for Today's High line.", Order = 0, GroupName = "4. Prev Day – Today")]
		public Brush TdhBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "TDL Colour", Description = "Line colour for Today's Low line.", Order = 1, GroupName = "4. Prev Day – Today")]
		public Brush TdlBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "TPVAH Colour", Description = "Line colour for Today's Value Area High line.", Order = 2, GroupName = "4. Prev Day – Today")]
		public Brush TpvahBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "TPVAL Colour", Description = "Line colour for Today's Value Area Low line.", Order = 3, GroupName = "4. Prev Day – Today")]
		public Brush TpvalBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "TDH/TDL Line Width", Description = "Pixel width of Today's High and Low lines.", Order = 4, GroupName = "4. Prev Day – Today")]
		public int TdLineWidth { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "TPVAH/TPVAL Line Width", Description = "Pixel width of Today's Value Area High and Low lines.", Order = 5, GroupName = "4. Prev Day – Today")]
		public int TpvaLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TDH/TDL Dash Style", Description = "Dash style for Today's High and Low lines.", Order = 6, GroupName = "4. Prev Day – Today")]
		public DashStyleHelper TdDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TPVAH/TPVAL Dash Style", Description = "Dash style for Today's Value Area High and Low lines.", Order = 7, GroupName = "4. Prev Day – Today")]
		public DashStyleHelper TpvaDashStyle { get; set; }
		#endregion

		#region PrevDayLevels – Visibility & Appearance
		[NinjaScriptProperty]
		[Display(Name = "Show Historic Lines", Description = "Show/hide the solid level lines drawn on each historical session day (PDH, PDL, PVAH, PVAL, POC). The dashed extension into the next day is controlled separately by the individual level toggles.", Order = 0, GroupName = "5. Prev Day – Visibility")]
		public bool ShowHistoricLines { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show PDH", Description = "Show/hide the Previous Day High line.", Order = 1, GroupName = "5. Prev Day – Visibility")]
		public bool ShowPDH { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show PDL", Description = "Show/hide the Previous Day Low line.", Order = 1, GroupName = "5. Prev Day – Visibility")]
		public bool ShowPDL { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show PVAH", Description = "Show/hide the Previous Value Area High line.", Order = 2, GroupName = "5. Prev Day – Visibility")]
		public bool ShowPVAH { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show PVAL", Description = "Show/hide the Previous Value Area Low line.", Order = 3, GroupName = "5. Prev Day – Visibility")]
		public bool ShowPVAL { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show TDH", Description = "Show/hide Today's High line. Starts at 18:00 ET and updates live.", Order = 4, GroupName = "5. Prev Day – Visibility")]
		public bool ShowTDH { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show TDL", Description = "Show/hide Today's Low line. Starts at 18:00 ET and updates live.", Order = 5, GroupName = "5. Prev Day – Visibility")]
		public bool ShowTDL { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show VA Background", Description = "Show/hide a light background fill between today's Value Area High and Low.", Order = 5, GroupName = "5. Prev Day – Visibility")]
		public bool ShowTodayVABackground { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "VA Background Color", Description = "Color and opacity of the value area background fill. Use a low alpha (e.g. 30/255) for a subtle effect.", Order = 6, GroupName = "5. Prev Day – Visibility")]
		public System.Windows.Media.Color TodayVABackgroundColor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show TPVAH", Description = "Show/hide Today's Value Area High line. Updates live as new volume data comes in.", Order = 7, GroupName = "5. Prev Day – Visibility")]
		public bool ShowTPVAH { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show TPVAL", Description = "Show/hide Today's Value Area Low line. Updates live as new volume data comes in.", Order = 7, GroupName = "5. Prev Day – Visibility")]
		public bool ShowTPVAL { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show POC", Description = "Show/hide today's live Point of Control line. Updates every bar.", Order = 8, GroupName = "5. Prev Day – Visibility")]
		public bool ShowPOC { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Prev POC", Description = "Show/hide the previous session Point of Control as a dashed reference line on the current day.", Order = 9, GroupName = "5. Prev Day – Visibility")]
		public bool ShowPrevPOC { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "POC Colour", Description = "Line colour for both the previous and today's Point of Control.", Order = 10, GroupName = "5. Prev Day – Visibility")]
		public Brush PocBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "POC Line Width", Description = "Pixel width of the Point of Control lines.", Order = 11, GroupName = "5. Prev Day – Visibility")]
		public int PocLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "POC Dash Style", Description = "Dash style for the Point of Control lines.", Order = 12, GroupName = "5. Prev Day – Visibility")]
		public DashStyleHelper PocDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show line labels", Description = "Show price labels at the end of each line segment. Previous day labels show on both the session day and the dashed extension into the next day.", Order = 13, GroupName = "5. Prev Day – Visibility")]
		public bool ShowLineLabels { get; set; }

		[NinjaScriptProperty]
		[Range(6, 24)]
		[Display(Name = "Label font size", Description = "Font size in points for all price labels drawn on the chart.", Order = 14, GroupName = "5. Prev Day – Visibility")]
		public int LabelFontSize { get; set; }
		#endregion

		#region Initial Balance
		[NinjaScriptProperty]
		[Display(Name = "Show Initial Balance", Description = "Show/hide the Initial Balance (first N minutes of NY session) high/low box.", Order = 0, GroupName = "6a. Initial Balance")]
		public bool ShowIB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "IB Minutes", Description = "Number of minutes after the NY session open (NYStartTime) that define the Initial Balance. Default 60.", Order = 1, GroupName = "6a. Initial Balance")]
		public int IBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "IB High Color", Description = "Color of the Initial Balance high line.", Order = 2, GroupName = "6a. Initial Balance")]
		public Brush IBHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "IB Low Color", Description = "Color of the Initial Balance low line.", Order = 3, GroupName = "6a. Initial Balance")]
		public Brush IBLowBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "IB Line Width", Description = "Pixel width of the Initial Balance box lines.", Order = 4, GroupName = "6a. Initial Balance")]
		public int IBLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "IB Dash Style", Description = "Dash style for the Initial Balance box lines.", Order = 5, GroupName = "6a. Initial Balance")]
		public DashStyleHelper IBDashStyle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show IB Extensions", Description = "Show/hide the 1x/2x Initial Balance range projections above and below the IB box, drawn once the IB period completes.", Order = 6, GroupName = "6a. Initial Balance")]
		public bool ShowIBExtensions { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "IB Extension Color", Description = "Color for the IB 1x/2x extension lines.", Order = 7, GroupName = "6a. Initial Balance")]
		public Brush IBExtBrush { get; set; }
		#endregion

		#region Opening Range Breakout
		[NinjaScriptProperty]
		[Display(Name = "Show ORB",
				 Description = "Show/hide the Opening Range Breakout high and low lines.",
				 Order = 0, GroupName = "6b. Opening Range")]
		public bool ShowORB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "ORB Minutes",
				 Description = "Number of minutes after the NY session open (NYStartTime) that define the opening range. Default 30 minutes.",
				 Order = 1, GroupName = "6b. Opening Range")]
		public int ORBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "ORB High Color",
				 Description = "Color of the ORB High line.",
				 Order = 2, GroupName = "6b. Opening Range")]
		public Brush OrbHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "ORB Low Color",
				 Description = "Color of the ORB Low line.",
				 Order = 3, GroupName = "6b. Opening Range")]
		public Brush OrbLowBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "ORB Line Width",
				 Description = "Pixel width of the ORB lines.",
				 Order = 4, GroupName = "6b. Opening Range")]
		public int OrbLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "ORB Dash Style",
				 Description = "Dash style for all ORB lines.",
				 Order = 5, GroupName = "6b. Opening Range")]
		public DashStyleHelper OrbDashStyle { get; set; }

		// ── Asia ORB ──
		[NinjaScriptProperty]
		[Display(Name = "Show Asia ORB", Description = "Show/hide the Asia Opening Range Breakout lines.", Order = 6, GroupName = "6b. Opening Range")]
		public bool ShowAsiaORB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Asia ORB Minutes", Description = "Minutes after Asia open (AsiaStartTime) for the opening range. Default 60.", Order = 7, GroupName = "6b. Opening Range")]
		public int AsiaORBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia ORB High Color", Description = "Color of the Asia ORB High line.", Order = 8, GroupName = "6b. Opening Range")]
		public Brush AsiaOrbHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia ORB Low Color", Description = "Color of the Asia ORB Low line.", Order = 9, GroupName = "6b. Opening Range")]
		public Brush AsiaOrbLowBrush { get; set; }

		// ── Europe ORB ──
		[NinjaScriptProperty]
		[Display(Name = "Show Europe ORB", Description = "Show/hide the Europe Opening Range Breakout lines.", Order = 10, GroupName = "6b. Opening Range")]
		public bool ShowEuropeORB { get; set; }

		[NinjaScriptProperty]
		[Range(1, 240)]
		[Display(Name = "Europe ORB Minutes", Description = "Minutes after Europe open (EuropeStartTime) for the opening range. Default 60.", Order = 11, GroupName = "6b. Opening Range")]
		public int EuropeORBMinutes { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Europe ORB High Color", Description = "Color of the Europe ORB High line.", Order = 12, GroupName = "6b. Opening Range")]
		public Brush EuropeOrbHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Europe ORB Low Color", Description = "Color of the Europe ORB Low line.", Order = 13, GroupName = "6b. Opening Range")]
		public Brush EuropeOrbLowBrush { get; set; }
		#endregion

		#region Session High / Low
		[NinjaScriptProperty]
		[Display(Name = "Show Asia High/Low", Description = "Draw horizontal lines for the Asia session high and low.", Order = 0, GroupName = "6c. Session High/Low")]
		public bool ShowAsiaHighLow { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia High Color", Description = "Color of the Asia session high line.", Order = 1, GroupName = "6c. Session High/Low")]
		public Brush AsiaHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia Low Color", Description = "Color of the Asia session low line.", Order = 2, GroupName = "6c. Session High/Low")]
		public Brush AsiaLowBrush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Europe High/Low", Description = "Draw horizontal lines for the Europe session high and low.", Order = 3, GroupName = "6c. Session High/Low")]
		public bool ShowEuropeHighLow { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Europe High Color", Description = "Color of the Europe session high line.", Order = 4, GroupName = "6c. Session High/Low")]
		public Brush EuropeHighBrush { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Europe Low Color", Description = "Color of the Europe session low line.", Order = 5, GroupName = "6c. Session High/Low")]
		public Brush EuropeLowBrush { get; set; }
		#endregion

		#region Globex Open
		[NinjaScriptProperty]
		[Display(Name = "Show Globex Open", Description = "Draw a horizontal line at today's Globex (overnight) session open price, starting at 18:00 ET.", Order = 0, GroupName = "6d. Globex Open")]
		public bool ShowGlobexOpen { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Globex Open Color", Description = "Color of the Globex open line.", Order = 1, GroupName = "6d. Globex Open")]
		public Brush GlobexOpenBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Globex Open Line Width", Description = "Pixel width of the Globex open line.", Order = 2, GroupName = "6d. Globex Open")]
		public int GlobexLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Globex Open Dash Style", Description = "Dash style for the Globex open line.", Order = 3, GroupName = "6d. Globex Open")]
		public DashStyleHelper GlobexDashStyle { get; set; }
		#endregion

		#region Midnight Open
		[NinjaScriptProperty]
		[Display(Name = "Show Midnight Open", Description = "Draw a horizontal line at the open price of the 00:00 ET bar.", Order = 0, GroupName = "6e. Midnight Open")]
		public bool ShowMidnightOpen { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Midnight Open Color", Description = "Color of the midnight open line.", Order = 1, GroupName = "6e. Midnight Open")]
		public Brush MidnightOpenBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10)]
		[Display(Name = "Midnight Open Line Width", Description = "Pixel width of the midnight open line.", Order = 2, GroupName = "6e. Midnight Open")]
		public int MidnightLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Midnight Open Dash Style", Description = "Dash style for the midnight open line.", Order = 3, GroupName = "6e. Midnight Open")]
		public DashStyleHelper MidnightDashStyle { get; set; }
		#endregion

		#region Session Open Lines
		[NinjaScriptProperty]
		[Display(Name = "Show Day Open Line", Description = "Vertical line marking the daily session open (18:00 ET / Globex open).", Order = 0, GroupName = "6g. Session Open Lines")]
		public bool ShowDayOpenLine { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Day Open Line Color", Description = "Color of the day-open vertical line.", Order = 1, GroupName = "6g. Session Open Lines")]
		public Brush DayOpenLineBrush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Asia Open Line", Description = "Vertical line marking the Asia session open.", Order = 2, GroupName = "6g. Session Open Lines")]
		public bool ShowAsiaOpenLine { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Asia Open Line Color", Description = "Color of the Asia-open vertical line.", Order = 3, GroupName = "6g. Session Open Lines")]
		public Brush AsiaOpenLineBrush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show London Open Line", Description = "Vertical line marking the London/Europe session open.", Order = 4, GroupName = "6g. Session Open Lines")]
		public bool ShowLondonOpenLine { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "London Open Line Color", Description = "Color of the London-open vertical line.", Order = 5, GroupName = "6g. Session Open Lines")]
		public Brush LondonOpenLineBrush { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show NY Open Line", Description = "Vertical line marking the NY session open.", Order = 6, GroupName = "6g. Session Open Lines")]
		public bool ShowNYOpenLine { get; set; }

		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "NY Open Line Color", Description = "Color of the NY-open vertical line.", Order = 7, GroupName = "6g. Session Open Lines")]
		public Brush NYOpenLineBrush { get; set; }

		[NinjaScriptProperty]
		[Range(1, 5)]
		[Display(Name = "Line Width", Description = "Pixel width of the session-open vertical lines.", Order = 8, GroupName = "6g. Session Open Lines")]
		public int VerticalLineWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Line Dash Style", Description = "Dash style for the session-open vertical lines.", Order = 9, GroupName = "6g. Session Open Lines")]
		public DashStyleHelper VerticalLineDashStyle { get; set; }
		#endregion

		#region Fixed Lines
		[NinjaScriptProperty]
		[XmlIgnore]
		[Display(Name = "Line colour",
				 Description = "Colour for the round-number horizontal lines.",
				 Order = 0, GroupName = "6. Fixed Lines")]
		public Brush FixedLinesColor { get; set; }

		[NinjaScriptProperty]
		[Range(0.001, 10000)]
		[Display(Name = "Step",
				 Description = "Price interval between fixed lines. E.g. 100 for NQ, 10 for ES, 1 for CL.",
				 Order = 1, GroupName = "6. Fixed Lines")]
		public double FixedLinesStep { get; set; }

		[NinjaScriptProperty]
		[Range(5, 200)]
		[Display(Name = "Lines above/below",
				 Description = "Number of lines drawn above and below the current price. Total lines = 2 × this value.",
				 Order = 2, GroupName = "6. Fixed Lines")]
		public int FixedLinesRange { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Fixed Lines",
				 Description = "Show/hide the round-number horizontal reference lines.",
				 Order = 3, GroupName = "6. Fixed Lines")]
		public bool ShowFixedLines { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private ItCodeNerd.ICNImportantLines[] cacheICNImportantLines;
		public ItCodeNerd.ICNImportantLines ICNImportantLines(bool showEma1, int ema1Period, Brush ema1Brush, bool showEma2, int ema2Period, Brush ema2Brush, bool showEma3, int ema3Period, Brush ema3Brush, bool showEma4, int ema4Period, Brush ema4Brush, int asiaStartTime, int asiaEndTime, int europeStartTime, int europeEndTime, int nYStartTime, int nYEndTime, bool showSessionVWAP, bool showAsiaVWAP, bool showEuropeVWAP, bool showNYVWAP, bool showDayHighVWAP, bool showDayLowVWAP, bool showWeeklyVWAP, bool showVwap24h, bool showPDVwapNY, bool showPDVwapSession, System.Windows.Media.Color pDVwapNYColor, System.Windows.Media.Color pDVwapSessionColor, bool showVwapBands, Brush vwap1SDBrush, Brush vwap2SDBrush, bool showPrevWeekHigh, bool showPrevWeekLow, Brush prevWeekHighBrush, Brush prevWeekLowBrush, ValueAreaMethod vAMethod, double valueAreaPercent, int ticksPerBucket, Brush pdhBrush, Brush pdlBrush, Brush pvahBrush, Brush pvalBrush, int pdLineWidth, int pvaLineWidth, DashStyleHelper pdDashStyle, DashStyleHelper pvaDashStyle, Brush tdhBrush, Brush tdlBrush, Brush tpvahBrush, Brush tpvalBrush, int tdLineWidth, int tpvaLineWidth, DashStyleHelper tdDashStyle, DashStyleHelper tpvaDashStyle, bool showHistoricLines, bool showPDH, bool showPDL, bool showPVAH, bool showPVAL, bool showTDH, bool showTDL, bool showTodayVABackground, System.Windows.Media.Color todayVABackgroundColor, bool showTPVAH, bool showTPVAL, bool showPOC, bool showPrevPOC, Brush pocBrush, int pocLineWidth, DashStyleHelper pocDashStyle, bool showLineLabels, int labelFontSize, bool showIB, int iBMinutes, Brush iBHighBrush, Brush iBLowBrush, int iBLineWidth, DashStyleHelper iBDashStyle, bool showIBExtensions, Brush iBExtBrush, bool showORB, int oRBMinutes, Brush orbHighBrush, Brush orbLowBrush, int orbLineWidth, DashStyleHelper orbDashStyle, bool showAsiaORB, int asiaORBMinutes, Brush asiaOrbHighBrush, Brush asiaOrbLowBrush, bool showEuropeORB, int europeORBMinutes, Brush europeOrbHighBrush, Brush europeOrbLowBrush, bool showAsiaHighLow, Brush asiaHighBrush, Brush asiaLowBrush, bool showEuropeHighLow, Brush europeHighBrush, Brush europeLowBrush, bool showGlobexOpen, Brush globexOpenBrush, int globexLineWidth, DashStyleHelper globexDashStyle, bool showMidnightOpen, Brush midnightOpenBrush, int midnightLineWidth, DashStyleHelper midnightDashStyle, bool showDayOpenLine, Brush dayOpenLineBrush, bool showAsiaOpenLine, Brush asiaOpenLineBrush, bool showLondonOpenLine, Brush londonOpenLineBrush, bool showNYOpenLine, Brush nYOpenLineBrush, int verticalLineWidth, DashStyleHelper verticalLineDashStyle, Brush fixedLinesColor, double fixedLinesStep, int fixedLinesRange, bool showFixedLines)
		{
			return ICNImportantLines(Input, showEma1, ema1Period, ema1Brush, showEma2, ema2Period, ema2Brush, showEma3, ema3Period, ema3Brush, showEma4, ema4Period, ema4Brush, asiaStartTime, asiaEndTime, europeStartTime, europeEndTime, nYStartTime, nYEndTime, showSessionVWAP, showAsiaVWAP, showEuropeVWAP, showNYVWAP, showDayHighVWAP, showDayLowVWAP, showWeeklyVWAP, showVwap24h, showPDVwapNY, showPDVwapSession, pDVwapNYColor, pDVwapSessionColor, showVwapBands, vwap1SDBrush, vwap2SDBrush, showPrevWeekHigh, showPrevWeekLow, prevWeekHighBrush, prevWeekLowBrush, vAMethod, valueAreaPercent, ticksPerBucket, pdhBrush, pdlBrush, pvahBrush, pvalBrush, pdLineWidth, pvaLineWidth, pdDashStyle, pvaDashStyle, tdhBrush, tdlBrush, tpvahBrush, tpvalBrush, tdLineWidth, tpvaLineWidth, tdDashStyle, tpvaDashStyle, showHistoricLines, showPDH, showPDL, showPVAH, showPVAL, showTDH, showTDL, showTodayVABackground, todayVABackgroundColor, showTPVAH, showTPVAL, showPOC, showPrevPOC, pocBrush, pocLineWidth, pocDashStyle, showLineLabels, labelFontSize, showIB, iBMinutes, iBHighBrush, iBLowBrush, iBLineWidth, iBDashStyle, showIBExtensions, iBExtBrush, showORB, oRBMinutes, orbHighBrush, orbLowBrush, orbLineWidth, orbDashStyle, showAsiaORB, asiaORBMinutes, asiaOrbHighBrush, asiaOrbLowBrush, showEuropeORB, europeORBMinutes, europeOrbHighBrush, europeOrbLowBrush, showAsiaHighLow, asiaHighBrush, asiaLowBrush, showEuropeHighLow, europeHighBrush, europeLowBrush, showGlobexOpen, globexOpenBrush, globexLineWidth, globexDashStyle, showMidnightOpen, midnightOpenBrush, midnightLineWidth, midnightDashStyle, showDayOpenLine, dayOpenLineBrush, showAsiaOpenLine, asiaOpenLineBrush, showLondonOpenLine, londonOpenLineBrush, showNYOpenLine, nYOpenLineBrush, verticalLineWidth, verticalLineDashStyle, fixedLinesColor, fixedLinesStep, fixedLinesRange, showFixedLines);
		}

		public ItCodeNerd.ICNImportantLines ICNImportantLines(ISeries<double> input, bool showEma1, int ema1Period, Brush ema1Brush, bool showEma2, int ema2Period, Brush ema2Brush, bool showEma3, int ema3Period, Brush ema3Brush, bool showEma4, int ema4Period, Brush ema4Brush, int asiaStartTime, int asiaEndTime, int europeStartTime, int europeEndTime, int nYStartTime, int nYEndTime, bool showSessionVWAP, bool showAsiaVWAP, bool showEuropeVWAP, bool showNYVWAP, bool showDayHighVWAP, bool showDayLowVWAP, bool showWeeklyVWAP, bool showVwap24h, bool showPDVwapNY, bool showPDVwapSession, System.Windows.Media.Color pDVwapNYColor, System.Windows.Media.Color pDVwapSessionColor, bool showVwapBands, Brush vwap1SDBrush, Brush vwap2SDBrush, bool showPrevWeekHigh, bool showPrevWeekLow, Brush prevWeekHighBrush, Brush prevWeekLowBrush, ValueAreaMethod vAMethod, double valueAreaPercent, int ticksPerBucket, Brush pdhBrush, Brush pdlBrush, Brush pvahBrush, Brush pvalBrush, int pdLineWidth, int pvaLineWidth, DashStyleHelper pdDashStyle, DashStyleHelper pvaDashStyle, Brush tdhBrush, Brush tdlBrush, Brush tpvahBrush, Brush tpvalBrush, int tdLineWidth, int tpvaLineWidth, DashStyleHelper tdDashStyle, DashStyleHelper tpvaDashStyle, bool showHistoricLines, bool showPDH, bool showPDL, bool showPVAH, bool showPVAL, bool showTDH, bool showTDL, bool showTodayVABackground, System.Windows.Media.Color todayVABackgroundColor, bool showTPVAH, bool showTPVAL, bool showPOC, bool showPrevPOC, Brush pocBrush, int pocLineWidth, DashStyleHelper pocDashStyle, bool showLineLabels, int labelFontSize, bool showIB, int iBMinutes, Brush iBHighBrush, Brush iBLowBrush, int iBLineWidth, DashStyleHelper iBDashStyle, bool showIBExtensions, Brush iBExtBrush, bool showORB, int oRBMinutes, Brush orbHighBrush, Brush orbLowBrush, int orbLineWidth, DashStyleHelper orbDashStyle, bool showAsiaORB, int asiaORBMinutes, Brush asiaOrbHighBrush, Brush asiaOrbLowBrush, bool showEuropeORB, int europeORBMinutes, Brush europeOrbHighBrush, Brush europeOrbLowBrush, bool showAsiaHighLow, Brush asiaHighBrush, Brush asiaLowBrush, bool showEuropeHighLow, Brush europeHighBrush, Brush europeLowBrush, bool showGlobexOpen, Brush globexOpenBrush, int globexLineWidth, DashStyleHelper globexDashStyle, bool showMidnightOpen, Brush midnightOpenBrush, int midnightLineWidth, DashStyleHelper midnightDashStyle, bool showDayOpenLine, Brush dayOpenLineBrush, bool showAsiaOpenLine, Brush asiaOpenLineBrush, bool showLondonOpenLine, Brush londonOpenLineBrush, bool showNYOpenLine, Brush nYOpenLineBrush, int verticalLineWidth, DashStyleHelper verticalLineDashStyle, Brush fixedLinesColor, double fixedLinesStep, int fixedLinesRange, bool showFixedLines)
		{
			if (cacheICNImportantLines != null)
				for (int idx = 0; idx < cacheICNImportantLines.Length; idx++)
					if (cacheICNImportantLines[idx] != null && cacheICNImportantLines[idx].ShowEma1 == showEma1 && cacheICNImportantLines[idx].Ema1Period == ema1Period && cacheICNImportantLines[idx].Ema1Brush == ema1Brush && cacheICNImportantLines[idx].ShowEma2 == showEma2 && cacheICNImportantLines[idx].Ema2Period == ema2Period && cacheICNImportantLines[idx].Ema2Brush == ema2Brush && cacheICNImportantLines[idx].ShowEma3 == showEma3 && cacheICNImportantLines[idx].Ema3Period == ema3Period && cacheICNImportantLines[idx].Ema3Brush == ema3Brush && cacheICNImportantLines[idx].ShowEma4 == showEma4 && cacheICNImportantLines[idx].Ema4Period == ema4Period && cacheICNImportantLines[idx].Ema4Brush == ema4Brush && cacheICNImportantLines[idx].AsiaStartTime == asiaStartTime && cacheICNImportantLines[idx].AsiaEndTime == asiaEndTime && cacheICNImportantLines[idx].EuropeStartTime == europeStartTime && cacheICNImportantLines[idx].EuropeEndTime == europeEndTime && cacheICNImportantLines[idx].NYStartTime == nYStartTime && cacheICNImportantLines[idx].NYEndTime == nYEndTime && cacheICNImportantLines[idx].ShowSessionVWAP == showSessionVWAP && cacheICNImportantLines[idx].ShowAsiaVWAP == showAsiaVWAP && cacheICNImportantLines[idx].ShowEuropeVWAP == showEuropeVWAP && cacheICNImportantLines[idx].ShowNYVWAP == showNYVWAP && cacheICNImportantLines[idx].ShowDayHighVWAP == showDayHighVWAP && cacheICNImportantLines[idx].ShowDayLowVWAP == showDayLowVWAP && cacheICNImportantLines[idx].ShowWeeklyVWAP == showWeeklyVWAP && cacheICNImportantLines[idx].ShowVwap24h == showVwap24h && cacheICNImportantLines[idx].ShowPDVwapNY == showPDVwapNY && cacheICNImportantLines[idx].ShowPDVwapSession == showPDVwapSession && cacheICNImportantLines[idx].PDVwapNYColor == pDVwapNYColor && cacheICNImportantLines[idx].PDVwapSessionColor == pDVwapSessionColor && cacheICNImportantLines[idx].ShowVwapBands == showVwapBands && cacheICNImportantLines[idx].Vwap1SDBrush == vwap1SDBrush && cacheICNImportantLines[idx].Vwap2SDBrush == vwap2SDBrush && cacheICNImportantLines[idx].ShowPrevWeekHigh == showPrevWeekHigh && cacheICNImportantLines[idx].ShowPrevWeekLow == showPrevWeekLow && cacheICNImportantLines[idx].PrevWeekHighBrush == prevWeekHighBrush && cacheICNImportantLines[idx].PrevWeekLowBrush == prevWeekLowBrush && cacheICNImportantLines[idx].VAMethod == vAMethod && cacheICNImportantLines[idx].ValueAreaPercent == valueAreaPercent && cacheICNImportantLines[idx].TicksPerBucket == ticksPerBucket && cacheICNImportantLines[idx].PdhBrush == pdhBrush && cacheICNImportantLines[idx].PdlBrush == pdlBrush && cacheICNImportantLines[idx].PvahBrush == pvahBrush && cacheICNImportantLines[idx].PvalBrush == pvalBrush && cacheICNImportantLines[idx].PdLineWidth == pdLineWidth && cacheICNImportantLines[idx].PvaLineWidth == pvaLineWidth && cacheICNImportantLines[idx].PdDashStyle == pdDashStyle && cacheICNImportantLines[idx].PvaDashStyle == pvaDashStyle && cacheICNImportantLines[idx].TdhBrush == tdhBrush && cacheICNImportantLines[idx].TdlBrush == tdlBrush && cacheICNImportantLines[idx].TpvahBrush == tpvahBrush && cacheICNImportantLines[idx].TpvalBrush == tpvalBrush && cacheICNImportantLines[idx].TdLineWidth == tdLineWidth && cacheICNImportantLines[idx].TpvaLineWidth == tpvaLineWidth && cacheICNImportantLines[idx].TdDashStyle == tdDashStyle && cacheICNImportantLines[idx].TpvaDashStyle == tpvaDashStyle && cacheICNImportantLines[idx].ShowHistoricLines == showHistoricLines && cacheICNImportantLines[idx].ShowPDH == showPDH && cacheICNImportantLines[idx].ShowPDL == showPDL && cacheICNImportantLines[idx].ShowPVAH == showPVAH && cacheICNImportantLines[idx].ShowPVAL == showPVAL && cacheICNImportantLines[idx].ShowTDH == showTDH && cacheICNImportantLines[idx].ShowTDL == showTDL && cacheICNImportantLines[idx].ShowTodayVABackground == showTodayVABackground && cacheICNImportantLines[idx].TodayVABackgroundColor == todayVABackgroundColor && cacheICNImportantLines[idx].ShowTPVAH == showTPVAH && cacheICNImportantLines[idx].ShowTPVAL == showTPVAL && cacheICNImportantLines[idx].ShowPOC == showPOC && cacheICNImportantLines[idx].ShowPrevPOC == showPrevPOC && cacheICNImportantLines[idx].PocBrush == pocBrush && cacheICNImportantLines[idx].PocLineWidth == pocLineWidth && cacheICNImportantLines[idx].PocDashStyle == pocDashStyle && cacheICNImportantLines[idx].ShowLineLabels == showLineLabels && cacheICNImportantLines[idx].LabelFontSize == labelFontSize && cacheICNImportantLines[idx].ShowIB == showIB && cacheICNImportantLines[idx].IBMinutes == iBMinutes && cacheICNImportantLines[idx].IBHighBrush == iBHighBrush && cacheICNImportantLines[idx].IBLowBrush == iBLowBrush && cacheICNImportantLines[idx].IBLineWidth == iBLineWidth && cacheICNImportantLines[idx].IBDashStyle == iBDashStyle && cacheICNImportantLines[idx].ShowIBExtensions == showIBExtensions && cacheICNImportantLines[idx].IBExtBrush == iBExtBrush && cacheICNImportantLines[idx].ShowORB == showORB && cacheICNImportantLines[idx].ORBMinutes == oRBMinutes && cacheICNImportantLines[idx].OrbHighBrush == orbHighBrush && cacheICNImportantLines[idx].OrbLowBrush == orbLowBrush && cacheICNImportantLines[idx].OrbLineWidth == orbLineWidth && cacheICNImportantLines[idx].OrbDashStyle == orbDashStyle && cacheICNImportantLines[idx].ShowAsiaORB == showAsiaORB && cacheICNImportantLines[idx].AsiaORBMinutes == asiaORBMinutes && cacheICNImportantLines[idx].AsiaOrbHighBrush == asiaOrbHighBrush && cacheICNImportantLines[idx].AsiaOrbLowBrush == asiaOrbLowBrush && cacheICNImportantLines[idx].ShowEuropeORB == showEuropeORB && cacheICNImportantLines[idx].EuropeORBMinutes == europeORBMinutes && cacheICNImportantLines[idx].EuropeOrbHighBrush == europeOrbHighBrush && cacheICNImportantLines[idx].EuropeOrbLowBrush == europeOrbLowBrush && cacheICNImportantLines[idx].ShowAsiaHighLow == showAsiaHighLow && cacheICNImportantLines[idx].AsiaHighBrush == asiaHighBrush && cacheICNImportantLines[idx].AsiaLowBrush == asiaLowBrush && cacheICNImportantLines[idx].ShowEuropeHighLow == showEuropeHighLow && cacheICNImportantLines[idx].EuropeHighBrush == europeHighBrush && cacheICNImportantLines[idx].EuropeLowBrush == europeLowBrush && cacheICNImportantLines[idx].ShowGlobexOpen == showGlobexOpen && cacheICNImportantLines[idx].GlobexOpenBrush == globexOpenBrush && cacheICNImportantLines[idx].GlobexLineWidth == globexLineWidth && cacheICNImportantLines[idx].GlobexDashStyle == globexDashStyle && cacheICNImportantLines[idx].ShowMidnightOpen == showMidnightOpen && cacheICNImportantLines[idx].MidnightOpenBrush == midnightOpenBrush && cacheICNImportantLines[idx].MidnightLineWidth == midnightLineWidth && cacheICNImportantLines[idx].MidnightDashStyle == midnightDashStyle && cacheICNImportantLines[idx].ShowDayOpenLine == showDayOpenLine && cacheICNImportantLines[idx].DayOpenLineBrush == dayOpenLineBrush && cacheICNImportantLines[idx].ShowAsiaOpenLine == showAsiaOpenLine && cacheICNImportantLines[idx].AsiaOpenLineBrush == asiaOpenLineBrush && cacheICNImportantLines[idx].ShowLondonOpenLine == showLondonOpenLine && cacheICNImportantLines[idx].LondonOpenLineBrush == londonOpenLineBrush && cacheICNImportantLines[idx].ShowNYOpenLine == showNYOpenLine && cacheICNImportantLines[idx].NYOpenLineBrush == nYOpenLineBrush && cacheICNImportantLines[idx].VerticalLineWidth == verticalLineWidth && cacheICNImportantLines[idx].VerticalLineDashStyle == verticalLineDashStyle && cacheICNImportantLines[idx].FixedLinesColor == fixedLinesColor && cacheICNImportantLines[idx].FixedLinesStep == fixedLinesStep && cacheICNImportantLines[idx].FixedLinesRange == fixedLinesRange && cacheICNImportantLines[idx].ShowFixedLines == showFixedLines && cacheICNImportantLines[idx].EqualsInput(input))
						return cacheICNImportantLines[idx];
			return CacheIndicator<ItCodeNerd.ICNImportantLines>(new ItCodeNerd.ICNImportantLines(){ ShowEma1 = showEma1, Ema1Period = ema1Period, Ema1Brush = ema1Brush, ShowEma2 = showEma2, Ema2Period = ema2Period, Ema2Brush = ema2Brush, ShowEma3 = showEma3, Ema3Period = ema3Period, Ema3Brush = ema3Brush, ShowEma4 = showEma4, Ema4Period = ema4Period, Ema4Brush = ema4Brush, AsiaStartTime = asiaStartTime, AsiaEndTime = asiaEndTime, EuropeStartTime = europeStartTime, EuropeEndTime = europeEndTime, NYStartTime = nYStartTime, NYEndTime = nYEndTime, ShowSessionVWAP = showSessionVWAP, ShowAsiaVWAP = showAsiaVWAP, ShowEuropeVWAP = showEuropeVWAP, ShowNYVWAP = showNYVWAP, ShowDayHighVWAP = showDayHighVWAP, ShowDayLowVWAP = showDayLowVWAP, ShowWeeklyVWAP = showWeeklyVWAP, ShowVwap24h = showVwap24h, ShowPDVwapNY = showPDVwapNY, ShowPDVwapSession = showPDVwapSession, PDVwapNYColor = pDVwapNYColor, PDVwapSessionColor = pDVwapSessionColor, ShowVwapBands = showVwapBands, Vwap1SDBrush = vwap1SDBrush, Vwap2SDBrush = vwap2SDBrush, ShowPrevWeekHigh = showPrevWeekHigh, ShowPrevWeekLow = showPrevWeekLow, PrevWeekHighBrush = prevWeekHighBrush, PrevWeekLowBrush = prevWeekLowBrush, VAMethod = vAMethod, ValueAreaPercent = valueAreaPercent, TicksPerBucket = ticksPerBucket, PdhBrush = pdhBrush, PdlBrush = pdlBrush, PvahBrush = pvahBrush, PvalBrush = pvalBrush, PdLineWidth = pdLineWidth, PvaLineWidth = pvaLineWidth, PdDashStyle = pdDashStyle, PvaDashStyle = pvaDashStyle, TdhBrush = tdhBrush, TdlBrush = tdlBrush, TpvahBrush = tpvahBrush, TpvalBrush = tpvalBrush, TdLineWidth = tdLineWidth, TpvaLineWidth = tpvaLineWidth, TdDashStyle = tdDashStyle, TpvaDashStyle = tpvaDashStyle, ShowHistoricLines = showHistoricLines, ShowPDH = showPDH, ShowPDL = showPDL, ShowPVAH = showPVAH, ShowPVAL = showPVAL, ShowTDH = showTDH, ShowTDL = showTDL, ShowTodayVABackground = showTodayVABackground, TodayVABackgroundColor = todayVABackgroundColor, ShowTPVAH = showTPVAH, ShowTPVAL = showTPVAL, ShowPOC = showPOC, ShowPrevPOC = showPrevPOC, PocBrush = pocBrush, PocLineWidth = pocLineWidth, PocDashStyle = pocDashStyle, ShowLineLabels = showLineLabels, LabelFontSize = labelFontSize, ShowIB = showIB, IBMinutes = iBMinutes, IBHighBrush = iBHighBrush, IBLowBrush = iBLowBrush, IBLineWidth = iBLineWidth, IBDashStyle = iBDashStyle, ShowIBExtensions = showIBExtensions, IBExtBrush = iBExtBrush, ShowORB = showORB, ORBMinutes = oRBMinutes, OrbHighBrush = orbHighBrush, OrbLowBrush = orbLowBrush, OrbLineWidth = orbLineWidth, OrbDashStyle = orbDashStyle, ShowAsiaORB = showAsiaORB, AsiaORBMinutes = asiaORBMinutes, AsiaOrbHighBrush = asiaOrbHighBrush, AsiaOrbLowBrush = asiaOrbLowBrush, ShowEuropeORB = showEuropeORB, EuropeORBMinutes = europeORBMinutes, EuropeOrbHighBrush = europeOrbHighBrush, EuropeOrbLowBrush = europeOrbLowBrush, ShowAsiaHighLow = showAsiaHighLow, AsiaHighBrush = asiaHighBrush, AsiaLowBrush = asiaLowBrush, ShowEuropeHighLow = showEuropeHighLow, EuropeHighBrush = europeHighBrush, EuropeLowBrush = europeLowBrush, ShowGlobexOpen = showGlobexOpen, GlobexOpenBrush = globexOpenBrush, GlobexLineWidth = globexLineWidth, GlobexDashStyle = globexDashStyle, ShowMidnightOpen = showMidnightOpen, MidnightOpenBrush = midnightOpenBrush, MidnightLineWidth = midnightLineWidth, MidnightDashStyle = midnightDashStyle, ShowDayOpenLine = showDayOpenLine, DayOpenLineBrush = dayOpenLineBrush, ShowAsiaOpenLine = showAsiaOpenLine, AsiaOpenLineBrush = asiaOpenLineBrush, ShowLondonOpenLine = showLondonOpenLine, LondonOpenLineBrush = londonOpenLineBrush, ShowNYOpenLine = showNYOpenLine, NYOpenLineBrush = nYOpenLineBrush, VerticalLineWidth = verticalLineWidth, VerticalLineDashStyle = verticalLineDashStyle, FixedLinesColor = fixedLinesColor, FixedLinesStep = fixedLinesStep, FixedLinesRange = fixedLinesRange, ShowFixedLines = showFixedLines }, input, ref cacheICNImportantLines);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.ItCodeNerd.ICNImportantLines ICNImportantLines(bool showEma1, int ema1Period, Brush ema1Brush, bool showEma2, int ema2Period, Brush ema2Brush, bool showEma3, int ema3Period, Brush ema3Brush, bool showEma4, int ema4Period, Brush ema4Brush, int asiaStartTime, int asiaEndTime, int europeStartTime, int europeEndTime, int nYStartTime, int nYEndTime, bool showSessionVWAP, bool showAsiaVWAP, bool showEuropeVWAP, bool showNYVWAP, bool showDayHighVWAP, bool showDayLowVWAP, bool showWeeklyVWAP, bool showVwap24h, bool showPDVwapNY, bool showPDVwapSession, System.Windows.Media.Color pDVwapNYColor, System.Windows.Media.Color pDVwapSessionColor, bool showVwapBands, Brush vwap1SDBrush, Brush vwap2SDBrush, bool showPrevWeekHigh, bool showPrevWeekLow, Brush prevWeekHighBrush, Brush prevWeekLowBrush, ValueAreaMethod vAMethod, double valueAreaPercent, int ticksPerBucket, Brush pdhBrush, Brush pdlBrush, Brush pvahBrush, Brush pvalBrush, int pdLineWidth, int pvaLineWidth, DashStyleHelper pdDashStyle, DashStyleHelper pvaDashStyle, Brush tdhBrush, Brush tdlBrush, Brush tpvahBrush, Brush tpvalBrush, int tdLineWidth, int tpvaLineWidth, DashStyleHelper tdDashStyle, DashStyleHelper tpvaDashStyle, bool showHistoricLines, bool showPDH, bool showPDL, bool showPVAH, bool showPVAL, bool showTDH, bool showTDL, bool showTodayVABackground, System.Windows.Media.Color todayVABackgroundColor, bool showTPVAH, bool showTPVAL, bool showPOC, bool showPrevPOC, Brush pocBrush, int pocLineWidth, DashStyleHelper pocDashStyle, bool showLineLabels, int labelFontSize, bool showIB, int iBMinutes, Brush iBHighBrush, Brush iBLowBrush, int iBLineWidth, DashStyleHelper iBDashStyle, bool showIBExtensions, Brush iBExtBrush, bool showORB, int oRBMinutes, Brush orbHighBrush, Brush orbLowBrush, int orbLineWidth, DashStyleHelper orbDashStyle, bool showAsiaORB, int asiaORBMinutes, Brush asiaOrbHighBrush, Brush asiaOrbLowBrush, bool showEuropeORB, int europeORBMinutes, Brush europeOrbHighBrush, Brush europeOrbLowBrush, bool showAsiaHighLow, Brush asiaHighBrush, Brush asiaLowBrush, bool showEuropeHighLow, Brush europeHighBrush, Brush europeLowBrush, bool showGlobexOpen, Brush globexOpenBrush, int globexLineWidth, DashStyleHelper globexDashStyle, bool showMidnightOpen, Brush midnightOpenBrush, int midnightLineWidth, DashStyleHelper midnightDashStyle, bool showDayOpenLine, Brush dayOpenLineBrush, bool showAsiaOpenLine, Brush asiaOpenLineBrush, bool showLondonOpenLine, Brush londonOpenLineBrush, bool showNYOpenLine, Brush nYOpenLineBrush, int verticalLineWidth, DashStyleHelper verticalLineDashStyle, Brush fixedLinesColor, double fixedLinesStep, int fixedLinesRange, bool showFixedLines)
		{
			return indicator.ICNImportantLines(Input, showEma1, ema1Period, ema1Brush, showEma2, ema2Period, ema2Brush, showEma3, ema3Period, ema3Brush, showEma4, ema4Period, ema4Brush, asiaStartTime, asiaEndTime, europeStartTime, europeEndTime, nYStartTime, nYEndTime, showSessionVWAP, showAsiaVWAP, showEuropeVWAP, showNYVWAP, showDayHighVWAP, showDayLowVWAP, showWeeklyVWAP, showVwap24h, showPDVwapNY, showPDVwapSession, pDVwapNYColor, pDVwapSessionColor, showVwapBands, vwap1SDBrush, vwap2SDBrush, showPrevWeekHigh, showPrevWeekLow, prevWeekHighBrush, prevWeekLowBrush, vAMethod, valueAreaPercent, ticksPerBucket, pdhBrush, pdlBrush, pvahBrush, pvalBrush, pdLineWidth, pvaLineWidth, pdDashStyle, pvaDashStyle, tdhBrush, tdlBrush, tpvahBrush, tpvalBrush, tdLineWidth, tpvaLineWidth, tdDashStyle, tpvaDashStyle, showHistoricLines, showPDH, showPDL, showPVAH, showPVAL, showTDH, showTDL, showTodayVABackground, todayVABackgroundColor, showTPVAH, showTPVAL, showPOC, showPrevPOC, pocBrush, pocLineWidth, pocDashStyle, showLineLabels, labelFontSize, showIB, iBMinutes, iBHighBrush, iBLowBrush, iBLineWidth, iBDashStyle, showIBExtensions, iBExtBrush, showORB, oRBMinutes, orbHighBrush, orbLowBrush, orbLineWidth, orbDashStyle, showAsiaORB, asiaORBMinutes, asiaOrbHighBrush, asiaOrbLowBrush, showEuropeORB, europeORBMinutes, europeOrbHighBrush, europeOrbLowBrush, showAsiaHighLow, asiaHighBrush, asiaLowBrush, showEuropeHighLow, europeHighBrush, europeLowBrush, showGlobexOpen, globexOpenBrush, globexLineWidth, globexDashStyle, showMidnightOpen, midnightOpenBrush, midnightLineWidth, midnightDashStyle, showDayOpenLine, dayOpenLineBrush, showAsiaOpenLine, asiaOpenLineBrush, showLondonOpenLine, londonOpenLineBrush, showNYOpenLine, nYOpenLineBrush, verticalLineWidth, verticalLineDashStyle, fixedLinesColor, fixedLinesStep, fixedLinesRange, showFixedLines);
		}

		public Indicators.ItCodeNerd.ICNImportantLines ICNImportantLines(ISeries<double> input , bool showEma1, int ema1Period, Brush ema1Brush, bool showEma2, int ema2Period, Brush ema2Brush, bool showEma3, int ema3Period, Brush ema3Brush, bool showEma4, int ema4Period, Brush ema4Brush, int asiaStartTime, int asiaEndTime, int europeStartTime, int europeEndTime, int nYStartTime, int nYEndTime, bool showSessionVWAP, bool showAsiaVWAP, bool showEuropeVWAP, bool showNYVWAP, bool showDayHighVWAP, bool showDayLowVWAP, bool showWeeklyVWAP, bool showVwap24h, bool showPDVwapNY, bool showPDVwapSession, System.Windows.Media.Color pDVwapNYColor, System.Windows.Media.Color pDVwapSessionColor, bool showVwapBands, Brush vwap1SDBrush, Brush vwap2SDBrush, bool showPrevWeekHigh, bool showPrevWeekLow, Brush prevWeekHighBrush, Brush prevWeekLowBrush, ValueAreaMethod vAMethod, double valueAreaPercent, int ticksPerBucket, Brush pdhBrush, Brush pdlBrush, Brush pvahBrush, Brush pvalBrush, int pdLineWidth, int pvaLineWidth, DashStyleHelper pdDashStyle, DashStyleHelper pvaDashStyle, Brush tdhBrush, Brush tdlBrush, Brush tpvahBrush, Brush tpvalBrush, int tdLineWidth, int tpvaLineWidth, DashStyleHelper tdDashStyle, DashStyleHelper tpvaDashStyle, bool showHistoricLines, bool showPDH, bool showPDL, bool showPVAH, bool showPVAL, bool showTDH, bool showTDL, bool showTodayVABackground, System.Windows.Media.Color todayVABackgroundColor, bool showTPVAH, bool showTPVAL, bool showPOC, bool showPrevPOC, Brush pocBrush, int pocLineWidth, DashStyleHelper pocDashStyle, bool showLineLabels, int labelFontSize, bool showIB, int iBMinutes, Brush iBHighBrush, Brush iBLowBrush, int iBLineWidth, DashStyleHelper iBDashStyle, bool showIBExtensions, Brush iBExtBrush, bool showORB, int oRBMinutes, Brush orbHighBrush, Brush orbLowBrush, int orbLineWidth, DashStyleHelper orbDashStyle, bool showAsiaORB, int asiaORBMinutes, Brush asiaOrbHighBrush, Brush asiaOrbLowBrush, bool showEuropeORB, int europeORBMinutes, Brush europeOrbHighBrush, Brush europeOrbLowBrush, bool showAsiaHighLow, Brush asiaHighBrush, Brush asiaLowBrush, bool showEuropeHighLow, Brush europeHighBrush, Brush europeLowBrush, bool showGlobexOpen, Brush globexOpenBrush, int globexLineWidth, DashStyleHelper globexDashStyle, bool showMidnightOpen, Brush midnightOpenBrush, int midnightLineWidth, DashStyleHelper midnightDashStyle, bool showDayOpenLine, Brush dayOpenLineBrush, bool showAsiaOpenLine, Brush asiaOpenLineBrush, bool showLondonOpenLine, Brush londonOpenLineBrush, bool showNYOpenLine, Brush nYOpenLineBrush, int verticalLineWidth, DashStyleHelper verticalLineDashStyle, Brush fixedLinesColor, double fixedLinesStep, int fixedLinesRange, bool showFixedLines)
		{
			return indicator.ICNImportantLines(input, showEma1, ema1Period, ema1Brush, showEma2, ema2Period, ema2Brush, showEma3, ema3Period, ema3Brush, showEma4, ema4Period, ema4Brush, asiaStartTime, asiaEndTime, europeStartTime, europeEndTime, nYStartTime, nYEndTime, showSessionVWAP, showAsiaVWAP, showEuropeVWAP, showNYVWAP, showDayHighVWAP, showDayLowVWAP, showWeeklyVWAP, showVwap24h, showPDVwapNY, showPDVwapSession, pDVwapNYColor, pDVwapSessionColor, showVwapBands, vwap1SDBrush, vwap2SDBrush, showPrevWeekHigh, showPrevWeekLow, prevWeekHighBrush, prevWeekLowBrush, vAMethod, valueAreaPercent, ticksPerBucket, pdhBrush, pdlBrush, pvahBrush, pvalBrush, pdLineWidth, pvaLineWidth, pdDashStyle, pvaDashStyle, tdhBrush, tdlBrush, tpvahBrush, tpvalBrush, tdLineWidth, tpvaLineWidth, tdDashStyle, tpvaDashStyle, showHistoricLines, showPDH, showPDL, showPVAH, showPVAL, showTDH, showTDL, showTodayVABackground, todayVABackgroundColor, showTPVAH, showTPVAL, showPOC, showPrevPOC, pocBrush, pocLineWidth, pocDashStyle, showLineLabels, labelFontSize, showIB, iBMinutes, iBHighBrush, iBLowBrush, iBLineWidth, iBDashStyle, showIBExtensions, iBExtBrush, showORB, oRBMinutes, orbHighBrush, orbLowBrush, orbLineWidth, orbDashStyle, showAsiaORB, asiaORBMinutes, asiaOrbHighBrush, asiaOrbLowBrush, showEuropeORB, europeORBMinutes, europeOrbHighBrush, europeOrbLowBrush, showAsiaHighLow, asiaHighBrush, asiaLowBrush, showEuropeHighLow, europeHighBrush, europeLowBrush, showGlobexOpen, globexOpenBrush, globexLineWidth, globexDashStyle, showMidnightOpen, midnightOpenBrush, midnightLineWidth, midnightDashStyle, showDayOpenLine, dayOpenLineBrush, showAsiaOpenLine, asiaOpenLineBrush, showLondonOpenLine, londonOpenLineBrush, showNYOpenLine, nYOpenLineBrush, verticalLineWidth, verticalLineDashStyle, fixedLinesColor, fixedLinesStep, fixedLinesRange, showFixedLines);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.ItCodeNerd.ICNImportantLines ICNImportantLines(bool showEma1, int ema1Period, Brush ema1Brush, bool showEma2, int ema2Period, Brush ema2Brush, bool showEma3, int ema3Period, Brush ema3Brush, bool showEma4, int ema4Period, Brush ema4Brush, int asiaStartTime, int asiaEndTime, int europeStartTime, int europeEndTime, int nYStartTime, int nYEndTime, bool showSessionVWAP, bool showAsiaVWAP, bool showEuropeVWAP, bool showNYVWAP, bool showDayHighVWAP, bool showDayLowVWAP, bool showWeeklyVWAP, bool showVwap24h, bool showPDVwapNY, bool showPDVwapSession, System.Windows.Media.Color pDVwapNYColor, System.Windows.Media.Color pDVwapSessionColor, bool showVwapBands, Brush vwap1SDBrush, Brush vwap2SDBrush, bool showPrevWeekHigh, bool showPrevWeekLow, Brush prevWeekHighBrush, Brush prevWeekLowBrush, ValueAreaMethod vAMethod, double valueAreaPercent, int ticksPerBucket, Brush pdhBrush, Brush pdlBrush, Brush pvahBrush, Brush pvalBrush, int pdLineWidth, int pvaLineWidth, DashStyleHelper pdDashStyle, DashStyleHelper pvaDashStyle, Brush tdhBrush, Brush tdlBrush, Brush tpvahBrush, Brush tpvalBrush, int tdLineWidth, int tpvaLineWidth, DashStyleHelper tdDashStyle, DashStyleHelper tpvaDashStyle, bool showHistoricLines, bool showPDH, bool showPDL, bool showPVAH, bool showPVAL, bool showTDH, bool showTDL, bool showTodayVABackground, System.Windows.Media.Color todayVABackgroundColor, bool showTPVAH, bool showTPVAL, bool showPOC, bool showPrevPOC, Brush pocBrush, int pocLineWidth, DashStyleHelper pocDashStyle, bool showLineLabels, int labelFontSize, bool showIB, int iBMinutes, Brush iBHighBrush, Brush iBLowBrush, int iBLineWidth, DashStyleHelper iBDashStyle, bool showIBExtensions, Brush iBExtBrush, bool showORB, int oRBMinutes, Brush orbHighBrush, Brush orbLowBrush, int orbLineWidth, DashStyleHelper orbDashStyle, bool showAsiaORB, int asiaORBMinutes, Brush asiaOrbHighBrush, Brush asiaOrbLowBrush, bool showEuropeORB, int europeORBMinutes, Brush europeOrbHighBrush, Brush europeOrbLowBrush, bool showAsiaHighLow, Brush asiaHighBrush, Brush asiaLowBrush, bool showEuropeHighLow, Brush europeHighBrush, Brush europeLowBrush, bool showGlobexOpen, Brush globexOpenBrush, int globexLineWidth, DashStyleHelper globexDashStyle, bool showMidnightOpen, Brush midnightOpenBrush, int midnightLineWidth, DashStyleHelper midnightDashStyle, bool showDayOpenLine, Brush dayOpenLineBrush, bool showAsiaOpenLine, Brush asiaOpenLineBrush, bool showLondonOpenLine, Brush londonOpenLineBrush, bool showNYOpenLine, Brush nYOpenLineBrush, int verticalLineWidth, DashStyleHelper verticalLineDashStyle, Brush fixedLinesColor, double fixedLinesStep, int fixedLinesRange, bool showFixedLines)
		{
			return indicator.ICNImportantLines(Input, showEma1, ema1Period, ema1Brush, showEma2, ema2Period, ema2Brush, showEma3, ema3Period, ema3Brush, showEma4, ema4Period, ema4Brush, asiaStartTime, asiaEndTime, europeStartTime, europeEndTime, nYStartTime, nYEndTime, showSessionVWAP, showAsiaVWAP, showEuropeVWAP, showNYVWAP, showDayHighVWAP, showDayLowVWAP, showWeeklyVWAP, showVwap24h, showPDVwapNY, showPDVwapSession, pDVwapNYColor, pDVwapSessionColor, showVwapBands, vwap1SDBrush, vwap2SDBrush, showPrevWeekHigh, showPrevWeekLow, prevWeekHighBrush, prevWeekLowBrush, vAMethod, valueAreaPercent, ticksPerBucket, pdhBrush, pdlBrush, pvahBrush, pvalBrush, pdLineWidth, pvaLineWidth, pdDashStyle, pvaDashStyle, tdhBrush, tdlBrush, tpvahBrush, tpvalBrush, tdLineWidth, tpvaLineWidth, tdDashStyle, tpvaDashStyle, showHistoricLines, showPDH, showPDL, showPVAH, showPVAL, showTDH, showTDL, showTodayVABackground, todayVABackgroundColor, showTPVAH, showTPVAL, showPOC, showPrevPOC, pocBrush, pocLineWidth, pocDashStyle, showLineLabels, labelFontSize, showIB, iBMinutes, iBHighBrush, iBLowBrush, iBLineWidth, iBDashStyle, showIBExtensions, iBExtBrush, showORB, oRBMinutes, orbHighBrush, orbLowBrush, orbLineWidth, orbDashStyle, showAsiaORB, asiaORBMinutes, asiaOrbHighBrush, asiaOrbLowBrush, showEuropeORB, europeORBMinutes, europeOrbHighBrush, europeOrbLowBrush, showAsiaHighLow, asiaHighBrush, asiaLowBrush, showEuropeHighLow, europeHighBrush, europeLowBrush, showGlobexOpen, globexOpenBrush, globexLineWidth, globexDashStyle, showMidnightOpen, midnightOpenBrush, midnightLineWidth, midnightDashStyle, showDayOpenLine, dayOpenLineBrush, showAsiaOpenLine, asiaOpenLineBrush, showLondonOpenLine, londonOpenLineBrush, showNYOpenLine, nYOpenLineBrush, verticalLineWidth, verticalLineDashStyle, fixedLinesColor, fixedLinesStep, fixedLinesRange, showFixedLines);
		}

		public Indicators.ItCodeNerd.ICNImportantLines ICNImportantLines(ISeries<double> input , bool showEma1, int ema1Period, Brush ema1Brush, bool showEma2, int ema2Period, Brush ema2Brush, bool showEma3, int ema3Period, Brush ema3Brush, bool showEma4, int ema4Period, Brush ema4Brush, int asiaStartTime, int asiaEndTime, int europeStartTime, int europeEndTime, int nYStartTime, int nYEndTime, bool showSessionVWAP, bool showAsiaVWAP, bool showEuropeVWAP, bool showNYVWAP, bool showDayHighVWAP, bool showDayLowVWAP, bool showWeeklyVWAP, bool showVwap24h, bool showPDVwapNY, bool showPDVwapSession, System.Windows.Media.Color pDVwapNYColor, System.Windows.Media.Color pDVwapSessionColor, bool showVwapBands, Brush vwap1SDBrush, Brush vwap2SDBrush, bool showPrevWeekHigh, bool showPrevWeekLow, Brush prevWeekHighBrush, Brush prevWeekLowBrush, ValueAreaMethod vAMethod, double valueAreaPercent, int ticksPerBucket, Brush pdhBrush, Brush pdlBrush, Brush pvahBrush, Brush pvalBrush, int pdLineWidth, int pvaLineWidth, DashStyleHelper pdDashStyle, DashStyleHelper pvaDashStyle, Brush tdhBrush, Brush tdlBrush, Brush tpvahBrush, Brush tpvalBrush, int tdLineWidth, int tpvaLineWidth, DashStyleHelper tdDashStyle, DashStyleHelper tpvaDashStyle, bool showHistoricLines, bool showPDH, bool showPDL, bool showPVAH, bool showPVAL, bool showTDH, bool showTDL, bool showTodayVABackground, System.Windows.Media.Color todayVABackgroundColor, bool showTPVAH, bool showTPVAL, bool showPOC, bool showPrevPOC, Brush pocBrush, int pocLineWidth, DashStyleHelper pocDashStyle, bool showLineLabels, int labelFontSize, bool showIB, int iBMinutes, Brush iBHighBrush, Brush iBLowBrush, int iBLineWidth, DashStyleHelper iBDashStyle, bool showIBExtensions, Brush iBExtBrush, bool showORB, int oRBMinutes, Brush orbHighBrush, Brush orbLowBrush, int orbLineWidth, DashStyleHelper orbDashStyle, bool showAsiaORB, int asiaORBMinutes, Brush asiaOrbHighBrush, Brush asiaOrbLowBrush, bool showEuropeORB, int europeORBMinutes, Brush europeOrbHighBrush, Brush europeOrbLowBrush, bool showAsiaHighLow, Brush asiaHighBrush, Brush asiaLowBrush, bool showEuropeHighLow, Brush europeHighBrush, Brush europeLowBrush, bool showGlobexOpen, Brush globexOpenBrush, int globexLineWidth, DashStyleHelper globexDashStyle, bool showMidnightOpen, Brush midnightOpenBrush, int midnightLineWidth, DashStyleHelper midnightDashStyle, bool showDayOpenLine, Brush dayOpenLineBrush, bool showAsiaOpenLine, Brush asiaOpenLineBrush, bool showLondonOpenLine, Brush londonOpenLineBrush, bool showNYOpenLine, Brush nYOpenLineBrush, int verticalLineWidth, DashStyleHelper verticalLineDashStyle, Brush fixedLinesColor, double fixedLinesStep, int fixedLinesRange, bool showFixedLines)
		{
			return indicator.ICNImportantLines(input, showEma1, ema1Period, ema1Brush, showEma2, ema2Period, ema2Brush, showEma3, ema3Period, ema3Brush, showEma4, ema4Period, ema4Brush, asiaStartTime, asiaEndTime, europeStartTime, europeEndTime, nYStartTime, nYEndTime, showSessionVWAP, showAsiaVWAP, showEuropeVWAP, showNYVWAP, showDayHighVWAP, showDayLowVWAP, showWeeklyVWAP, showVwap24h, showPDVwapNY, showPDVwapSession, pDVwapNYColor, pDVwapSessionColor, showVwapBands, vwap1SDBrush, vwap2SDBrush, showPrevWeekHigh, showPrevWeekLow, prevWeekHighBrush, prevWeekLowBrush, vAMethod, valueAreaPercent, ticksPerBucket, pdhBrush, pdlBrush, pvahBrush, pvalBrush, pdLineWidth, pvaLineWidth, pdDashStyle, pvaDashStyle, tdhBrush, tdlBrush, tpvahBrush, tpvalBrush, tdLineWidth, tpvaLineWidth, tdDashStyle, tpvaDashStyle, showHistoricLines, showPDH, showPDL, showPVAH, showPVAL, showTDH, showTDL, showTodayVABackground, todayVABackgroundColor, showTPVAH, showTPVAL, showPOC, showPrevPOC, pocBrush, pocLineWidth, pocDashStyle, showLineLabels, labelFontSize, showIB, iBMinutes, iBHighBrush, iBLowBrush, iBLineWidth, iBDashStyle, showIBExtensions, iBExtBrush, showORB, oRBMinutes, orbHighBrush, orbLowBrush, orbLineWidth, orbDashStyle, showAsiaORB, asiaORBMinutes, asiaOrbHighBrush, asiaOrbLowBrush, showEuropeORB, europeORBMinutes, europeOrbHighBrush, europeOrbLowBrush, showAsiaHighLow, asiaHighBrush, asiaLowBrush, showEuropeHighLow, europeHighBrush, europeLowBrush, showGlobexOpen, globexOpenBrush, globexLineWidth, globexDashStyle, showMidnightOpen, midnightOpenBrush, midnightLineWidth, midnightDashStyle, showDayOpenLine, dayOpenLineBrush, showAsiaOpenLine, asiaOpenLineBrush, showLondonOpenLine, londonOpenLineBrush, showNYOpenLine, nYOpenLineBrush, verticalLineWidth, verticalLineDashStyle, fixedLinesColor, fixedLinesStep, fixedLinesRange, showFixedLines);
		}
	}
}

#endregion
