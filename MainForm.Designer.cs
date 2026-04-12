namespace MinimalGCS
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.Label lblSysId;
        private System.Windows.Forms.Label lblMode;
        private System.Windows.Forms.Button btnArm;
        private System.Windows.Forms.Button btnDisarm;
        private System.Windows.Forms.ComboBox cmbModes;
        private System.Windows.Forms.GroupBox groupControl;
        private System.Windows.Forms.ComboBox cmbDrones;
        private System.Windows.Forms.Button btnConnect;
        private System.Windows.Forms.Button btnSmartScan;
        private System.Windows.Forms.ComboBox cmbActiveDrone;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.lblStatus = new System.Windows.Forms.Label();
            this.lblSysId = new System.Windows.Forms.Label();
            this.lblMode = new System.Windows.Forms.Label();
            this.btnArm = new System.Windows.Forms.Button();
            this.btnDisarm = new System.Windows.Forms.Button();
            this.cmbModes = new System.Windows.Forms.ComboBox();
            this.groupControl = new System.Windows.Forms.GroupBox();
            this.cmbDrones = new System.Windows.Forms.ComboBox();
            this.btnConnect = new System.Windows.Forms.Button();
            this.btnSmartScan = new System.Windows.Forms.Button();
            this.groupControl.SuspendLayout();
            this.SuspendLayout();
            
            // btnSmartScan
            this.btnSmartScan.Location = new System.Drawing.Point(12, 10);
            this.btnSmartScan.Size = new System.Drawing.Size(150, 25);
            this.btnSmartScan.Text = "SMART SCAN AND CONNECT";
            this.btnSmartScan.Click += new System.EventHandler(this.btnSmartScan_Click);

            // lblStatus
            this.lblStatus.AutoSize = true;
            this.lblStatus.Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Bold);
            this.lblStatus.Location = new System.Drawing.Point(170, 10);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Text = "Idle";
            this.lblStatus.ForeColor = System.Drawing.Color.Red;

            // cmbDrones
            this.cmbDrones.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbDrones.Location = new System.Drawing.Point(12, 40);
            this.cmbDrones.Size = new System.Drawing.Size(180, 23);
            this.cmbDrones.Items.Add("-- Found Drones --");

            // btnConnect
            this.btnConnect.Location = new System.Drawing.Point(200, 39);
            this.btnConnect.Size = new System.Drawing.Size(72, 25);
            this.btnConnect.Text = "CONNECT";
            this.btnConnect.Click += new System.EventHandler(this.btnConnect_Click);

            // cmbActiveDrone
            this.cmbActiveDrone = new System.Windows.Forms.ComboBox();
            this.cmbActiveDrone.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbActiveDrone.Location = new System.Drawing.Point(12, 70);
            this.cmbActiveDrone.Size = new System.Drawing.Size(260, 23);
            this.cmbActiveDrone.SelectedIndexChanged += new System.EventHandler(this.cmbActiveDrone_SelectedIndexChanged);
            
            // lblSysId
            this.lblSysId.AutoSize = true;
            this.lblSysId.Location = new System.Drawing.Point(12, 100);
            this.lblSysId.Text = "SysID: -";

            // lblMode
            this.lblMode.AutoSize = true;
            this.lblMode.Location = new System.Drawing.Point(12, 90);
            this.lblMode.Text = "Mode: -";

            // groupControl
            this.groupControl.Controls.Add(this.btnArm);
            this.groupControl.Controls.Add(this.btnDisarm);
            this.groupControl.Controls.Add(this.cmbModes);
            this.groupControl.Location = new System.Drawing.Point(12, 120);
            this.groupControl.Size = new System.Drawing.Size(260, 110);
            this.groupControl.Text = "Drone Control";

            // btnArm
            this.btnArm.Location = new System.Drawing.Point(10, 25);
            this.btnArm.Size = new System.Drawing.Size(115, 30);
            this.btnArm.Text = "ARM";
            this.btnArm.BackColor = System.Drawing.Color.LightGreen;
            this.btnArm.Enabled = false;
            this.btnArm.Click += new System.EventHandler(this.btnArm_Click);

            // btnDisarm
            this.btnDisarm.Location = new System.Drawing.Point(135, 25);
            this.btnDisarm.Size = new System.Drawing.Size(115, 30);
            this.btnDisarm.Text = "DISARM";
            this.btnDisarm.BackColor = System.Drawing.Color.LightSalmon;
            this.btnDisarm.Enabled = false;
            this.btnDisarm.Click += new System.EventHandler(this.btnDisarm_Click);

            // cmbModes
            this.cmbModes.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbModes.Items.AddRange(new object[] { "STABILIZE", "GUIDED", "LOITER", "RTL", "AUTO" });
            this.cmbModes.Location = new System.Drawing.Point(10, 65);
            this.cmbModes.Size = new System.Drawing.Size(240, 23);
            this.cmbModes.Enabled = false;
            this.cmbModes.SelectedIndexChanged += new System.EventHandler(this.cmbModes_SelectedIndexChanged);

            // MainForm
            this.ClientSize = new System.Drawing.Size(294, 300);
            this.Controls.Add(this.btnSmartScan);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.cmbDrones);
            this.Controls.Add(this.btnConnect);
            this.Controls.Add(this.cmbActiveDrone);
            this.Controls.Add(this.lblSysId);
            this.Controls.Add(this.lblMode);
            this.Controls.Add(this.groupControl);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.Name = "MainForm";
            this.Text = "Minimal MAVLink GCS";
            
            this.groupControl.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
