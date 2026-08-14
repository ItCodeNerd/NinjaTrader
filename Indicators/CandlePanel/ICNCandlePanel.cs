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
using NinjaTrader.NinjaScript.Indicators;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using static System.Net.Mime.MediaTypeNames;
using Brush = SharpDX.Direct2D1.Brush;
using FontStyle = SharpDX.DirectWrite.FontStyle;
using FontWeight = SharpDX.DirectWrite.FontWeight;
using RectangleF = SharpDX.RectangleF;
using TextFormat = SharpDX.DirectWrite.TextFormat;
#endregion

public enum CandlePanelLayout
{
	// Self-contained box, fixed to a screen corner, each timeframe group scaled to its own
	// high/low range independently. Original layout — always visible regardless of scroll/zoom.
	FixedBox,
	// Drawn straight onto the price panel past the last real bar, using the chart's own real
	// price scale, so mini-candles sit at their true price level and the current-price tick
	// lines up with NT8's own live price line automatically.
	RealScaleFuture
}

public enum PanelCorner
{
	TopLeft,
	TopRight,
	BottomLeft,
	BottomRight
}


// This namespace holds indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{



	/// <summary>
	/// Draws the last N 5-minute, 15-minute and 1-hour candles, in one of two layouts (see
	/// <see cref="CandlePanelLayout"/>): a self-contained fixed-corner box with independent
	/// per-group scaling, or drawn on the chart's real price scale past the last real bar.
	///
	/// Data series indices (set up in State.Configure):
	///   BarsArray[0] = the chart's native series (used only to trigger OnBarUpdate / time anchoring)
	///   BarsArray[1] = 5 minute bars
	///   BarsArray[2] = 15 minute bars
	///   BarsArray[3] = 60 minute (1 hour) bars
	/// </summary>
	public class ICNCandlePanel : Indicator
	{
		private struct Candle
		{
			public double Open, High, Low, Close, Volume;
			public DateTime Time;
		}

		private enum Bias { Bullish, Bearish, Mixed }

		// SharpDX resources (created/disposed in OnRenderTargetChanged)
		private Brush upBrush;
		private Brush downBrush;
		private Brush wickBrush;
		private Brush textBrush;
		private Brush bgBrush;
		private Brush borderBrush;
		private Brush currentPriceBrush;
		private TextFormat labelFormat;
		private TextFormat countdownFormat;

		// ── WPF menu state ──────────────────────────────────────────────────
		private NinjaTrader.Gui.Chart.Chart chartWindow;
		private bool ntBarActive;
		private Menu ntBarMenu;
		private NTMenuItem ntBartopMenuItem;
		private NTMenuItem ntShowHide;
		private NTMenuItem ntLayoutItem;
		private NTMenuItem ntShow5mItem, ntShow15mItem, ntShow1hItem;
		private NTMenuItem ntBiasItem, ntTimeRemainingItem, ntPriceLineItem;
		private NTMenuItem ntCornerItem;
		private System.Windows.Style mainMenuItemStyle, systemMenuStyle;
		private System.Windows.Controls.TabItem tabItem;
		private ChartTab chartTab;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Last N 5m/15m/1H candles — either a fixed self-scaled box, or drawn on the chart's real price scale.";
				Name = "ICNCandlePanel";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = false;

				// ---- user-configurable defaults ----
				ShowPanel = true;
				Layout = CandlePanelLayout.RealScaleFuture;

				Show5mColumn = true;
				Show15mColumn = true;
				Show1hColumn = true;

				Candles5m = 5;
				Candles15m = 4;
				Candles1h = 1;
				CandlePitchPx = 16f;
				FutureGapBars = 3;
				MarginPx = 0f;

				PanelWidth = 230;
				PanelHeight = 260;
				PanelCorner = PanelCorner.TopRight;
				PanelMarginRight = 15;
				PanelMarginLeft = 15;
				PanelMarginTop = 60;
				PanelMarginBottom = 15;
				PanelBackgroundBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 20, 25, 35));
				PanelBorderBrush = System.Windows.Media.Brushes.Gray;

				UpBrush = System.Windows.Media.Brushes.SeaGreen;
				DownBrush = System.Windows.Media.Brushes.IndianRed;
				TextColorBrush = System.Windows.Media.Brushes.White;
				ShowCurrentPriceLine = true;
				CurrentPriceLineBrush = System.Windows.Media.Brushes.Yellow;

				ShowTimeRemaining = true;
				ShowBiasLabel = true;
			}
			else if (State == State.Configure)
			{
				// Add the three fixed higher-timeframe series regardless of the chart's own period.
				// Tick Replay is enabled only on these panel series (not the chart's primary series)
				// so the shown candles form/update intrabar instead of only on bar close.
				// barsToLoad is capped to ~1 trading day per timeframe (panel only ever shows today's
				// bars) so Tick Replay doesn't have to replay weeks/months of ticks on load.
				string instrumentName = Instrument.FullName;
				AddDataSeries(instrumentName, new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 5 }, 300, null, null);   // BarsArray[1]
				AddDataSeries(instrumentName, new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 15 }, 100, null, null);  // BarsArray[2]
				AddDataSeries(instrumentName, new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 60 }, 30, null, null);   // BarsArray[3]
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => CreateWPFControls());
			}
			else if (State == State.Terminated)
			{
				DisposeBrushes();

				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(() => DisposeWPFControls());
			}
		}

		protected override void OnBarUpdate()
		{
			// No per-bar plot values are needed; everything is drawn in OnRender.
			// Guard so we only react to the primary series to avoid redundant work.
			if (BarsInProgress != 0)
				return;
		}

		public override void OnRenderTargetChanged()
		{
			DisposeBrushes();

			if (RenderTarget == null)
				return;

			upBrush = ToBrush(UpBrush);
			downBrush = ToBrush(DownBrush);
			wickBrush = ToBrush(TextColorBrush);
			textBrush = ToBrush(TextColorBrush);
			bgBrush = ToBrush(PanelBackgroundBrush);
			borderBrush = ToBrush(PanelBorderBrush);
			currentPriceBrush = ToBrush(CurrentPriceLineBrush);

			labelFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontWeight.Normal, FontStyle.Normal, 11f)
			{
				TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
				ParagraphAlignment = ParagraphAlignment.Center
			};

			countdownFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontWeight.Normal, FontStyle.Normal, 10f)
			{
				TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
				ParagraphAlignment = ParagraphAlignment.Center
			};
		}

		private Brush ToBrush(System.Windows.Media.Brush mediaBrush)
		{
			var scb = mediaBrush as System.Windows.Media.SolidColorBrush;
			var c = scb != null ? scb.Color : Colors.Gray;
			return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
				new Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f));
		}

		private void DisposeBrushes()
		{
			upBrush?.Dispose(); upBrush = null;
			downBrush?.Dispose(); downBrush = null;
			wickBrush?.Dispose(); wickBrush = null;
			textBrush?.Dispose(); textBrush = null;
			bgBrush?.Dispose(); bgBrush = null;
			borderBrush?.Dispose(); borderBrush = null;
			currentPriceBrush?.Dispose(); currentPriceBrush = null;
			labelFormat?.Dispose(); labelFormat = null;
			countdownFormat?.Dispose(); countdownFormat = null;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (!ShowPanel || upBrush == null || State < State.Historical || ChartBars == null)
				return;

			// ---------- gather candle data from the three fixed series ----------
			// Each series always shows its own last N bars relative to the current bar,
			// regardless of hour boundaries.
			var c1h = Show1hColumn ? CollectCandles(3, Candles1h) : new List<Candle>();
			var c5 = Show5mColumn ? CollectCandles(1, Candles5m) : new List<Candle>();
			var c15 = Show15mColumn ? CollectCandles(2, Candles15m) : new List<Candle>();

			if (c5.Count == 0 && c15.Count == 0 && c1h.Count == 0)
				return;

			if (Layout == CandlePanelLayout.RealScaleFuture)
				RenderRealScaleFuture(chartControl, chartScale, c5, c15, c1h);
			else
				RenderFixedBox(chartControl, chartScale, c5, c15, c1h);
		}

		// ══════════════════════════════════════════════════════════════════
		//  Layout: RealScaleFuture — drawn on the chart's own price scale,
		//  past the last real bar.
		// ══════════════════════════════════════════════════════════════════
		private void RenderRealScaleFuture(ChartControl chartControl, ChartScale chartScale, List<Candle> c5, List<Candle> c15, List<Candle> c1h)
		{
			double barSeconds = GetAverageBarSeconds(chartControl);
			float x0 = GetFutureX(chartControl, barSeconds * FutureGapBars) + MarginPx;

			// 1H group is sized to match the 15m group's width (same slot count) rather than
			// its own (usually much smaller) candle count.
			int slots5m = Candles5m;
			int slots15m = Candles15m;
			int slots1h = Math.Max(Candles1h, Candles15m);
			int totalCandles = Math.Max(1, slots5m + slots15m + slots1h);
			int groupCount = new[] { c5.Count > 0, c15.Count > 0, c1h.Count > 0 }.Count(b => b);
			const float groupGap = 18f; // gap between timeframe groups
			float gapsWidth = groupGap * Math.Max(0, groupCount - 1);
			float pitch = CandlePitchPx;

			bool showPrice = ShowCurrentPriceLine && currentPriceBrush != null;
			float? tickY = showPrice ? (float?)chartScale.GetYByValue(GetLivePrice()) : null;

			float xCursor = x0;
			xCursor = DrawGroupRealScale(chartScale, c5, "5m", xCursor, pitch, slots5m, groupGap, c5.Count > 0 ? tickY : null, 5, GroupVolumeScore(c5));
			xCursor = DrawGroupRealScale(chartScale, c15, "15m", xCursor, pitch, slots15m, groupGap, c15.Count > 0 ? tickY : null, 15, GroupVolumeScore(c15));
			xCursor = DrawGroupRealScale(chartScale, c1h, "1H", xCursor, pitch, slots1h, groupGap, c1h.Count > 0 ? tickY : null, 60, GroupVolumeScore(c1h));

			if (ShowBiasLabel)
			{
				double minLowAll = new[] { c5, c15, c1h }.Where(g => g.Count > 0).Min(g => g.Min(c => c.Low));
				float areaBottomAll = chartScale.GetYByValue(minLowAll);

				Bias bias = ComputeBias(c5, c15, c1h);
				var biasBrush = bias == Bias.Bullish ? upBrush : bias == Bias.Bearish ? downBrush : textBrush;
				string biasText = "BIAS: " + (bias == Bias.Bullish ? "BULLISH" : bias == Bias.Bearish ? "BEARISH" : "MIXED");
				float totalWidth = pitch * totalCandles + gapsWidth;
				var biasRect = new RectangleF(x0, areaBottomAll + 32f, totalWidth, 14f);
				RenderTarget.DrawText(biasText, labelFormat, biasRect, biasBrush);
			}
		}

		// Real-time last trade price. Falls back to the primary series' last close if no quote
		// has arrived yet. Explicitly addresses series 0 (Closes[0][0], not the ambient Close[0])
		// since this is a multi-series indicator and the ambient accessor is BarsInProgress-dependent.
		private double GetLivePrice()
		{
			try
			{
				if (Instrument != null && Instrument.MarketData != null
					&& Instrument.MarketData.Last != null && Instrument.MarketData.Last.Price > 0)
					return Instrument.MarketData.Last.Price;
			}
			catch { }
			try { return Closes[0][0]; } catch { return 0; }
		}

		// Average seconds per primary-series bar, measured from the last two real bars. Used to
		// scale the future-anchor gap proportionally to the chart's own time resolution.
		private double GetAverageBarSeconds(ChartControl chartControl)
		{
			try
			{
				var bars0 = BarsArray[0];
				int toIdx = ChartBars.ToIndex;
				int fromIdx = Math.Max(0, toIdx - 20);
				if (toIdx <= fromIdx) return 60;
				double dtSeconds = (bars0.GetTime(toIdx) - bars0.GetTime(fromIdx)).TotalSeconds;
				int barCount = toIdx - fromIdx;
				return dtSeconds > 0 && barCount > 0 ? dtSeconds / barCount : 60;
			}
			catch { return 60; }
		}

		// Pixel X for a point secondsAhead past the last real bar, extrapolated linearly from the
		// last two real bars' time/pixel relationship. Approximate on non-time-based charts (tick,
		// range, etc.), where "seconds" doesn't map cleanly to bar spacing.
		private float GetFutureX(ChartControl chartControl, double secondsAhead)
		{
			try
			{
				var bars0 = BarsArray[0];
				int toIdx = ChartBars.ToIndex;
				int fromIdx = Math.Max(0, toIdx - 10);
				if (toIdx <= fromIdx) return chartControl.CanvasRight - 200f;

				DateTime tTo = bars0.GetTime(toIdx);
				DateTime tFrom = bars0.GetTime(fromIdx);
				double dtSeconds = (tTo - tFrom).TotalSeconds;
				if (dtSeconds <= 0) return chartControl.CanvasRight - 200f;

				float xTo = chartControl.GetXByBarIndex(ChartBars, toIdx);
				float xFrom = chartControl.GetXByBarIndex(ChartBars, fromIdx);
				double pixelsPerSecond = (xTo - xFrom) / dtSeconds;
				return (float)(xTo + pixelsPerSecond * secondsAhead);
			}
			catch { return chartControl.CanvasRight - 200f; }
		}

		/// <summary>
		/// Draws one timeframe group (e.g. the 4 five-minute candles), candles packed tightly
		/// together, and returns the x position where the next group (after its gap) should start.
		/// The group's box always spans <paramref name="slots"/> pitches wide (so e.g. the 1H
		/// group can be forced to match the 15m group's width even with fewer candles), and the
		/// candles it contains are spread evenly across that width. Y positions come straight from
		/// the chart's real price scale — no independent per-group normalization.
		/// </summary>
		private float DrawGroupRealScale(ChartScale cs, List<Candle> candles, string label, float xStart, float pitch, int slots,
			float groupGap, float? tickY, int periodMinutes, double volumeScore)
		{
			if (candles.Count == 0)
				return xStart; // nothing drawn, no gap to add

			double maxHigh = candles.Max(c => c.High);
			double minLow = candles.Min(c => c.Low);
			if (maxHigh <= minLow)
				maxHigh = minLow + TickSize; // avoid a zero-height area on flat data

			float areaTop = cs.GetYByValue(maxHigh);
			float areaBottom = cs.GetYByValue(minLow);

			slots = Math.Max(slots, candles.Count);
			float groupWidth = pitch * slots;
			float candlePitch = groupWidth / candles.Count;

			// countdown to this timeframe's current bar close, hugging the top of this group's candles
			if (ShowTimeRemaining)
			{
				var countdownRect = new RectangleF(xStart, areaTop - 16f, groupWidth, 14f);
				RenderTarget.DrawText(FormatTimeRemaining(periodMinutes), countdownFormat, countdownRect, textBrush);
			}

			// group label + volume-weighted direction score, hugging the bottom of this group's candles
			var labelRect = new RectangleF(xStart, areaBottom + 2f, groupWidth, 14f);
			RenderTarget.DrawText(label, labelFormat, labelRect, textBrush);

			var scoreBrush = volumeScore > 0 ? upBrush : volumeScore < 0 ? downBrush : textBrush;
			var scoreRect = new RectangleF(xStart, areaBottom + 16f, groupWidth, 12f);
			RenderTarget.DrawText(FormatVolumeScore(volumeScore), countdownFormat, scoreRect, scoreBrush);

			float x = xStart;
			foreach (var candle in candles)
			{
				float bodyWidth = Math.Min(pitch, candlePitch) * 0.7f;
				float xCenter = x + candlePitch * 0.5f;

				float yOpen = cs.GetYByValue(candle.Open);
				float yClose = cs.GetYByValue(candle.Close);
				float yHigh = cs.GetYByValue(candle.High);
				float yLow = cs.GetYByValue(candle.Low);

				bool isUp = candle.Close >= candle.Open;
				var bodyBrush = isUp ? upBrush : downBrush;

				// wick
				RenderTarget.DrawLine(new Vector2(xCenter, yHigh), new Vector2(xCenter, yLow), wickBrush, 1f);

				// body
				float bodyTop = Math.Min(yOpen, yClose);
				float bodyBottom = Math.Max(yOpen, yClose);
				if (bodyBottom - bodyTop < 1f)
					bodyBottom = bodyTop + 1f; // ensure a visible sliver for doji bars

				var bodyRect = new RectangleF(xCenter - bodyWidth * 0.5f, bodyTop, bodyWidth, bodyBottom - bodyTop);
				RenderTarget.FillRectangle(bodyRect, bodyBrush);

				x += candlePitch;
			}

			if (tickY.HasValue)
			{
				// small tick only across the current (rightmost) bar, not the whole group
				float lastBarStart = x - candlePitch;
				float tickHalf = Math.Min(pitch, candlePitch) * 0.5f;
				float lastBarCenter = lastBarStart + candlePitch * 0.5f;
				RenderTarget.DrawLine(new Vector2(lastBarCenter - tickHalf, tickY.Value), new Vector2(lastBarCenter + tickHalf, tickY.Value), currentPriceBrush, 2f);
			}

			return xStart + groupWidth + groupGap;
		}

		// ══════════════════════════════════════════════════════════════════
		//  Layout: FixedBox — self-contained box pinned to a screen corner,
		//  each group scaled to its own high/low range.
		// ══════════════════════════════════════════════════════════════════
		private void RenderFixedBox(ChartControl chartControl, ChartScale chartScale, List<Candle> c5, List<Candle> c15, List<Candle> c1h)
		{
			float panelX = PanelCorner == PanelCorner.TopLeft || PanelCorner == PanelCorner.BottomLeft
				? ChartPanel.X + PanelMarginLeft
				: ChartPanel.X + ChartPanel.W - PanelMarginRight - PanelWidth;
			float panelY = PanelCorner == PanelCorner.TopLeft || PanelCorner == PanelCorner.TopRight
				? ChartPanel.Y + PanelMarginTop
				: ChartPanel.Y + ChartPanel.H - PanelMarginBottom - PanelHeight;
			var panelRect = new RectangleF(panelX, panelY, PanelWidth, PanelHeight);

			RenderTarget.FillRectangle(panelRect, bgBrush);
			RenderTarget.DrawRectangle(panelRect, borderBrush, 1f);

			float biasPad = ShowBiasLabel ? 16f : 0f;   // room for the overall bias row at the very bottom
			float topPad = ShowTimeRemaining ? 22f : 8f;    // room for the countdown row above the bars
			float bottomPad = 30f + biasPad;    // room for the "5m/15m/1H" label row + volume-score row + bias row below the bars
			float candleAreaTop = panelY + topPad;
			float candleAreaHeight = PanelHeight - topPad - bottomPad;

			if (ShowBiasLabel)
			{
				Bias bias = ComputeBias(c5, c15, c1h);
				var biasBrush = bias == Bias.Bullish ? upBrush : bias == Bias.Bearish ? downBrush : textBrush;
				string biasText = "BIAS: " + (bias == Bias.Bullish ? "BULLISH" : bias == Bias.Bearish ? "BEARISH" : "MIXED");
				var biasRect = new RectangleF(panelX, panelY + PanelHeight - 16f, PanelWidth, 14f);
				RenderTarget.DrawText(biasText, labelFormat, biasRect, biasBrush);
			}

			// 1H group is sized to match the 15m group's width (same slot count) rather than
			// its own (usually much smaller) candle count.
			int slots5m = Candles5m;
			int slots15m = Candles15m;
			int slots1h = Math.Max(Candles1h, Candles15m);

			int totalCandles = Math.Max(1, slots5m + slots15m + slots1h);
			int groupCount = new[] { c5.Count > 0, c15.Count > 0, c1h.Count > 0 }.Count(b => b);
			float innerPad = 10f;
			const float groupGap = 18f; // gap between timeframe groups (room for outline boxes)
			const float maxPitch = 22f; // cap so bars don't get too fat with few candles
			float gapsWidth = groupGap * Math.Max(0, groupCount - 1);
			float pitch = Math.Min(maxPitch, (PanelWidth - innerPad * 2 - gapsWidth) / totalCandles);
			float xCursor = panelX + innerPad;

			bool showPrice = ShowCurrentPriceLine && currentPriceBrush != null;

			xCursor = DrawGroupFixedBox(c5, "5m", xCursor, pitch, slots5m, candleAreaTop, candleAreaHeight, groupGap, showPrice && c5.Count > 0 ? (double?)c5[c5.Count - 1].Close : null, 5, GroupVolumeScore(c5));
			xCursor = DrawGroupFixedBox(c15, "15m", xCursor, pitch, slots15m, candleAreaTop, candleAreaHeight, groupGap, showPrice && c15.Count > 0 ? (double?)c15[c15.Count - 1].Close : null, 15, GroupVolumeScore(c15));
			xCursor = DrawGroupFixedBox(c1h, "1H", xCursor, pitch, slots1h, candleAreaTop, candleAreaHeight, groupGap, showPrice && c1h.Count > 0 ? (double?)c1h[c1h.Count - 1].Close : null, 60, GroupVolumeScore(c1h));
		}

		private float DrawGroupFixedBox(List<Candle> candles, string label, float xStart, float pitch, int slots,
			float areaTop, float areaHeight, float groupGap, double? currentPrice, int periodMinutes, double volumeScore)
		{
			if (candles.Count == 0)
				return xStart; // nothing drawn, no gap to add

			// Each group is scaled to its own high/low range so all groups fill the same box
			// height regardless of how much the underlying timeframe actually moved.
			double maxHigh = candles.Max(c => c.High);
			double minLow = candles.Min(c => c.Low);
			if (currentPrice.HasValue)
			{
				maxHigh = Math.Max(maxHigh, currentPrice.Value);
				minLow = Math.Min(minLow, currentPrice.Value);
			}
			if (maxHigh <= minLow)
				maxHigh = minLow + 1; // avoid divide-by-zero on flat data

			slots = Math.Max(slots, candles.Count);
			float groupWidth = pitch * slots;
			float candlePitch = groupWidth / candles.Count;

			// countdown to this timeframe's current bar close, centered above the bars
			if (ShowTimeRemaining)
			{
				var countdownRect = new RectangleF(xStart, areaTop - 16f, groupWidth, 14f);
				RenderTarget.DrawText(FormatTimeRemaining(periodMinutes), countdownFormat, countdownRect, textBrush);
			}

			// group label centered below this group's slots
			var labelRect = new RectangleF(xStart, areaTop + areaHeight + 2f, groupWidth, 14f);
			RenderTarget.DrawText(label, labelFormat, labelRect, textBrush);

			// volume-weighted direction score below the label, colored by sign (drives the overall bias vote)
			var scoreBrush = volumeScore > 0 ? upBrush : volumeScore < 0 ? downBrush : textBrush;
			var scoreRect = new RectangleF(xStart, areaTop + areaHeight + 16f, groupWidth, 12f);
			RenderTarget.DrawText(FormatVolumeScore(volumeScore), countdownFormat, scoreRect, scoreBrush);

			// faint outline box around the group so its candles read as one cluster
			const float boxPad = 4f;
			var groupBox = new RectangleF(xStart - boxPad, areaTop - 2f, groupWidth + boxPad * 2f, areaHeight + 4f);
			RenderTarget.DrawRectangle(groupBox, borderBrush, 1f);

			float x = xStart;
			foreach (var candle in candles)
			{
				float bodyWidth = Math.Min(pitch, candlePitch) * 0.7f;
				float xCenter = x + candlePitch * 0.5f;

				float yOpen = PriceToY(candle.Open, minLow, maxHigh, areaTop, areaHeight);
				float yClose = PriceToY(candle.Close, minLow, maxHigh, areaTop, areaHeight);
				float yHigh = PriceToY(candle.High, minLow, maxHigh, areaTop, areaHeight);
				float yLow = PriceToY(candle.Low, minLow, maxHigh, areaTop, areaHeight);

				bool isUp = candle.Close >= candle.Open;
				var bodyBrush = isUp ? upBrush : downBrush;

				// wick
				RenderTarget.DrawLine(new Vector2(xCenter, yHigh), new Vector2(xCenter, yLow), wickBrush, 1f);

				// body
				float bodyTop = Math.Min(yOpen, yClose);
				float bodyBottom = Math.Max(yOpen, yClose);
				if (bodyBottom - bodyTop < 1f)
					bodyBottom = bodyTop + 1f; // ensure a visible sliver for doji bars

				var bodyRect = new RectangleF(xCenter - bodyWidth * 0.5f, bodyTop, bodyWidth, bodyBottom - bodyTop);
				RenderTarget.FillRectangle(bodyRect, bodyBrush);

				x += candlePitch;
			}

			if (currentPrice.HasValue && currentPrice.Value >= minLow && currentPrice.Value <= maxHigh)
			{
				float yPrice = PriceToY(currentPrice.Value, minLow, maxHigh, areaTop, areaHeight);

				// small tick only across the current (rightmost) bar, not the whole group
				float lastBarStart = x - candlePitch;
				float tickHalf = Math.Min(pitch, candlePitch) * 0.5f;
				float lastBarCenter = lastBarStart + candlePitch * 0.5f;
				RenderTarget.DrawLine(new Vector2(lastBarCenter - tickHalf, yPrice), new Vector2(lastBarCenter + tickHalf, yPrice), currentPriceBrush, 2f);
			}

			return xStart + groupWidth + groupGap;
		}

		private static float PriceToY(double price, double minLow, double maxHigh, float areaTop, float areaHeight)
		{
			double range = maxHigh - minLow;
			double frac = (price - minLow) / range; // 0 = bottom, 1 = top
			return (float)(areaTop + (1.0 - frac) * areaHeight);
		}

		/// <summary>
		/// Time remaining until the current bar of a fixed-period (period-minutes) series closes,
		/// assuming standard bars aligned to clock boundaries from midnight.
		/// </summary>
		private static string FormatTimeRemaining(int periodMinutes)
		{
			DateTime now = Core.Globals.Now;
			double elapsedMinutes = (now - now.Date).TotalMinutes;
			double nextBoundaryMinutes = Math.Ceiling(elapsedMinutes / periodMinutes) * periodMinutes;
			DateTime nextClose = now.Date.AddMinutes(nextBoundaryMinutes);
			TimeSpan remaining = nextClose - now;
			if (remaining < TimeSpan.Zero)
				remaining = TimeSpan.Zero;

			int totalSeconds = (int)remaining.TotalSeconds;
			return string.Format("{0}:{1:00}", totalSeconds / 60, totalSeconds % 60);
		}

		/// <summary>
		/// Volume-weighted direction score for one timeframe group:
		/// sum of each candle's volume, signed by that candle's up/down direction.
		/// Positive = net buying volume, negative = net selling volume.
		/// </summary>
		private static double GroupVolumeScore(List<Candle> candles)
		{
			double score = 0;
			foreach (var c in candles)
				score += c.Volume * (c.Close >= c.Open ? 1 : -1);
			return score;
		}

		/// <summary>
		/// Formats a volume score as a signed, abbreviated number, e.g. +12.3K, -4.5M, +0.
		/// </summary>
		private static string FormatVolumeScore(double score)
		{
			double abs = Math.Abs(score);
			string magnitude = abs >= 1_000_000 ? (abs / 1_000_000).ToString("0.0") + "M"
				: abs >= 1_000 ? (abs / 1_000).ToString("0.0") + "K"
				: abs.ToString("0");
			return (score > 0 ? "+" : score < 0 ? "-" : "") + magnitude;
		}

		/// <summary>
		/// Overall bias from majority vote of the three timeframe groups' volume-weighted
		/// direction scores. Empty groups don't get a vote. Ties/no-data fall back to Mixed.
		/// </summary>
		private static Bias ComputeBias(List<Candle> c5, List<Candle> c15, List<Candle> c1h)
		{
			int bullish = 0, bearish = 0;
			foreach (var group in new[] { c5, c15, c1h })
			{
				if (group.Count == 0)
					continue;
				double score = GroupVolumeScore(group);
				if (score > 0) bullish++;
				else if (score < 0) bearish++;
			}

			if (bullish > bearish) return Bias.Bullish;
			if (bearish > bullish) return Bias.Bearish;
			return Bias.Mixed;
		}

		/// <summary>
		/// Reads the last <paramref name="count"/> closed bars (oldest first) from the
		/// given BarsArray index, e.g. index 1 = the 5-minute series added in Configure.
		/// Stops walking backward as soon as a bar from a prior day is hit, so the panel
		/// never shows candles from before today's session.
		/// </summary>
		private List<Candle> CollectCandles(int seriesIndex, int count)
		{
			var list = new List<Candle>();
			if (count <= 0 || CurrentBars.Length <= seriesIndex || CurrentBars[seriesIndex] < 0)
				return list;

			int lastIdx = CurrentBars[seriesIndex];
			var bars = BarsArray[seriesIndex];
			DateTime today = Core.Globals.Now.Date;

			for (int absIdx = lastIdx; absIdx >= 0 && list.Count < count; absIdx--)
			{
				DateTime barTime;
				try
				{
					barTime = bars.GetTime(absIdx);
				}
				catch (Exception)
				{
					break; // bar not yet materialized at this absolute index
				}

				if (barTime.Date != today)
					break; // walked back into a prior session

				try
				{
					list.Add(new Candle
					{
						Open = bars.GetOpen(absIdx),
						High = bars.GetHigh(absIdx),
						Low = bars.GetLow(absIdx),
						Close = bars.GetClose(absIdx),
						Volume = bars.GetVolume(absIdx),
						Time = barTime
					});
				}
				catch (Exception)
				{
					break;
				}
			}

			list.Reverse(); // oldest first
			return list;
		}

		// ══════════════════════════════════════════════════════════════════
		//  WPF top menu — layout switch, master show/hide, per-feature toggles
		// ══════════════════════════════════════════════════════════════════
		private static System.Windows.Shapes.Rectangle MakeGutterIcon() =>
			new System.Windows.Shapes.Rectangle { Width = 16, Height = 16, Fill = System.Windows.Media.Brushes.Black };

		private NTMenuItem MakeItem(string header) =>
			new NTMenuItem { Header = header, StaysOpenOnClick = true, Background = System.Windows.Media.Brushes.Black, Foreground = System.Windows.Media.Brushes.WhiteSmoke, Icon = MakeGutterIcon() };

		protected void CreateWPFControls()
		{
			try
			{
				chartWindow = System.Windows.Window.GetWindow(ChartControl.Parent) as NinjaTrader.Gui.Chart.Chart;
				if (chartWindow == null) return;

				mainMenuItemStyle = Application.Current.TryFindResource("MainMenuItem") as System.Windows.Style;
				systemMenuStyle = Application.Current.TryFindResource("SystemMenuStyle") as System.Windows.Style;
				if (mainMenuItemStyle == null || systemMenuStyle == null) return;

				ntBarMenu = new Menu
				{
					VerticalAlignment = VerticalAlignment.Top,
					VerticalContentAlignment = VerticalAlignment.Top,
					Style = systemMenuStyle
				};

				ntBartopMenuItem = new NTMenuItem
				{
					Header = "ICN_CandlePanel",
					Margin = new Thickness(0),
					Padding = new Thickness(1),
					Style = mainMenuItemStyle,
					VerticalAlignment = VerticalAlignment.Center,
				};
				ntBarMenu.Items.Add(ntBartopMenuItem);

				ntShowHide = MakeItem(ShowPanel ? "Hide Panel" : "Show Panel"); ntShowHide.Tag = "ShowPanel";
				ntShowHide.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntShowHide);
				ntBartopMenuItem.Items.Add(new Separator());

				ntLayoutItem = MakeItem(LayoutSwitchLabel()); ntLayoutItem.Tag = "ToggleLayout";
				ntLayoutItem.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntLayoutItem);

				ntCornerItem = MakeItem("Corner: " + PanelCorner + " (click to cycle)"); ntCornerItem.Tag = "CycleCorner";
				ntCornerItem.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntCornerItem);
				ntBartopMenuItem.Items.Add(new Separator());

				ntShow5mItem = MakeItem(Show5mColumn ? "Hide 5m" : "Show 5m"); ntShow5mItem.Tag = "Show5m";
				ntShow15mItem = MakeItem(Show15mColumn ? "Hide 15m" : "Show 15m"); ntShow15mItem.Tag = "Show15m";
				ntShow1hItem = MakeItem(Show1hColumn ? "Hide 1H" : "Show 1H"); ntShow1hItem.Tag = "Show1h";
				ntShow5mItem.Click += NTBarMenu_Click;
				ntShow15mItem.Click += NTBarMenu_Click;
				ntShow1hItem.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntShow5mItem);
				ntBartopMenuItem.Items.Add(ntShow15mItem);
				ntBartopMenuItem.Items.Add(ntShow1hItem);
				ntBartopMenuItem.Items.Add(new Separator());

				ntBiasItem = MakeItem(ShowBiasLabel ? "Hide Bias Label" : "Show Bias Label"); ntBiasItem.Tag = "ShowBiasLabel";
				ntTimeRemainingItem = MakeItem(ShowTimeRemaining ? "Hide Time Remaining" : "Show Time Remaining"); ntTimeRemainingItem.Tag = "ShowTimeRemaining";
				ntPriceLineItem = MakeItem(ShowCurrentPriceLine ? "Hide Current Price Line" : "Show Current Price Line"); ntPriceLineItem.Tag = "ShowCurrentPriceLine";
				ntBiasItem.Click += NTBarMenu_Click;
				ntTimeRemainingItem.Click += NTBarMenu_Click;
				ntPriceLineItem.Click += NTBarMenu_Click;
				ntBartopMenuItem.Items.Add(ntBiasItem);
				ntBartopMenuItem.Items.Add(ntTimeRemainingItem);
				ntBartopMenuItem.Items.Add(ntPriceLineItem);

				if (TabSelected()) ShowWPFControls();
				chartWindow.MainTabControl.SelectionChanged += TabChangedHandler;
			}
			catch (Exception ex) { Print(ex); }
		}

		private string LayoutSwitchLabel() =>
			Layout == CandlePanelLayout.RealScaleFuture ? "Switch to Fixed Box layout" : "Switch to Real Scale layout";

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
					case "ShowPanel": ShowPanel = !ShowPanel; break;
					case "ToggleLayout":
						Layout = Layout == CandlePanelLayout.RealScaleFuture ? CandlePanelLayout.FixedBox : CandlePanelLayout.RealScaleFuture;
						break;
					case "CycleCorner":
						PanelCorner = (PanelCorner)(((int)PanelCorner + 1) % 4);
						break;
					case "Show5m": Show5mColumn = !Show5mColumn; break;
					case "Show15m": Show15mColumn = !Show15mColumn; break;
					case "Show1h": Show1hColumn = !Show1hColumn; break;
					case "ShowBiasLabel": ShowBiasLabel = !ShowBiasLabel; break;
					case "ShowTimeRemaining": ShowTimeRemaining = !ShowTimeRemaining; break;
					case "ShowCurrentPriceLine": ShowCurrentPriceLine = !ShowCurrentPriceLine; break;
				}
			}
			catch (Exception ex) { Print("ICNCandlePanel menu error: " + ex.Message); }
			finally
			{
				try
				{
					ntShowHide.Header = ShowPanel ? "Hide Panel" : "Show Panel";
					ntLayoutItem.Header = LayoutSwitchLabel();
					ntCornerItem.Header = "Corner: " + PanelCorner + " (click to cycle)";
					ntShow5mItem.Header = Show5mColumn ? "Hide 5m" : "Show 5m";
					ntShow15mItem.Header = Show15mColumn ? "Hide 15m" : "Show 15m";
					ntShow1hItem.Header = Show1hColumn ? "Hide 1H" : "Show 1H";
					ntBiasItem.Header = ShowBiasLabel ? "Hide Bias Label" : "Show Bias Label";
					ntTimeRemainingItem.Header = ShowTimeRemaining ? "Hide Time Remaining" : "Show Time Remaining";
					ntPriceLineItem.Header = ShowCurrentPriceLine ? "Hide Current Price Line" : "Show Current Price Line";

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
		[Display(Name = "Show panel", Description = "Master show/hide for the whole indicator.", GroupName = "Parameters", Order = -1)]
		public bool ShowPanel { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Layout", Description = "FixedBox = self-contained box pinned to a corner. RealScaleFuture = drawn on the chart's real price scale, past the last bar.", GroupName = "Parameters", Order = 0)]
		public CandlePanelLayout Layout { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 5m column", GroupName = "Parameters", Order = 10)]
		public bool Show5mColumn { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 15m column", GroupName = "Parameters", Order = 11)]
		public bool Show15mColumn { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show 1H column", GroupName = "Parameters", Order = 12)]
		public bool Show1hColumn { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "5m candles to show", GroupName = "Parameters", Order = 1)]
		public int Candles5m { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "15m candles to show", GroupName = "Parameters", Order = 2)]
		public int Candles15m { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "1H candles to show", GroupName = "Parameters", Order = 3)]
		public int Candles1h { get; set; }

		[NinjaScriptProperty]
		[Range(6, 30)]
		[Display(Name = "Candle pitch (px)", Description = "Pixel width of each mini-candle slot. RealScaleFuture layout only.", GroupName = "Parameters", Order = 4)]
		public float CandlePitchPx { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Future gap (bars)", Description = "How many primary-chart bar-widths of gap to leave between the last real bar and the first mini-candle column. RealScaleFuture layout only.", GroupName = "Parameters", Order = 5)]
		public int FutureGapBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 500)]
		[Display(Name = "Extra margin (px)", Description = "Additional fixed pixel margin added on top of the Future gap, so spacing stays constant regardless of zoom. RealScaleFuture layout only.", GroupName = "Parameters", Order = 6)]
		public float MarginPx { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show current price line", GroupName = "Parameters", Order = 7)]
		public bool ShowCurrentPriceLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show time remaining", GroupName = "Parameters", Order = 8)]
		public bool ShowTimeRemaining { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show bias label", GroupName = "Parameters", Order = 9)]
		public bool ShowBiasLabel { get; set; }

		[NinjaScriptProperty]
		[Range(100, 600)]
		[Display(Name = "Panel width (px)", GroupName = "Fixed Box Layout", Order = 1)]
		public float PanelWidth { get; set; }

		[NinjaScriptProperty]
		[Range(100, 600)]
		[Display(Name = "Panel height (px)", GroupName = "Fixed Box Layout", Order = 2)]
		public float PanelHeight { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Panel corner", GroupName = "Fixed Box Layout", Order = 3)]
		public PanelCorner PanelCorner { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from right (px)", GroupName = "Fixed Box Layout", Order = 4)]
		public float PanelMarginRight { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from left (px)", GroupName = "Fixed Box Layout", Order = 5)]
		public float PanelMarginLeft { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from top (px)", GroupName = "Fixed Box Layout", Order = 6)]
		public float PanelMarginTop { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from bottom (px)", GroupName = "Fixed Box Layout", Order = 7)]
		public float PanelMarginBottom { get; set; }

		[XmlIgnore]
		[Display(Name = "Panel background", GroupName = "Fixed Box Layout", Order = 8)]
		public System.Windows.Media.Brush PanelBackgroundBrush { get; set; }
		[Browsable(false)]
		public string PanelBackgroundBrushSerializable
		{
			get { return Serialize.BrushToString(PanelBackgroundBrush); }
			set { PanelBackgroundBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Panel border", GroupName = "Fixed Box Layout", Order = 9)]
		public System.Windows.Media.Brush PanelBorderBrush { get; set; }
		[Browsable(false)]
		public string PanelBorderBrushSerializable
		{
			get { return Serialize.BrushToString(PanelBorderBrush); }
			set { PanelBorderBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Up candle color", GroupName = "Colors", Order = 1)]
		public System.Windows.Media.Brush UpBrush { get; set; }
		[Browsable(false)]
		public string UpBrushSerializable
		{
			get { return Serialize.BrushToString(UpBrush); }
			set { UpBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Down candle color", GroupName = "Colors", Order = 2)]
		public System.Windows.Media.Brush DownBrush { get; set; }
		[Browsable(false)]
		public string DownBrushSerializable
		{
			get { return Serialize.BrushToString(DownBrush); }
			set { DownBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Text color", GroupName = "Colors", Order = 3)]
		public System.Windows.Media.Brush TextColorBrush { get; set; }
		[Browsable(false)]
		public string TextColorBrushSerializable
		{
			get { return Serialize.BrushToString(TextColorBrush); }
			set { TextColorBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Current price line color", GroupName = "Colors", Order = 4)]
		public System.Windows.Media.Brush CurrentPriceLineBrush { get; set; }
		[Browsable(false)]
		public string CurrentPriceLineBrushSerializable
		{
			get { return Serialize.BrushToString(CurrentPriceLineBrush); }
			set { CurrentPriceLineBrush = Serialize.StringToBrush(value); }
		}

		#endregion
	}
}

