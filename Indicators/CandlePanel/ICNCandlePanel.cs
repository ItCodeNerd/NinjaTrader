#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript.Indicators.ItCodeNerd;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
using Brush = SharpDX.Direct2D1.Brush;
using FontStyle = SharpDX.DirectWrite.FontStyle;
using FontWeight = SharpDX.DirectWrite.FontWeight;
using RectangleF = SharpDX.RectangleF;
using TextFormat = SharpDX.DirectWrite.TextFormat;
#endregion

// This namespace holds indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{
	public enum PanelCorner
	{
		TopLeft,
		TopRight,
		BottomLeft,
		BottomRight
	}

	/// <summary>
	/// Draws a fixed on-screen panel showing the last N 5-minute, 15-minute and 1-hour
	/// candles, completely independent of the timeframe the chart itself is set to.
	/// Also optionally plots a support and resistance line based on a recent swing
	/// high/low on the chart's own timeframe.
	///
	/// Data series indices (set up in State.Configure):
	///   BarsArray[0] = the chart's native series (used only to trigger OnBarUpdate / for S&R)
	///   BarsArray[1] = 5 minute bars
	///   BarsArray[2] = 15 minute bars
	///   BarsArray[3] = 60 minute (1 hour) bars
	/// </summary>
	public class ICNCandlePanel : Indicator
	{
		private struct Candle
		{
			public double Open, High, Low, Close;
			public DateTime Time;
		}

		// SharpDX resources (created/disposed in OnRenderTargetChanged)
		private Brush upBrush;
		private Brush downBrush;
		private Brush wickBrush;
		private Brush textBrush;
		private Brush bgBrush;
		private Brush borderBrush;
		private Brush srBrush;
		private Brush currentPriceBrush;
		private TextFormat labelFormat;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Fixed panel showing the last N 5m / 15m / 1H candles regardless of chart timeframe, plus optional S/R lines.";
				Name = "ICNCandlePanel";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				IsSuspendedWhileInactive = false;

				// ---- user-configurable defaults ----
				Candles5m = 5;
				Candles15m = 4;
				Candles1h = 1;
				ShowSRLines = true;
				SRLookbackBars = 40;
				PanelWidth = 230;
				PanelHeight = 260;
				PanelCorner = PanelCorner.TopRight;
				PanelMarginRight = 15;
				PanelMarginLeft = 15;
				PanelMarginTop = 60;
				PanelMarginBottom = 15;

				UpBrush = System.Windows.Media.Brushes.SeaGreen;
				DownBrush = System.Windows.Media.Brushes.IndianRed;
				PanelBackgroundBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 20, 25, 35));
				PanelBorderBrush = System.Windows.Media.Brushes.Gray;
				SRLineBrush = System.Windows.Media.Brushes.DodgerBlue;
				TextColorBrush = System.Windows.Media.Brushes.White;
				ShowCurrentPriceLine = true;
				CurrentPriceLineBrush = System.Windows.Media.Brushes.Yellow;
			}
			else if (State == State.Configure)
			{
				// Add the three fixed higher-timeframe series regardless of the chart's own period
				AddDataSeries(BarsPeriodType.Minute, 5);   // BarsArray[1]
				AddDataSeries(BarsPeriodType.Minute, 15);  // BarsArray[2]
				AddDataSeries(BarsPeriodType.Minute, 60);  // BarsArray[3]
			}
			else if (State == State.Terminated)
			{
				DisposeBrushes();
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
			srBrush = ToBrush(SRLineBrush);
			currentPriceBrush = ToBrush(CurrentPriceLineBrush);

			labelFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontWeight.Normal, FontStyle.Normal, 11f)
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
			srBrush?.Dispose(); srBrush = null;
			currentPriceBrush?.Dispose(); currentPriceBrush = null;
			dashedStrokeStyle?.Dispose(); dashedStrokeStyle = null;
			labelFormat?.Dispose(); labelFormat = null;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (upBrush == null || State < State.Historical)
				return;

			// ---------- gather candle data from the three fixed series ----------
			// Each series always shows its own last N bars relative to the current bar,
			// regardless of hour boundaries.
			var c1h = CollectCandles(3, Candles1h);
			var c5 = CollectCandles(1, Candles5m);
			var c15 = CollectCandles(2, Candles15m);

			// ---------- optional support / resistance lines (based on chart series) ----------
			if (ShowSRLines && CurrentBar >= SRLookbackBars)
			{
				try
				{
					double resistance = MAX(High, SRLookbackBars)[0];
					double support = MIN(Low, SRLookbackBars)[0];

					float yRes = chartScale.GetYByValue(resistance);
					float ySup = chartScale.GetYByValue(support);
					float xLeft = ChartPanel.X;
					float xRight = ChartPanel.X + ChartPanel.W;

					RenderTarget.DrawLine(new Vector2(xLeft, yRes), new Vector2(xRight, yRes), srBrush, 1.25f);
					RenderTarget.DrawLine(new Vector2(xLeft, ySup), new Vector2(xRight, ySup), srBrush, 1.25f);
				}
				catch (Exception)
				{
					// stale read during cross-thread render; skip SR lines this pass
				}
			}

			if (c5.Count == 0 && c15.Count == 0 && c1h.Count == 0)
				return;

			// ---------- panel background ----------
			float panelX = PanelCorner == PanelCorner.TopLeft || PanelCorner == PanelCorner.BottomLeft
				? ChartPanel.X + PanelMarginLeft
				: ChartPanel.X + ChartPanel.W - PanelMarginRight - PanelWidth;
			float panelY = PanelCorner == PanelCorner.TopLeft || PanelCorner == PanelCorner.TopRight
				? ChartPanel.Y + PanelMarginTop
				: ChartPanel.Y + ChartPanel.H - PanelMarginBottom - PanelHeight;
			var panelRect = new RectangleF(panelX, panelY, PanelWidth, PanelHeight);

			RenderTarget.FillRectangle(panelRect, bgBrush);
			RenderTarget.DrawRectangle(panelRect, borderBrush, 1f);

			const float topPad = 18f;    // room for the "5m/15m/1H" label row
			const float bottomPad = 8f;
			float candleAreaTop = panelY + topPad;
			float candleAreaHeight = PanelHeight - topPad - bottomPad;

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

			xCursor = DrawGroup(c5, "5m", xCursor, pitch, slots5m, candleAreaTop, candleAreaHeight, groupGap);
			xCursor = DrawGroup(c15, "15m", xCursor, pitch, slots15m, candleAreaTop, candleAreaHeight, groupGap);
			xCursor = DrawGroup(c1h, "1H", xCursor, pitch, slots1h, candleAreaTop, candleAreaHeight, groupGap);

			// ---------- current price line across all panel groups ----------
			var all = new List<Candle>();
			all.AddRange(c5); all.AddRange(c15); all.AddRange(c1h);
			double maxHigh = all.Max(c => c.High);
			double minLow = all.Min(c => c.Low);
			if (maxHigh <= minLow)
				maxHigh = minLow + 1; // avoid divide-by-zero on flat data

			try
			{
				double currentPrice = Close[0];
				bool inRange = currentPrice >= minLow && currentPrice <= maxHigh;
				Print($"[ICNCandlePanel] priceline ShowCurrentPriceLine={ShowCurrentPriceLine} currentPrice={currentPrice} minLow={minLow} maxHigh={maxHigh} inRange={inRange} currentPriceBrush={(currentPriceBrush == null ? "NULL" : "ok")}");

				if (ShowCurrentPriceLine && CurrentBar >= 0 && inRange && currentPriceBrush != null)
				{
					float yPrice = PriceToY(currentPrice, minLow, maxHigh, candleAreaTop, candleAreaHeight);
					float lineLeft = panelX + innerPad * 0.25f;
					float lineRight = panelX + PanelWidth - innerPad * 0.25f;

					RenderTarget.DrawLine(new Vector2(lineLeft, yPrice), new Vector2(lineRight, yPrice), currentPriceBrush, 1f, GetDashedStrokeStyle());

					var priceLabelRect = new RectangleF(lineLeft, yPrice - 7f, lineRight - lineLeft, 14f);
					RenderTarget.DrawText(currentPrice.ToString("F2"), labelFormat, priceLabelRect, currentPriceBrush);
				}
			}
			catch (Exception ex)
			{
				Print($"[ICNCandlePanel] priceline EXCEPTION: {ex}");
			}
		}

		private SharpDX.Direct2D1.StrokeStyle dashedStrokeStyle;
		private SharpDX.Direct2D1.StrokeStyle GetDashedStrokeStyle()
		{
			if (dashedStrokeStyle == null || dashedStrokeStyle.IsDisposed)
			{
				dashedStrokeStyle = new SharpDX.Direct2D1.StrokeStyle(RenderTarget.Factory,
					new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
			}
			return dashedStrokeStyle;
		}

		/// <summary>
		/// Draws one timeframe group (e.g. the 4 five-minute candles), candles packed tightly
		/// together, and returns the x position where the next group (after its gap) should start.
		/// The group's box always spans <paramref name="slots"/> pitches wide (so e.g. the 1H
		/// group can be forced to match the 15m group's width even with fewer candles), and the
		/// candles it contains are spread evenly across that width.
		/// </summary>
		private float DrawGroup(List<Candle> candles, string label, float xStart, float pitch, int slots,
			float areaTop, float areaHeight, float groupGap)
		{
			if (candles.Count == 0)
				return xStart; // nothing drawn, no gap to add

			// Each group is scaled to its own high/low range so all groups fill the same box
			// height regardless of how much the underlying timeframe actually moved.
			double maxHigh = candles.Max(c => c.High);
			double minLow = candles.Min(c => c.Low);
			if (maxHigh <= minLow)
				maxHigh = minLow + 1; // avoid divide-by-zero on flat data

			slots = Math.Max(slots, candles.Count);
			float groupWidth = pitch * slots;
			float candlePitch = groupWidth / candles.Count;

			// group label centered above this group's slots
			var labelRect = new RectangleF(xStart, areaTop - 16f, groupWidth, 14f);
			RenderTarget.DrawText(label, labelFormat, labelRect, textBrush);

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

			return xStart + groupWidth + groupGap;
		}

		private static float PriceToY(double price, double minLow, double maxHigh, float areaTop, float areaHeight)
		{
			double range = maxHigh - minLow;
			double frac = (price - minLow) / range; // 0 = bottom, 1 = top
			return (float)(areaTop + (1.0 - frac) * areaHeight);
		}

		/// <summary>
		/// Reads the last <paramref name="count"/> closed bars (oldest first) from the
		/// given BarsArray index, e.g. index 1 = the 5-minute series added in Configure.
		/// </summary>
		private List<Candle> CollectCandles(int seriesIndex, int count)
		{
			var list = new List<Candle>();
			if (count <= 0 || CurrentBars.Length <= seriesIndex || CurrentBars[seriesIndex] < 0)
				return list;

			int lastIdx = CurrentBars[seriesIndex];
			int available = Math.Min(count, lastIdx + 1);
			var bars = BarsArray[seriesIndex];
			for (int absIdx = lastIdx - available + 1; absIdx <= lastIdx; absIdx++)
			{
				try
				{
					list.Add(new Candle
					{
						Open = bars.GetOpen(absIdx),
						High = bars.GetHigh(absIdx),
						Low = bars.GetLow(absIdx),
						Close = bars.GetClose(absIdx),
						Time = bars.GetTime(absIdx)
					});
				}
				catch (Exception)
				{
					// Bar not yet materialized at this absolute index; skip it.
				}
			}
			return list;
		}

		#region Properties

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
		[Display(Name = "Show support/resistance lines", GroupName = "Parameters", Order = 4)]
		public bool ShowSRLines { get; set; }

		[NinjaScriptProperty]
		[Range(2, 500)]
		[Display(Name = "S/R lookback (bars)", GroupName = "Parameters", Order = 5)]
		public int SRLookbackBars { get; set; }

		[NinjaScriptProperty]
		[Range(100, 600)]
		[Display(Name = "Panel width (px)", GroupName = "Panel Layout", Order = 1)]
		public float PanelWidth { get; set; }

		[NinjaScriptProperty]
		[Range(100, 600)]
		[Display(Name = "Panel height (px)", GroupName = "Panel Layout", Order = 2)]
		public float PanelHeight { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Panel corner", GroupName = "Panel Layout", Order = 3)]
		public PanelCorner PanelCorner { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from right (px)", GroupName = "Panel Layout", Order = 4)]
		public float PanelMarginRight { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from left (px)", GroupName = "Panel Layout", Order = 5)]
		public float PanelMarginLeft { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from top (px)", GroupName = "Panel Layout", Order = 6)]
		public float PanelMarginTop { get; set; }

		[NinjaScriptProperty]
		[Range(0, 400)]
		[Display(Name = "Panel margin from bottom (px)", GroupName = "Panel Layout", Order = 7)]
		public float PanelMarginBottom { get; set; }

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
		[Display(Name = "Panel background", GroupName = "Colors", Order = 3)]
		public System.Windows.Media.Brush PanelBackgroundBrush { get; set; }
		[Browsable(false)]
		public string PanelBackgroundBrushSerializable
		{
			get { return Serialize.BrushToString(PanelBackgroundBrush); }
			set { PanelBackgroundBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Panel border", GroupName = "Colors", Order = 4)]
		public System.Windows.Media.Brush PanelBorderBrush { get; set; }
		[Browsable(false)]
		public string PanelBorderBrushSerializable
		{
			get { return Serialize.BrushToString(PanelBorderBrush); }
			set { PanelBorderBrush = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Show current price line", GroupName = "Parameters", Order = 6)]
		public bool ShowCurrentPriceLine { get; set; }

		[XmlIgnore]
		[Display(Name = "Support/resistance line color", GroupName = "Colors", Order = 5)]
		public System.Windows.Media.Brush SRLineBrush { get; set; }
		[Browsable(false)]
		public string SRLineBrushSerializable
		{
			get { return Serialize.BrushToString(SRLineBrush); }
			set { SRLineBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Text color", GroupName = "Colors", Order = 6)]
		public System.Windows.Media.Brush TextColorBrush { get; set; }
		[Browsable(false)]
		public string TextColorBrushSerializable
		{
			get { return Serialize.BrushToString(TextColorBrush); }
			set { TextColorBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Current price line color", GroupName = "Colors", Order = 7)]
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
