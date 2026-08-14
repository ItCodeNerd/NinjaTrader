// TickStreamServer.cs — minimal tick relay with a Control Center settings screen.
//
// Subscribes to ONE instrument's live MarketData feed and pushes every
// trade/bid/ask as a newline-delimited JSON line to any TCP client connected
// on 127.0.0.1:5555. No files consumed externally, no polling — an external
// process just opens the socket and reads lines as they arrive.
//
// Symbol is configurable from within NinjaTrader: Control Center menu bar
// gets a "Tick Stream" entry that opens a small window to type/save the
// instrument. Changing it live re-subscribes without restarting NinjaTrader.
//
// HISTORY, two ways:
//  1. Push, from the settings window ("Send History") — broadcasts bars to
//     every connected client.
//  2. Pull, from a connected client — the client sends one request line:
//       {"cmd":"history","symbol":"NQ 06-26","from":"2026-08-01T00:00:00Z",
//        "to":"2026-08-05T00:00:00Z","barType":"Minute","barValue":1}
//     ("symbol" optional — defaults to the addon's currently configured
//     symbol). The addon replies to THAT client only.
//
// Both paths emit the same line shapes: {"kind":"bar",...} per bar, then one
// {"kind":"historyEnd",...} marker, or {"kind":"historyError",...} on
// failure. Live tick lines carry no "kind" field (back-compat) — treat any
// line without "kind" as a live tick.
//
// Install: copy into Documents\NinjaTrader 8\bin\Custom\AddOns\, compile
// (F5) in the NinjaScript editor.
#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
	public class TickStreamServer : AddOnBase
	{
		private const string DefaultSymbol = "NQ 06-26";
		private const int Port = 5555;

		// One entry per connected client. WriteLock serializes writes onto that
		// client's NetworkStream — live ticks (NT8 market-data thread), pushed
		// history (UI thread), and pulled history (this client's own read-loop
		// thread, via the BarsRequest callback thread) can all write concurrently.
		private class ClientConn
		{
			public NetworkStream Stream;
			public readonly object WriteLock = new object();
		}

		private TcpListener _listener;
		private Instrument _instrument;
		private Timer _resolveTimer;
		private string _symbol;
		private string _settingsPath;
		private string _enabledPath;
		private bool _streamEnabled = true;
		private NTMenuItem _menuItem;
		private readonly ConcurrentDictionary<TcpClient, ClientConn> _clients =
			new ConcurrentDictionary<TcpClient, ClientConn>();

		protected override void OnStateChange()
		{
			try
			{
				if (State == State.SetDefaults)
				{
					Name = "TickStreamServer";
				}
				else if (State == State.Configure)
				{
					_settingsPath = Path.Combine(Globals.UserDataDir, "NT8Bridge", "tickstream_symbol.txt");
					_enabledPath = Path.Combine(Globals.UserDataDir, "NT8Bridge", "tickstream_enabled.txt");
					_symbol = LoadSymbol();
					_streamEnabled = LoadEnabled();
					StartListener();
					if (_streamEnabled) StartResolveTimer();
				}
				else if (State == State.Terminated)
				{
					try { if (_resolveTimer != null) _resolveTimer.Dispose(); } catch { }
					Unsubscribe();
					try { if (_listener != null) _listener.Stop(); } catch { }
					foreach (var kv in _clients)
					{
						try { kv.Value.Stream.Dispose(); } catch { }
						try { kv.Key.Close(); } catch { }
					}
					_clients.Clear();
				}
			}
			catch { }
		}

		// ===== Control Center menu integration =====
		protected override void OnWindowCreated(Window window)
		{
			ControlCenter cc = window as ControlCenter;
			if (cc == null) return;
			try
			{
				_menuItem = new NTMenuItem { Header = "Tick Stream..." };
				_menuItem.Click += (s, e) => OpenSettingsWindow();
				cc.MainMenu.Add(_menuItem);
			}
			catch { }
		}

		protected override void OnWindowDestroyed(Window window)
		{
			ControlCenter cc = window as ControlCenter;
			if (cc == null || _menuItem == null) return;
			try { cc.MainMenu.Remove(_menuItem); } catch { }
			_menuItem = null;
		}

		private void OpenSettingsWindow()
		{
			var win = new Window
			{
				Title = "Tick Stream Settings",
				Width = 380,
				Height = 390,
				WindowStartupLocation = WindowStartupLocation.CenterScreen,
				ResizeMode = ResizeMode.NoResize,
			};

			var panel = new StackPanel { Margin = new Thickness(16) };

			// --- Symbol / live stream ---
			panel.Children.Add(new TextBlock { Text = "Instrument symbol (e.g. ES 12-25):", Margin = new Thickness(0, 0, 0, 6) });
			var textBox = new TextBox { Text = _symbol, Margin = new Thickness(0, 0, 0, 6), IsEnabled = _streamEnabled };
			panel.Children.Add(textBox);

			var enabledCheck = new CheckBox
			{
				Content = "Enable live tick stream",
				IsChecked = _streamEnabled,
				Margin = new Thickness(0, 4, 0, 4)
			};
			enabledCheck.Checked += (s, e) => textBox.IsEnabled = true;
			enabledCheck.Unchecked += (s, e) => textBox.IsEnabled = false;
			panel.Children.Add(enabledCheck);
			panel.Children.Add(new TextBlock
			{
				Text = "Disabling stops the live MarketData subscription entirely — no CPU/network cost when only pulling history.",
				Foreground = System.Windows.Media.Brushes.Gray,
				FontSize = 11,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0, 0, 0, 6)
			});

			var status = new TextBlock
			{
				Text = "Port " + Port + " | streaming: " + (!_streamEnabled ? "disabled" : (_instrument != null ? _instrument.FullName : "(waiting)")),
				Foreground = System.Windows.Media.Brushes.Gray,
				Margin = new Thickness(0, 0, 0, 10)
			};
			panel.Children.Add(status);

			var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
			var saveBtn = new Button { Content = "Save", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
			var cancelBtn = new Button { Content = "Cancel", Width = 80 };
			saveBtn.Click += (s, e) =>
			{
				bool wantEnabled = enabledCheck.IsChecked == true;
				string newSymbol = textBox.Text.Trim();

				SetStreamEnabled(wantEnabled);
				if (wantEnabled && !string.IsNullOrEmpty(newSymbol) && newSymbol != _symbol)
					ChangeSymbol(newSymbol);
				win.Close();
			};
			cancelBtn.Click += (s, e) => win.Close();
			buttonRow.Children.Add(saveBtn);
			buttonRow.Children.Add(cancelBtn);
			panel.Children.Add(buttonRow);

			panel.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });

			// --- Historical backfill (push to all connected clients) ---
			panel.Children.Add(new TextBlock { Text = "Send historical bars to connected clients:", Margin = new Thickness(0, 0, 0, 6) });

			var dateRow = new Grid();
			dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
			dateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			var fromPicker = new DatePicker { SelectedDate = DateTime.Today.AddDays(-5) };
			var toPicker = new DatePicker { SelectedDate = DateTime.Today };
			Grid.SetColumn(fromPicker, 0);
			Grid.SetColumn(toPicker, 2);
			dateRow.Children.Add(fromPicker);
			dateRow.Children.Add(toPicker);
			dateRow.Margin = new Thickness(0, 0, 0, 6);
			panel.Children.Add(dateRow);

			var periodRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
			var periodTypeBox = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
			periodTypeBox.Items.Add(BarsPeriodType.Minute);
			periodTypeBox.Items.Add(BarsPeriodType.Second);
			periodTypeBox.Items.Add(BarsPeriodType.Tick);
			periodTypeBox.Items.Add(BarsPeriodType.Day);
			periodTypeBox.SelectedItem = BarsPeriodType.Minute;
			var periodValueBox = new TextBox { Width = 50, Text = "1" };
			periodRow.Children.Add(new TextBlock { Text = "Bar type:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
			periodRow.Children.Add(periodTypeBox);
			periodRow.Children.Add(new TextBlock { Text = "Value:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
			periodRow.Children.Add(periodValueBox);
			panel.Children.Add(periodRow);

			var historyResult = new TextBlock { Text = "", Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
			var sendHistoryBtn = new Button { Content = "Send History", Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
			sendHistoryBtn.Click += (s, e) =>
			{
				DateTime? from = fromPicker.SelectedDate;
				DateTime? to = toPicker.SelectedDate;
				int periodValue;
				if (from == null || to == null)
				{
					historyResult.Text = "pick both From and To dates";
					return;
				}
				if (!int.TryParse(periodValueBox.Text.Trim(), out periodValue) || periodValue <= 0)
				{
					historyResult.Text = "bar value must be a positive integer";
					return;
				}
				BarsPeriodType periodType = (BarsPeriodType)periodTypeBox.SelectedItem;
				sendHistoryBtn.IsEnabled = false;
				historyResult.Text = "requesting...";

				Instrument inst = ResolveInstrument(_symbol);
				if (inst == null)
				{
					historyResult.Text = "instrument not resolved yet: " + _symbol;
					sendHistoryBtn.IsEnabled = true;
					return;
				}
				RunHistoryRequest(inst, from.Value, to.Value.AddDays(1).AddSeconds(-1), periodType, periodValue, Broadcast,
					(msg) => win.Dispatcher.BeginInvoke(new Action(() =>
					{
						historyResult.Text = msg;
						sendHistoryBtn.IsEnabled = true;
					})));
			};
			panel.Children.Add(sendHistoryBtn);
			panel.Children.Add(historyResult);

			win.Content = panel;
			win.ShowDialog();
		}

		private void ChangeSymbol(string newSymbol)
		{
			SaveSymbol(newSymbol);
			_symbol = newSymbol;
			if (!_streamEnabled) return;   // nothing subscribed to change while disabled
			Unsubscribe();
			StartResolveTimer();   // picks up _symbol and resubscribes once resolvable
		}

		// Turning the stream off stops the resolve timer AND drops the live MarketData
		// subscription — the whole point is to cost nothing when the user only wants history.
		// Turning it back on just restarts the normal resolve/subscribe path.
		private void SetStreamEnabled(bool enabled)
		{
			if (enabled == _streamEnabled) return;
			_streamEnabled = enabled;
			SaveEnabled(enabled);
			if (enabled)
			{
				StartResolveTimer();
			}
			else
			{
				try { if (_resolveTimer != null) _resolveTimer.Dispose(); } catch { }
				_resolveTimer = null;
				Unsubscribe();
			}
		}

		private bool LoadEnabled()
		{
			try
			{
				if (File.Exists(_enabledPath))
				{
					string s = File.ReadAllText(_enabledPath).Trim();
					if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
				}
			}
			catch { }
			return true;   // default: enabled, matches pre-existing behavior
		}

		private void SaveEnabled(bool enabled)
		{
			try
			{
				string dir = Path.GetDirectoryName(_enabledPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.WriteAllText(_enabledPath, enabled ? "true" : "false");
			}
			catch { }
		}

		private string LoadSymbol()
		{
			try
			{
				if (File.Exists(_settingsPath))
				{
					string s = File.ReadAllText(_settingsPath).Trim();
					if (!string.IsNullOrEmpty(s)) return s;
				}
			}
			catch { }
			return DefaultSymbol;
		}

		private void SaveSymbol(string symbol)
		{
			try
			{
				string dir = Path.GetDirectoryName(_settingsPath);
				if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
				File.WriteAllText(_settingsPath, symbol);
			}
			catch { }
		}

		private Instrument ResolveInstrument(string symbol)
		{
			if (_instrument != null && string.Equals(_instrument.FullName, symbol, StringComparison.OrdinalIgnoreCase))
				return _instrument;
			try { return Instrument.GetInstrument(symbol); } catch { return null; }
		}

		// ===== Historical bars — shared core =====
		// Fires a BarsRequest and streams the result through lineSink (Broadcast for the
		// settings-window push, or a single client's writer for a pull request). Non-blocking:
		// BarsRequest's callback fires on its own thread, caller never waits on it.
		private void RunHistoryRequest(Instrument inst, DateTime from, DateTime to, BarsPeriodType periodType,
			int periodValue, Action<string> lineSink, Action<string> onDone)
		{
			try
			{
				var req = new BarsRequest(inst, from, to);
				req.BarsPeriod = new BarsPeriod { BarsPeriodType = periodType, Value = periodValue };
				req.Request((r, errorCode, errorMessage) =>
				{
					if (errorCode != ErrorCode.NoError)
					{
						string msg = "request failed: " + errorCode + (string.IsNullOrEmpty(errorMessage) ? "" : " (" + errorMessage + ")");
						lineSink(ErrorLine(msg));
						if (onDone != null) onDone(msg);
						return;
					}
					int count = (r.Bars != null) ? r.Bars.Count : 0;
					for (int i = 0; i < count; i++)
						lineSink(BarLine(inst.FullName, periodType, periodValue,
							r.Bars.GetTime(i), r.Bars.GetOpen(i), r.Bars.GetHigh(i), r.Bars.GetLow(i), r.Bars.GetClose(i), r.Bars.GetVolume(i)));

					lineSink(HistoryEndLine(inst.FullName, periodType, periodValue, count));
					if (onDone != null) onDone("sent " + count + " bars (" + periodValue + " " + periodType + ") for " + inst.FullName);
				});
			}
			catch (Exception ex)
			{
				lineSink(ErrorLine("error: " + ex.Message));
				if (onDone != null) onDone("error: " + ex.Message);
			}
		}

		private static string BarLine(string symbol, BarsPeriodType periodType, int periodValue,
			DateTime time, double open, double high, double low, double close, long volume)
		{
			return "{\"kind\":\"bar\",\"symbol\":\"" + symbol
				+ "\",\"barType\":\"" + periodType + "\",\"barValue\":" + periodValue.ToString(CultureInfo.InvariantCulture)
				+ ",\"t\":\"" + time.ToUniversalTime().ToString("o")
				+ "\",\"o\":" + open.ToString(CultureInfo.InvariantCulture)
				+ ",\"h\":" + high.ToString(CultureInfo.InvariantCulture)
				+ ",\"l\":" + low.ToString(CultureInfo.InvariantCulture)
				+ ",\"c\":" + close.ToString(CultureInfo.InvariantCulture)
				+ ",\"v\":" + volume.ToString(CultureInfo.InvariantCulture) + "}\n";
		}

		private static string HistoryEndLine(string symbol, BarsPeriodType periodType, int periodValue, int count)
		{
			return "{\"kind\":\"historyEnd\",\"symbol\":\"" + symbol
				+ "\",\"barType\":\"" + periodType + "\",\"barValue\":" + periodValue.ToString(CultureInfo.InvariantCulture)
				+ ",\"count\":" + count.ToString(CultureInfo.InvariantCulture) + "}\n";
		}

		private static string ErrorLine(string message)
		{
			return "{\"kind\":\"historyError\",\"message\":\"" + message.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}\n";
		}

		// ===== Tick plumbing =====
		private void StartListener()
		{
			_listener = new TcpListener(IPAddress.Loopback, Port);
			_listener.Start();
			_listener.BeginAcceptTcpClient(OnAccept, null);
		}

		private void OnAccept(IAsyncResult ar)
		{
			try
			{
				TcpClient client = _listener.EndAcceptTcpClient(ar);
				var conn = new ClientConn { Stream = client.GetStream() };
				_clients[client] = conn;

				Thread reader = new Thread(() => ClientReadLoop(client, conn));
				reader.IsBackground = true;
				reader.Start();
			}
			catch { }
			finally
			{
				try { _listener.BeginAcceptTcpClient(OnAccept, null); } catch { }
			}
		}

		// One blocking read loop per client, off the NT8 UI/market-data threads. Each line in
		// is a pull request (currently only "cmd":"history"); malformed lines are ignored.
		private void ClientReadLoop(TcpClient client, ClientConn conn)
		{
			try
			{
				using (var reader = new StreamReader(conn.Stream, Encoding.UTF8, false, 1024, true))
				{
					string line;
					while ((line = reader.ReadLine()) != null)
					{
						if (string.IsNullOrWhiteSpace(line)) continue;
						HandleClientRequest(conn, line);
					}
				}
			}
			catch { }
			finally
			{
				ClientConn removed;
				_clients.TryRemove(client, out removed);
				try { conn.Stream.Dispose(); } catch { }
				try { client.Close(); } catch { }
			}
		}

		private void HandleClientRequest(ClientConn conn, string line)
		{
			string cmd = ExtractJsonString(line, "cmd");
			if (cmd != "history") return;   // unknown/unsupported command — ignore

			string symbol = ExtractJsonString(line, "symbol");
			if (string.IsNullOrEmpty(symbol)) symbol = _symbol;
			string fromStr = ExtractJsonString(line, "from");
			string toStr = ExtractJsonString(line, "to");
			string barTypeStr = ExtractJsonString(line, "barType");
			if (string.IsNullOrEmpty(barTypeStr)) barTypeStr = "Minute";
			int barValue = ExtractJsonInt(line, "barValue", 1);

			DateTime fromUtc, toUtc;
			var styles = System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal;
			if (string.IsNullOrEmpty(fromStr) || string.IsNullOrEmpty(toStr)
				|| !DateTime.TryParse(fromStr, CultureInfo.InvariantCulture, styles, out fromUtc)
				|| !DateTime.TryParse(toStr, CultureInfo.InvariantCulture, styles, out toUtc))
			{
				WriteToConn(conn, ErrorLine("history request requires valid ISO-8601 UTC 'from' and 'to'"));
				return;
			}

			BarsPeriodType periodType;
			try { periodType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), barTypeStr, true); }
			catch { WriteToConn(conn, ErrorLine("unknown barType: " + barTypeStr)); return; }

			Instrument inst = ResolveInstrument(symbol);
			if (inst == null) { WriteToConn(conn, ErrorLine("instrument not found: " + symbol)); return; }

			RunHistoryRequest(inst, fromUtc.ToLocalTime(), toUtc.ToLocalTime(), periodType, barValue,
				(l) => WriteToConn(conn, l), null);
		}

		private static string ExtractJsonString(string json, string key)
		{
			if (json == null) return null;
			string pat = "\"" + key + "\"";
			int i = json.IndexOf(pat, StringComparison.Ordinal);
			if (i < 0) return null;
			int j = i + pat.Length;
			while (j < json.Length && (json[j] == ' ' || json[j] == ':')) j++;
			if (j >= json.Length || json[j] != '"') return null;
			j++;
			var sb = new StringBuilder();
			while (j < json.Length && json[j] != '"')
			{
				if (json[j] == '\\' && j + 1 < json.Length) { sb.Append(json[j + 1]); j += 2; }
				else { sb.Append(json[j]); j++; }
			}
			return sb.ToString();
		}

		private static int ExtractJsonInt(string json, string key, int def)
		{
			if (json == null) return def;
			string pat = "\"" + key + "\"";
			int i = json.IndexOf(pat, StringComparison.Ordinal);
			if (i < 0) return def;
			int j = i + pat.Length;
			while (j < json.Length && json[j] != ':') j++;
			j++;
			while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
			int start = j;
			while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
			int val;
			return (j > start && int.TryParse(json.Substring(start, j - start), out val)) ? val : def;
		}

		private void StartResolveTimer()
		{
			try { if (_resolveTimer != null) _resolveTimer.Dispose(); } catch { }
			_resolveTimer = new Timer(delegate { TrySubscribe(); }, null, 0, 2000);
		}

		private void TrySubscribe()
		{
			if (!_streamEnabled || _instrument != null) return;
			try
			{
				Instrument inst = Instrument.GetInstrument(_symbol);
				if (inst == null) return;
				_instrument = inst;
				_instrument.MarketData.Update += OnTick;   // auto-subscribes live feed
				try { _resolveTimer.Dispose(); } catch { }
			}
			catch { }
		}

		private void Unsubscribe()
		{
			try { if (_instrument != null) _instrument.MarketData.Update -= OnTick; } catch { }
			_instrument = null;
		}

		// Fires on an arbitrary NT8 thread per trade/bid/ask update.
		private void OnTick(object sender, MarketDataEventArgs e)
		{
			string type = e.MarketDataType == MarketDataType.Last ? "trade"
						: e.MarketDataType == MarketDataType.Bid ? "bid"
						: e.MarketDataType == MarketDataType.Ask ? "ask" : null;
			if (type == null) return;   // ignore DailyVolume/DailyHigh/etc.

			Broadcast("{\"ts\":\"" + e.Time.ToUniversalTime().ToString("o")
				+ "\",\"symbol\":\"" + _symbol
				+ "\",\"type\":\"" + type
				+ "\",\"price\":" + e.Price.ToString(CultureInfo.InvariantCulture)
				+ ",\"volume\":" + e.Volume.ToString(CultureInfo.InvariantCulture) + "}\n");
		}

		// Writes one line to every connected client (live ticks, and the settings-window push).
		private void Broadcast(string line)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(line);
			foreach (var kv in _clients)
				WriteBytes(kv.Key, kv.Value, bytes);
		}

		// Writes one line to a single client (pull-request replies).
		private void WriteToConn(ClientConn conn, string line)
		{
			WriteBytes(null, conn, Encoding.UTF8.GetBytes(line));
		}

		private void WriteBytes(TcpClient client, ClientConn conn, byte[] bytes)
		{
			try
			{
				lock (conn.WriteLock) { conn.Stream.Write(bytes, 0, bytes.Length); }
			}
			catch
			{
				if (client != null)
				{
					ClientConn removed;
					_clients.TryRemove(client, out removed);
					try { client.Close(); } catch { }
				}
			}
		}
	}
}
