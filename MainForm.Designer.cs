namespace MinimalGCS
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.GroupBox groupControl;
        private System.Windows.Forms.Label lblWatermark;
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
            this.groupControl = new System.Windows.Forms.GroupBox();
            this.lblWatermark = new System.Windows.Forms.Label();
            this.cmbDrones = new System.Windows.Forms.ComboBox();
            this.btnConnect = new System.Windows.Forms.Button();
            this.btnSmartScan = new System.Windows.Forms.Button();
            this.groupControl.SuspendLayout();
            this.SuspendLayout();
            
            // btnSmartScan
            this.btnSmartScan.Location = new System.Drawing.Point(12, 10);
            this.btnSmartScan.Size = new System.Drawing.Size(150, 25);
            this.btnSmartScan.Text = "SMART SCAN AND CONNECT";

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

            // cmbActiveDrone
            this.cmbActiveDrone = new System.Windows.Forms.ComboBox();
            this.cmbActiveDrone.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbActiveDrone.Location = new System.Drawing.Point(12, 70);
            this.cmbActiveDrone.Size = new System.Drawing.Size(260, 23);
            
            // groupControl
            this.groupControl.Location = new System.Drawing.Point(12, 120);
            this.groupControl.Size = new System.Drawing.Size(260, 140);
            this.groupControl.Text = "Discovery Control";
            this.groupControl.Visible = true;
            
            // lblWatermark
            this.lblWatermark.AutoSize = true;
            this.lblWatermark.Font = new System.Drawing.Font("Segoe UI", 8F, System.Drawing.FontStyle.Italic);
            this.lblWatermark.ForeColor = System.Drawing.Color.Gray;
            this.lblWatermark.Anchor = (System.Windows.Forms.AnchorStyles)(System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right);
            this.lblWatermark.Location = new System.Drawing.Point(180, 275);
            this.lblWatermark.Text = "© Prince Tagadiya";

            // MainForm
            this.ClientSize = new System.Drawing.Size(294, 300);
            this.Controls.Add(this.lblWatermark);
            this.Controls.Add(this.btnSmartScan);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.cmbDrones);
            this.Controls.Add(this.btnConnect);
            this.Controls.Add(this.cmbActiveDrone);
            this.Controls.Add(this.groupControl);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.Name = "MainForm";
            this.Text = "Agri-Drone Enterprise v1.3.3";
            
            this.groupControl.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
