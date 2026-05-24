using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MinimalGCS
{
    [System.Runtime.InteropServices.ComVisible(true)]
    public class MissionMapBridge
    {
        private MissionPlannerForm _form;
        public MissionMapBridge(MissionPlannerForm form) { _form = form; }
        public void TriggerAction(string action, string payload)
        {
            _form.Invoke((MethodInvoker)delegate {
                _form.HandleMapAction(action, payload);
            });
        }
    }

    public class MissionPlannerForm : Form
    {
        private FlowLayoutPanel _missionListPanel;
        private DataGridView _dgvWaypoints;
        private Label _lblDistance, _lblEstTime, _lblWpCount;
        private Button _btnActivate;
        private WebBrowser _previewMap;
        private Mission _currentSelectedMission;

        public Mission SelectedMission { get; private set; }

        public MissionPlannerForm()
        {
            Text = "AGRI-TITAN Mission Control & Planner";
            Size = new Size(1340, 800);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(20, 20, 20);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            // Main layout using panels
            // 1. LEFT PANEL: Library
            var pnlLeft = new Panel { Dock = DockStyle.Left, Width = 340, BackColor = Color.FromArgb(28, 28, 28), Padding = new Padding(10) };
            
            var lblLibTitle = new Label { Text = "📁 WAYPOINT LIBRARY", Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(0, 123, 255), TextAlign = ContentAlignment.MiddleLeft };
            pnlLeft.Controls.Add(lblLibTitle);

            var btnUpload = new Button 
            { 
                Text = "+ IMPORT WAYPOINTS (.txt / .waypoints)", 
                Dock = DockStyle.Top, 
                Height = 45, 
                BackColor = Color.FromArgb(0, 123, 255), 
                ForeColor = Color.White, 
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), 
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnUpload.FlatAppearance.BorderSize = 0;
            btnUpload.Click += BtnUpload_Click;
            pnlLeft.Controls.Add(btnUpload);

            _missionListPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 10, 0, 0), Margin = new Padding(0) };
            pnlLeft.Controls.Add(_missionListPanel);
            _missionListPanel.BringToFront();

            Controls.Add(pnlLeft);

            // Left Separator
            var sep1 = new Panel { Dock = DockStyle.Left, Width = 2, BackColor = Color.FromArgb(45, 45, 45) };
            Controls.Add(sep1);

            // 2. MIDDLE PANEL: Waypoint Details & Actions
            var pnlMiddle = new Panel { Dock = DockStyle.Left, Width = 400, BackColor = Color.FromArgb(28, 28, 28), Padding = new Padding(10) };

            var lblGridTitle = new Label { Text = "📝 FLIGHT PATH DETAILS", Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(253, 126, 20), TextAlign = ContentAlignment.MiddleLeft };
            pnlMiddle.Controls.Add(lblGridTitle);

            // Setup DataGridView for waypoints
            _dgvWaypoints = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(35, 35, 35),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(50, 50, 50),
                BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                EnableHeadersVisualStyles = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical
            };

            _dgvWaypoints.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.FromArgb(253, 126, 20),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(6, 4, 6, 4)
            };

            _dgvWaypoints.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(35, 35, 35),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(60, 60, 60),
                SelectionForeColor = Color.White,
                Font = new Font("Segoe UI", 9f),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(6, 4, 6, 4)
            };

            _dgvWaypoints.Columns.Add("Index", "#");
            _dgvWaypoints.Columns.Add("Command", "COMMAND");
            _dgvWaypoints.Columns.Add("Alt", "ALT (m)");
            _dgvWaypoints.Columns.Add("Coords", "COORDINATES");

            _dgvWaypoints.Columns[0].Width = 35;
            _dgvWaypoints.Columns[1].Width = 90;
            _dgvWaypoints.Columns[2].Width = 60;
            _dgvWaypoints.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

            pnlMiddle.Controls.Add(_dgvWaypoints);

            // Bottom stats & confirmation block
            var pnlStats = new Panel { Dock = DockStyle.Bottom, Height = 185, BackColor = Color.FromArgb(24, 24, 24), Padding = new Padding(12), Margin = new Padding(0, 10, 0, 0) };
            
            _lblWpCount = new Label { Text = "Waypoints: --", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5f), ForeColor = Color.LightGray };
            _lblDistance = new Label { Text = "Full Distance: -- km", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5f), ForeColor = Color.LightGray };
            _lblEstTime = new Label { Text = "Est. Flight Time: --", Dock = DockStyle.Top, Height = 22, Font = new Font("Segoe UI", 9.5f), ForeColor = Color.LightGray };
            
            pnlStats.Controls.Add(_lblEstTime);
            pnlStats.Controls.Add(_lblDistance);
            pnlStats.Controls.Add(_lblWpCount);

            var spacer = new Panel { Dock = DockStyle.Bottom, Height = 8 };

            var btnSave = new Button 
            { 
                Text = "💾 SAVE CHANGES TO LIBRARY", 
                Dock = DockStyle.Bottom, 
                Height = 40, 
                BackColor = Color.FromArgb(0, 123, 255), 
                ForeColor = Color.White, 
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), 
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += BtnSave_Click;

            _btnActivate = new Button 
            { 
                Text = "🚀 ACTIVATE MISSION", 
                Dock = DockStyle.Bottom, 
                Height = 48, 
                BackColor = Color.FromArgb(253, 126, 20), 
                ForeColor = Color.White, 
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), 
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
                Cursor = Cursors.Hand
            };
            _btnActivate.FlatAppearance.BorderSize = 0;
            _btnActivate.Click += BtnActivate_Click;

            pnlStats.Controls.Add(_btnActivate);
            pnlStats.Controls.Add(spacer);
            pnlStats.Controls.Add(btnSave);

            pnlMiddle.Controls.Add(pnlStats);
            pnlStats.BringToFront();

            Controls.Add(pnlMiddle);

            // Middle Separator
            var sep2 = new Panel { Dock = DockStyle.Left, Width = 2, BackColor = Color.FromArgb(45, 45, 45) };
            Controls.Add(sep2);

            // 3. RIGHT PANEL: Geographic Map Preview
            var pnlRight = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10), BackColor = Color.FromArgb(28, 28, 28) };
            
            var lblMapTitle = new Label { Text = "🗺️ GEOGRAPHIC MAP PREVIEW", Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.FromArgb(40, 167, 69), TextAlign = ContentAlignment.MiddleLeft };
            pnlRight.Controls.Add(lblMapTitle);

            _previewMap = new WebBrowser { Dock = DockStyle.Fill, ScrollBarsEnabled = false, WebBrowserShortcutsEnabled = false };
            _previewMap.ObjectForScripting = new MissionMapBridge(this);
            pnlRight.Controls.Add(_previewMap);

            Controls.Add(pnlRight);

            // Load map
            string mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "map.html");
            if (File.Exists(mapPath)) _previewMap.Navigate(new Uri(mapPath));

            RefreshMissionList();

            // Highlight first mission automatically
            this.Load += (s, e) =>
            {
                var missions = MissionManager.GetMissions();
                if (missions.Count > 0)
                {
                    SelectMission(missions[0]);
                }
            };
        }

        private void BtnUpload_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog { Filter = "Waypoint Files|*.waypoints;*.txt" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var waypoints = MissionManager.ParseWaypointFile(ofd.FileName);
                        if (waypoints.Count > 0)
                        {
                            var mission = new Mission
                            {
                                Name = Path.GetFileNameWithoutExtension(ofd.FileName),
                                Waypoints = waypoints
                            };
                            MissionManager.SaveMission(mission);
                            RefreshMissionList();
                            SelectMission(mission);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Invalid waypoint file: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void RefreshMissionList()
        {
            _missionListPanel.Controls.Clear();
            var missions = MissionManager.GetMissions();

            foreach (var m in missions)
            {
                var card = new Panel 
                { 
                    Width = _missionListPanel.Width - 25, 
                    Height = 65, 
                    BackColor = _currentSelectedMission?.Id == m.Id ? Color.FromArgb(60, 60, 60) : Color.FromArgb(45, 45, 45), 
                    Margin = new Padding(0, 0, 0, 8),
                    Cursor = Cursors.Hand
                };

                var lblName = new Label { Text = m.Name, Font = new Font("Segoe UI", 10, FontStyle.Bold), ForeColor = Color.White, AutoSize = true, Location = new Point(10, 10), Enabled = false };
                var lblStats = new Label { Text = $"WPs: {m.Waypoints.Count(w => w.Command == 16)}  |  {(m.TotalDistanceMeters/1000):F2} km", Font = new Font("Segoe UI", 8.5f), ForeColor = Color.LightGray, AutoSize = true, Location = new Point(10, 32), Enabled = false };

                var btnDelete = new Button
                {
                    Text = "🗑",
                    Size = new Size(30, 30),
                    Location = new Point(card.Width - 40, 17),
                    BackColor = Color.FromArgb(100, 20, 20),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Cursor = Cursors.Hand
                };
                btnDelete.FlatAppearance.BorderSize = 0;
                btnDelete.Click += (s, e) =>
                {
                    var confirm = MessageBox.Show($"Delete mission '{m.Name}' permanently?", "Delete Mission", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (confirm == DialogResult.Yes)
                    {
                        MissionManager.DeleteMission(m.Id);
                        RefreshMissionList();
                        _dgvWaypoints.Rows.Clear();
                        _lblWpCount.Text = "Waypoints: --";
                        _lblDistance.Text = "Est. Distance: -- km";
                        _lblEstTime.Text = "Est. Flight Time: --";
                        _btnActivate.Enabled = false;
                        _currentSelectedMission = null;
                        
                        var updated = MissionManager.GetMissions();
                        if (updated.Count > 0) SelectMission(updated[0]);
                    }
                };

                card.Controls.AddRange(new Control[] { lblName, lblStats, btnDelete });
                
                // Allow clicking the card itself to select
                card.Click += (s, e) => SelectMission(m);

                _missionListPanel.Controls.Add(card);
            }
        }

        private void SelectMission(Mission m)
        {
            _currentSelectedMission = m;
            
            // Re-render cards to show selection styling
            RefreshMissionList();

            _dgvWaypoints.Rows.Clear();
            foreach (var wp in m.Waypoints)
            {
                string cmdName = GetWpCommandName(wp.Command, wp.Param2);
                string coordsStr = wp.Lat == 0 && wp.Lon == 0 ? "N/A" : $"{wp.Lat:F6}, {wp.Lon:F6}";
                _dgvWaypoints.Rows.Add(new object[] { wp.Index, cmdName, wp.Alt, coordsStr });
            }

            // Style Rows based on Command Types
            foreach (DataGridViewRow row in _dgvWaypoints.Rows)
            {
                string cmd = row.Cells[1].Value.ToString();
                if (cmd == "TAKEOFF") row.DefaultCellStyle.ForeColor = Color.FromArgb(0, 123, 255);
                else if (cmd.StartsWith("PUMP: ON")) row.DefaultCellStyle.ForeColor = Color.FromArgb(40, 167, 69);
                else if (cmd == "PUMP: OFF") row.DefaultCellStyle.ForeColor = Color.FromArgb(160, 160, 160);
                else if (cmd == "LAND" || cmd == "RTL") row.DefaultCellStyle.ForeColor = Color.FromArgb(220, 53, 69);
            }

            // Stats computation
            double fullDist = CalculateFullDistance(m.Waypoints);
            double km = fullDist / 1000.0;
            _lblWpCount.Text = $"Waypoints: {m.Waypoints.Count}";
            _lblDistance.Text = $"Full Distance: {km:F2} km ({fullDist:F0} m)";
            
            // Cruise speed: 5m/s. Climb speed: 1.5m/s. Descent speed: 1.0m/s.
            double cruiseTime = fullDist / 5.0;
            double takeoffAlt = 5.0;
            var takeoffWp = m.Waypoints.FirstOrDefault(w => w.Command == 22);
            if (takeoffWp != null) takeoffAlt = takeoffWp.Alt;

            double climbTime = takeoffAlt / 1.5;
            double descentTime = takeoffAlt / 1.0;

            double totalTimeSeconds = cruiseTime + climbTime + descentTime;
            int mins = (int)(totalTimeSeconds / 60);
            int secs = (int)(totalTimeSeconds % 60);
            _lblEstTime.Text = $"Est. Flight Time: {mins} min {secs} sec (Takeoff ➔ Land)";
            _btnActivate.Enabled = true;

            // Render on map
            PreviewMission(m);
        }

        private void BtnActivate_Click(object sender, EventArgs e)
        {
            if (_currentSelectedMission != null)
            {
                var confirm = MessageBox.Show($"Activate Mission '{_currentSelectedMission.Name}' and upload coordinates to Drone?", 
                                              "Confirm Mission Activation", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm == DialogResult.Yes)
                {
                    SelectedMission = _currentSelectedMission;
                    _currentSelectedMission.LastUsed = DateTime.Now;
                    MissionManager.SaveMission(_currentSelectedMission);
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            }
        }

        private void PreviewMission(Mission m)
        {
            if (_previewMap.Document != null)
            {
                _previewMap.Document.InvokeScript("clearWaypoints");
                foreach (var wp in m.Waypoints)
                {
                    _previewMap.Document.InvokeScript("addWaypoint", new object[] { wp.Lat, wp.Lon, wp.Index, wp.Command });
                }
                _previewMap.Document.InvokeScript("drawSprayZone");
            }
        }

        public void HandleMapAction(string action, string payload)
        {
            if (action == "UPDATE_WP_COORDS")
            {
                var parts = payload.Split(',');
                if (parts.Length >= 3)
                {
                    try
                    {
                        int index = int.Parse(parts[0]);
                        double lat = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                        double lon = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);

                        if (_currentSelectedMission != null)
                        {
                            var wp = _currentSelectedMission.Waypoints.FirstOrDefault(w => w.Index == index);
                            if (wp != null)
                            {
                                wp.Lat = lat;
                                wp.Lon = lon;

                                // Sync Takeoff command coords to match dragged Home (index 0)
                                if (index == 0)
                                {
                                    var tWp = _currentSelectedMission.Waypoints.FirstOrDefault(w => w.Command == 22);
                                    if (tWp != null)
                                    {
                                        tWp.Lat = lat;
                                        tWp.Lon = lon;
                                    }
                                }

                                // Refresh grid rows
                                _dgvWaypoints.Rows.Clear();
                                foreach (var item in _currentSelectedMission.Waypoints)
                                {
                                    string cmdName = GetWpCommandName(item.Command, item.Param2);
                                    string coordsStr = item.Lat == 0 && item.Lon == 0 ? "N/A" : $"{item.Lat:F6}, {item.Lon:F6}";
                                    _dgvWaypoints.Rows.Add(new object[] { item.Index, cmdName, item.Alt, coordsStr });
                                }

                                foreach (DataGridViewRow row in _dgvWaypoints.Rows)
                                {
                                    string cmd = row.Cells[1].Value.ToString();
                                    if (cmd == "TAKEOFF") row.DefaultCellStyle.ForeColor = Color.FromArgb(0, 123, 255);
                                    else if (cmd.StartsWith("PUMP: ON")) row.DefaultCellStyle.ForeColor = Color.FromArgb(40, 167, 69);
                                    else if (cmd == "PUMP: OFF") row.DefaultCellStyle.ForeColor = Color.FromArgb(160, 160, 160);
                                    else if (cmd == "LAND" || cmd == "RTL") row.DefaultCellStyle.ForeColor = Color.FromArgb(220, 53, 69);
                                }

                                // Re-calculate stats
                                double fullDist = CalculateFullDistance(_currentSelectedMission.Waypoints);
                                double km = fullDist / 1000.0;
                                _lblWpCount.Text = $"Waypoints: {_currentSelectedMission.Waypoints.Count}";
                                _lblDistance.Text = $"Full Distance: {km:F2} km ({fullDist:F0} m)";
                                
                                // Cruise speed: 5m/s. Climb speed: 1.5m/s. Descent speed: 1.0m/s.
                                double cruiseTime = fullDist / 5.0;
                                double takeoffAlt = 5.0;
                                var takeoffWp = _currentSelectedMission.Waypoints.FirstOrDefault(w => w.Command == 22);
                                if (takeoffWp != null) takeoffAlt = takeoffWp.Alt;

                                double climbTime = takeoffAlt / 1.5;
                                double descentTime = takeoffAlt / 1.0;

                                double totalTimeSeconds = cruiseTime + climbTime + descentTime;
                                int mins = (int)(totalTimeSeconds / 60);
                                int secs = (int)(totalTimeSeconds % 60);
                                _lblEstTime.Text = $"Est. Flight Time: {mins} min {secs} sec (Takeoff ➔ Land)";
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            if (_currentSelectedMission != null)
            {
                MissionManager.SaveMission(_currentSelectedMission);
                RefreshMissionList();
                MessageBox.Show($"Mission '{_currentSelectedMission.Name}' successfully saved to library!", "Mission Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }


        private double CalculateFullDistance(List<WaypointItem> wps)
        {
            var geoWps = wps.Where(w => w.Lat != 0 && w.Lon != 0).OrderBy(w => w.Index).ToList();
            if (geoWps.Count == 0) return 0;
            
            double total = 0;
            for (int i = 1; i < geoWps.Count; i++)
            {
                total += Haversine(geoWps[i - 1].Lat, geoWps[i - 1].Lon, geoWps[i].Lat, geoWps[i].Lon);
            }

            // RTL Return path to takeoff
            var lastWp = wps.OrderBy(w => w.Index).LastOrDefault();
            if (lastWp != null && lastWp.Command == 20)
            {
                var home = wps.FirstOrDefault(w => w.Index == 0);
                if (home != null && geoWps.Count > 0)
                {
                    var lastGeo = geoWps.Last();
                    total += Haversine(lastGeo.Lat, lastGeo.Lon, home.Lat, home.Lon);
                }
            }

            return total;
        }

        private double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double R = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private string GetWpCommandName(int cmd, float param2) => cmd switch
        {
            16 => "WAYPOINT",
            22 => "TAKEOFF",
            21 => "LAND",
            20 => "RTL",
            179 => "SET HOME",
            93 => "DELAY",
            181 => param2 == 0 ? "PUMP: ON (SPRAY)" : "PUMP: OFF",
            178 => "SERVO TRIGGER",
            _ => $"CMD ({cmd})"
        };
    }
}
