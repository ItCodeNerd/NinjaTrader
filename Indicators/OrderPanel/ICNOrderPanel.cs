#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using WpfMedia = System.Windows.Media;
#endregion

public enum PanelPosition { Right, Left }
public enum OffsetUnit { Ticks, Dollars }

namespace NinjaTrader.NinjaScript.Indicators.ItCodeNerd
{
	public class ICNOrderPanel : Indicator
	{
		#region Private Fields

		private const float LineHitZone = 8f;

		// SharpDX brushes for the on-chart TP/SL lines and price tags
		private SharpDX.Direct2D1.SolidColorBrush _entryLineBrush, _tpLineBrush, _slLineBrush;
		private SharpDX.Direct2D1.SolidColorBrush _textBrush, _priceLabelBgBrush, _profitBrush, _lossBrush;
		private SharpDX.DirectWrite.TextFormat _priceLabelFormat;
		private SharpDX.Direct2D1.StrokeStyle _dashStyle;

		// Drag
		private enum DragTarget { None, TP, SL }
		private DragTarget _dragging = DragTarget.None, _hoverLine = DragTarget.None;
		private bool _tpUserDrag, _slUserDrag;
		private double _tpOffset, _slOffset;
		private ChartScale _lastChartScale;

		private ChartControl _chartControlRef;

		// State
		private enum TrackingState { Idle, TrackingLong, TrackingShort, LockedLong, LockedShort }
		private TrackingState _state = TrackingState.Idle;
		private double _liveEntryPrice, _liveTpPrice, _liveSlPrice;
		private double _lockedEntryPrice, _lockedTpPrice, _lockedSlPrice;

		// Order references for cancellation
		private Order _tpOrder;
		private Order _slOrder;
		private Account _lastSubmitAccount;  // account used for order submission
		private Account _cachedAccount;      // memoised Chart Trader account (avoids per-frame reflection)
		private DateTime _lastAcctResolve = DateTime.MinValue;
		private Account _subscribedAccount;  // account we've hooked position/order events on
		private bool _orderSubmitted;        // guards against duplicate bracket submission
		private string _ocoId;               // OCO group linking TP/SL (and later the BE stop)

		private string _lastAccountName = "";
		private string _shownAcctName = "";

		// ── WPF panel (docked under Chart Trader) ──
		private Chart _chartWindow;
		private Wpf.Window _panelWindow;
		private WpfControls.Border _wpfPanel;
		private bool _wpfCreated;
		private WpfControls.TextBox _tpValue, _slValue, _qtyValue;
		private WpfControls.TextBlock _statusText, _acctText;
		private WpfControls.Button _buyBtn, _sellBtn, _placeBtn, _closeBtn, _cancelBtn;
		private readonly System.Collections.Generic.List<WpfControls.Button> _spinButtons = new System.Collections.Generic.List<WpfControls.Button>();
		private WpfControls.TextBlock _tpLabel, _slLabel, _qtyLabel, _placeText, _unitToggleText;
		private WpfControls.Button _unitToggleBtn;
		private WpfControls.ControlTemplate _btnTemplate;

		#endregion

		#region Properties

