using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using MinimalGCS.Connection;
using MinimalGCS.Mavlink;

namespace MinimalGCS
{
    public partial class MainForm : Form
    {
        private AutoConnector _connector;
        private FlowLayoutPanel _workArea;
        private MapForm _mapWindow;
        private Label _lblSearching;
        private System.Windows.Forms.Timer _uiTicker;
        
        // CENTRAL STATE MANAGER: One dictionary for all drones
        private ConcurrentDictionary<byte, DroneState> _drones = new ConcurrentDictionary<byte, DroneState>();
        private Dictionary<byte, AgriWorkPanel> _panels = new Dictionary<byte, AgriWorkPanel>();

        public MainForm()
        {
            InitializeComponent();
            SetupAgriUI();
            
            // Show the floating tracking map window next to main form
            _mapWindow = new MapForm();
            _mapWindow.Show();

            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.Start();

            // UI UPDATE TIMER (10Hz) - Reads from stable state
            _uiTicker = new System.Windows.Forms.Timer { Interval = 100 };
            _uiTicker.Tick += Timer_Tick;
            _uiTicker.Start();
        }

        private void SetupAgriUI()
        {
            this.Text = "Agri-Drone Enterprise v1.3.5 (Stable) - Prince Tagadiya";
            this.Size = new Size(410, 720);
            this.BackColor = Color.FromArgb(245, 245, 245);
            
            _workArea = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(240, 240, 240),
                Padding = new Padding(15)
            };
            this.Controls.Add(_workArea);

            _lblSearching = new Label
            {
                Text = "SCANNING FOR FLEET...",
                Size = new Size(340, 100),
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
            if (_mapWindow != null && !_mapWindow.IsDisposed)
            {
                _mapWindow.ClearWaypoints();
            }
        }

        public void AddMapWaypoint(double lat, double lon, int index)
        {
            if (_mapWindow != null && !_mapWindow.IsDisposed)
            {
                _mapWindow.AddWaypoint(lat, lon, index);
            }
        }

        public void UpdateDroneMap(double lat, double lon, float heading)
        {
            if (_mapWindow != null && !_mapWindow.IsDisposed)
            {
                _mapWindow.UpdateDrone(lat, lon, heading);
            }
        }

