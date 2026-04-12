using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MinimalGCS.Connection;
using MinimalGCS.Mavlink;

namespace MinimalGCS
{
    public partial class MainForm : Form
    {
        private AutoConnector _connector;
        private MavLinkInterface? _activeInterface;
        private byte _droneSysId;
        private byte _droneCompId;
        private string _currentMode = "Unknown";

        public MainForm()
        {
            InitializeComponent();
            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.OnDeviceDisconnected += (name) => {
                this.Invoke((Action)(() => {
                    for(int i=0; i<cmbActiveDrone.Items.Count; i++)
                        if (((DiscoveredDevice)cmbActiveDrone.Items[i]).Interface.Name == name)
                            cmbActiveDrone.Items.RemoveAt(i);
                }));
            };
        }

        private void btnSmartScan_Click(object sender, EventArgs e)
        {
            _connector.Start();
            UpdateStatus("Scanning & Connecting...");
        }

        private void OnDeviceConnected(DiscoveredDevice device)
        {
            this.Invoke((Action)(() =>
            {
                // Update Nearby Drones dropdown
                if (!cmbDrones.Items.Cast<object>().Any(x => x is DiscoveredDevice d && d.Interface.Name == device.Interface.Name))
                {
                    cmbDrones.Items.Add(device);
                }

                // Update Active Drones dropdown
                if (!cmbActiveDrone.Items.Cast<object>().Any(x => x is DiscoveredDevice d && d.Interface.Name == device.Interface.Name))
                {
                    cmbActiveDrone.Items.Add(device);
                    if (cmbActiveDrone.SelectedIndex == -1) cmbActiveDrone.SelectedIndex = 0;
                }
            }));

            // Start a telemetry parser specifically for this device
            var parser = new MavLinkParser();
            parser.PacketReceived += (p) => {
                if (_activeInterface == device.Interface) HandleIncomingPacket(p);
            };
            device.Interface.StartReading(data => parser.Parse(data));
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            if (cmbDrones.SelectedItem is DiscoveredDevice device)
            {
                // Force switch to this drone in the active list
                foreach (var item in cmbActiveDrone.Items)
                {
                    if (((DiscoveredDevice)item).Interface.Name == device.Interface.Name)
                    {
                        cmbActiveDrone.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void cmbActiveDrone_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbActiveDrone.SelectedItem is DiscoveredDevice device)
            {
                _activeInterface = device.Interface;
                _droneSysId = device.SysId;
                _droneCompId = device.CompId;
                
                lblStatus.Text = $"Active: {device.Interface.Name}";
                lblStatus.ForeColor = Color.Green;
                lblSysId.Text = $"SysID: {_droneSysId}";
                btnArm.Enabled = true;
                btnDisarm.Enabled = true;
                cmbModes.Enabled = true;
            }
        }

        private void HandleIncomingPacket(MavLinkPacket pkt)
        {
            if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID)
            {
                // Parse custom mode (0-3 bytes of payload for HEARTBEAT)
                uint customMode = BitConverter.ToUInt32(pkt.Payload, 0);
                string modeStr = GetArduPilotMode(customMode);
                
                this.Invoke((Action)(() => {
                    lblMode.Text = $"Mode: {modeStr}";
                }));
            }
        }

        private string GetArduPilotMode(uint mode)
        {
            return mode switch
            {
                0 => "STABILIZE",
                2 => "ALT_HOLD",
                3 => "AUTO",
                4 => "GUIDED",
                5 => "LOITER",
                6 => "RTL",
                9 => "LAND",
                11 => "DRIFT",
                16 => "POSHOLD",
                _ => $"MODE({mode})"
            };
        }

        private void UpdateStatus(string text)
        {
            if (this.InvokeRequired) this.Invoke(new Action(() => UpdateStatus(text)));
            else lblStatus.Text = text;
        }

        private void btnArm_Click(object sender, EventArgs e)
        {
            if (_activeInterface != null)
            {
                // Param2 = 21196 is the "Magic Number" to force ARM (bypass pre-arm checks) in ArduPilot
                byte[] cmd = MavLinkCommands.CreateCommandLong(255, 1, _droneSysId, _droneCompId, 400, 1, 21196);
                _activeInterface.Send(cmd);
            }
        }

        private void btnDisarm_Click(object sender, EventArgs e)
        {
            if (_activeInterface != null)
            {
                byte[] cmd = MavLinkCommands.CreateCommandLong(255, 1, _droneSysId, _droneCompId, 400, 0);
                _activeInterface.Send(cmd);
            }
        }

        private void cmbModes_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_activeInterface != null && cmbModes.SelectedItem != null)
            {
                string? selected = cmbModes.SelectedItem.ToString();
                uint mode = selected switch
                {
                    "STABILIZE" => 0,
                    "GUIDED" => 4,
                    "LOITER" => 5,
                    "RTL" => 6,
                    "AUTO" => 3,
                    _ => 0
                };
                byte[] cmd = MavLinkCommands.CreateSetMode(255, 1, 1, mode); // 1 = MAV_MODE_FLAG_CUSTOM_MODE_ENABLED
                _activeInterface.Send(cmd);
            }
        }
    }
}