		[NinjaScriptProperty]
		[Display(Name = "TP Ticks", Order = 1, GroupName = "Order Settings")]
		[Range(1, int.MaxValue)]
		public int TpTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "SL Ticks", Order = 2, GroupName = "Order Settings")]
		[Range(1, int.MaxValue)]
		public int SlTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Order Quantity", Order = 3, GroupName = "Order Settings")]
		[Range(1, int.MaxValue)]
		public int OrderQuantity { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Entry Line Color", Order = 1, GroupName = "Appearance")]
		public System.Windows.Media.Brush EntryLineColor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TP Line Color", Order = 2, GroupName = "Appearance")]
		public System.Windows.Media.Brush TpLineColor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "SL Line Color", Order = 3, GroupName = "Appearance")]
		public System.Windows.Media.Brush SlLineColor { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Panel Side", Description = "Retained for compatibility; the control panel now docks under Chart Trader.", Order = 4, GroupName = "Appearance")]
		public PanelPosition PanelSide { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TP/SL Units", Description = "Show and adjust TP/SL in ticks or dollars", Order = 5, GroupName = "Order Settings")]
		public OffsetUnit TpSlUnit { get; set; }

		#endregion

		#region Account Resolution

		// Returns the Chart Trader account. Caches the result so the reflection
		// walk does not run on every render frame. While idle it re-resolves at
		// most every 2s (to pick up an account-dropdown change), but never
		// re-resolves mid-trade so a locked setup keeps its original account.
		private Account GetSelectedAccount()
		{
			if (_cachedAccount != null && (!IsIdle || (DateTime.UtcNow - _lastAcctResolve).TotalSeconds < 2.0))
				return _cachedAccount;
			Account resolved = ResolveAccountViaReflection();
			if (resolved != null) { _cachedAccount = resolved; _lastAcctResolve = DateTime.UtcNow; }
			return _cachedAccount;
		}

		private Account ResolveAccountViaReflection()
		{
			try
			{
				if (_chartControlRef == null) return null;
				var fl = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
				System.Windows.DependencyObject p = _chartControlRef;
				object chartObj = null;
				for (int i = 0; i < 30 && p != null; i++)
				{
					p = System.Windows.Media.VisualTreeHelper.GetParent(p);
					if (p != null && p.GetType().Name == "Chart") { chartObj = p; break; }
				}
				if (chartObj != null)
				{
					foreach (string n in new[] { "ChartTrader", "chartTrader", "ChartTraderControl" })
					{
						var prop = chartObj.GetType().GetProperty(n, fl);
						if (prop != null) { var a = ExtractAcct(prop.GetValue(chartObj), fl); if (a != null) return a; }
						var field = chartObj.GetType().GetField(n, fl);
						if (field != null) { var a = ExtractAcct(field.GetValue(chartObj), fl); if (a != null) return a; }
					}
					var a2 = ScanAcct(chartObj, fl); if (a2 != null) return a2;
				}
				var op = _chartControlRef.GetType().GetProperty("OwnerChart", fl);
				if (op != null) { var ow = op.GetValue(_chartControlRef); if (ow != null) { var a3 = ScanAcct(ow, fl); if (a3 != null) return a3; } }
			}
			catch (Exception ex) { Print("ICNOrderPanel: Acct error — " + ex.Message); }
			return null;
		}
		private Account ExtractAcct(object o, System.Reflection.BindingFlags fl)
		{
			if (o == null) return null;
			foreach (var n in new[] { "Account", "SelectedAccount" })
			{ var pr = o.GetType().GetProperty(n, fl); if (pr != null) { var a = pr.GetValue(o) as Account; if (a != null) { _lastAccountName = a.Name; return a; } } }
			return null;
		}
		private Account ScanAcct(object o, System.Reflection.BindingFlags fl)
		{
			if (o == null) return null;
			try
			{
				foreach (var pr in o.GetType().GetProperties(fl))
					if (typeof(Account).IsAssignableFrom(pr.PropertyType))
						try { var a = pr.GetValue(o) as Account; if (a != null) { _lastAccountName = a.Name; return a; } } catch { }
				foreach (var f in o.GetType().GetFields(fl))
					if (typeof(Account).IsAssignableFrom(f.FieldType))
						try { var a = f.GetValue(o) as Account; if (a != null) { _lastAccountName = a.Name; return a; } } catch { }
			}
			catch { }
			return null;
		}

		#endregion

		#region State Management

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Order panel docked under Chart Trader with draggable TP/SL and editable settings";
				Name = "ICNOrderPanel";
				Calculate = Calculate.OnEachTick;
				IsOverlay = true;
				DisplayInDataBox = false;
				IsSuspendedWhileInactive = false;
				TpTicks = 40; SlTicks = 20; OrderQuantity = 1;
				EntryLineColor = System.Windows.Media.Brushes.DodgerBlue;
				TpLineColor = System.Windows.Media.Brushes.LimeGreen;
				SlLineColor = System.Windows.Media.Brushes.Crimson;
				PanelSide = PanelPosition.Right;
				TpSlUnit = OffsetUnit.Ticks;
			}
			else if (State == State.DataLoaded)
			{
				if (ChartControl != null)
				{
					_chartControlRef = ChartControl;
					ChartPanel.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
					ChartPanel.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
					ChartPanel.PreviewMouseMove += OnPreviewMouseMove;
				}
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(CreateWPFControls);
			}
			else if (State == State.Terminated)
			{
				if (ChartPanel != null)
				{
					ChartPanel.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
					ChartPanel.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
					ChartPanel.PreviewMouseMove -= OnPreviewMouseMove;
				}
				UnsubscribeAccount();
				if (ChartControl != null)
					ChartControl.Dispatcher.InvokeAsync(DisposeWPFControls);
				DisposeResources();
			}
		}

		protected override void OnBarUpdate()
		{
			if (IsTracking) UpdateTrackingPrices();
		}

		#endregion

		#region Price Tracking

		private void UpdateTrackingPrices()
		{
			double ts = Instrument.MasterInstrument.TickSize;
			double price = GetMarketPrice();
			_liveEntryPrice = price;
			double tpOff = _tpUserDrag ? _tpOffset : TpTicks;
			double slOff = _slUserDrag ? _slOffset : SlTicks;
			if (_state == TrackingState.TrackingLong)
			{ _liveTpPrice = price + tpOff * ts; _liveSlPrice = price - slOff * ts; }
			else
			{ _liveTpPrice = price - tpOff * ts; _liveSlPrice = price + slOff * ts; }
		}

		// Real-time last trade price. Unlike Close[0], MarketData is valid from any
		// thread (the WPF button handlers run on the UI thread, where Close[0] can be
		// a stale value from the previous OnBarUpdate). Falls back to Close[0] if no
		// quote has arrived yet.
		private double GetMarketPrice()
		{
			try
			{
				if (Instrument != null && Instrument.MarketData != null
					&& Instrument.MarketData.Last != null && Instrument.MarketData.Last.Price > 0)
					return Instrument.MarketData.Last.Price;
			}
			catch { }
			return Close[0];
		}

		private bool IsTracking { get { return _state == TrackingState.TrackingLong || _state == TrackingState.TrackingShort; } }
		private bool IsLocked { get { return _state == TrackingState.LockedLong || _state == TrackingState.LockedShort; } }
		private bool HasSetup { get { return _state != TrackingState.Idle; } }
		private bool IsIdle { get { return _state == TrackingState.Idle; } }

		private double ActiveEntry { get { return IsLocked ? _lockedEntryPrice : _liveEntryPrice; } }
		private double ActiveTp { get { return IsLocked ? _lockedTpPrice : _liveTpPrice; } }
		private double ActiveSl { get { return IsLocked ? _lockedSlPrice : _liveSlPrice; } }

		private int TpTicksCurrent
		{
			get
			{
				if (!HasSetup) return TpTicks;
				double ts = Instrument.MasterInstrument.TickSize;
				return ts > 0 ? (int)Math.Round(Math.Abs(ActiveTp - ActiveEntry) / ts) : TpTicks;
			}
		}
		private int SlTicksCurrent
		{
			get
			{
				if (!HasSetup) return SlTicks;
				double ts = Instrument.MasterInstrument.TickSize;
				return ts > 0 ? (int)Math.Round(Math.Abs(ActiveSl - ActiveEntry) / ts) : SlTicks;
			}
		}

		private double TicksToDollars(int ticks)
		{
			double ts = Instrument.MasterInstrument.TickSize;
			double pv = Instrument.MasterInstrument.PointValue;
			return ticks * ts * pv;
		}
		private string FmtDollars(double v) { return (v >= 0 ? "+" : "-") + "$" + Math.Abs(v).ToString("N2"); }

		#endregion

		#region Mouse Events (TP/SL line dragging)

		private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
		{
			if (!IsTracking || _lastChartScale == null) return;
			System.Windows.Point pt = e.GetPosition(ChartPanel as System.Windows.IInputElement);
			float my = (float)pt.Y;
			float tpY = (float)_lastChartScale.GetYByValue(_liveTpPrice);
			float slY = (float)_lastChartScale.GetYByValue(_liveSlPrice);
			if (Math.Abs(my - tpY) <= LineHitZone) { _dragging = DragTarget.TP; ApplyDrag(my); ChartPanel.CaptureMouse(); e.Handled = true; _chartControlRef?.InvalidateVisual(); return; }
			if (Math.Abs(my - slY) <= LineHitZone) { _dragging = DragTarget.SL; ApplyDrag(my); ChartPanel.CaptureMouse(); e.Handled = true; _chartControlRef?.InvalidateVisual(); return; }
		}

		private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
		{
			if (_dragging != DragTarget.None) { _dragging = DragTarget.None; ChartPanel.ReleaseMouseCapture(); e.Handled = true; }
		}

		private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			System.Windows.Point pt = e.GetPosition(ChartPanel as System.Windows.IInputElement);
			float my = (float)pt.Y;

			if (_dragging != DragTarget.None && IsTracking && _lastChartScale != null)
			{ ApplyDrag(my); e.Handled = true; _chartControlRef?.InvalidateVisual(); return; }

			// Line hover + cursor
			DragTarget newHL = DragTarget.None;
			if (IsTracking && _lastChartScale != null)
			{
				float tpY = (float)_lastChartScale.GetYByValue(_liveTpPrice);
				float slY = (float)_lastChartScale.GetYByValue(_liveSlPrice);
				if (Math.Abs(my - tpY) <= LineHitZone) newHL = DragTarget.TP;
				else if (Math.Abs(my - slY) <= LineHitZone) newHL = DragTarget.SL;
			}
			if (newHL != _hoverLine)
			{
				_hoverLine = newHL;
				ChartPanel.Cursor = newHL != DragTarget.None ? System.Windows.Input.Cursors.SizeNS : System.Windows.Input.Cursors.Arrow;
				_chartControlRef?.InvalidateVisual();
			}
		}

		private void ApplyDrag(float mouseY)
		{
			if (_lastChartScale == null) return;
			double dragPrice = _lastChartScale.GetValueByY(mouseY);
			double ts = Instrument.MasterInstrument.TickSize;
			dragPrice = Math.Round(dragPrice / ts) * ts;
			if (_dragging == DragTarget.TP) { _liveTpPrice = dragPrice; _tpOffset = Math.Abs(dragPrice - _liveEntryPrice) / ts; _tpUserDrag = true; }
			else if (_dragging == DragTarget.SL) { _liveSlPrice = dragPrice; _slOffset = Math.Abs(dragPrice - _liveEntryPrice) / ts; _slUserDrag = true; }
		}

		#endregion

		#region Order Placement

		private void LockAndPlaceOrder()
		{
			if (IsLocked || _orderSubmitted) return; // already placed — guard double submission
			_lockedEntryPrice = _liveEntryPrice; _lockedTpPrice = _liveTpPrice; _lockedSlPrice = _liveSlPrice;
			_state = (_state == TrackingState.TrackingLong) ? TrackingState.LockedLong : TrackingState.LockedShort;
			ChartPanel.Cursor = System.Windows.Input.Cursors.Arrow;
			// Only the entry line is drawn; the broker's own TP/SL order lines render the bracket.
			Draw.HorizontalLine(this, "ICNPanel_Entry", _lockedEntryPrice, EntryLineColor, DashStyleHelper.Solid, 2);
			SubmitOrders();
		}

		private void SubmitOrders()
		{
			if (_orderSubmitted) { Print("ICNOrderPanel: Order already submitted — ignoring duplicate."); return; }
			Account acct = GetSelectedAccount();
			if (acct == null) { Print("ICNOrderPanel: No account."); return; }
			_lastSubmitAccount = acct;
			SubscribeAccount(acct);
			Print("ICNOrderPanel: Submitting to '" + acct.Name + "'");
			try
			{
				bool isL = (_state == TrackingState.LockedLong);
				OrderAction ent = isL ? OrderAction.Buy : OrderAction.Sell;
				OrderAction ext = isL ? OrderAction.Sell : OrderAction.Buy;
				var entO = acct.CreateOrder(Instrument, ent, OrderType.Market, OrderEntry.Manual, TimeInForce.Day, OrderQuantity, 0, 0, string.Empty, "ICNPanel_Entry", Core.Globals.MaxDate, null);
				_ocoId = Guid.NewGuid().ToString("N").Substring(0, 16);
				_tpOrder = acct.CreateOrder(Instrument, ext, OrderType.Limit, OrderEntry.Manual, TimeInForce.Day, OrderQuantity, _lockedTpPrice, 0, _ocoId, "ICNPanel_TP", Core.Globals.MaxDate, null);
				_slOrder = acct.CreateOrder(Instrument, ext, OrderType.StopMarket, OrderEntry.Manual, TimeInForce.Day, OrderQuantity, 0, _lockedSlPrice, _ocoId, "ICNPanel_SL", Core.Globals.MaxDate, null);
				acct.Submit(new[] { entO, _tpOrder, _slOrder });
				_orderSubmitted = true;
			}
			catch (Exception ex) { Print("ICNOrderPanel: " + ex.Message); }
		}

		#region Position Reconciliation

		// Hooks position/order events on the submission account so the panel can
		// reconcile its state with what actually happened at the broker.
		private void SubscribeAccount(Account acct)
		{
			if (acct == null || _subscribedAccount == acct) return;
			UnsubscribeAccount();
			_subscribedAccount = acct;
			_subscribedAccount.PositionUpdate += OnAccountPositionUpdate;
			_subscribedAccount.OrderUpdate += OnAccountOrderUpdate;
		}

		private void UnsubscribeAccount()
		{
			if (_subscribedAccount == null) return;
			_subscribedAccount.PositionUpdate -= OnAccountPositionUpdate;
			_subscribedAccount.OrderUpdate -= OnAccountOrderUpdate;
			_subscribedAccount = null;
		}

		// Fired when a position changes on the submission account. If our instrument
		// goes flat while a bracket is live (TP or SL filled, or flattened elsewhere),
		// the panel is showing a stale 'ORDER PLACED' — reconcile by resetting to idle.
		private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
		{
			try
			{
				if (!IsLocked || e == null || e.Position == null || Instrument == null) return;
				if (e.Position.Instrument != null && e.Position.Instrument.FullName != Instrument.FullName) return;
				if (e.MarketPosition == MarketPosition.Flat)
					MarshalReset("position flat (TP/SL filled or closed)");
			}
			catch (Exception ex) { Print("ICNOrderPanel: PositionUpdate error — " + ex.Message); }
		}

		// Detects a rejected entry so the panel doesn't stay stuck on 'ORDER PLACED'.
		private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
		{
			try
			{
				if (!IsLocked || e == null || e.Order == null) return;
				if (e.Order.Name == "ICNPanel_Entry" && e.OrderState == OrderState.Rejected)
					MarshalReset("entry order rejected");
			}
			catch (Exception ex) { Print("ICNOrderPanel: OrderUpdate error — " + ex.Message); }
		}

		// Position/order events arrive off the UI thread; marshal the reset onto the
		// chart dispatcher before touching draw objects, the cursor, or invalidating.
		private void MarshalReset(string reason)
		{
			var cc = _chartControlRef;
			if (cc == null) { ResetToIdle(); return; }
			cc.Dispatcher.InvokeAsync(() =>
			{
				if (!IsLocked) return; // already reset between event and dispatch
				Print("ICNOrderPanel: auto-reset — " + reason);
				ResetToIdle();
				RefreshPanel();
				cc.InvalidateVisual();
			});
		}

		#endregion

		private void ResetToIdle()
		{
			// Never leave live orders behind: cancel anything still working before
			// dropping the references (entry rejected, position flat, user cancel).
			// CancelOrderSafe checks OrderState, so already-cancelled/filled orders are no-ops.
			CancelOrderSafe(_tpOrder); CancelOrderSafe(_slOrder);
			RemoveDrawObject("ICNPanel_Entry"); RemoveDrawObject("ICNPanel_TP"); RemoveDrawObject("ICNPanel_SL");
			_state = TrackingState.Idle; _dragging = DragTarget.None; _hoverLine = DragTarget.None;
			_tpUserDrag = false; _slUserDrag = false;
			_liveEntryPrice = _liveTpPrice = _liveSlPrice = 0;
			_lockedEntryPrice = _lockedTpPrice = _lockedSlPrice = 0;
			_tpOrder = null; _slOrder = null; _lastSubmitAccount = null;
			_orderSubmitted = false; _ocoId = null;
			UnsubscribeAccount();
			if (ChartPanel != null) ChartPanel.Cursor = System.Windows.Input.Cursors.Arrow;
		}

		/// <summary>
		/// Safely cancels a pending order if it exists and is still in a cancellable state.
		/// </summary>
		private void CancelOrderSafe(Order order)
		{
			if (order == null) return;
			try
			{
				if (order.OrderState == OrderState.Working
					|| order.OrderState == OrderState.Accepted
					|| order.OrderState == OrderState.Submitted)
				{
					Account acct = _lastSubmitAccount ?? GetSelectedAccount();
					if (acct != null)
					{
						acct.Cancel(new[] { order });
						Print("ICNOrderPanel: Cancelled order " + order.Name + " (" + order.OrderState + ")");
					}
				}
			}
			catch (Exception ex)
			{
				Print("ICNOrderPanel: Cancel error for " + order.Name + " — " + ex.Message);
			}
		}

		/// <summary>
		/// Closes the position by submitting a market order in the opposite direction.
		/// Flattens the instrument on the selected account and resets the panel.
		/// </summary>
		private void ClosePosition()
		{
			Account acct = _lastSubmitAccount ?? GetSelectedAccount();
			if (acct == null) { Print("ICNOrderPanel: No account for close."); return; }

			try
			{
				// Cancel all pending TP/SL orders first
				CancelOrderSafe(_tpOrder);
				CancelOrderSafe(_slOrder);

				bool isLong = (_state == TrackingState.LockedLong);
				OrderAction closeAction = isLong ? OrderAction.Sell : OrderAction.Buy;

				// Submit a market order to close the position
				Order closeOrder = acct.CreateOrder(
					Instrument, closeAction, OrderType.Market, OrderEntry.Manual,
					TimeInForce.Day, OrderQuantity, 0, 0,
					string.Empty, "ICNPanel_Close", Core.Globals.MaxDate, null);

				acct.Submit(new[] { closeOrder });

				Print("ICNOrderPanel: Cancelled pending orders + closed position — " + closeAction + " " + OrderQuantity + " @ Market");
			}
			catch (Exception ex)
			{
				Print("ICNOrderPanel: Close error — " + ex.Message);
			}

			ResetToIdle();
		}

		#endregion

		#region WPF Panel (docked under Chart Trader)

		// Builds the WPF controls and hosts them in a floating window owned by the
		// chart. A separate top-level window owns its own keyboard focus, so the chart's
		// hotkeys / instrument quick-search can't intercept typing in the fields.
		private void CreateWPFControls()
		{
			try
			{
				if (_wpfCreated) return;
				_chartWindow = System.Windows.Window.GetWindow(ChartControl.Parent) as Chart;

				_wpfPanel = BuildPanel();
				_wpfPanel.Margin = new Wpf.Thickness(0);

				_panelWindow = new Wpf.Window
				{
					Title = "ICN Order Panel",
					Content = _wpfPanel,
					Width = 230,
					SizeToContent = Wpf.SizeToContent.Height,
					WindowStyle = Wpf.WindowStyle.ToolWindow,
					ResizeMode = Wpf.ResizeMode.NoResize,
					ShowInTaskbar = false,
					Background = Rgb(0x1A, 0x1A, 0x24)
				};
				if (_chartWindow != null) { try { _panelWindow.Owner = _chartWindow; } catch { } }

				_panelWindow.Show();

				// Park it near the top-right of the chart window.
				if (_chartWindow != null)
				{
					try
					{
						_panelWindow.Left = _chartWindow.Left + Math.Max(0, _chartWindow.ActualWidth - _panelWindow.ActualWidth - 60);
						_panelWindow.Top = _chartWindow.Top + 120;
					}
					catch { }
				}

				_wpfCreated = true;
				RefreshPanel();
			}
			catch (Exception ex) { Print("ICNOrderPanel: WPF create error — " + ex.Message); }
		}

		private void DisposeWPFControls()
		{
			try
			{
				if (_panelWindow != null) _panelWindow.Close();
			}
			catch (Exception ex) { Print("ICNOrderPanel: WPF dispose error — " + ex.Message); }
			_panelWindow = null; _wpfPanel = null; _chartWindow = null;
			_spinButtons.Clear();
			_wpfCreated = false;
		}

		private WpfControls.Border BuildPanel()
		{
			var stack = new WpfControls.StackPanel { Orientation = WpfControls.Orientation.Vertical, Margin = new Wpf.Thickness(6) };
			// Units toggle: Ticks <-> Dollars
			_unitToggleBtn = MakeBtn("Units: Ticks", Rgb(0x33, 0x33, 0x40), out _unitToggleText);
			_unitToggleBtn.Height = 22;
			_unitToggleText.FontSize = 11;
			_unitToggleBtn.Click += (s, e) => { OnUnitToggleClick(); };
			stack.Children.Add(_unitToggleBtn);
			stack.Children.Add(MakeSpinRow("TP Ticks", out _tpLabel, out _tpValue, () => { HandleSpin(0); }, () => { HandleSpin(1); }, t => { CommitTpSl(true, t); }));
			stack.Children.Add(MakeSpinRow("SL Ticks", out _slLabel, out _slValue, () => { HandleSpin(2); }, () => { HandleSpin(3); }, t => { CommitTpSl(false, t); }));
			stack.Children.Add(MakeSpinRow("Qty", out _qtyLabel, out _qtyValue, () => { HandleSpin(4); }, () => { HandleSpin(5); }, t => { if (IsIdle) { int v; if (int.TryParse(t.Trim(), out v)) OrderQuantity = Math.Max(1, v); } RefreshPanel(); }));
			_statusText = new WpfControls.TextBlock { Text = "Idle", Foreground = Rgb(0x8C, 0x8C, 0x99), FontSize = 11, Margin = new Wpf.Thickness(0, 4, 0, 2), HorizontalAlignment = Wpf.HorizontalAlignment.Center };
			stack.Children.Add(_statusText);
			var bsGrid = new WpfControls.Grid { Margin = new Wpf.Thickness(0, 2, 0, 0) };
			bsGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
			bsGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(6) });
			bsGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
			_buyBtn = MakeBtn("MARK BUY", Rgb(0x0D, 0x80, 0x48));
			_buyBtn.Click += (s, e) => { OnBuyClick(); };
			WpfControls.Grid.SetColumn(_buyBtn, 0); bsGrid.Children.Add(_buyBtn);
			_sellBtn = MakeBtn("MARK SELL", Rgb(0xA6, 0x1F, 0x1F));
			_sellBtn.Click += (s, e) => { OnSellClick(); };
			WpfControls.Grid.SetColumn(_sellBtn, 2); bsGrid.Children.Add(_sellBtn);
			stack.Children.Add(bsGrid);
			_placeBtn = MakeBtn("PLACE ORDER", Rgb(0x26, 0x66, 0xBF), out _placeText);
			_placeBtn.Click += (s, e) => { OnPlaceClick(); };
			stack.Children.Add(_placeBtn);
			_closeBtn = MakeBtn("CLOSE TRADE", Rgb(0xB3, 0x26, 0x26));
			_closeBtn.Click += (s, e) => { OnCloseClick(); };
			_closeBtn.Visibility = Wpf.Visibility.Collapsed;
			stack.Children.Add(_closeBtn);
			_cancelBtn = MakeBtn("CANCEL", Rgb(0x59, 0x59, 0x5E));
			_cancelBtn.Click += (s, e) => { OnCancelClick(); };
			stack.Children.Add(_cancelBtn);
			_acctText = new WpfControls.TextBlock { Text = "Acct: (none)", Foreground = Rgb(0x8C, 0x8C, 0x99), FontSize = 10, Margin = new Wpf.Thickness(0, 4, 0, 0), HorizontalAlignment = Wpf.HorizontalAlignment.Center };
			stack.Children.Add(_acctText);
			return new WpfControls.Border { Background = Rgb(0x1A, 0x1A, 0x24), BorderBrush = Rgb(0x4D, 0x4D, 0x59), BorderThickness = new Wpf.Thickness(1), CornerRadius = new Wpf.CornerRadius(4), Margin = new Wpf.Thickness(4, 6, 4, 4), Child = stack };
		}

		private WpfControls.Grid MakeSpinRow(string label, out WpfControls.TextBlock labelTb, out WpfControls.TextBox valueTb, Action onMinus, Action onPlus, Action<string> onCommit)
		{
			var g = new WpfControls.Grid { Margin = new Wpf.Thickness(0, 2, 0, 2) };
			g.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(58) });
			g.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
			labelTb = new WpfControls.TextBlock { Text = label, Foreground = Rgb(0x8C, 0x8C, 0x99), VerticalAlignment = Wpf.VerticalAlignment.Center, FontSize = 11 };
			WpfControls.Grid.SetColumn(labelTb, 0); g.Children.Add(labelTb);
			valueTb = new WpfControls.TextBox { Text = "0", Foreground = Rgb(0xEA, 0xEA, 0xF2), Background = Rgb(0x12, 0x12, 0x18), BorderBrush = Rgb(0x4D, 0x4D, 0x59), BorderThickness = new Wpf.Thickness(1), CaretBrush = WpfMedia.Brushes.White, TextAlignment = Wpf.TextAlignment.Center, VerticalContentAlignment = Wpf.VerticalAlignment.Center, FontWeight = Wpf.FontWeights.Bold, FontSize = 13, Height = 22, Margin = new Wpf.Thickness(2, 0, 2, 0) };
			var vb = valueTb;
			vb.GotKeyboardFocus += (s, e) => { vb.SelectAll(); };
			// Force keyboard focus into the field on click. Once a TextBox (not the chart)
			// owns focus, NinjaTrader's 'type to find instrument' search no longer fires.
			// Do NOT mark the key events handled — in NT's hosted WPF that also suppresses
			// the character insertion.
			vb.PreviewMouseLeftButtonDown += (s, e) => { if (!vb.IsKeyboardFocused) { vb.Focus(); e.Handled = true; } };
			vb.LostKeyboardFocus += (s, e) => { onCommit(vb.Text); };
			vb.KeyDown += (s, e) =>
			{
				if (e.Key == System.Windows.Input.Key.Enter) { onCommit(vb.Text); System.Windows.Input.Keyboard.ClearFocus(); e.Handled = true; }
				else if (e.Key == System.Windows.Input.Key.Escape) { System.Windows.Input.Keyboard.ClearFocus(); e.Handled = true; }
			};
			WpfControls.Grid.SetColumn(vb, 1); g.Children.Add(vb);
			return g;
		}

		private WpfControls.ControlTemplate _spinBtnTemplatePlus, _spinBtnTemplateMinus;

		// Icon baked directly into the ControlTemplate's visual tree (as FrameworkElementFactory
		// siblings of the background Border), NOT routed through Button.Content/ContentPresenter.
		// Three different Content-based approaches (TextBlock glyph, Canvas+Line, Grid+Rectangle)
		// all rendered blank specifically on these narrow 22px buttons, while the Border's own
		// Background reliably renders (confirmed with a magenta-background test) — so the icon
		// is drawn via the same mechanism already proven to work.
		private WpfControls.ControlTemplate SpinBtnTemplate(bool plus)
		{
			var cached = plus ? _spinBtnTemplatePlus : _spinBtnTemplateMinus;
			if (cached != null) return cached;

			var border = new Wpf.FrameworkElementFactory(typeof(WpfControls.Border));
			border.SetBinding(WpfControls.Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
			border.SetValue(WpfControls.Border.CornerRadiusProperty, new Wpf.CornerRadius(3));

			// TEMP DIAGNOSTIC: Rectangle as Border's DIRECT single child (no Grid in between)
			// — testing whether the extra Grid nesting layer was the actual problem, since the
			// working BtnTemplate() is also just Border -> single child directly.
			var hBar = new Wpf.FrameworkElementFactory(typeof(System.Windows.Shapes.Rectangle));
			hBar.SetValue(System.Windows.Shapes.Shape.FillProperty, WpfMedia.Brushes.Red);
			hBar.SetValue(Wpf.FrameworkElement.WidthProperty, 18.0);
			hBar.SetValue(Wpf.FrameworkElement.HeightProperty, 18.0);
			hBar.SetValue(Wpf.FrameworkElement.HorizontalAlignmentProperty, Wpf.HorizontalAlignment.Center);
			hBar.SetValue(Wpf.FrameworkElement.VerticalAlignmentProperty, Wpf.VerticalAlignment.Center);
			border.AppendChild(hBar);

			var template = new WpfControls.ControlTemplate(typeof(WpfControls.Button)) { VisualTree = border };

			if (plus) _spinBtnTemplatePlus = template; else _spinBtnTemplateMinus = template;
			return template;
		}

		private WpfControls.Button MakeSpinBtn(bool plus)
		{
			// No Content is set — the icon is baked into SpinBtnTemplate's visual tree
			// directly, so this button doesn't route anything through ContentPresenter.
			return new WpfControls.Button { Background = Rgb(0x2E, 0x2E, 0x3A), OverridesDefaultStyle = true, Template = SpinBtnTemplate(plus), BorderThickness = new Wpf.Thickness(0), Width = 20, Height = 22, Margin = new Wpf.Thickness(1, 0, 1, 0), Cursor = System.Windows.Input.Cursors.Hand };
		}

		private WpfControls.Button MakeBtn(string text, WpfMedia.Brush bg)
		{
			WpfControls.TextBlock ignore;
			return MakeBtn(text, bg, out ignore);
		}

		// Buttons use a private ControlTemplate (+ OverridesDefaultStyle) so NinjaTrader's
		// themed Button style can't suppress the content. Background binds to the button's
		// own Background; the ContentPresenter shows our white-foreground TextBlock.
		private WpfControls.Button MakeBtn(string text, WpfMedia.Brush bg, out WpfControls.TextBlock label)
		{
			label = new WpfControls.TextBlock { Text = text, Foreground = WpfMedia.Brushes.White, FontWeight = Wpf.FontWeights.Bold, FontSize = 12, HorizontalAlignment = Wpf.HorizontalAlignment.Center, VerticalAlignment = Wpf.VerticalAlignment.Center };
			return new WpfControls.Button { Content = label, Background = bg, OverridesDefaultStyle = true, Template = BtnTemplate(), BorderThickness = new Wpf.Thickness(0), Height = 28, Margin = new Wpf.Thickness(0, 3, 0, 0), Cursor = System.Windows.Input.Cursors.Hand };
		}

		private WpfControls.ControlTemplate BtnTemplate()
		{
			if (_btnTemplate != null) return _btnTemplate;
			var border = new Wpf.FrameworkElementFactory(typeof(WpfControls.Border));
			border.SetBinding(WpfControls.Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
			border.SetValue(WpfControls.Border.CornerRadiusProperty, new Wpf.CornerRadius(3));
			var cp = new Wpf.FrameworkElementFactory(typeof(WpfControls.ContentPresenter));
			cp.SetValue(WpfControls.ContentPresenter.HorizontalAlignmentProperty, Wpf.HorizontalAlignment.Center);
			cp.SetValue(WpfControls.ContentPresenter.VerticalAlignmentProperty, Wpf.VerticalAlignment.Center);
			border.AppendChild(cp);
			_btnTemplate = new WpfControls.ControlTemplate(typeof(WpfControls.Button)) { VisualTree = border };
			return _btnTemplate;
		}

		private static WpfMedia.SolidColorBrush Rgb(byte r, byte g, byte b)
		{
			return new WpfMedia.SolidColorBrush(WpfMedia.Color.FromRgb(r, g, b));
		}

		// Updates control text, enabled/visibility state from the current panel state.
		private void RefreshPanel()
		{
			if (_wpfPanel == null) return;
			if (!_wpfPanel.Dispatcher.CheckAccess()) { _wpfPanel.Dispatcher.InvokeAsync(RefreshPanel); return; }

			bool idle = IsIdle, tracking = IsTracking, locked = IsLocked, hasSetup = HasSetup;
			bool dollars = TpSlUnit == OffsetUnit.Dollars && Instrument != null;

			if (dollars)
			{
				_tpLabel.Text = "TP $";
				_slLabel.Text = "SL $";
				if (!_tpValue.IsKeyboardFocused) _tpValue.Text = (TicksToDollars(TpTicks) * OrderQuantity).ToString("N0");
				if (!_slValue.IsKeyboardFocused) _slValue.Text = (TicksToDollars(SlTicks) * OrderQuantity).ToString("N0");
			}
			else
			{
				_tpLabel.Text = "TP Ticks";
				_slLabel.Text = "SL Ticks";
				if (!_tpValue.IsKeyboardFocused) _tpValue.Text = TpTicks.ToString();
				if (!_slValue.IsKeyboardFocused) _slValue.Text = SlTicks.ToString();
			}
			if (!_qtyValue.IsKeyboardFocused) _qtyValue.Text = OrderQuantity.ToString();

			// Values are editable only while idle (ticks/qty are locked once a setup is active).
			_tpValue.IsReadOnly = !idle; _slValue.IsReadOnly = !idle; _qtyValue.IsReadOnly = !idle;
			_tpValue.Opacity = idle ? 1.0 : 0.4; _slValue.Opacity = idle ? 1.0 : 0.4; _qtyValue.Opacity = idle ? 1.0 : 0.4;
			foreach (var b in _spinButtons) b.IsEnabled = idle;

			_unitToggleText.Text = dollars ? "Units: $  (tap for Ticks)" : "Units: Ticks  (tap for $)";

			_buyBtn.IsEnabled = !hasSetup;
			_sellBtn.IsEnabled = !hasSetup;
			_placeBtn.IsEnabled = tracking;
			_placeText.Text = locked ? "ORDER SENT" : "PLACE ORDER";

			_closeBtn.Visibility = locked ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
			// CANCEL only while tracking (pre-submit). Once locked, cancelling would pull
			// the bracket and leave a naked position — CLOSE TRADE is the correct exit then.
			_cancelBtn.IsEnabled = tracking;

			// The button ControlTemplate overrides the default style (OverridesDefaultStyle),
			// so WPF's built-in disabled dimming never applies — IsEnabled=false blocked clicks
			// but looked identical to enabled. Opacity makes the disabled state visible.
			_buyBtn.Opacity = _buyBtn.IsEnabled ? 1.0 : 0.4;
			_sellBtn.Opacity = _sellBtn.IsEnabled ? 1.0 : 0.4;
			_placeBtn.Opacity = _placeBtn.IsEnabled ? 1.0 : 0.4;
			_cancelBtn.Opacity = _cancelBtn.IsEnabled ? 1.0 : 0.4;
			foreach (var b in _spinButtons) b.Opacity = b.IsEnabled ? 1.0 : 0.4;

			_statusText.Text = idle ? "Idle — set ticks & qty"
				: tracking ? ("TRACKING " + (_state == TrackingState.TrackingLong ? "LONG" : "SHORT") + " — drag TP/SL")
				: "ORDER PLACED";

			_acctText.Text = "Acct: " + (_lastAccountName.Length > 0 ? _lastAccountName : "(none)");
		}

		// Converts a manually-typed value (ticks or dollars, per the unit mode) to a tick
		// offset. Dollar input is divided by the per-tick dollar value for the position size.
		private bool TryParseOffset(string text, int min, out int ticks)
		{
			ticks = min;
			double val;
			if (!double.TryParse(text.Replace("$", "").Replace(",", "").Trim(), out val)) return false;
			if (TpSlUnit == OffsetUnit.Dollars && Instrument != null)
			{
				double perTick = TicksToDollars(1) * Math.Max(1, OrderQuantity);
				ticks = perTick > 0 ? (int)Math.Round(val / perTick) : (int)Math.Round(val);
			}
			else ticks = (int)Math.Round(val);
			ticks = Math.Max(min, ticks);
			return true;
		}

		private void CommitTpSl(bool isTp, string text)
		{
			if (!IsIdle) { RefreshPanel(); return; }
			int ticks;
			if (TryParseOffset(text, 1, out ticks)) { if (isTp) TpTicks = ticks; else SlTicks = ticks; }
			RefreshPanel();
		}

		private void HandleSpin(int idx)
		{
			if (!IsIdle) return; // ticks/qty are locked once a setup is active
			switch (idx)
			{
				case 0: TpTicks = Math.Max(1, TpTicks - 1); break;
				case 1: TpTicks++; break;
				case 2: SlTicks = Math.Max(1, SlTicks - 1); break;
				case 3: SlTicks++; break;
				case 4: OrderQuantity = Math.Max(1, OrderQuantity - 1); break;
				case 5: OrderQuantity++; break;
			}
			RefreshPanel();
		}

		private void OnUnitToggleClick()
		{
			TpSlUnit = TpSlUnit == OffsetUnit.Ticks ? OffsetUnit.Dollars : OffsetUnit.Ticks;
			RefreshPanel();
		}

		private void OnBuyClick()
		{
			if (HasSetup) return;
			_state = TrackingState.TrackingLong; _tpUserDrag = false; _slUserDrag = false;
			UpdateTrackingPrices(); RefreshPanel(); _chartControlRef?.InvalidateVisual();
		}

		private void OnSellClick()
		{
			if (HasSetup) return;
			_state = TrackingState.TrackingShort; _tpUserDrag = false; _slUserDrag = false;
			UpdateTrackingPrices(); RefreshPanel(); _chartControlRef?.InvalidateVisual();
		}

		private void OnPlaceClick()
		{
			if (!IsTracking) return;
			LockAndPlaceOrder(); RefreshPanel(); _chartControlRef?.InvalidateVisual();
		}

		private void OnCloseClick()
		{
			if (!IsLocked) return;
			ClosePosition(); RefreshPanel(); _chartControlRef?.InvalidateVisual();
		}

		private void OnCancelClick()
		{
			if (!IsTracking) return; // locked setups must use CLOSE TRADE, not CANCEL
			ResetToIdle(); RefreshPanel(); _chartControlRef?.InvalidateVisual();
		}

		#endregion

		#region SharpDX Rendering (TP/SL lines only)

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			base.OnRender(chartControl, chartScale);
			if (chartControl == null || chartScale == null) return;
			var rt = RenderTarget; if (rt == null) return;
			_lastChartScale = chartScale;
			EnsureResources(rt);
			GetSelectedAccount();
			if (_wpfPanel != null && _lastAccountName != _shownAcctName)
			{
				_shownAcctName = _lastAccountName;
				_wpfPanel.Dispatcher.InvokeAsync(RefreshPanel);
			}
			if (IsTracking) DrawTrackingLines(rt, chartScale);
		}

		private void DrawTrackingLines(RenderTarget rt, ChartScale cs)
		{
			float L = (float)ChartPanel.X, R = (float)(ChartPanel.X + ChartPanel.W);
			float eY = (float)cs.GetYByValue(_liveEntryPrice);
			float tY = (float)cs.GetYByValue(_liveTpPrice);
			float sY = (float)cs.GetYByValue(_liveSlPrice);
			bool tA = _hoverLine == DragTarget.TP || _dragging == DragTarget.TP;
			bool sA = _hoverLine == DragTarget.SL || _dragging == DragTarget.SL;

			rt.DrawLine(new Vector2(L, eY), new Vector2(R, eY), _entryLineBrush, 2f);
			rt.DrawLine(new Vector2(L, tY), new Vector2(R, tY), _tpLineBrush, tA ? 3f : 2f, _dashStyle);
			rt.DrawLine(new Vector2(L, sY), new Vector2(R, sY), _slLineBrush, sA ? 3f : 2f, _dashStyle);

			float gripX = R - 40;
			if (tA) DrawGrip(rt, gripX, tY, _tpLineBrush);
			if (sA) DrawGrip(rt, gripX, sY, _slLineBrush);

			double tpPnl = TicksToDollars(TpTicksCurrent) * OrderQuantity;
			double slPnl = TicksToDollars(SlTicksCurrent) * OrderQuantity;

			float anc = R - 25;
			DrawPriceTag(rt, anc, eY, FormatPrice(_liveEntryPrice), _entryLineBrush, _textBrush);
			DrawPriceTag(rt, anc, tY, FormatPrice(_liveTpPrice) + "  TP " + TpTicksCurrent + "t  " + FmtDollars(tpPnl), _tpLineBrush, _profitBrush);
			DrawPriceTag(rt, anc, sY, FormatPrice(_liveSlPrice) + "  SL " + SlTicksCurrent + "t  " + FmtDollars(-slPnl), _slLineBrush, _lossBrush);
		}

		private void DrawGrip(RenderTarget rt, float cx, float cy, SharpDX.Direct2D1.SolidColorBrush b)
		{
			for (int i = -1; i <= 1; i++) { float y = cy + i * 3f; rt.DrawLine(new Vector2(cx - 5, y), new Vector2(cx + 5, y), b, 1.5f); }
		}

		private void DrawPriceTag(RenderTarget rt, float rAnc, float y, string txt, SharpDX.Direct2D1.SolidColorBrush border, SharpDX.Direct2D1.SolidColorBrush fg)
		{
			float w = 220f, h = 18f, x = rAnc - w, ly = y - h / 2f;
			var r = new SharpDX.RectangleF(x, ly, w, h);
			var rr = new RoundedRectangle { Rect = r, RadiusX = 3f, RadiusY = 3f };
			rt.FillRoundedRectangle(rr, _priceLabelBgBrush);
			rt.DrawRoundedRectangle(rr, border, 1f);
			rt.DrawText(txt, _priceLabelFormat, r, fg);
		}

		#endregion

		#region Resources

		private void EnsureResources(RenderTarget rt)
		{
			if (_entryLineBrush != null) return;
			_textBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.92f, 0.92f, 0.95f, 1f));
			_priceLabelBgBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.12f, 0.12f, 0.16f, 0.90f));
			_profitBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.10f, 0.90f, 0.40f, 1f));
			_lossBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.95f, 0.25f, 0.25f, 1f));
			_entryLineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.12f, 0.56f, 1.00f, 1f));
			_tpLineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.20f, 0.80f, 0.20f, 1f));
			_slLineBrush = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.86f, 0.08f, 0.24f, 1f));
			_dashStyle = new StrokeStyle(rt.Factory, new StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });

			var dw = Core.Globals.DirectWriteFactory;
			_priceLabelFormat = new SharpDX.DirectWrite.TextFormat(dw, "Consolas", SharpDX.DirectWrite.FontWeight.Normal, SharpDX.DirectWrite.FontStyle.Normal, SharpDX.DirectWrite.FontStretch.Normal, 10f)
			{ TextAlignment = SharpDX.DirectWrite.TextAlignment.Center, ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center };
		}

		private void DisposeResources()
		{
			_textBrush?.Dispose(); _textBrush = null;
			_priceLabelBgBrush?.Dispose(); _priceLabelBgBrush = null;
			_profitBrush?.Dispose(); _profitBrush = null;
			_lossBrush?.Dispose(); _lossBrush = null;
			_entryLineBrush?.Dispose(); _entryLineBrush = null;
			_tpLineBrush?.Dispose(); _tpLineBrush = null;
			_slLineBrush?.Dispose(); _slLineBrush = null;
			_dashStyle?.Dispose(); _dashStyle = null;
			_priceLabelFormat?.Dispose(); _priceLabelFormat = null;
		}

		public override void OnRenderTargetChanged() { DisposeResources(); }

		#endregion

		#region Helpers
		private string FormatPrice(double p) { return Instrument.MasterInstrument.FormatPrice(p); }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private ItCodeNerd.ICNOrderPanel[] cacheICNOrderPanel;
		public ItCodeNerd.ICNOrderPanel ICNOrderPanel(int tpTicks, int slTicks, int orderQuantity, System.Windows.Media.Brush entryLineColor, System.Windows.Media.Brush tpLineColor, System.Windows.Media.Brush slLineColor, PanelPosition panelSide, OffsetUnit tpSlUnit)
		{
			return ICNOrderPanel(Input, tpTicks, slTicks, orderQuantity, entryLineColor, tpLineColor, slLineColor, panelSide, tpSlUnit);
		}

		public ItCodeNerd.ICNOrderPanel ICNOrderPanel(ISeries<double> input, int tpTicks, int slTicks, int orderQuantity, System.Windows.Media.Brush entryLineColor, System.Windows.Media.Brush tpLineColor, System.Windows.Media.Brush slLineColor, PanelPosition panelSide, OffsetUnit tpSlUnit)
		{
			if (cacheICNOrderPanel != null)
				for (int idx = 0; idx < cacheICNOrderPanel.Length; idx++)
					if (cacheICNOrderPanel[idx] != null && cacheICNOrderPanel[idx].TpTicks == tpTicks && cacheICNOrderPanel[idx].SlTicks == slTicks && cacheICNOrderPanel[idx].OrderQuantity == orderQuantity && cacheICNOrderPanel[idx].EntryLineColor == entryLineColor && cacheICNOrderPanel[idx].TpLineColor == tpLineColor && cacheICNOrderPanel[idx].SlLineColor == slLineColor && cacheICNOrderPanel[idx].PanelSide == panelSide && cacheICNOrderPanel[idx].TpSlUnit == tpSlUnit && cacheICNOrderPanel[idx].EqualsInput(input))
						return cacheICNOrderPanel[idx];
			return CacheIndicator<ItCodeNerd.ICNOrderPanel>(new ItCodeNerd.ICNOrderPanel(){ TpTicks = tpTicks, SlTicks = slTicks, OrderQuantity = orderQuantity, EntryLineColor = entryLineColor, TpLineColor = tpLineColor, SlLineColor = slLineColor, PanelSide = panelSide, TpSlUnit = tpSlUnit }, input, ref cacheICNOrderPanel);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.ItCodeNerd.ICNOrderPanel ICNOrderPanel(int tpTicks, int slTicks, int orderQuantity, System.Windows.Media.Brush entryLineColor, System.Windows.Media.Brush tpLineColor, System.Windows.Media.Brush slLineColor, PanelPosition panelSide, OffsetUnit tpSlUnit)
		{
			return indicator.ICNOrderPanel(Input, tpTicks, slTicks, orderQuantity, entryLineColor, tpLineColor, slLineColor, panelSide, tpSlUnit);
		}

		public Indicators.ItCodeNerd.ICNOrderPanel ICNOrderPanel(ISeries<double> input , int tpTicks, int slTicks, int orderQuantity, System.Windows.Media.Brush entryLineColor, System.Windows.Media.Brush tpLineColor, System.Windows.Media.Brush slLineColor, PanelPosition panelSide, OffsetUnit tpSlUnit)
		{
			return indicator.ICNOrderPanel(input, tpTicks, slTicks, orderQuantity, entryLineColor, tpLineColor, slLineColor, panelSide, tpSlUnit);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.ItCodeNerd.ICNOrderPanel ICNOrderPanel(int tpTicks, int slTicks, int orderQuantity, System.Windows.Media.Brush entryLineColor, System.Windows.Media.Brush tpLineColor, System.Windows.Media.Brush slLineColor, PanelPosition panelSide, OffsetUnit tpSlUnit)
		{
			return indicator.ICNOrderPanel(Input, tpTicks, slTicks, orderQuantity, entryLineColor, tpLineColor, slLineColor, panelSide, tpSlUnit);
		}

		public Indicators.ItCodeNerd.ICNOrderPanel ICNOrderPanel(ISeries<double> input , int tpTicks, int slTicks, int orderQuantity, System.Windows.Media.Brush entryLineColor, System.Windows.Media.Brush tpLineColor, System.Windows.Media.Brush slLineColor, PanelPosition panelSide, OffsetUnit tpSlUnit)
		{
			return indicator.ICNOrderPanel(input, tpTicks, slTicks, orderQuantity, entryLineColor, tpLineColor, slLineColor, panelSide, tpSlUnit);
		}
	}
}

#endregion
