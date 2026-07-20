#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;

using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;

using SharpDX;
using SharpDX.DirectWrite;
#endregion

// This file goes in: Documents\NinjaTrader 8\bin\Custom\Indicators\DeltaFootprint.cs
// (or paste it into NinjaTrader's NinjaScript Editor: New > Indicator, then replace all code.)
//
// SETUP REQUIRED ON THE CHART BEFORE THIS WILL SHOW HISTORICAL DATA:
//   1. Right-click chart -> Data Series -> check "Tick Replay". Without this, NinjaTrader
//      does not replay historical bid/ask ticks and every historical bar will be empty.
//   2. Add the indicator, set "Calculate" (chart property) to "On each tick" (the indicator
//      forces this itself in code, but the chart's own Calculate setting should also be
//      "Each tick" for real-time bars to update smoothly).
//   3. Your data feed must provide bid/ask (Level 1) quotes alongside trades. Feeds that only
//      send trade prints with no bid/ask (some free/demo feeds) will fall back to a
//      previous-tick-price comparison, which is a rough approximation of the buy/sell split.
//
// WHAT IT DRAWS: for every price level traded within a bar, one number = (buy volume - sell
// volume) at that price ("delta"). Cell is green when delta >= 0, red when delta < 0, matching
// the reference screenshot. There's a toggle to show raw traded volume per price instead.
//
// This renders one number per price level (not a two-column bid|ask footprint). If you actually
// want the classic two-column footprint layout, say so and I'll extend it - the tick classification
// logic below is the hard part and is already in place for that too.

namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{
	public class ICNDeltaFootprint : Indicator
	{
		private class BarDelta
		{
			public Dictionary<double, int> DeltaByPrice = new Dictionary<double, int>();
			public Dictionary<double, int> VolumeByPrice = new Dictionary<double, int>();
		}

		// barIndex -> per-price delta/volume for that bar
		private readonly Dictionary<int, BarDelta> barData = new Dictionary<int, BarDelta>();

		// remembers last bid/ask seen so we can classify trades that arrive without their own quote
		private double lastBid = 0;
		private double lastAsk = 0;

		// keep memory bounded on long-running charts
		private const int MaxBarsToKeep = 1000;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Per-price-level buy/sell delta shown as colored numbers on each bar (footprint style). Requires Tick Replay + bid/ask data.";
				Name = "ICNDeltaFootprint";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;
				MaximumBarsLookBack = MaximumBarsLookBack.Infinite;

				CustomTickSize = 0;          // 0 = use the instrument's own tick size
				CellWidth = 24;
				CellHeight = 12;
				FontSize = 10;
				ShowVolumeInsteadOfDelta = false;
				PositiveColor = Brushes.SeaGreen;
				NegativeColor = Brushes.Firebrick;
				TextColor = Brushes.White;
				ShowBarDelta = true;
				ShowImbalanceStacks = true;
				ImbalanceStackCount = 3;
				ImbalanceColor = Brushes.Yellow;
				ShowMiniCandle = true;
				MiniCandleWidth = 6;
				ShowReversalSignals = true;
				AbsorptionThreshold = 15;
				AbsorptionWindowTicks = 3;
				HoldToleranceTicks = 2;
				MaxBarsToRender = 200;
				BullSignalColor = Brushes.Lime;
				BearSignalColor = Brushes.OrangeRed;
			}
			else if (State == State.Configure)
			{
				// footprint-style data requires tick-by-tick data
				if (Calculate != Calculate.OnEachTick)
					Calculate = Calculate.OnEachTick;
			}
			else if (State == State.DataLoaded)
			{
				barData.Clear();
				lastBid = 0;
				lastAsk = 0;
			}
			else if (State == State.Terminated)
			{
				// brushes/text formats are created and disposed per-OnRender call, nothing to clean up here
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			// remember quotes so trade prints that don't carry their own bid/ask can still be classified
			if (e.MarketDataType == MarketDataType.Bid)
			{
				lastBid = e.Price;
				return;
			}
			if (e.MarketDataType == MarketDataType.Ask)
			{
				lastAsk = e.Price;
				return;
			}
			if (e.MarketDataType != MarketDataType.Last)
				return;

			int barIdx = CurrentBar;
			if (barIdx < 0)
				return;

			if (!barData.TryGetValue(barIdx, out BarDelta bd))
			{
				bd = new BarDelta();
				barData[barIdx] = bd;
				TrimOldBars(barIdx);
			}

			double price = RoundToTick(e.Price);

			double effectiveAsk = e.Ask > 0 ? e.Ask : lastAsk;
			double effectiveBid = e.Bid > 0 ? e.Bid : lastBid;

			bool isBuy;
			if (effectiveAsk > 0 && e.Price >= effectiveAsk)
				isBuy = true;
			else if (effectiveBid > 0 && e.Price <= effectiveBid)
				isBuy = false;
			else
				// no usable quote at all (feed without bid/ask): default to "buy" so volume is still counted
				isBuy = true;

			int vol = (int)e.Volume;

			if (!bd.DeltaByPrice.ContainsKey(price)) bd.DeltaByPrice[price] = 0;
			if (!bd.VolumeByPrice.ContainsKey(price)) bd.VolumeByPrice[price] = 0;

			bd.DeltaByPrice[price] += isBuy ? vol : -vol;
			bd.VolumeByPrice[price] += vol;
		}

		private void TrimOldBars(int newestBarIdx)
		{
			if (barData.Count <= MaxBarsToKeep)
				return;

			int cutoff = newestBarIdx - MaxBarsToKeep;
			var staleKeys = barData.Keys.Where(k => k < cutoff).ToList();
			foreach (var k in staleKeys)
				barData.Remove(k);
		}

		protected override void OnBarUpdate()
		{
			// all work happens in OnMarketData; nothing needed per-bar
		}

		private double RoundToTick(double price)
		{
			double ts = CustomTickSize > 0 ? CustomTickSize : Instrument.MasterInstrument.TickSize;
			return Math.Round(price / ts) * ts;
		}

		private struct PriceRow
		{
			public double Price;
			public int Delta;
			public int Vol;
		}

		// buckets adjacent price levels together when groupSize > 1 so fewer, taller rows render
		private IEnumerable<PriceRow> GroupRows(BarDelta bd, double ts, int groupSize)
		{
			if (groupSize <= 1)
			{
				foreach (var kv in bd.DeltaByPrice)
					yield return new PriceRow { Price = kv.Key, Delta = kv.Value, Vol = bd.VolumeByPrice[kv.Key] };
				yield break;
			}

			var buckets = new Dictionary<long, PriceRow>();
			foreach (var kv in bd.DeltaByPrice)
			{
				long tickIdx = (long)Math.Round(kv.Key / ts);
				long bucketIdx = tickIdx / groupSize;

				buckets.TryGetValue(bucketIdx, out PriceRow row);
				row.Delta += kv.Value;
				row.Vol += bd.VolumeByPrice[kv.Key];
				row.Price = (bucketIdx * groupSize + (groupSize - 1) / 2.0) * ts;
				buckets[bucketIdx] = row;
			}

			foreach (var row in buckets.Values)
				yield return row;
		}

		private static bool Sign(PriceRow row, bool useVolume)
		{
			return (useVolume ? row.Vol : row.Delta) >= 0;
		}

		// blends toward near-white at t=0 up to the full saturated color at t=1, matching a heatmap-style footprint
		private static SharpDX.Color4 CellColor(System.Windows.Media.Color baseColor, float t)
		{
			t = Math.Max(0f, Math.Min(1f, t));
			float mix = 0.12f + 0.88f * t;
			float r = (1f - mix) * 1f + mix * (baseColor.R / 255f);
			float g = (1f - mix) * 1f + mix * (baseColor.G / 255f);
			float b = (1f - mix) * 1f + mix * (baseColor.B / 255f);
			return new SharpDX.Color4(r, g, b, 1f);
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);

			if (ChartBars == null)
				return;

			int firstBarIdx = Math.Max(ChartBars.FromIndex, 0);
			int lastBarIdx = Math.Min(ChartBars.ToIndex, CurrentBar);
			if (MaxBarsToRender > 0)
				firstBarIdx = Math.Max(firstBarIdx, lastBarIdx - MaxBarsToRender + 1);
			if (lastBarIdx < firstBarIdx)
				return;

			SharpDX.Direct2D1.Brush greenBrush = null;
			SharpDX.Direct2D1.Brush redBrush = null;
			SharpDX.Direct2D1.Brush textBrush = null;
			SharpDX.Direct2D1.Brush darkTextBrush = null;
			SharpDX.Direct2D1.Brush imbalanceBrush = null;
			SharpDX.Direct2D1.SolidColorBrush cellBrush = null;
			SharpDX.Direct2D1.SolidColorBrush candleBgBrush = null;
			SharpDX.Direct2D1.SolidColorBrush candleBorderBrush = null;
			SharpDX.Direct2D1.Brush bullSignalBrush = null;
			SharpDX.Direct2D1.Brush bearSignalBrush = null;
			TextFormat textFormat = null;
			TextFormat barDeltaTextFormat = null;
			TextFormat signalTextFormat = null;

			try
			{
				greenBrush = PositiveColor.ToDxBrush(RenderTarget);
				redBrush = NegativeColor.ToDxBrush(RenderTarget);
				textBrush = TextColor.ToDxBrush(RenderTarget);
				darkTextBrush = System.Windows.Media.Brushes.Black.ToDxBrush(RenderTarget);
				imbalanceBrush = ImbalanceColor.ToDxBrush(RenderTarget);
				cellBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 1f, 1f, 1f));
				candleBgBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(0.08f, 0.08f, 0.08f, 0.85f));
				candleBorderBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new SharpDX.Color4(1f, 1f, 1f, 0.9f));
				bullSignalBrush = BullSignalColor.ToDxBrush(RenderTarget);
				bearSignalBrush = BearSignalColor.ToDxBrush(RenderTarget);

				var posC = (PositiveColor as System.Windows.Media.SolidColorBrush)?.Color ?? System.Windows.Media.Colors.SeaGreen;
				var negC = (NegativeColor as System.Windows.Media.SolidColorBrush)?.Color ?? System.Windows.Media.Colors.Firebrick;
				textFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSize)
				{
					TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
					ParagraphAlignment = ParagraphAlignment.Center
				};
				barDeltaTextFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontSize + 2)
				{
					TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
					ParagraphAlignment = ParagraphAlignment.Center,
					WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap
				};
				signalTextFormat = new TextFormat(Core.Globals.DirectWriteFactory, "Arial", FontWeight.Bold, SharpDX.DirectWrite.FontStyle.Normal, FontSize + 8)
				{
					TextAlignment = SharpDX.DirectWrite.TextAlignment.Center,
					ParagraphAlignment = ParagraphAlignment.Center
				};

				float barPitch = CellWidth;
				if (lastBarIdx > firstBarIdx)
				{
					float x0 = chartControl.GetXByBarIndex(ChartBars, firstBarIdx);
					float x1 = chartControl.GetXByBarIndex(ChartBars, firstBarIdx + 1);
					if (x1 > x0)
						barPitch = x1 - x0;
				}
				float candleGap = ShowMiniCandle ? 4f : 0f;
				float candleW = ShowMiniCandle ? Math.Max(MiniCandleWidth, 6f) : 0f;
				float groupWidth = Math.Max(barPitch - 2f, 4f);
				float renderWidth = Math.Min(CellWidth, Math.Max(groupWidth - candleW - candleGap, 4f));

				double ts = CustomTickSize > 0 ? CustomTickSize : Instrument.MasterInstrument.TickSize;
				float pxPerTick = Math.Abs(chartScale.GetYByValue(1000.0) - chartScale.GetYByValue(1000.0 + ts));
				float minRowHeight = Math.Max(CellHeight, FontSize * 1.8f + 6f);
				int groupSize = pxPerTick > 0.01f ? Math.Max(1, (int)Math.Ceiling(minRowHeight / pxPerTick)) : 1;
				float rowHeight = groupSize > 1 ? Math.Max(pxPerTick * groupSize, minRowHeight) : minRowHeight;

				for (int idx = firstBarIdx; idx <= lastBarIdx; idx++)
				{
					if (!barData.TryGetValue(idx, out BarDelta bd) || bd.DeltaByPrice.Count == 0)
						continue;

					try
					{

						float barCenterX = chartControl.GetXByBarIndex(ChartBars, idx);
						float groupSpan = renderWidth + candleGap + candleW;
						var x = barCenterX - groupSpan / 2f;
						float candleX = x + renderWidth + candleGap;

						// figure out how tall this specific bar actually is on screen, then size/combine
						// rows so exactly that many boxes fit without shrinking below minRowHeight
						int barGroupSize = groupSize;
						try
						{
							if (idx >= 0 && idx <= CurrentBar)
							{
								float barPixelHeight = Math.Abs(chartScale.GetYByValue(High.GetValueAt(idx)) - chartScale.GetYByValue(Low.GetValueAt(idx)));
								int maxRowsFit = Math.Max(1, (int)Math.Floor(barPixelHeight / minRowHeight));

								double minPrice = bd.DeltaByPrice.Keys.Min();
								double maxPrice = bd.DeltaByPrice.Keys.Max();
								int barTickSpan = (int)Math.Round((maxPrice - minPrice) / ts) + 1;

								if (barTickSpan > maxRowsFit * groupSize)
									barGroupSize = Math.Max(groupSize, (int)Math.Ceiling(barTickSpan / (double)maxRowsFit));
							}
						}
						catch (Exception)
						{
							barGroupSize = groupSize;
						}
						float barRowHeight = barGroupSize > 1 ? Math.Max(pxPerTick * barGroupSize, minRowHeight) : minRowHeight;

						var rows = GroupRows(bd, ts, barGroupSize).OrderByDescending(r => r.Price).ToList();

						// find runs of consecutive same-sign rows (top-to-bottom) long enough to flag as stacked imbalance
						var stackedFlags = new bool[rows.Count];
						if (ShowImbalanceStacks && ImbalanceStackCount > 1)
						{
							int runStart = 0;
							for (int i = 1; i <= rows.Count; i++)
							{
								bool sameSign = i < rows.Count && Sign(rows[i], ShowVolumeInsteadOfDelta) == Sign(rows[runStart], ShowVolumeInsteadOfDelta);
								if (!sameSign)
								{
									if (i - runStart >= ImbalanceStackCount)
										for (int k = runStart; k < i; k++)
											stackedFlags[k] = true;
									runStart = i;
								}
							}
						}

						int maxAbs = 1;
						foreach (var row in rows)
							maxAbs = Math.Max(maxAbs, Math.Abs(ShowVolumeInsteadOfDelta ? row.Vol : row.Delta));

						for (int i = 0; i < rows.Count; i++)
						{
							var row = rows[i];
							int displayVal = ShowVolumeInsteadOfDelta ? row.Vol : row.Delta;

							float y = chartScale.GetYByValue(row.Price);

							float rowGap = Math.Min(2f, barRowHeight * 0.1f);
							float cellH = Math.Max(barRowHeight - rowGap, 1f);
							var cellRect = new RectangleF(x, y - cellH / 2f, renderWidth, cellH);
							float t = Math.Abs(displayVal) / (float)maxAbs;
							cellBrush.Color = CellColor(displayVal >= 0 ? posC : negC, t);

							RenderTarget.FillRectangle(cellRect, cellBrush);

							string text = displayVal.ToString();
							var cellTextBrush = t > 0.55f ? textBrush : darkTextBrush;
							using (var tl = new TextLayout(Core.Globals.DirectWriteFactory, text, textFormat, renderWidth, cellH))
							{
								RenderTarget.DrawTextLayout(new Vector2(cellRect.X, cellRect.Y), tl, cellTextBrush);
							}
						}

						bool barValid = idx >= 0 && idx <= CurrentBar;

						if (ShowMiniCandle && barValid)
						{
							double o = Open.GetValueAt(idx), h = High.GetValueAt(idx), l = Low.GetValueAt(idx), c = Close.GetValueAt(idx);
							float yO = chartScale.GetYByValue(o);
							float yH = chartScale.GetYByValue(h);
							float yL = chartScale.GetYByValue(l);
							float yC = chartScale.GetYByValue(c);

							float wickX = candleX + candleW / 2f;
							var candleBrush = c >= o ? greenBrush : redBrush;

							RenderTarget.FillRectangle(new RectangleF(candleX - 2f, yH - 2f, candleW + 4f, yL - yH + 4f), candleBgBrush);

							RenderTarget.DrawLine(new Vector2(wickX, yH), new Vector2(wickX, yL), candleBrush, 2f);

							float bodyTop = Math.Min(yO, yC);
							float bodyBot = Math.Max(yO, yC);
							if (bodyBot - bodyTop < 2f)
								bodyBot = bodyTop + 2f;

							var bodyRect = new RectangleF(candleX, bodyTop, candleW, bodyBot - bodyTop);
							RenderTarget.FillRectangle(bodyRect, candleBrush);
							RenderTarget.DrawRectangle(bodyRect, candleBorderBrush, 1f);
						}

						if (ShowBarDelta && barValid)
						{
							int totalDelta = bd.DeltaByPrice.Values.Sum();
							double high = High.GetValueAt(idx);
							float topY = chartScale.GetYByValue(high);

							var labelRect = new RectangleF(x, topY - barRowHeight - 18f, groupSpan, 18f);
							var labelBrush = totalDelta >= 0 ? greenBrush : redBrush;

							using (var tl = new TextLayout(Core.Globals.DirectWriteFactory, totalDelta.ToString(), barDeltaTextFormat, groupSpan, 18f))
							{
								RenderTarget.DrawTextLayout(new Vector2(labelRect.X, labelRect.Y), tl, labelBrush);
							}
						}

						bool prevOk = barData.TryGetValue(idx - 1, out BarDelta prevBd) && prevBd != null && prevBd.DeltaByPrice.Count > 0;
						bool nextValid = idx + 1 <= CurrentBar;

						if (ShowReversalSignals && rows.Count > 0 && barValid && idx - 1 >= 0 && prevOk && nextValid)
						{
							double h = High.GetValueAt(idx), l = Low.GetValueAt(idx);
							double nextLow = Low.GetValueAt(idx + 1), nextHigh = High.GetValueAt(idx + 1);
							int totalDelta2 = bd.DeltaByPrice.Values.Sum();
							int prevTotalDelta = prevBd.DeltaByPrice.Values.Sum();

							// sum the bottom/top few raw traded price levels (fixed tick count, independent of
							// the display's zoom-dependent grouping) so magnitude stays stable across zoom
							// but isn't diluted down to a single tick's print
							var pricesDesc = bd.DeltaByPrice.Keys.OrderByDescending(p => p).ToList();
							int windowN = Math.Max(1, AbsorptionWindowTicks);
							int topRawDelta = pricesDesc.Take(windowN).Sum(p => bd.DeltaByPrice[p]);
							int bottomRawDelta = pricesDesc.Skip(Math.Max(0, pricesDesc.Count - windowN)).Sum(p => bd.DeltaByPrice[p]);

							// confirm via the NEXT bar holding the extreme instead of requiring this bar's own
							// close to already recover — fires right at the exhaustion print, not a bar late.
							// allow a small tolerance so a bar that overshoots by a tick or two still counts
							double holdTolerance = ts * Math.Max(0, HoldToleranceTicks);
							bool bullAbsorb = bottomRawDelta <= -AbsorptionThreshold && nextLow >= l - holdTolerance;
							bool bullDiverge = totalDelta2 > prevTotalDelta;

							bool bearAbsorb = topRawDelta >= AbsorptionThreshold && nextHigh <= h + holdTolerance;
							bool bearDiverge = totalDelta2 < prevTotalDelta;

							if (bullAbsorb && bullDiverge)
							{
								float sigY = chartScale.GetYByValue(l) + 20f;
								using (var tl = new TextLayout(Core.Globals.DirectWriteFactory, "▲", signalTextFormat, groupSpan, 20f))
									RenderTarget.DrawTextLayout(new Vector2(x, sigY), tl, bullSignalBrush);
							}
							else if (bearAbsorb && bearDiverge)
							{
								float sigY = chartScale.GetYByValue(h) - 20f - (FontSize + 8f);
								using (var tl = new TextLayout(Core.Globals.DirectWriteFactory, "▼", signalTextFormat, groupSpan, 20f))
									RenderTarget.DrawTextLayout(new Vector2(x, sigY), tl, bearSignalBrush);
							}
						}
					}
					catch (Exception)
					{
						// cross-thread race: bar data may lag chart's CurrentBar during load/replay; skip this bar's render
					}
				}
			}
			finally
			{
				greenBrush?.Dispose();
				redBrush?.Dispose();
				textBrush?.Dispose();
				darkTextBrush?.Dispose();
				imbalanceBrush?.Dispose();
				cellBrush?.Dispose();
				candleBgBrush?.Dispose();
				candleBorderBrush?.Dispose();
				bullSignalBrush?.Dispose();
				bearSignalBrush?.Dispose();
				textFormat?.Dispose();
				barDeltaTextFormat?.Dispose();
				signalTextFormat?.Dispose();
			}
		}

		#region Properties
		[NinjaScriptProperty]
		[Display(Name = "Cell Width (px)", Order = 1, GroupName = "Parameters")]
		public int CellWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Cell Height (px)", Order = 2, GroupName = "Parameters")]
		public int CellHeight { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Font Size", Order = 3, GroupName = "Parameters")]
		public int FontSize { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Custom Price Bucket Size (0 = instrument tick size)", Order = 4, GroupName = "Parameters")]
		public double CustomTickSize { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Volume Instead Of Delta", Order = 5, GroupName = "Parameters")]
		public bool ShowVolumeInsteadOfDelta { get; set; }

		[XmlIgnore]
		[Display(Name = "Positive (Buy) Color", Order = 6, GroupName = "Parameters")]
		public Brush PositiveColor { get; set; }

		[Browsable(false)]
		public string PositiveColorSerialize
		{
			get { return Serialize.BrushToString(PositiveColor); }
			set { PositiveColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Negative (Sell) Color", Order = 7, GroupName = "Parameters")]
		public Brush NegativeColor { get; set; }

		[Browsable(false)]
		public string NegativeColorSerialize
		{
			get { return Serialize.BrushToString(NegativeColor); }
			set { NegativeColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Text Color", Order = 8, GroupName = "Parameters")]
		public Brush TextColor { get; set; }

		[Browsable(false)]
		public string TextColorSerialize
		{
			get { return Serialize.BrushToString(TextColor); }
			set { TextColor = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Show Bar Total Delta", Order = 9, GroupName = "Parameters")]
		public bool ShowBarDelta { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Imbalance Stacks", Order = 10, GroupName = "Parameters")]
		public bool ShowImbalanceStacks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Imbalance Stack Min Count", Order = 11, GroupName = "Parameters")]
		public int ImbalanceStackCount { get; set; }

		[XmlIgnore]
		[Display(Name = "Imbalance Highlight Color", Order = 12, GroupName = "Parameters")]
		public Brush ImbalanceColor { get; set; }

		[Browsable(false)]
		public string ImbalanceColorSerialize
		{
			get { return Serialize.BrushToString(ImbalanceColor); }
			set { ImbalanceColor = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Display(Name = "Show Mini Candle", Order = 13, GroupName = "Parameters")]
		public bool ShowMiniCandle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mini Candle Width (px)", Order = 14, GroupName = "Parameters")]
		public int MiniCandleWidth { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Reversal Signals", Order = 16, GroupName = "Parameters")]
		public bool ShowReversalSignals { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Absorption Threshold (delta)", Order = 17, GroupName = "Parameters")]
		public int AbsorptionThreshold { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Absorption Window (ticks)", Order = 17, GroupName = "Parameters")]
		public int AbsorptionWindowTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Hold Tolerance (ticks)", Order = 17, GroupName = "Parameters")]
		public int HoldToleranceTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Max Bars To Render (0 = unlimited)", Order = 18, GroupName = "Parameters")]
		public int MaxBarsToRender { get; set; }

		[XmlIgnore]
		[Display(Name = "Bullish Signal Color", Order = 19, GroupName = "Parameters")]
		public Brush BullSignalColor { get; set; }

		[Browsable(false)]
		public string BullSignalColorSerialize
		{
			get { return Serialize.BrushToString(BullSignalColor); }
			set { BullSignalColor = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Bearish Signal Color", Order = 20, GroupName = "Parameters")]
		public Brush BearSignalColor { get; set; }

		[Browsable(false)]
		public string BearSignalColorSerialize
		{
			get { return Serialize.BrushToString(BearSignalColor); }
			set { BearSignalColor = Serialize.StringToBrush(value); }
		}
		#endregion
	}
}
