using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using MinimalGCS.Connection;
using MinimalGCS.Mavlink;

namespace MinimalGCS
{
    [ComVisible(true)]
    public class MapBridge
    {
        private MainForm _main;
        public MapBridge(MainForm main) { _main = main; }
        public void TriggerAction(string action, string payload)
        {
            _main.Invoke((MethodInvoker)delegate {
                _main.HandleMapAction(action, payload);
            });
        }
    }

    public partial class MainForm : Form
    {
        private AutoConnector _connector;
        private FlowLayoutPanel _workArea;
        private WebBrowser _mapBrowser;
        private Label _lblSearching;
        private System.Windows.Forms.Timer _uiTicker;
        
        // PRIMARY/SECONDARY GCS Relay
        private Label _lblRelayStatus;
        private Label _lblMobileRelayStatus;
        private CheckBox _chkRelayEnabled;
        private volatile bool _relayActive = true;
        private volatile int _relayTxCount = 0;
        private volatile int _relayRxCount = 0;
        private volatile bool _mpConnected = false;
        private volatile bool _mobileRelayOnline = false;
        private volatile int _mobileClientCount = 0;
        private volatile string _mobileRelayUrl = "wss://agri-titan-relay.onrender.com/ws";
        
        // CENTRAL STATE MANAGER: One dictionary for all drones
        private ConcurrentDictionary<byte, DroneState> _drones = new ConcurrentDictionary<byte, DroneState>();
        private Dictionary<byte, AgriWorkPanel> _panels = new Dictionary<byte, AgriWorkPanel>();

        // WebSocket Client for Mobile Remote Control
        private ClientWebSocket? _wsClient;
        private CancellationTokenSource? _wsCts;

        public MainForm()
        {
            InitializeComponent();
            SetupAgriUI();

            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.Start();

            // UI UPDATE TIMER (10Hz) - Reads from stable state
            _uiTicker = new System.Windows.Forms.Timer { Interval = 100 };
            _uiTicker.Tick += Timer_Tick;
            _uiTicker.Start();

            // Start background WebSocket client to connect to Mobile Relay
            Task.Run(() => StartWebSocketClient());
        }

