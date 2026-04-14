// (FULL FILE REWRITTEN WITH FIXES)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
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
        private Timer _uiTicker;

        private ConcurrentDictionary<byte, DroneState> _drones = new();
        private Dictionary<byte, AgriWorkPanel> _panels = new();

        public MainForm()
        {
            InitializeComponent();
            SetupAgriUI();

            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.Start();

            _uiTicker = new Timer { Interval = 100 };
            _uiTicker.Tick += Timer_Tick;
            _uiTicker.Start();
        }

        private void SetupAgriUI()
        {
            this.Text = "Agri-Drone Enterprise v1.3.0";
            this.Size = new Size(1200, 800);

            _workArea = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(15)
            };

            this.Controls.Add(_workArea);

            _lblSearching = new Label
            {
                Text = "SCANNING...",
                Size = new Size(300, 100),
                TextAlign = ContentAlignment.MiddleCenter
            };

            _workArea.Controls.Add(_lblSearching);
        }

        private void OnDeviceConnected(DiscoveredDevice device)
        {
            this.Invoke(() =>
            {
                _workArea.Controls.Remove(_lblSearching);

                if (_drones.ContainsKey(device.SysId)) return;

                var state = new DroneState(device.SysId);
                _drones.TryAdd(device.SysId, state);

                var panel = new AgriWorkPanel(device, state, this);
                _panels[device.SysId] = panel;
                _workArea.Controls.Add(panel);

                var parser = new MavLinkParser();
                parser.PacketReceived += DispatchPacket;

                device.Interface.OnDataReceived += parser.Parse;
                device.Interface.StartReading();

                // ✅ ONE TIME STREAM REQUEST (IMPORTANT)
                device.Interface.Send(
                    MavLinkCommands.CreateCommandLong(
                        255, 1,
                        device.SysId,
                        device.CompId,
                        66, 6, 10, 1
                    )
                );
            });
        }

        private void DispatchPacket(MavLinkPacket pkt)
        {
            if (!_drones.TryGetValue(pkt.SystemId, out var state)) return;

            if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID)
            {
                state.LastHeartbeat = DateTime.Now;
                state.Mode = BitConverter.ToUInt32(pkt.Payload, 0);

                bool armed = (pkt.Payload[6] & 128) != 0;
                state.IsArmed = armed;
            }

            else if (pkt.MessageId == 24)
            {
                if (pkt.Payload.Length > 8)
                    state.GpsFixType = pkt.Payload[8];
            }

            // 🔥 FIXED ALTITUDE LOGIC
            else if (pkt.MessageId == 33 && pkt.Payload.Length >= 16)
            {
                int rawAlt = BitConverter.ToInt32(pkt.Payload, 12);

                if (rawAlt != 0)
                {
                    state.Alt = rawAlt / 1000f;
                }
            }

            else if (pkt.MessageId == 74 && pkt.Payload.Length >= 12)
            {
                float alt = BitConverter.ToSingle(pkt.Payload, 8);

                if (state.Alt <= 0 && alt > 0)
                {
                    state.Alt = alt;
                }
            }

            else if (pkt.MessageId == 42)
            {
                state.CurrentWp = BitConverter.ToUInt16(pkt.Payload, 0);
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

        public string GetModeName(uint m)
        {
            return m switch
            {
                3 => "AUTO",
                4 => "GUIDED",
                5 => "LOITER",
                6 => "RTL",
                9 => "LAND",
                _ => "MODE"
            };
        }

        public class AgriWorkPanel : Panel
        {
            private MainForm _main;
            private DroneState _state;
            private DiscoveredDevice _device;

            private Label _telemetry;

            public int BaseSysId => _device.SysId;

            public AgriWorkPanel(DiscoveredDevice d, DroneState s, MainForm m)
            {
                _device = d;
                _state = s;
                _main = m;

                this.Size = new Size(300, 200);

                _telemetry = new Label
                {
                    Location = new Point(10, 10),
                    Size = new Size(280, 50)
                };

                Controls.Add(_telemetry);
            }

            public void SyncWithState(DroneState state)
            {
                _telemetry.Text =
                    $"ALT: {state.Alt:F1}m | WP: {state.CurrentWp}";
            }
        }
    }
}