        public void UpdateMissionHUD(int currentWp, int totalWp, string status)
        {
            if (_mapWindow != null && !_mapWindow.IsDisposed)
            {
                _mapWindow.UpdateHUD(currentWp, totalWp, status);
            }
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

                    // --- AUTOMATIC GCS RELAY FOR MISSION PLANNER (UDP 14550 PUSH) ---
                    if (device.Interface is SerialInterface)
                    {
                        try
                        {
                            var udpRelay = new System.Net.Sockets.UdpClient();
                            var remoteEP = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);

                            // 1. Forward incoming UDP packets from Mission Planner to the physical Serial port
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
                                            if (fromMP.Length > 0 && device.Interface.IsOpen)
                                            {
                                                device.Interface.Send(fromMP);
                                                parser.Parse(fromMP); // Parse locally so our UI updates in real-time
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                                try { udpRelay.Close(); } catch { }
                            });

                            // 2. Push telemetry data received from Serial to local UDP port 14550
                            device.Interface.OnDataReceived += (data) =>
                            {
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
                                }
                                catch { }
                            };
                        }
                        catch { }
                    }
                }
            }));
        }

        private void DispatchPacket(MavLinkPacket pkt)
        {
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
                state.Voltage = volt;
                if (volt >= 12.6f) state.BatteryPercent = 100;
                else if (volt <= 10.5f) state.BatteryPercent = 0;
                else state.BatteryPercent = (int)((volt - 10.5f) / 2.1f * 100);
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
            else if (pkt.MessageId == 253) // STATUSTEXT
            {
                string msg = System.Text.Encoding.ASCII.GetString(pkt.Payload, 1, pkt.Payload.Length - 1).TrimEnd('\0');
                state.AddLog(msg);
            }
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            foreach (var panel in _panels.Values)
            {
                if (_drones.TryGetValue((byte)panel.BaseSysId, out var state))
                {
                    panel.SyncWithState(state);
                }
            }
        } 

        public string GetModeName(uint mode) => mode switch { 0 => "STABILIZE", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", 16 => "POSHOLD", _ => $"MODE({mode})" };
        public string GetGpsStatusName(int fixType) => fixType switch { 0 => "No GPS", 1 => "No Fix", 2 => "2D Fix", 3 => "3D Fix", 4 => "DGPS", 5 => "RTK Float", 6 => "RTK Fixed", _ => $"FIX({fixType})" };

        // --- MANAGES ONE DRONE'S UI AND COMMANDS ---
        public class AgriWorkPanel : Panel
        {
            private MainForm _main;
            private DroneState _state;
            private DiscoveredDevice _device;
            public int BaseSysId => _device.SysId;
            
            private Label _lblStatus, _lblTelemetry, _lblGPS, _lblMsg;
            private Button _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency, _btnPump, _btnUploadWp;
            
            private enum PanelState { IDLE, BUSY }
            private PanelState _pState = PanelState.IDLE;

            public AgriWorkPanel(DiscoveredDevice device, DroneState state, MainForm main)
            {
                _device = device; _state = state; _main = main;
                this.Size = new Size(360, 580); this.BorderStyle = BorderStyle.FixedSingle; this.BackColor = Color.White; this.Margin = new Padding(0, 0, 0, 15);
                InitializeControls();
            }

            private void InitializeControls()
            {
                var lblTitle = new Label { Text = $"DRONE #{_state.SysId}", Location = new Point(10, 10), Size = new Size(340, 25), Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.DarkGreen };
                _lblStatus = new Label { Text = "READY", Location = new Point(10, 40), Size = new Size(340, 30), Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.Blue };
                _lblTelemetry = new Label { Text = "...", Location = new Point(10, 75), Size = new Size(340, 35), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
                _lblGPS = new Label { Text = "Lat: 0.0000000  Lng: 0.0000000", Location = new Point(10, 110), Size = new Size(340, 20), Font = new Font("Segoe UI", 9, FontStyle.Regular), ForeColor = Color.DarkSlateGray };
                _lblMsg = new Label { Text = "Initializing...", Location = new Point(10, 130), Size = new Size(340, 20), Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Color.DarkSlateGray };

                _btnStart = CreateBtn("START MISSION", Color.FromArgb(40, 167, 69), 150);
                _btnPause = CreateBtn("PAUSE", Color.FromArgb(255, 193, 7), 210);
                _btnResume = CreateBtn("RESUME", Color.FromArgb(23, 162, 184), 210);
                _btnRTL = CreateBtn("RETURN HOME (RTL)", Color.FromArgb(108, 117, 125), 270);
                
                // Side-by-side layout for LAND NOW and PUMP CONTROL
                _btnLand = new Button { Text = "LAND NOW", Location = new Point(20, 330), Size = new Size(155, 48), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(255, 69, 0), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnLand.FlatAppearance.BorderSize = 0;

                _btnPump = new Button { Text = "PUMP: OFF", Location = new Point(185, 330), Size = new Size(155, 48), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(108, 117, 125), ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                _btnPump.FlatAppearance.BorderSize = 0;

                _btnUploadWp = CreateBtn("UPLOAD .WAYPOINTS FILE", Color.FromArgb(23, 162, 184), 390);
                
                // --- SWIPE TO DISARM ---
                var pnlSwipe = new Panel { Location = new Point(20, 450), Size = new Size(320, 60), BackColor = Color.FromArgb(220, 53, 69), BorderStyle = BorderStyle.None };
                var lblSwipe = new Label { Text = ">>> SWIPE TO DISARM >>>", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                var pnlHandle = new Panel { Location = new Point(2, 2), Size = new Size(80, 56), BackColor = Color.White, Cursor = Cursors.Hand };
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
                    if (_state.Relay1 == 0) // Pump is currently ON (Relay 0) -> Turn it OFF (Relay 1)
                    {
                        SendCmd(181, 0, 1); // MAV_CMD_DO_SET_RELAY: param1=0 (Relay 1), param2=1 (HIGH/OFF)
                        _state.Relay1 = 1;
                        _state.AddLog("PUMP COMMAND: OFF");
                    }
                    else // Pump is currently OFF (Relay 1 or default -1) -> Turn it ON (Relay 0)
                    {
                        SendCmd(181, 0, 0); // MAV_CMD_DO_SET_RELAY: param1=0 (Relay 1), param2=0 (LOW/ON)
                        _state.Relay1 = 0;
                        _state.AddLog("PUMP COMMAND: ON");
                    }
                };

                _btnUploadWp.Click += (s, e) =>
                {
                    using (var ofd = new OpenFileDialog { Filter = "Waypoint Files (*.waypoints;*.txt)|*.waypoints;*.txt" })
                    {
                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            UploadWaypointFile(ofd.FileName);
                        }
                    }
                };

                this.Controls.AddRange(new Control[] { lblTitle, _lblStatus, _lblTelemetry, _lblGPS, _lblMsg, _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnPump, _btnUploadWp, pnlSwipe });
            }

            public void SyncWithState(DroneState state)
            {
                _lblMsg.Text = state.LastMessage;
                string pumpStr = state.Relay1 == 0 ? "ON" : "OFF";
                _lblTelemetry.Text = $"ALTITUDE: {state.Alt:F1}m (Max: {state.MaxAlt:F1}m) | BATT: {state.BatteryPercent}% ({state.Voltage:F1}V)\nMODE: {_main.GetModeName(state.Mode)} | PUMP: {pumpStr} | WPs: {state.TotalWp}";
                _lblTelemetry.ForeColor = state.IsArmed ? Color.DarkRed : Color.Black;
                _lblGPS.Text = $"GPS: {_main.GetGpsStatusName(state.GpsFixType)} (Sats: {state.SatellitesCount} | HDOP: {state.Hdop:F1}) | Lat: {state.Lat:F7} Lng: {state.Lon:F7}";
                
                // Track dynamic telemetry position and heading on Leaflet Map
                if (state.Lat != 0.0 && state.Lon != 0.0)
                {
                    _main.UpdateDroneMap(state.Lat, state.Lon, state.Heading);
                    _main.UpdateMissionHUD(state.CurrentWp, state.TotalWp, _lblStatus.Text);
                }
                
                // Sync Pump button UI state dynamically
                if (state.Relay1 == 0) // ON
                {
                    _btnPump.Text = "PUMP: ON";
                    _btnPump.BackColor = Color.FromArgb(40, 167, 69); // Rich green for active
                }
                else // OFF
                {
                    _btnPump.Text = "PUMP: OFF";
                    _btnPump.BackColor = Color.FromArgb(108, 117, 125); // Sleek gray for off
                }
                
                if (!state.IsConnected) { _lblStatus.Text = "LOST CONNECTION"; _lblStatus.ForeColor = Color.Red; return; }
                
                if (_pState == PanelState.IDLE)
                {
                    if (state.Mode == 3) { _lblStatus.Text = "MISSION ACTIVE"; _lblStatus.ForeColor = Color.Green; }
                    else if (state.Mode == 5 || state.Mode == 16) { _lblStatus.Text = "MISSION PAUSED"; _lblStatus.ForeColor = Color.Orange; }
                    else { _lblStatus.Text = "READY"; _lblStatus.ForeColor = Color.Blue; }
                    
                    // DYNAMIC BUTTON: START (On Ground) vs RESUME (In Air)
                    if (!state.IsArmed)
                    {
                        _btnStart.Text = "START MISSION";
                        _btnStart.BackColor = Color.FromArgb(40, 167, 69);
                        _btnStart.Visible = true;
                    }
                    else if (state.Mode != 3) // RTL, LAND, LOITER
                    {
                        _btnStart.Text = "RESUME MISSION";
                        _btnStart.BackColor = Color.FromArgb(0, 123, 255);
                        _btnStart.Visible = true;
                    }
                    else
                    {
                        _btnStart.Visible = false;
                    }
                }

                _btnPause.Visible = (state.IsArmed && state.Mode == 3);
                _btnResume.Visible = false; // Internal resume integrated into main button

                if (!state.IsArmed && _pState == PanelState.IDLE) 
                { 
                    _lblStatus.Text = "READY"; _lblStatus.ForeColor = Color.Blue; 
                }
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

            private void UploadWaypointFile(string filePath)
            {
                try
                {
                    var lines = System.IO.File.ReadAllLines(filePath);
                    if (lines.Length < 2 || !lines[0].StartsWith("QGC WPL"))
                    {
                        MessageBox.Show("Invalid Waypoint File Format!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var items = new List<WaypointItem>();
                    for (int i = 1; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 12)
                        {
                            items.Add(new WaypointItem
                            {
                                Index = ushort.Parse(parts[0]),
                                CurrentWp = byte.Parse(parts[1]),
                                CoordFrame = byte.Parse(parts[2]),
                                Command = ushort.Parse(parts[3]),
                                Param1 = float.Parse(parts[4]),
                                Param2 = float.Parse(parts[5]),
                                Param3 = float.Parse(parts[6]),
                                Param4 = float.Parse(parts[7]),
                                Lat = double.Parse(parts[8]),
                                Lon = double.Parse(parts[9]),
                                Alt = float.Parse(parts[10]),
                                AutoContinue = byte.Parse(parts[11])
                            });
                        }
                    }

                    if (items.Count == 0)
                    {
                        MessageBox.Show("No waypoints found in file!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    _uploadQueue = items;
                    _state.TotalWp = items.Count;
                    _state.AddLog($"Loaded {items.Count} Waypoints from file!");

                    // Render mission path on the map
                    _main.ClearMapWaypoints();
                    foreach (var item in items)
                    {
                        if (item.Lat != 0 && item.Lon != 0 && (item.Command == 16 || item.Command == 22)) 
                        {
                            _main.AddMapWaypoint(item.Lat, item.Lon, item.Index);
                        }
                    }

                    // Start upload sequence: Send MISSION_COUNT to drone
                    byte[] countPkt = MavLinkCommands.CreateMissionCount((byte)255, 1, (byte)BaseSysId, 1, (ushort)items.Count);
                    _device.Interface.Send(countPkt);
                    _state.AddLog($"Sent Waypoint Count: {items.Count}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to parse waypoint file: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            public void HandleWaypointRequest(ushort seq)
            {
                if (seq < _uploadQueue.Count)
                {
                    var wp = _uploadQueue[seq];
                    byte[] itemPkt = MavLinkCommands.CreateMissionItem(
                        (byte)255, 1, 
                        (byte)BaseSysId, 1, 
                        wp.Index, 
                        wp.Command, 
                        wp.Param1, wp.Param2, wp.Param3, wp.Param4, 
                        (float)wp.Lat, (float)wp.Lon, wp.Alt, 
                        wp.CoordFrame, 
                        wp.CurrentWp, 
                        wp.AutoContinue
                    );
                    _device.Interface.Send(itemPkt);
                    _state.AddLog($"Sent Waypoint #{seq} of {_uploadQueue.Count}");
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
                var b = new Button { Text = t, Location = new Point(20, y), Size = new Size(320, 50), BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0; return b;
            }

            private void SendSetMode(uint m) => _device.Interface.Send(MavLinkCommands.CreateSetMode(255, 1, _device.SysId, 1, m));
            private void SendCmd(ushort c, float p1, float p2=0, float p3=0, float p4=0, float p5=0, float p6=0, float p7=0) 
                => _device.Interface.Send(MavLinkCommands.CreateCommandLong(255, 1, _device.SysId, _device.CompId, c, p1, p2, p3, p4, p5, p6, p7));
        }

        public class WaypointItem
        {
            public ushort Index { get; set; }
            public byte CurrentWp { get; set; }
            public byte CoordFrame { get; set; }
            public ushort Command { get; set; }
            public float Param1 { get; set; }
            public float Param2 { get; set; }
            public float Param3 { get; set; }
            public float Param4 { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public float Alt { get; set; }
            public byte AutoContinue { get; set; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_mapWindow != null)
            {
                _mapWindow.Dispose();
            }
            base.OnFormClosing(e);
        }
    }

    public class MapForm : Form
    {
        private WebBrowser _mapBrowser;

        public MapForm()
        {
            this.Text = "Agri-Drone Live Tracking Map (Satellite) - Prince Tagadiya";
            this.Size = new Size(850, 650);
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(Screen.PrimaryScreen.WorkingArea.Width - 900, 50);
            this.ShowInTaskbar = true;

            _mapBrowser = new WebBrowser
            {
                Dock = DockStyle.Fill,
                ScrollBarsEnabled = false,
                WebBrowserShortcutsEnabled = false
            };
            this.Controls.Add(_mapBrowser);

            // Load map file
            string mapPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");
            if (!System.IO.File.Exists(mapPath))
            {
                mapPath = @"a:\AGRI\Drone_Software\map.html";
            }
            _mapBrowser.Navigate(new Uri(mapPath));
        }

        public void ClearWaypoints()
        {
            this.Invoke((MethodInvoker)delegate {
                try
                {
                    _mapBrowser.Document.InvokeScript("clearWaypoints");
                }
                catch { }
            });
        }

        public void AddWaypoint(double lat, double lon, int index)
        {
            this.Invoke((MethodInvoker)delegate {
                try
                {
                    _mapBrowser.Document.InvokeScript("addWaypoint", new object[] { lat, lon, index });
                }
                catch { }
            });
        }

        public void UpdateDrone(double lat, double lon, float heading)
        {
            this.Invoke((MethodInvoker)delegate {
                try
                {
                    _mapBrowser.Document.InvokeScript("updateDrone", new object[] { lat, lon, heading });
                }
                catch { }
            });
        }

        public void UpdateHUD(int currentWp, int totalWp, string status)
        {
            this.Invoke((MethodInvoker)delegate {
                try
                {
                    _mapBrowser.Document.InvokeScript("updateMissionProgress", new object[] { currentWp, totalWp, status });
                }
                catch { }
            });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
            }
            base.OnFormClosing(e);
        }
    }
}