        private void SetupAgriUI()
        {
            this.Text = "AGRI-TITAN GCS v1.7.3 — PRIMARY";
            this.Size = new Size(1340, 780);
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.StartPosition = FormStartPosition.CenterScreen;

            // --- STATUS BAR (Bottom) ---
            var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = Color.FromArgb(25, 25, 25) };
            var lblPrimary = new Label { Text = "■ PRIMARY GCS", AutoSize = true, Location = new Point(10, 7), Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.FromArgb(40, 167, 69) };
            _chkRelayEnabled = new CheckBox { Text = "Mission Planner UDP Relay", AutoSize = true, Location = new Point(160, 6), Font = new Font("Segoe UI", 8.5f), ForeColor = Color.White, Checked = true, BackColor = Color.Transparent };
            _chkRelayEnabled.CheckedChanged += (s, e) => { _relayActive = _chkRelayEnabled.Checked; };
            _lblRelayStatus = new Label { Text = "MP RELAY: Waiting...", AutoSize = true, Location = new Point(385, 7), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), ForeColor = Color.Gray };
            _lblMobileRelayStatus = new Label { Text = "WEB APP: Offline", AutoSize = true, Location = new Point(700, 7), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), ForeColor = Color.Gray };
            statusBar.Controls.AddRange(new Control[] { lblPrimary, _chkRelayEnabled, _lblRelayStatus, _lblMobileRelayStatus });
            this.Controls.Add(statusBar);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 480,
                FixedPanel = FixedPanel.Panel1,
                IsSplitterFixed = false,
                BackColor = Color.FromArgb(40, 40, 40),
                SplitterWidth = 5,
                Panel1Collapsed = true
            };
            this.Controls.Add(split);

            // LEFT: Controls Panel
            _workArea = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(245, 245, 245),
                Padding = new Padding(8)
            };
            split.Panel1.Controls.Add(_workArea);

            // RIGHT: Satellite Map
            _mapBrowser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScrollBarsEnabled = false,
                WebBrowserShortcutsEnabled = false,
                ObjectForScripting = new MapBridge(this)
            };
            split.Panel2.Controls.Add(_mapBrowser);

            string mapPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");
            if (!System.IO.File.Exists(mapPath)) mapPath = @"a:\AGRI\Drone_Software\map.html";
            _mapBrowser.Navigate(new Uri(mapPath));

            _lblSearching = new Label
            {
                Text = "SCANNING FOR FLEET...",
                Size = new Size(370, 100),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12, FontStyle.Bold),
                ForeColor = Color.Gray
            };
            _workArea.Controls.Add(_lblSearching);

            btnSmartScan.Visible = cmbDrones.Visible = btnConnect.Visible = lblStatus.Visible = cmbActiveDrone.Visible = groupControl.Visible = false;
            lblWatermark.BringToFront();
        }

        public void ClearMapWaypoints()
        {
            try { _mapBrowser?.Document?.InvokeScript("clearWaypoints"); } catch { }
        }

        public void AddMapWaypoint(double lat, double lon, int index, int cmd)
        {
            try { _mapBrowser?.Document?.InvokeScript("addWaypoint", new object[] { lat, lon, index, cmd }); } catch { }
        }

        public void UpdateDroneMap(double lat, double lon, float heading)
        {
            try { _mapBrowser?.Document?.InvokeScript("updateDrone", new object[] { lat, lon, heading }); } catch { }
        }

        public void UpdateMissionHUD(int currentWp, int totalWp, string status, float groundSpeed)
        {
            try { _mapBrowser?.Document?.InvokeScript("updateMissionProgress", new object[] { currentWp, totalWp, status, groundSpeed }); } catch { }
        }

        private void OnDeviceConnected(DiscoveredDevice device)
        {
            this.Invoke((Action)(() =>
            {
                if (_lblSearching != null) { _workArea.Controls.Remove(_lblSearching); _lblSearching = null; }

                if (!_drones.ContainsKey(device.SysId))
                {
                    var state = new DroneState(device.SysId);
                    _drones.TryAdd(device.SysId, state);
                    
                    var panel = new AgriWorkPanel(device, state, this);
                    _panels[device.SysId] = panel;
                    _workArea.Controls.Add(panel);

                    // Auto refresh advanced settings on connection!
                    panel.TriggerRefreshFence();

                    // SINGLE READER PER DEVICE -> Dispatches to Central State
                    var parser = new MavLinkParser();
                    parser.PacketReceived += (pkt) => DispatchPacket(pkt);
                    device.Interface.OnDataReceived += (data) => parser.Parse(data);
                    device.Interface.StartReading();

                    // Force Telemetry Streams (Multiple methods for compatibility)
                    device.Interface.Send(MavLinkCommands.CreateRequestDataStream(255, 1, device.SysId, 0, 10, 1)); // ALL
                    device.Interface.Send(MavLinkCommands.CreateRequestDataStream(255, 1, device.SysId, 6, 10, 1)); // POSITION
                    device.Interface.Send(MavLinkCommands.CreateRequestDataStream(255, 1, device.SysId, 2, 5, 1));  // EXTENDED_STATUS

                    // Modern MAV_CMD_SET_MESSAGE_INTERVAL (10Hz for Alt)
                    device.Interface.Send(MavLinkCommands.CreateSetMessageInterval(255, 1, device.SysId, 33, 100000));
                    
                    // 2Hz for GPS_RAW_INT (24)
                    device.Interface.Send(MavLinkCommands.CreateSetMessageInterval(255, 1, device.SysId, 24, 500000));

                    // 2Hz for SERVO_OUTPUT_RAW (36)
                    device.Interface.Send(MavLinkCommands.CreateSetMessageInterval(255, 1, device.SysId, 36, 500000));

                    // --- PRIMARY GCS RELAY TO MISSION PLANNER (SECONDARY) ---
                    if (device.Interface is SerialInterface)
                    {
                        try
                        {
                            var udpRelay = new System.Net.Sockets.UdpClient();
                            var remoteEP = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);

                            // 1. Listen for Mission Planner commands on UDP 14551 -> forward to drone
                            Task.Run(() =>
                            {
                                try
                                {
                                    udpRelay.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 14551));
                                    while (device.Interface.IsOpen)
                                    {
                                        try
                                        {
                                            byte[] fromMP = udpRelay.Receive(ref remoteEP);
                                            if (fromMP.Length > 0 && device.Interface.IsOpen && _relayActive)
                                            {
                                                device.Interface.Send(fromMP);
                                                parser.Parse(fromMP);
                                                _relayRxCount++;
                                                _mpConnected = true;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                                try { udpRelay.Close(); } catch { }
                            });

                            // 2. Forward drone telemetry to Mission Planner on UDP 14550
                            device.Interface.OnDataReceived += (data) =>
                            {
                                if (!_relayActive) return;
                                try
                                {
                                    if (remoteEP != null && remoteEP.Address != System.Net.IPAddress.Any && remoteEP.Port != 0)
                                    {
                                        udpRelay.Send(data, data.Length, remoteEP);
                                    }
                                    else
                                    {
                                        udpRelay.Send(data, data.Length, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 14550));
                                    }
                                    _relayTxCount++;
                                }
                                catch { }
                            };

                            state.AddLog("PRIMARY GCS: UDP relay to Mission Planner started (14550/14551)");
                        }
                        catch { }
                    }
                }
            }));
        }

        private void DispatchPacket(MavLinkPacket pkt)
        {
            if (pkt.SystemId == 255) return;

            if (!_drones.TryGetValue(pkt.SystemId, out var state))
            {
                // Smart Fallback: If we only have one active drone connection, route all telemetry to it
                if (_drones.Count == 1)
                {
                    state = System.Linq.Enumerable.First(_drones.Values);
                }
                else
                {
                    return;
                }
            }

            if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID)
            {
                state.LastHeartbeat = DateTime.Now;
                state.Mode = BitConverter.ToUInt32(pkt.Payload, 0);
                bool armed = (pkt.Payload[6] & 128) != 0;
                if (state.IsArmed != armed) state.AddLog(armed ? "MOTORS ARMED" : "MOTORS DISARMED");
                state.IsArmed = armed;
            }
            else if (pkt.MessageId == 24 && pkt.Payload.Length >= 30) // GPS_RAW_INT
            {
                state.GpsFixType = pkt.Payload[28];
                state.SatellitesCount = pkt.Payload[29];
                state.Hdop = BitConverter.ToUInt16(pkt.Payload, 20) / 100.0f;
            }
            else if (pkt.MessageId == 124) // GPS2_RAW
            {
                if (pkt.Payload.Length > 32) state.GpsFixType = pkt.Payload[32];
            }
            else if (pkt.MessageId == 1 && pkt.Payload.Length >= 16) // SYS_STATUS
            {
                ushort voltageMv = BitConverter.ToUInt16(pkt.Payload, 14);
                float volt = voltageMv / 1000.0f;
                // EMA filter for stable voltage reading (alpha=0.1)
                state.Voltage = state.Voltage < 1f ? volt : state.Voltage * 0.9f + volt * 0.1f;
                float v = state.Voltage;
                
                // Highly accurate 3S LiPo capacity piecewise curve mapping
                if (v >= 12.7f) state.BatteryPercent = 100;
                else if (v <= 9.9f) state.BatteryPercent = 0;
                else
                {
                    if (v >= 12.0f) // 12.0V to 12.7V -> 85% to 100%
                        state.BatteryPercent = (int)(85 + (v - 12.0f) / 0.7f * 15);
                    else if (v >= 11.4f) // 11.4V to 12.0V -> 50% to 85%
                        state.BatteryPercent = (int)(50 + (v - 11.4f) / 0.6f * 35);
                    else if (v >= 11.1f) // 11.1V to 11.4V -> 20% to 50%
                        state.BatteryPercent = (int)(20 + (v - 11.1f) / 0.3f * 30);
                    else if (v >= 10.5f) // 10.5V to 11.1V -> 5% to 20%
                        state.BatteryPercent = (int)(5 + (v - 10.5f) / 0.6f * 15);
                    else // 9.9V to 10.5V -> 0% to 5%
                        state.BatteryPercent = (int)((v - 9.9f) / 0.6f * 5);
                }
            }
            else if (pkt.MessageId == 33 && pkt.Payload.Length >= 20) 
            {
                // GLOBAL_POSITION_INT: 12=Alt_MSL, 16=Alt_AGL (Relative)
                state.Lat = BitConverter.ToInt32(pkt.Payload, 4) / 10000000.0;
                state.Lon = BitConverter.ToInt32(pkt.Payload, 8) / 10000000.0;
                state.Alt = BitConverter.ToInt32(pkt.Payload, 16) / 1000.0f;
                if (pkt.Payload.Length >= 28)
                {
                    state.Heading = BitConverter.ToUInt16(pkt.Payload, 26) / 100.0f;
                }
                // Ground speed from VX/VY (cm/s) at offset 20,22
                if (pkt.Payload.Length >= 24)
                {
                    short vx = BitConverter.ToInt16(pkt.Payload, 20);
                    short vy = BitConverter.ToInt16(pkt.Payload, 22);
                    state.GroundSpeed = (float)Math.Sqrt(vx * vx + vy * vy) / 100.0f;
                }
                if (state.Alt > state.MaxAlt) state.MaxAlt = state.Alt;
            }
            else if (pkt.MessageId == 40 && pkt.Payload.Length >= 4) // MISSION_REQUEST
            {
                ushort seq = BitConverter.ToUInt16(pkt.Payload, 0);
                if (_panels.TryGetValue((byte)pkt.SystemId, out var panel))
                {
                    panel.HandleWaypointRequest(seq);
                }
            }
            else if (pkt.MessageId == 42 && pkt.Payload.Length >= 2) // MISSION_CURRENT
            {
                state.CurrentWp = BitConverter.ToUInt16(pkt.Payload, 0);
            }
            else if (pkt.MessageId == 76 && pkt.Payload.Length >= 30) // COMMAND_LONG
            {
                ushort command = BitConverter.ToUInt16(pkt.Payload, 28);
                if (command == 181) // MAV_CMD_DO_SET_RELAY
                {
                    float relayNum = BitConverter.ToSingle(pkt.Payload, 0);
                    float relayState = BitConverter.ToSingle(pkt.Payload, 4);
                    if (relayNum == 0) // Relay 1
                    {
                        state.Relay1 = (int)relayState;
                    }
                }
                else if (command == 183) // MAV_CMD_DO_SET_SERVO
                {
                    float channel = BitConverter.ToSingle(pkt.Payload, 0);
                    float pwm = BitConverter.ToSingle(pkt.Payload, 4);
                    if (channel == 9) // Servo 9 (AUX1)
                    {
                        state.Relay1 = pwm > 1500 ? 1 : 0;
                    }
                }
            }
            else if (pkt.MessageId == 22 && pkt.Payload.Length >= 25) // PARAM_VALUE
            {
                float val = BitConverter.ToSingle(pkt.Payload, 0);
                string paramId = System.Text.Encoding.ASCII.GetString(pkt.Payload, 8, 16).TrimEnd('\0');
                if (paramId.StartsWith("FENCE_ALT_MAX"))
                {
                    state.FenceAltMax = val;
                    state.AddLog($"DRONE FENCE ALT: {val}m");
                }
                else if (paramId.StartsWith("FENCE_RADIUS"))
                {
                    state.FenceRadius = val;
                    state.AddLog($"DRONE FENCE RADIUS: {val}m");
                }
            }
            else if (pkt.MessageId == 47 && pkt.Payload.Length >= 3) // MISSION_ACK
            {
                byte type = pkt.Payload[2];
                string ackStr = type == 0 ? "ACCEPTED (SUCCESS)" : "REJECTED (Code: " + type + ")";
                state.AddLog("MISSION STATUS: " + ackStr);
            }
            else if (pkt.MessageId == 253) // STATUSTEXT
            {
                string msg = System.Text.Encoding.ASCII.GetString(pkt.Payload, 1, pkt.Payload.Length - 1).TrimEnd('\0');
                state.AddLog(msg);
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            bool anyDroneConnected = false;
            foreach (var panel in _panels.Values)
            {
                if (_drones.TryGetValue((byte)panel.BaseSysId, out var state))
                {
                    panel.SyncWithState(state);

                    if (state.IsConnected)
                    {
                        anyDroneConnected = true;
                        // Push real-time telemetry to the gorgeous floating overlay inside map.html!
                        try
                        {
                            string pumpStr = state.Relay1 == 0 ? "ON" : "OFF";
                            _mapBrowser?.Document?.InvokeScript("updateTelemetry", new object[] {
                                state.SysId,
                                state.IsArmed,
                                GetModeName(state.Mode),
                                state.Alt,
                                state.MaxAlt,
                                state.BatteryPercent,
                                state.Voltage,
                                pumpStr,
                                state.GroundSpeed,
                                GetGpsStatusName(state.GpsFixType),
                                state.SatellitesCount,
                                state.Hdop,
                                state.Lat,
                                state.Lon,
                                state.LastMessage,
                                state.FenceAltMax,
                                state.FenceRadius,
                                _chkRelayEnabled.Checked,
                                _lblRelayStatus.Text
                            });
                        }
                        catch { }
                    }
                }
            }

            if (!anyDroneConnected)
            {
                try
                {
                    _mapBrowser?.Document?.InvokeScript("setConnectedState", new object[] { false });
                }
                catch { }
            }
            // Update relay status bar
            if (_relayActive && _relayTxCount > 0)
            {
                string mpStat = _mpConnected ? "CONNECTED" : "LISTENING";
                _lblRelayStatus.Text = $"MP RELAY: {mpStat} | TX: {_relayTxCount} | RX: {_relayRxCount}";
                _lblRelayStatus.ForeColor = _mpConnected ? Color.FromArgb(40, 167, 69) : Color.FromArgb(255, 193, 7);
            }
            else if (!_relayActive)
            {
                _lblRelayStatus.Text = "MP RELAY: DISABLED";
                _lblRelayStatus.ForeColor = Color.FromArgb(220, 53, 69);
            }
            else
            {
                _lblRelayStatus.Text = "MP RELAY: Waiting for Mission Planner...";
                _lblRelayStatus.ForeColor = Color.Gray;
            }

            if (_mobileRelayOnline)
            {
                _lblMobileRelayStatus.Text = _mobileClientCount > 0
                    ? $"WEB APP: Connected ({_mobileClientCount} mobile)"
                    : "WEB APP: Relay online, no mobile";
                _lblMobileRelayStatus.ForeColor = _mobileClientCount > 0 ? Color.FromArgb(40, 167, 69) : Color.FromArgb(255, 193, 7);
            }
            else
            {
                _lblMobileRelayStatus.Text = "WEB APP: Offline";
                _lblMobileRelayStatus.ForeColor = Color.FromArgb(220, 53, 69);
            }
        } 

        public string GetModeName(uint mode) => mode switch { 0 => "STABILIZE", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", 16 => "POSHOLD", _ => $"MODE({mode})" };
        public string GetGpsStatusName(int fixType) => fixType switch { 0 => "No GPS", 1 => "No Fix", 2 => "2D Fix", 3 => "3D Fix", 4 => "DGPS", 5 => "RTK Float", 6 => "RTK Fixed", _ => $"FIX({fixType})" };



        public void HandleMapAction(string action, string payload = "")
        {
            try
            {
                if (action == "TOGGLE_RELAY")
                {
                    bool isChecked = bool.Parse(payload);
                    _chkRelayEnabled.Checked = isChecked;
                    return;
                }

                foreach (var activePanel in _panels.Values)
                {
                    if (action == "START") activePanel.TriggerStart();
                    else if (action == "PAUSE") activePanel.TriggerPause();
                    else if (action == "RESUME") activePanel.TriggerResume();
                    else if (action == "RTL") activePanel.TriggerRTL();
                    else if (action == "LAND") activePanel.TriggerLand();
                    else if (action == "PUMP") activePanel.TriggerPump();
                    else if (action == "MISSION_MANAGER") activePanel.TriggerUploadWp();
                    else if (action == "DISARM") activePanel.TriggerDisarm();
                    else if (action == "REFRESH_FENCE") activePanel.TriggerRefreshFence();
                    else if (action == "APPLY_FENCE")
                    {
                        var parts = payload.Split(',');
                        if (parts.Length >= 3)
                        {
                            float alt = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                            float radius = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                            bool enabled = bool.Parse(parts[2]);
                            activePanel.TriggerApplyFence(alt, radius, enabled);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to execute map action: {ex.Message}", "Error");
            }
        }

        // --- MANAGES ONE DRONE'S UI AND COMMANDS ---
        public class AgriWorkPanel : Panel
        {
            private MainForm _main;
            private DroneState _state;
            private DiscoveredDevice _device;
            public int BaseSysId => _device.SysId;
            
            private Label _lblStatus, _lblTelemetry, _lblGPS, _lblMsg, _lblWarning;
            private Button _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency, _btnPump, _btnUploadWp, _btnRefreshFence, _btnApplyFence;
            private NumericUpDown _nudGeoAlt, _nudGeoRadius;
            private CheckBox _chkGeoFence;
            private float _geoHomeLat, _geoHomeLon;
            
            private Label lblTitle, lblGeo, lblMaxAlt, lblRadius, lblSwipe, lblLang;
            private TabPage tp1, tp2;
            private ComboBox _cbLanguage;
            private string _currentLang = "en";
            
            private enum PanelState { IDLE, BUSY }
            private PanelState _pState = PanelState.IDLE;

            private bool _isUploadingWaypoints = false;
            private int _uploadProgressSeq = 0;

            public bool IsUploadingWaypoints => _isUploadingWaypoints;
            public int UploadProgressPercent => _uploadQueue.Count > 0 ? (int)((float)_uploadProgressSeq / _uploadQueue.Count * 100) : 0;

            public AgriWorkPanel(DiscoveredDevice device, DroneState state, MainForm main)
            {
                _device = device; _state = state; _main = main;
                this.Size = new Size(450, 580); this.BorderStyle = BorderStyle.FixedSingle; this.BackColor = Color.White; this.Margin = new Padding(0, 0, 0, 15);
                InitializeControls();
            }

            public void TriggerStart() => _main.Invoke((MethodInvoker)async delegate { await CommandStartMission(); });
            public void TriggerPause() => _main.Invoke((MethodInvoker)delegate { _state.ResumeWp = _state.CurrentWp; SendSetMode(16); });
            public void TriggerResume() => _main.Invoke((MethodInvoker)delegate { SendSetMode(3); });
            public void TriggerRTL() => _main.Invoke((MethodInvoker)delegate { _state.ResumeWp = _state.CurrentWp; SendSetMode(6); });
            public void TriggerLand() => _main.Invoke((MethodInvoker)delegate { _state.ResumeWp = _state.CurrentWp; SendSetMode(9); });
            
            public void DeactivateButtonsForUpload(int totalWaypoints)
            {
                _isUploadingWaypoints = true;
                _uploadProgressSeq = 0;
                UpdateControlButtonsState(false);
            }

            public void UpdateControlButtonsState(bool enabled)
            {
                if (_btnStart != null) _btnStart.Enabled = enabled;
                if (_btnPause != null) _btnPause.Enabled = enabled;
                if (_btnResume != null) _btnResume.Enabled = enabled;
                if (_btnRTL != null) _btnRTL.Enabled = enabled;
                if (_btnLand != null) _btnLand.Enabled = enabled;
                if (_btnPump != null) _btnPump.Enabled = enabled;
                if (_btnUploadWp != null) _btnUploadWp.Enabled = enabled;
                if (_btnRefreshFence != null) _btnRefreshFence.Enabled = enabled;
                if (_btnApplyFence != null) _btnApplyFence.Enabled = enabled;
            }
            public void TriggerPump() => _main.Invoke((MethodInvoker)delegate {
                if (_state.Relay1 == 0)
                {
                    SendCmd(181, 0, 1);
                    _state.Relay1 = 1;
                    _state.AddLog("PUMP COMMAND: OFF");
                }
                else
                {
                    SendCmd(181, 0, 0);
                    _state.Relay1 = 0;
                    _state.AddLog("PUMP COMMAND: ON");
                }
            });
            public void TriggerUploadWp() => _main.Invoke((MethodInvoker)delegate {
                using (var frm = new MissionPlannerForm())
                {
                    if (frm.ShowDialog() == DialogResult.OK && frm.SelectedMission != null)
                    {
                        UploadMission(frm.SelectedMission);
                    }
                }
            });
            public void TriggerDisarm() => _main.Invoke((MethodInvoker)delegate {
                SendCmd(400, 0, 21196); // MAV_CMD_COMPONENT_ARM_DISARM: param1=0 (disarm), param2=21196 (force)
                _state.AddLog("EMERGENCY DISARM VIA MAP SLIDER!");
            });
            public void TriggerRefreshFence() => _main.Invoke((MethodInvoker)delegate { RefreshGeofenceConfig(); });
            public void TriggerApplyFence(float alt, float radius, bool enabled) => _main.Invoke((MethodInvoker)delegate {
                _nudGeoAlt.Value = (decimal)alt;
                _nudGeoRadius.Value = (decimal)radius;
                _chkGeoFence.Checked = enabled;
                ApplyGeofenceConfig();
            });

            private void InitializeControls()
            {
                // Create TabControl docked to fill the AgriWorkPanel
                var tc = new TabControl { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
                
                tp1 = new TabPage("Mission Control") { BackColor = Color.White };
                tp2 = new TabPage("Advanced Settings") { BackColor = Color.White };
                
                tc.TabPages.Add(tp1);
                tc.TabPages.Add(tp2);
                this.Controls.Add(tc);

                // --- TAB 1: MISSION CONTROL ---
                lblTitle = new Label { Text = $"DRONE #{_state.SysId}", Location = new Point(15, 10), Size = new Size(400, 25), Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.DarkGreen };
                _lblStatus = new Label { Text = "READY", Location = new Point(15, 35), Size = new Size(400, 30), Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.Blue };
                _lblTelemetry = new Label { Text = "...", Location = new Point(15, 70), Size = new Size(400, 35), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
                _lblGPS = new Label { Text = "Lat: 0.0000000  Lng: 0.0000000", Location = new Point(15, 105), Size = new Size(400, 20), Font = new Font("Segoe UI", 9, FontStyle.Regular), ForeColor = Color.DarkSlateGray };
                _lblMsg = new Label { Text = "Initializing...", Location = new Point(15, 125), Size = new Size(400, 20), Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Color.DarkSlateGray };

                // 🚀 Beautiful spacing & padding: 15px gap grid with standard height 44!
                _btnStart = CreateBtn("START MISSION", Color.FromArgb(40, 167, 69), 145);
                
                // Side-by-side layout for PAUSE and RESUME
                _btnPause = new Button { Text = "PAUSE", Location = new Point(25, 204), Size = new Size(190, 44), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, 193, 7), ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnPause.FlatAppearance.BorderSize = 0;
                
                _btnResume = new Button { Text = "RESUME", Location = new Point(235, 204), Size = new Size(190, 44), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(23, 162, 184), ForeColor = Color.White, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnResume.FlatAppearance.BorderSize = 0;

                _btnRTL = CreateBtn("RETURN HOME (RTL)", Color.FromArgb(108, 117, 125), 263);
                
                // Side-by-side layout for LAND NOW and PUMP CONTROL
                _btnLand = new Button { Text = "LAND NOW", Location = new Point(25, 322), Size = new Size(190, 44), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, 69, 0), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnLand.FlatAppearance.BorderSize = 0;

                _btnPump = new Button { Text = "PUMP: OFF", Location = new Point(235, 322), Size = new Size(190, 44), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(108, 117, 125), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnPump.FlatAppearance.BorderSize = 0;

                _btnUploadWp = CreateBtn("MISSION MANAGER", Color.FromArgb(0, 123, 255), 381);

                // --- SWIPE TO DISARM ---
                var pnlSwipe = new Panel { Location = new Point(25, 440), Size = new Size(400, 50), BackColor = Color.FromArgb(220, 53, 69), BorderStyle = BorderStyle.None };
                lblSwipe = new Label { Text = ">>> SWIPE TO DISARM >>>", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                var pnlHandle = new Panel { Location = new Point(2, 2), Size = new Size(80, 46), BackColor = Color.White, Cursor = Cursors.Hand };
                pnlSwipe.Controls.Add(pnlHandle);
                pnlSwipe.Controls.Add(lblSwipe);
                lblSwipe.SendToBack();

                bool isSwiping = false; int startX = 0;
                pnlHandle.MouseDown += (s, e) => { isSwiping = true; startX = e.X; };
                pnlHandle.MouseMove += (s, e) => 
                {
                    if (!isSwiping) return;
                    int newX = pnlHandle.Left + (e.X - startX);
                    if (newX < 2) newX = 2;
                    if (newX > pnlSwipe.Width - pnlHandle.Width - 2) newX = pnlSwipe.Width - pnlHandle.Width - 2;
                    pnlHandle.Left = newX;
                    if (pnlHandle.Left > pnlSwipe.Width - pnlHandle.Width - 10) 
                    {
                        isSwiping = false; pnlHandle.Left = 2;
                        SendCmd(400, 0, 21196); _pState = PanelState.IDLE;
                        _state.AddLog("EMERGENCY SWIPE TRIPPED!");
                    }
                };
                pnlHandle.MouseUp += (s, e) => { isSwiping = false; pnlHandle.Left = 2; };

                _btnStart.Click += async (s, e) => await CommandStartMission();
                _btnPause.Click += (s, e) => { _state.ResumeWp = _state.CurrentWp; SendSetMode(16); }; // POSHOLD
                _btnResume.Click += (s, e) => SendSetMode(3); // AUTO
                _btnRTL.Click += (s, e) => { _state.ResumeWp = _state.CurrentWp; SendSetMode(6); }; // RTL
                _btnLand.Click += (s, e) => { _state.ResumeWp = _state.CurrentWp; SendSetMode(9); }; // LAND

                _btnPump.Click += (s, e) =>
                {
                    if (_state.Relay1 == 0) // Pump is ON -> Turn it OFF
                    {
                        SendCmd(181, 0, 1);
                        _state.Relay1 = 1;
                        _state.AddLog("PUMP COMMAND: OFF");
                    }
                    else
                    {
                        SendCmd(181, 0, 0);
                        _state.Relay1 = 0;
                        _state.AddLog("PUMP COMMAND: ON");
                    }
                };

                _btnUploadWp.Click += (s, e) =>
                {
                    using (var frm = new MissionPlannerForm())
                    {
                        if (frm.ShowDialog() == DialogResult.OK && frm.SelectedMission != null)
                        {
                            UploadMission(frm.SelectedMission);
                        }
                    }
                };

                // --- WARNING LABEL ---
                _lblWarning = new Label { Text = "", Location = new Point(15, 505), Size = new Size(420, 20), Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Color.Red, Visible = false };

                tp1.Controls.AddRange(new Control[] { lblTitle, _lblStatus, _lblTelemetry, _lblGPS, _lblMsg, _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnPump, _btnUploadWp, pnlSwipe, _lblWarning });

                // --- TAB 2: ADVANCED SETTINGS (GEOFENCE & LANGUAGE) ---
                lblGeo = new Label { Text = "DRONE GEOFENCE CONFIG", Location = new Point(15, 15), Size = new Size(400, 20), Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.DarkSlateGray };
                _chkGeoFence = new CheckBox { Text = "Enable Geo-Fence Failsafe", Location = new Point(20, 45), Size = new Size(400, 22), Font = new Font("Segoe UI", 9.5f), Checked = true };
                
                lblMaxAlt = new Label { Text = "Max Altitude (m):", Location = new Point(20, 85), Size = new Size(130, 20), Font = new Font("Segoe UI", 9.5f) };
                _nudGeoAlt = new NumericUpDown { Location = new Point(160, 83), Size = new Size(80, 25), Minimum = 5, Maximum = 200, Value = 6, Font = new Font("Segoe UI", 9.5f) };
                
                lblRadius = new Label { Text = "Max Radius (m):", Location = new Point(20, 125), Size = new Size(130, 20), Font = new Font("Segoe UI", 9.5f) };
                _nudGeoRadius = new NumericUpDown { Location = new Point(160, 123), Size = new Size(80, 25), Minimum = 10, Maximum = 2000, Value = 200, Font = new Font("Segoe UI", 9.5f) };

                _btnRefreshFence = new Button { Text = "REFRESH CONFIG", Location = new Point(25, 175), Size = new Size(195, 45), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(108, 117, 125), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnRefreshFence.FlatAppearance.BorderSize = 0;

                _btnApplyFence = new Button { Text = "APPLY CONFIG", Location = new Point(230, 175), Size = new Size(195, 45), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(40, 167, 69), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnApplyFence.FlatAppearance.BorderSize = 0;

                _btnRefreshFence.Click += (s, e) => RefreshGeofenceConfig();
                _btnApplyFence.Click += (s, e) => ApplyGeofenceConfig();

                // 🌎 LANGUAGE SELECTOR
                lblLang = new Label { Text = "Select Language:", Location = new Point(20, 245), Size = new Size(130, 20), Font = new Font("Segoe UI", 9.5f) };
                _cbLanguage = new ComboBox { Location = new Point(160, 243), Size = new Size(150, 25), Font = new Font("Segoe UI", 9.5f), DropDownStyle = ComboBoxStyle.DropDownList };
                _cbLanguage.Items.AddRange(new object[] { "English", "ગુજરાતી" });
                _cbLanguage.SelectedIndex = 0;
                
                _cbLanguage.SelectedIndexChanged += (s, e) =>
                {
                    _currentLang = _cbLanguage.SelectedIndex == 1 ? "gu" : "en";
                    UpdateLanguageText();
                };

                tp2.Controls.AddRange(new Control[] { lblGeo, _chkGeoFence, lblMaxAlt, _nudGeoAlt, lblRadius, _nudGeoRadius, _btnRefreshFence, _btnApplyFence, lblLang, _cbLanguage });
            }

            public void SyncWithState(DroneState state)
            {
                _lblMsg.Text = state.LastMessage;
                string pumpStr = state.Relay1 == 0 ? (_currentLang == "gu" ? "ચાલુ" : "ON") : (_currentLang == "gu" ? "બંધ" : "OFF");
                
                string altText = _currentLang == "gu" ? "ઊંચાઈ" : "ALT";
                string battText = _currentLang == "gu" ? "બેટરી" : "BATT";
                string modeText = _currentLang == "gu" ? "મોડ" : "MODE";
                string spdText = _currentLang == "gu" ? "ઝડપ" : "SPD";
                string pumpText = _currentLang == "gu" ? "પંપ" : "PUMP";
                string gpsText = _currentLang == "gu" ? "જીપીએસ" : "GPS";
                string satText = _currentLang == "gu" ? "સેટેલાઇટ" : "Sats";
                
                _lblTelemetry.Text = $"{altText}: {state.Alt:F1}m (Max: {state.MaxAlt:F1}m) | {battText}: {state.BatteryPercent}% ({state.Voltage:F1}V)\n{modeText}: {(_currentLang == "gu" ? GetModeNameGujarati(state.Mode) : _main.GetModeName(state.Mode))} | {pumpText}: {pumpStr} | {spdText}: {state.GroundSpeed:F1}m/s";
                _lblTelemetry.ForeColor = state.IsArmed ? Color.DarkRed : Color.Black;
                
                _lblGPS.Text = $"{gpsText}: {(_currentLang == "gu" ? GetGpsStatusNameGujarati(state.GpsFixType) : _main.GetGpsStatusName(state.GpsFixType))} ({satText}: {state.SatellitesCount} | HDOP: {state.Hdop:F1}) | Lat: {state.Lat:F7} Lng: {state.Lon:F7}";
                
                // Track dynamic telemetry on map
                if (state.Lat != 0.0 && state.Lon != 0.0)
                {
                    _main.UpdateDroneMap(state.Lat, state.Lon, state.Heading);
                    _main.UpdateMissionHUD(state.CurrentWp, state.TotalWp, _lblStatus.Text, state.GroundSpeed);

                    // Store home position for geo-fence
                    if (_geoHomeLat == 0) { _geoHomeLat = (float)state.Lat; _geoHomeLon = (float)state.Lon; }

                    // --- GEO-FENCE CHECK ---
                    if (_chkGeoFence.Checked && state.IsArmed)
                    {
                        float maxAlt = (float)_nudGeoAlt.Value;
                        float maxRadius = (float)_nudGeoRadius.Value;
                        float distFromHome = HaversineDist(_geoHomeLat, _geoHomeLon, (float)state.Lat, (float)state.Lon);

                        if (state.Alt > maxAlt + 1.0f) // 1-meter safety buffer
                        {
                            _lblWarning.Text = _currentLang == "gu" ? $"જીઓફેન્સ ભંગ: ઊંચાઈ {state.Alt:F1}m > {maxAlt + 1.0f:F1}m! ઓટો-RTL" : $"GEOFENCE: ALT {state.Alt:F1}m > {maxAlt + 1.0f:F1}m! AUTO-RTL";
                            _lblWarning.Visible = true;
                            SendSetMode(6); // RTL
                            state.AddLog($"GEOFENCE BREACH: Alt {state.Alt:F1}m exceeded {maxAlt + 1.0f}m limit");
                        }
                        else if (distFromHome > maxRadius + 1.0f) // 1-meter safety buffer
                        {
                            _lblWarning.Text = _currentLang == "gu" ? $"જીઓફેન્સ ભંગ: અંતર {distFromHome:F1}m > {maxRadius + 1.0f:F1}m! ઓટો-RTL" : $"GEOFENCE: DIST {distFromHome:F1}m > {maxRadius + 1.0f:F1}m! AUTO-RTL";
                            _lblWarning.Visible = true;
                            SendSetMode(6); // RTL
                            state.AddLog($"GEOFENCE BREACH: Distance {distFromHome:F1}m exceeded {maxRadius + 1.0f}m radius");
                        }
                        else
                        {
                            _lblWarning.Visible = false;
                        }
                    }
                    else
                    {
                        _lblWarning.Visible = false;
                    }
                }

                // Update UI values if refreshed from drone (only when drone sends active non-zero configuration)
                if (state.FenceAltMax > 0 && state.FenceAltMax != (float)_nudGeoAlt.Value && !_nudGeoAlt.Focused)
                {
                    _nudGeoAlt.Value = (decimal)state.FenceAltMax;
                }
                if (state.FenceRadius > 0 && state.FenceRadius != (float)_nudGeoRadius.Value && !_nudGeoRadius.Focused)
                {
                    _nudGeoRadius.Value = (decimal)state.FenceRadius;
                }
                
                // --- SMART SPRAY LOGIC ENGINE ---
                if (ActiveMission != null && state.IsArmed && state.Mode == 3)
                {
                    float safeAlt = _chkGeoFence.Checked ? (float)_nudGeoAlt.Value - 10 : 2; // Rough spray alt
                    float geoRadius = _chkGeoFence.Checked ? (float)_nudGeoRadius.Value : float.MaxValue;
                    
                    int desiredRelay = SprayLogicEngine.EvaluatePumpState(state, ActiveMission, safeAlt, geoRadius);
                    
                    if (desiredRelay != _lastPumpState)
                    {
                        SendCmd(181, 0, desiredRelay); // MAV_CMD_DO_SET_RELAY: param1=0 (Relay 1), param2=desiredRelay
                        state.Relay1 = desiredRelay;
                        _lastPumpState = desiredRelay;
                        state.AddLog(desiredRelay == 0 ? "SMART PUMP: AUTO-ON" : "SMART PUMP: AUTO-OFF");
                    }
                }

                // Sync Pump button UI state
                _btnPump.Text = state.Relay1 == 0 ? (_currentLang == "gu" ? "પંપ: ચાલુ (ઓટો)" : "PUMP: ON (AUTO)") : (_currentLang == "gu" ? "પંપ: બંધ" : "PUMP: OFF");
                _btnPump.BackColor = state.Relay1 == 0 ? Color.FromArgb(40, 167, 69) : Color.FromArgb(108, 117, 125);
                
                if (!state.IsConnected) { _lblStatus.Text = _currentLang == "gu" ? "જોડાણ તૂટી ગયું" : "LOST CONNECTION"; _lblStatus.ForeColor = Color.Red; return; }
                
                if (_pState == PanelState.IDLE)
                {
                    if (state.Mode == 3) { _lblStatus.Text = _currentLang == "gu" ? "મિશન સક્રિય છે" : "MISSION ACTIVE"; _lblStatus.ForeColor = Color.Green; }
                    else if (state.Mode == 5 || state.Mode == 16) { _lblStatus.Text = _currentLang == "gu" ? "મિશન અટકાવેલ છે" : "MISSION PAUSED"; _lblStatus.ForeColor = Color.Orange; }
                    else { _lblStatus.Text = _currentLang == "gu" ? "તૈયાર" : "READY"; _lblStatus.ForeColor = Color.Blue; }
                    
                    if (!state.IsArmed)
                    {
                        _btnStart.Text = _currentLang == "gu" ? "મિશન શરૂ કરો" : "START MISSION";
                        _btnStart.BackColor = Color.FromArgb(40, 167, 69);
                        _btnStart.Visible = true;
                    }
                    else if (state.Mode != 3)
                    {
                        _btnStart.Text = _currentLang == "gu" ? "મિશન ફરી શરૂ કરો" : "RESUME MISSION";
                        _btnStart.BackColor = Color.FromArgb(0, 123, 255);
                        _btnStart.Visible = true;
                    }
                    else { _btnStart.Visible = false; }
                }

                _btnPause.Visible = (state.IsArmed && state.Mode == 3);
                _btnResume.Visible = false;

                if (!state.IsArmed && _pState == PanelState.IDLE) 
                { _lblStatus.Text = _currentLang == "gu" ? "તૈયાર" : "READY"; _lblStatus.ForeColor = Color.Blue; }
            }

            private float HaversineDist(float lat1, float lon1, float lat2, float lon2)
            {
                double R = 6371000;
                double dLat = (lat2 - lat1) * Math.PI / 180;
                double dLon = (lon2 - lon1) * Math.PI / 180;
                double a = Math.Sin(dLat/2)*Math.Sin(dLat/2) + Math.Cos(lat1*Math.PI/180)*Math.Cos(lat2*Math.PI/180)*Math.Sin(dLon/2)*Math.Sin(dLon/2);
                return (float)(R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a)));
            }

            private async Task CommandStartMission()
            {
                _pState = PanelState.BUSY;
                
                if (!_state.IsArmed)
                {
                    _lblStatus.Text = "LOITER MODE...";
                    _state.AddLog("ACTION -> SET LOITER");
                    SendSetMode(5); // LOITER mode
                    await Task.Delay(500);

                    _lblStatus.Text = "ARMING DRONE...";
                    _state.AddLog("ACTION -> ARMING");
                    SendCmd(400, 1, 21196); // ARM
                    await Task.Delay(1000); // Give it a second to arm

                    // Ask user confirmation
                    var result = MessageBox.Show(
                        "Drone is armed in LOITER mode.\nAre you sure you want to start the mission?", 
                        "Confirm Start Mission", 
                        MessageBoxButtons.YesNo, 
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button1,
                        MessageBoxOptions.DefaultDesktopOnly
                    );

                    if (result != DialogResult.Yes)
                    {
                        _lblStatus.Text = "DISARMING...";
                        _state.AddLog("MISSION CANCELED -> DISARMING");
                        SendCmd(400, 0, 21196); // DISARM for safety
                        await Task.Delay(500);
                        _pState = PanelState.IDLE;
                        _lblStatus.Text = "READY";
                        return;
                    }
                }

                _lblStatus.Text = "STARTING MISSION...";
                _state.AddLog("ACTION -> SET AUTO MODE");
                SendSetMode(3); // AUTO mode
                await Task.Delay(500);

                _state.AddLog($"ACTION -> START (WP: {_state.ResumeWp})");
                SendCmd(300, _state.ResumeWp, 0); // MISSION_START
                
                _state.ResumeWp = 0; // Reset after use
                _pState = PanelState.IDLE;
                _lblStatus.Text = "MISSION ACTIVE";
                _state.AddLog("COMMAND SENT.");
            }

            private List<WaypointItem> _uploadQueue = new List<WaypointItem>();
            public Mission ActiveMission { get; set; }
            private int _lastPumpState = 1;

            public void UploadMission(Mission mission, bool silent = false)
            {
                try
                {
                    if (mission.Waypoints.Count == 0)
                    {
                        if (!silent) MessageBox.Show("No waypoints found in mission!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    ActiveMission = mission;
                    _uploadQueue = mission.Waypoints;
                    _state.TotalWp = mission.Waypoints.Count;
                    _state.AddLog($"Loaded {mission.Waypoints.Count} Waypoints for '{mission.Name}'");

                    // --- DYNAMIC GEOFENCE AUTO-CONFIGURATION (+1m buffer) ---
                    float missionMaxAlt = 5.0f;
                    var altWps = mission.Waypoints.Where(w => w.Command == 16 || w.Command == 22).ToList();
                    if (altWps.Count > 0)
                    {
                        missionMaxAlt = altWps.Max(w => w.Alt);
                    }
                    float autoGeoAlt = missionMaxAlt + 1.0f;

                    double maxWpRadius = 10.0;
                    var takeoffWpForRadius = mission.Waypoints.FirstOrDefault(w => w.Command == 22 || w.Index == 1);
                    if (takeoffWpForRadius != null && takeoffWpForRadius.Lat != 0 && takeoffWpForRadius.Lon != 0)
                    {
                        foreach (var wp in mission.Waypoints.Where(w => w.Lat != 0 && w.Lon != 0))
                        {
                            double dist = Haversine(takeoffWpForRadius.Lat, takeoffWpForRadius.Lon, wp.Lat, wp.Lon);
                            if (dist > maxWpRadius) maxWpRadius = dist;
                        }
                    }
                    float autoGeoRadius = (float)(maxWpRadius + 1.0);

                    // Apply configured bounds with safety clamp
                    _nudGeoAlt.Value = (decimal)Math.Clamp(autoGeoAlt, (float)_nudGeoAlt.Minimum, (float)_nudGeoAlt.Maximum);
                    _nudGeoRadius.Value = (decimal)Math.Clamp(autoGeoRadius, (float)_nudGeoRadius.Minimum, (float)_nudGeoRadius.Maximum);
                    _chkGeoFence.Checked = true;

                    _state.AddLog($"GEOFENCE AUTO-CONFIG: Alt = {autoGeoAlt:F1}m, Radius = {autoGeoRadius:F1}m (+1m buffer applied)");
                    if (!silent) MessageBox.Show($"Mission uploaded successfully!\n\nAuto-Configured Geo-Fence Safeguards (+1m buffer):\n- Max Altitude: {autoGeoAlt:F1} meters\n- Max Radius: {autoGeoRadius:F1} meters", "Agri-Titan Safeguards Active", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // Render mission path on the map with command types
                    _main.ClearMapWaypoints();
                    foreach (var item in mission.Waypoints)
                    {
                        if (item.Lat != 0 && item.Lon != 0 && (item.Command == 16 || item.Command == 22 || item.Command == 21)) 
                        {
                            _main.AddMapWaypoint(item.Lat, item.Lon, item.Index, item.Command);
                        }
                    }

                    // Set drone home using takeoff waypoint
                    var takeoffWp = mission.Waypoints.FirstOrDefault(w => w.Command == 22 || w.Index == 1);
                    if (takeoffWp != null)
                    {
                        _geoHomeLat = (float)takeoffWp.Lat;
                        _geoHomeLon = (float)takeoffWp.Lon;
                        
                        // Send MAV_CMD_DO_SET_HOME (179) via MAVLink
                        byte[] setHomeCmd = MavLinkCommands.CreateCommandLong(
                            255, 1, (byte)BaseSysId, 1,
                            179, // MAV_CMD_DO_SET_HOME
                            0, 0, 0, 0, // params 1-4
                            (float)takeoffWp.Lat, (float)takeoffWp.Lon, takeoffWp.Alt // params 5-7
                        );
                        _device.Interface.Send(setHomeCmd);
                        _state.AddLog($"Set Drone Home to Takeoff: Lat {takeoffWp.Lat:F6}, Lon {takeoffWp.Lon:F6}");
                    }

                    // Start upload sequence: Send MISSION_COUNT to drone
                    byte[] countPkt = MavLinkCommands.CreateMissionCount((byte)255, 1, (byte)BaseSysId, 1, (ushort)mission.Waypoints.Count);
                    _device.Interface.Send(countPkt);
                    _state.AddLog($"Sent Waypoint Count: {mission.Waypoints.Count}");
                }
                catch (Exception ex)
                {
                    if (!silent) MessageBox.Show($"Failed to parse waypoint file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            private double Haversine(double lat1, double lon1, double lat2, double lon2)
            {
                double R = 6371000;
                double dLat = (lat2 - lat1) * Math.PI / 180;
                double dLon = (lon2 - lon1) * Math.PI / 180;
                double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
                return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            }

            public void HandleWaypointRequest(ushort seq)
            {
                if (seq < _uploadQueue.Count)
                {
                    _uploadProgressSeq = seq;
                    var wp = _uploadQueue[seq];
                    byte[] itemPkt = MavLinkCommands.CreateMissionItem(
                        (byte)255, 1, 
                        (byte)BaseSysId, 1, 
                        (ushort)wp.Index, 
                        (ushort)wp.Command, 
                        wp.Param1, wp.Param2, wp.Param3, wp.Param4, 
                        (float)wp.Lat, (float)wp.Lon, wp.Alt, 
                        (byte)wp.CoordFrame, 
                        (byte)wp.CurrentWp, 
                        (byte)wp.AutoContinue
                    );
                    _device.Interface.Send(itemPkt);
                    _state.AddLog($"Sent Waypoint #{seq} of {_uploadQueue.Count}");

                    // If upload finished, reactivate GCS buttons
                    if (seq == _uploadQueue.Count - 1)
                    {
                        _isUploadingWaypoints = false;
                        _uploadProgressSeq = 0;
                        this.Invoke((Action)(() => UpdateControlButtonsState(true)));
                        _state.AddLog("WAYPOINT UPLOAD COMPLETED!");
                    }
                }
            }

            private async Task<bool> ExecuteStep(string log, Action send, Func<bool> verify)
            {
                _state.AddLog(log);
                int retries = 5;
                while (retries-- > 0)
                {
                    send();
                    // High-frequency polling (50ms) for 1 second per retry
                    for (int i = 0; i < 20; i++) 
                    { 
                        if (verify()) return true; 
                        await Task.Delay(50); 
                    }
                }
                _state.AddLog($"Step Failed: {log}");
                return false;
            }

            private Button CreateBtn(string t, Color c, int y)
            {
                var b = new Button { Text = t, Location = new Point(25, y), Size = new Size(400, 50), BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0; return b;
            }

            public void SendSetMode(uint m) => _device.Interface.Send(MavLinkCommands.CreateSetMode(255, 1, _device.SysId, 1, m));
            public void SendCmd(ushort c, float p1, float p2=0, float p3=0, float p4=0, float p5=0, float p6=0, float p7=0) 
                => _device.Interface.Send(MavLinkCommands.CreateCommandLong(255, 1, _device.SysId, _device.CompId, c, p1, p2, p3, p4, p5, p6, p7));

            private void RefreshGeofenceConfig()
            {
                _state.AddLog("REFRESHING FENCE CONFIG FROM DRONE...");
                byte[] altReq = MavLinkCommands.CreateParamRequestRead(255, 1, (byte)BaseSysId, 1, "FENCE_ALT_MAX");
                _device.Interface.Send(altReq);
                byte[] radReq = MavLinkCommands.CreateParamRequestRead(255, 1, (byte)BaseSysId, 1, "FENCE_RADIUS");
                _device.Interface.Send(radReq);
            }

            private void ApplyGeofenceConfig()
            {
                var result = MessageBox.Show(
                    $"Confirm writing new Geofence configuration to Drone?\n\n" +
                    $"- Fence Enabled: {(_chkGeoFence.Checked ? "YES" : "NO")}\n" +
                    $"- Max Altitude: {_nudGeoAlt.Value} meters\n" +
                    $"- Max Radius: {_nudGeoRadius.Value} meters",
                    "Confirm Geofence Change",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );

                if (result == DialogResult.Yes)
                {
                    byte[] altPkt = MavLinkCommands.CreateParamSet(255, 1, (byte)BaseSysId, 1, "FENCE_ALT_MAX", (float)_nudGeoAlt.Value, 9);
                    _device.Interface.Send(altPkt);

                    byte[] radPkt = MavLinkCommands.CreateParamSet(255, 1, (byte)BaseSysId, 1, "FENCE_RADIUS", (float)_nudGeoRadius.Value, 9);
                    _device.Interface.Send(radPkt);

                    byte[] enableCmd = MavLinkCommands.CreateCommandLong(255, 1, (byte)BaseSysId, 1, 1001, _chkGeoFence.Checked ? 1 : 0);
                    _device.Interface.Send(enableCmd);

                    _state.AddLog("GEOFENCE CONFIG WRITTEN TO DRONE!");
                }
            }

            private void UpdateLanguageText()
            {
                if (_currentLang == "gu")
                {
                    tp1.Text = "મિશન કંટ્રોલ";
                    tp2.Text = "ઉન્નત સેટિંગ્સ";
                    
                    lblTitle.Text = $"ડ્રોન #{_state.SysId}";
                    lblGeo.Text = "ડ્રોન જીઓફેન્સ ગોઠવણી";
                    _chkGeoFence.Text = "જીઓફેન્સ ફેલસેફ સક્ષમ કરો";
                    lblMaxAlt.Text = "મહત્તમ ઊંચાઈ (મીટર):";
                    lblRadius.Text = "મહત્તમ ત્રિજ્યા (મીટર):";
                    lblLang.Text = "ભાષા પસંદ કરો:";
                    
                    _btnRefreshFence.Text = "ગોઠવણી તાજી કરો";
                    _btnApplyFence.Text = "ગોઠવણી લાગુ કરો";
                    
                    _btnRTL.Text = "ઘરે પાછા ફરો (RTL)";
                    _btnLand.Text = "અત્યારે લેન્ડ કરો";
                    _btnUploadWp.Text = "મિશન મેનેજર";
                    lblSwipe.Text = ">>> મોટર્સ બંધ કરવા સ્વાઇપ કરો >>>";
                }
                else
                {
                    tp1.Text = "Mission Control";
                    tp2.Text = "Advanced Settings";
                    
                    lblTitle.Text = $"DRONE #{_state.SysId}";
                    lblGeo.Text = "DRONE GEOFENCE CONFIG";
                    _chkGeoFence.Text = "Enable Geo-Fence Failsafe";
                    lblMaxAlt.Text = "Max Altitude (m):";
                    lblRadius.Text = "Max Radius (m):";
                    lblLang.Text = "Select Language:";
                    
                    _btnRefreshFence.Text = "REFRESH CONFIG";
                    _btnApplyFence.Text = "APPLY CONFIG";
                    
                    _btnRTL.Text = "RETURN HOME (RTL)";
                    _btnLand.Text = "LAND NOW";
                    _btnUploadWp.Text = "MISSION MANAGER";
                    lblSwipe.Text = ">>> SWIPE TO DISARM >>>";
                }
            }

            private string GetModeNameGujarati(uint mode)
            {
                switch (mode)
                {
                    case 0: return "STABILIZE (સ્થિર)";
                    case 2: return "ALT HOLD (ઊંચાઈ જાળવો)";
                    case 3: return "AUTO (ઓટો)";
                    case 4: return "GUIDED (માર્ગદર્શિત)";
                    case 5: return "LOITER (લોઇટર)";
                    case 6: return "RTL (ઘરે પાછા ફરો)";
                    case 9: return "LAND (લેન્ડ કરો)";
                    case 16: return "POS HOLD (સ્થાન જાળવો)";
                    default: return "UNKNOWN (અજ્ઞાત)";
                }
            }

            private string GetGpsStatusNameGujarati(int fixType)
            {
                switch (fixType)
                {
                    case 0:
                    case 1: return "કોઈ ફિક્સ નથી";
                    case 2: return "2D ફિક્સ";
                    case 3: return "3D ફિક્સ";
                    case 4: return "DGPS ફિક્સ";
                    case 5: return "RTK ફ્લોટ";
                    case 6: return "RTK ફિક્સ";
                    default: return "અજ્ઞાત";
                }
            }
        }



        // --- WEB APP / WEBSOCKET CONTROL CLIENT ---
        private async Task StartWebSocketClient()
        {
            _wsCts = new CancellationTokenSource();
            var token = _wsCts.Token;

            while (!token.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "relay_config.txt");
                    string wsUrl = "wss://agri-titan-relay.onrender.com/ws";
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            string readUrl = File.ReadAllText(configPath).Trim();
                            if (!string.IsNullOrEmpty(readUrl))
                            {
                                wsUrl = NormalizeRelayWebSocketUrl(readUrl);
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        try { File.WriteAllText(configPath, wsUrl); } catch { }
                    }

                    wsUrl = NormalizeRelayWebSocketUrl(wsUrl);
                    _mobileRelayUrl = wsUrl;
                    Uri serverUri = new Uri(wsUrl);
                    
                    ws = new ClientWebSocket();
                    // Bypass SSL/TLS validation for connection robustness (focusing on connection, not security)
                    ws.Options.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
                    
                    _wsClient = ws;

                    await ws.ConnectAsync(serverUri, token);
                    _mobileRelayOnline = true;
                    _mobileClientCount = 0;

                    var regMsg = JsonSerializer.Serialize(new { type = "register", client = "gcs" });
                    byte[] regBytes = System.Text.Encoding.UTF8.GetBytes(regMsg);
                    await ws.SendAsync(new ArraySegment<byte>(regBytes), WebSocketMessageType.Text, true, token);

                    // Send missions list immediately upon GCS handshake
                    SendMissionsListToMobile(ws, token);

                    // 2Hz Telemetry stream
                    var senderTask = Task.Run(async () =>
                    {
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            try
                            {
                                if (_drones.Count > 0)
                                {
                                    var activeDrone = _drones.Values.FirstOrDefault(d => d.IsConnected);
                                    if (activeDrone != null)
                                    {
                                        _panels.TryGetValue((byte)activeDrone.SysId, out var activePanel);
                                        var teleData = new {
                                            type = "telemetry",
                                            sysId = activeDrone.SysId,
                                            isArmed = activeDrone.IsArmed,
                                            mode = activeDrone.Mode,
                                            modeName = GetModeName(activeDrone.Mode),
                                            gpsFix = activeDrone.GpsFixType,
                                            gpsStatus = GetGpsStatusName(activeDrone.GpsFixType),
                                            sats = activeDrone.SatellitesCount,
                                            hdop = activeDrone.Hdop,
                                            lat = activeDrone.Lat,
                                            lon = activeDrone.Lon,
                                            heading = activeDrone.Heading,
                                            speed = activeDrone.GroundSpeed,
                                            alt = activeDrone.Alt,
                                            maxAlt = activeDrone.MaxAlt,
                                            voltage = activeDrone.Voltage,
                                            battery = activeDrone.BatteryPercent,
                                            pump = activeDrone.Relay1,
                                            lastMessage = activeDrone.LastMessage,
                                            isUploading = activePanel != null && activePanel.IsUploadingWaypoints,
                                            uploadProgress = activePanel != null ? activePanel.UploadProgressPercent : 0,
                                            currentWp = activeDrone.CurrentWp,
                                            totalWp = activeDrone.TotalWp,
                                            activeMission = activePanel != null && activePanel.ActiveMission != null ? new {
                                                id = activePanel.ActiveMission.Id,
                                                name = activePanel.ActiveMission.Name,
                                                altitude = activePanel.ActiveMission.FlightAltitude,
                                                speed = activePanel.ActiveMission.DroneSpeed,
                                                waypointsCount = activePanel.ActiveMission.Waypoints.Count,
                                                distance = activePanel.ActiveMission.TotalDistanceMeters,
                                                waypoints = activePanel.ActiveMission.Waypoints.Select(wp => new {
                                                    index = wp.Index,
                                                    command = wp.Command,
                                                    lat = wp.Lat,
                                                    lon = wp.Lon,
                                                    alt = wp.Alt
                                                }).ToList()
                                            } : null
                                        };

                                        string json = JsonSerializer.Serialize(teleData);
                                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                                    }
                                }
                            }
                            catch { }
                            await Task.Delay(500, token);
                        }
                    }, token);

                    // Receive commands loop
                    byte[] buffer = new byte[8192];
                    while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string jsonMsg = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                            try
                            {
                                using (var doc = JsonDocument.Parse(jsonMsg))
                                {
                                    var root = doc.RootElement;
                                    if (root.TryGetProperty("type", out var typeProp))
                                    {
                                        string msgType = typeProp.GetString() ?? "";
                                        if (msgType == "command")
                                        {
                                            string cmd = root.GetProperty("command").GetString() ?? "";
                                            ExecuteMobileCommand(cmd);
                                        }
                                        else if (msgType == "request_missions")
                                        {
                                            SendMissionsListToMobile(ws, token);
                                        }
                                        else if (msgType == "load_mission")
                                        {
                                            string missionId = root.GetProperty("missionId").GetString() ?? "";
                                            float speed = root.GetProperty("speed").GetSingle();
                                            float height = root.GetProperty("height").GetSingle();
                                            ExecuteMobileLoadMission(missionId, speed, height, ws, token);
                                        }
                                        else if (msgType == "mobile_status")
                                        {
                                            if (root.TryGetProperty("count", out var countProp))
                                            {
                                                _mobileClientCount = countProp.GetInt32();
                                            }
                                            else if (root.TryGetProperty("connected", out var connectedProp))
                                            {
                                                _mobileClientCount = connectedProp.GetBoolean() ? 1 : 0;
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebSocket Client Error]: {ex.Message}");
                    await Task.Delay(3000, token);
                }
                finally
                {
                    ws?.Dispose();
                    _wsClient = null;
                    _mobileRelayOnline = false;
                    _mobileClientCount = 0;
                }
            }
        }

        private static string NormalizeRelayWebSocketUrl(string relayUrl)
        {
            string url = (relayUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                return "wss://agri-titan-relay.onrender.com/ws";
            }

            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "wss://" + url.Substring("https://".Length);
            }
            else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                url = "ws://" + url.Substring("http://".Length);
            }
            else if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                bool isLocal = url.Contains("localhost", StringComparison.OrdinalIgnoreCase) || url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);
                url = (isLocal ? "ws://" : "wss://") + url;
            }

            url = url.TrimEnd('/');
            return url.EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ? url : url + "/ws";
        }

        private void SendMissionsListToMobile(ClientWebSocket ws, CancellationToken token)
        {
            try
            {
                var missionsList = MissionManager.GetMissions().Select(m => new {
                    id = m.Id,
                    name = m.Name,
                    altitude = m.FlightAltitude,
                    speed = m.DroneSpeed,
                    waypointsCount = m.Waypoints.Count,
                    distance = m.TotalDistanceMeters,
                    waypoints = m.Waypoints.Select(wp => new {
                        index = wp.Index,
                        command = wp.Command,
                        lat = wp.Lat,
                        lon = wp.Lon,
                        alt = wp.Alt
                    }).ToList()
                }).ToList();

                var msg = JsonSerializer.Serialize(new {
                    type = "missions_list",
                    missions = missionsList
                });
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(msg);
                
                Task.Run(async () =>
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void ExecuteMobileLoadMission(string missionId, float speed, float height, ClientWebSocket ws, CancellationToken token)
        {
            if (this.IsDisposed) return;
            try
            {
                var mission = MissionManager.GetMissions().FirstOrDefault(m => m.Id == missionId);
                if (mission != null)
                {
                    // Update height for WAYPOINT (16) and TAKEOFF (22)
                    foreach (var wp in mission.Waypoints)
                    {
                        if (wp.Command == 16 || wp.Command == 22)
                        {
                            wp.Alt = height;
                        }
                    }
                    mission.FlightAltitude = height;
                    mission.DroneSpeed = speed;

                    this.Invoke((Action)(() =>
                    {
                        if (_drones.Count == 0) return;
                        var activeDrone = _drones.Values.FirstOrDefault(d => d.IsConnected);
                        if (activeDrone == null) return;
                        if (_panels.TryGetValue((byte)activeDrone.SysId, out var panel))
                        {
                            activeDrone.AddLog($"MOBILE LOAD MISSION: '{mission.Name}' Speed={speed}m/s Height={height}m");
                            
                            // 1. Set cruise speed via DO_CHANGE_SPEED MAVLink command (178)
                            panel.SendCmd(178, 1, speed);

                            // 2. Deactivate GCS buttons during upload
                            panel.DeactivateButtonsForUpload(mission.Waypoints.Count);

                            // 3. Upload mission silently
                            panel.UploadMission(mission, silent: true);
                            SendMissionLoadedToMobile(mission, ws, token);
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket Mobile Load Mission Error]: {ex.Message}");
            }
        }

        private void SendMissionLoadedToMobile(Mission mission, ClientWebSocket ws, CancellationToken token)
        {
            try
            {
                var msg = JsonSerializer.Serialize(new {
                    type = "mission_loaded",
                    mission = new {
                        id = mission.Id,
                        name = mission.Name,
                        altitude = mission.FlightAltitude,
                        speed = mission.DroneSpeed,
                        waypointsCount = mission.Waypoints.Count,
                        distance = mission.TotalDistanceMeters,
                        waypoints = mission.Waypoints.Select(wp => new {
                            index = wp.Index,
                            command = wp.Command,
                            lat = wp.Lat,
                            lon = wp.Lon,
                            alt = wp.Alt
                        }).ToList()
                    }
                });
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(msg);
                Task.Run(async () =>
                {
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                        {
                            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                        }
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void ExecuteMobileCommand(string cmd)
        {
            if (this.IsDisposed) return;
            try
            {
                this.Invoke((Action)(() =>
                {
                    if (_drones.Count == 0) return;
                    var activeDrone = _drones.Values.FirstOrDefault(d => d.IsConnected);
                    if (activeDrone == null) return;

                    if (_panels.TryGetValue((byte)activeDrone.SysId, out var panel))
                    {
                        activeDrone.AddLog($"MOBILE COMMAND RECEIVED: {cmd}");
                        switch (cmd.ToUpper())
                        {
                            case "ARM":
                                panel.SendCmd(400, 1, 21196);
                                break;
                            case "DISARM":
                                panel.SendCmd(400, 0, 21196);
                                break;
                            case "RTL":
                                panel.SendSetMode(6);
                                break;
                            case "LAND":
                                panel.SendSetMode(9);
                                break;
                            case "START_MISSION":
                                Task.Run(async () => {
                                    try
                                    {
                                        panel.SendSetMode(5);
                                        await Task.Delay(500);
                                        panel.SendCmd(400, 1, 21196);
                                        await Task.Delay(1000);
                                        panel.SendSetMode(3);
                                        await Task.Delay(500);
                                        panel.SendCmd(300, activeDrone.ResumeWp, 0);
                                        activeDrone.AddLog("MOBILE START MISSION SENT");
                                    }
                                    catch (Exception ex)
                                    {
                                        activeDrone.AddLog($"MOBILE START MISSION ERROR: {ex.Message}");
                                    }
                                });
                                break;
                            case "PAUSE":
                                activeDrone.ResumeWp = activeDrone.CurrentWp;
                                panel.SendSetMode(16);
                                break;
                            case "RESUME":
                                panel.SendSetMode(3);
                                break;
                            case "PUMP_ON":
                                panel.SendCmd(181, 0, 0);
                                activeDrone.Relay1 = 0;
                                break;
                            case "PUMP_OFF":
                                panel.SendCmd(181, 0, 1);
                                activeDrone.Relay1 = 1;
                                break;
                        }
                    }
                }));
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _wsCts?.Cancel();
            base.OnFormClosing(e);
        }
    }
}
