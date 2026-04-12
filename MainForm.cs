using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
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
        private Label _lblSearching;
        private Dictionary<byte, AgriWorkPanel> _workPanels = new Dictionary<byte, AgriWorkPanel>();

        public MainForm()
        {
            InitializeComponent();
            SetupAgriUI();
            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.Start(); // Start instant scanning
        }

        private void SetupAgriUI()
        {
            this.Text = "Agri-Drone Pro v1.0.3 - Prince Tagadiya";
            this.Width = 400;
            this.Height = 550;
            this.BackColor = Color.White;

            _workArea = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(20),
                BackColor = Color.FromArgb(250, 250, 250)
            };
            this.Controls.Add(_workArea);
            _workArea.BringToFront();

            _lblSearching = new Label { 
                Text = "PREPARING SYSTEM...\nSearching for drones...", 
                Size = new Size(340, 100), TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.DarkGray,
                Location = new Point(0, 0)
            };
            _workArea.Controls.Add(_lblSearching);

            // Hide old controls
            btnSmartScan.Visible = cmbDrones.Visible = btnConnect.Visible = lblStatus.Visible = cmbActiveDrone.Visible = groupControl.Visible = false;
            lblWatermark.BringToFront();
        }

        private void OnDeviceConnected(DiscoveredDevice device)
        {
            this.Invoke((Action)(() =>
            {
                if (_lblSearching != null) { _workArea.Controls.Remove(_lblSearching); _lblSearching = null; }

                if (!_workPanels.ContainsKey(device.SysId))
                {
                    var panel = new AgriWorkPanel(device, this);
                    _workPanels[device.SysId] = panel;
                    _workArea.Controls.Add(panel);
                    
                    // CRITICAL: Only start reading from the FIRST discovery to prevent flickering
                    var parser = new MavLinkParser();
                    parser.PacketReceived += (p) => { if (_workPanels.TryGetValue(device.SysId, out var pnl)) pnl.ProcessMavLink(p); };
                    device.Interface.StartReading(data => parser.Parse(data));
                }
            }));
        }

        public string GetModeName(uint mode) => mode switch { 0 => "STABILIZE", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", _ => $"MODE({mode})" };

        // --- AUTOMATED AGRI WORK PANEL ---
        public class AgriWorkPanel : Panel
        {
            private DiscoveredDevice _device;
            private MainForm _main;
            private Label _lblDroneTitle, _lblWorkStatus, _lblTelemetry, _lblMsg;
            private Button _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency;
            
            private enum WorkState { IDLE, STARTING, WORKING, PAUSED, RETURNING, EMERGENCY }
            private WorkState _state = WorkState.IDLE;
            private bool _isArmed = false;
            private byte _gpsFix = 0;
            private float _alt = 0;

            public AgriWorkPanel(DiscoveredDevice device, MainForm main)
            {
                _device = device; _main = main;
                this.Size = new Size(340, 440);
                this.BorderStyle = BorderStyle.FixedSingle;
                this.BackColor = Color.White;
                this.Margin = new Padding(0, 0, 0, 20);
                InitializeAgriControls();
            }

            private void InitializeAgriControls()
            {
                _lblDroneTitle = new Label { Text = $"DRONE #{_device.SysId}", Location = new Point(10, 10), Size = new Size(320, 25), Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.Green };
                _lblWorkStatus = new Label { Text = "Status: READY", Location = new Point(10, 40), Size = new Size(320, 30), Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.Blue };
                _lblTelemetry = new Label { Text = "GPS: Waiting... | Alt: 0.0m", Location = new Point(10, 75), Size = new Size(320, 20), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
                _lblMsg = new Label { Text = "System Ready", Location = new Point(10, 100), Size = new Size(320, 40), Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Color.DarkSlateGray };

                _btnStart = CreateWorkButton("START MISSION", Color.FromArgb(40, 167, 69), 140);
                _btnPause = CreateWorkButton("PAUSE WORK", Color.FromArgb(255, 193, 7), 140);
                _btnResume = CreateWorkButton("RESUME WORK", Color.FromArgb(23, 162, 184), 140);
                _btnRTL = CreateWorkButton("RETURN HOME (RTL)", Color.FromArgb(108, 117, 125), 200);
                _btnLand = CreateWorkButton("LAND IMMEDIATELY", Color.FromArgb(255, 140, 0), 260);
                _btnEmergency = CreateWorkButton("EMERGENCY STOP", Color.Red, 320);

                _btnStart.Click += async (s, e) => await StartWorkflow();
                _btnPause.Click += (s, e) => { SetMode(5); _state = WorkState.PAUSED; UpdateUIState(); };
                _btnResume.Click += (s, e) => { SetMode(3); _state = WorkState.WORKING; UpdateUIState(); };
                _btnRTL.Click += (s, e) => { SetMode(6); _state = WorkState.RETURNING; UpdateUIState(); };
                _btnLand.Click += (s, e) => { SetMode(9); _state = WorkState.RETURNING; UpdateUIState(); };
                _btnEmergency.Click += (s, e) => EmergencyStop();

                this.Controls.AddRange(new Control[] { _lblDroneTitle, _lblWorkStatus, _lblTelemetry, _lblMsg, _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency });
                this.Size = new Size(340, 480);
                UpdateUIState();
            }

            private Button CreateWorkButton(string text, Color color, int y)
            {
                var btn = new Button { Text = text, Location = new Point(20, y), Size = new Size(300, 50), BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 12, FontStyle.Bold), Cursor = Cursors.Hand };
                btn.FlatAppearance.BorderSize = 0; return btn;
            }

            private int _gpsPacketCount = 0;

            public void ProcessMavLink(MavLinkPacket pkt)
            {
                if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID)
                {
                    uint mode = BitConverter.ToUInt32(pkt.Payload, 0);
                    _isArmed = (pkt.Payload[6] & 128) != 0;
                    string modeName = _main.GetModeName(mode);
                    _main.Invoke((Action)(() => {
                        _lblTelemetry.Text = $"GPS: {(_gpsFix >= 3 ? "3D FIX OK" : "SEARCHING...")} | Alt: {_alt:F1}m | {(_isArmed ? "ARMED" : "DISARMED")}";
                        if (modeName != "AUTO" && _state == WorkState.WORKING) AutoHandleRTL();
                    }));
                }
                else if (pkt.MessageId == 33) 
                { 
                    _alt = BitConverter.ToInt32(pkt.Payload, 16) / 1000.0f; 
                }
                else if (pkt.MessageId == 24) 
                { 
                    _gpsPacketCount++;
                    // Try common offsets and search for first non-zero fix type
                    byte fixChannel1 = pkt.Payload.Length > 8 ? pkt.Payload[8] : (byte)0;
                    byte fixChannel2 = pkt.Payload.Length > 2 ? pkt.Payload[2] : (byte)0;
                    _gpsFix = (fixChannel1 >= 3) ? fixChannel1 : fixChannel2;
                }
                else if (pkt.MessageId == 253) 
                { 
                    string m = System.Text.Encoding.ASCII.GetString(pkt.Payload, 1, pkt.Payload.Length - 1).TrimEnd('\0'); 
                    _main.Invoke((Action)(() => _lblMsg.Text = m)); 
                }
            }

            private void UpdateUIState()
            {
                _main.Invoke((Action)(() => {
                    _btnStart.Visible = (_state == WorkState.IDLE);
                    _btnPause.Visible = (_state == WorkState.WORKING);
                    _btnResume.Visible = (_state == WorkState.PAUSED);
                    _btnRTL.Visible = (_state == WorkState.WORKING || _state == WorkState.PAUSED);
                    _btnLand.Visible = (_state == WorkState.WORKING || _state == WorkState.PAUSED || _state == WorkState.RETURNING);
                    
                    switch(_state) {
                        case WorkState.IDLE: _lblWorkStatus.Text = "READY"; _lblWorkStatus.ForeColor = Color.Blue; break;
                        case WorkState.STARTING: _lblWorkStatus.Text = "STARTING..."; break;
                        case WorkState.WORKING: _lblWorkStatus.Text = "MISSION IN PROGRESS"; _lblWorkStatus.ForeColor = Color.Green; break;
                        case WorkState.PAUSED: _lblWorkStatus.Text = "WORK PAUSED"; _lblWorkStatus.ForeColor = Color.Orange; break;
                        case WorkState.RETURNING: _lblWorkStatus.Text = "RETURNING HOME"; break;
                    }
                }));
            }

            private async Task StartWorkflow()
            {
                _state = WorkState.STARTING; UpdateUIState();
                
                if (!_isArmed)
                {
                    _main.Invoke((Action)(() => _lblMsg.Text = "Initiating Loiter & Arm..."));
                    SetMode(5); await Task.Delay(1000); // LOITER
                    
                    _main.Invoke((Action)(() => _lblMsg.Text = "Forcing Motor ARM..."));
                    SendCmd(400, 1, 21196); // FORCE ARM
                    
                    for(int i=0; i<20; i++) { if (_isArmed) break; await Task.Delay(500); }
                    if (!_isArmed) { _state = WorkState.IDLE; UpdateUIState(); MessageBox.Show("Arming timeout. Ensure drone is ready."); return; }
                }

                _main.Invoke((Action)(() => _lblMsg.Text = "Initiating AUTO Mission..."));
                SetMode(3); // AUTO
                _state = WorkState.WORKING; UpdateUIState();
            }

            private void AutoHandleRTL() { _state = WorkState.RETURNING; SetMode(6); UpdateUIState(); }

            private void EmergencyStop()
            {
                if (MessageBox.Show("FORCE EMERGENCY STOP?", "WARNING", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    SendCmd(400, 0, 21196); _state = WorkState.IDLE; UpdateUIState();
                }
            }

            private void SetMode(uint m) => _device.Interface.Send(MavLinkCommands.CreateSetMode(255, 1, 1, m));
            private void SendCmd(ushort c, float p1, float p2=0, float p3=0, float p4=0, float p5=0, float p6=0, float p7=0) 
                => _device.Interface.Send(MavLinkCommands.CreateCommandLong(255, 1, _device.SysId, _device.CompId, c, p1, p2, p3, p4, p5, p6, p7));
        }
    }
}
