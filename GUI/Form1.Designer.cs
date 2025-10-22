namespace GUI
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            txtIP = new TextBox();
            txtUsername = new TextBox();
            txtPassword = new TextBox();
            IPAddressLabel = new Label();
            label2 = new Label();
            label1 = new Label();
            loginButton = new Button();
            SuspendLayout();
            // 
            // txtIP
            // 
            txtIP.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtIP.Location = new Point(12, 61);
            txtIP.Name = "txtIP";
            txtIP.Size = new Size(793, 23);
            txtIP.TabIndex = 0;
            // 
            // txtUsername
            // 
            txtUsername.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtUsername.Location = new Point(12, 201);
            txtUsername.Name = "txtUsername";
            txtUsername.Size = new Size(793, 23);
            txtUsername.TabIndex = 1;
            // 
            // txtPassword
            // 
            txtPassword.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtPassword.Location = new Point(12, 321);
            txtPassword.Name = "txtPassword";
            txtPassword.PasswordChar = '*';
            txtPassword.Size = new Size(793, 23);
            txtPassword.TabIndex = 2;
            // 
            // IPAddressLabel
            // 
            IPAddressLabel.AutoSize = true;
            IPAddressLabel.BackColor = SystemColors.ActiveCaptionText;
            IPAddressLabel.Font = new Font("Verdana", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            IPAddressLabel.ForeColor = SystemColors.ActiveBorder;
            IPAddressLabel.Location = new Point(12, 30);
            IPAddressLabel.Name = "IPAddressLabel";
            IPAddressLabel.Size = new Size(299, 18);
            IPAddressLabel.TabIndex = 4;
            IPAddressLabel.Text = "Enter the IP address to connect to:";
            IPAddressLabel.Click += IPAddressLabel_Click;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.BackColor = SystemColors.ActiveCaptionText;
            label2.Font = new Font("Verdana", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label2.ForeColor = SystemColors.ActiveBorder;
            label2.Location = new Point(12, 170);
            label2.Name = "label2";
            label2.Size = new Size(185, 18);
            label2.TabIndex = 5;
            label2.Text = "Enter your username:";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.BackColor = SystemColors.ActiveCaptionText;
            label1.Font = new Font("Verdana", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label1.ForeColor = SystemColors.ActiveBorder;
            label1.Location = new Point(12, 289);
            label1.Name = "label1";
            label1.Size = new Size(182, 18);
            label1.TabIndex = 6;
            label1.Text = "Enter your password:";
            // 
            // loginButton
            // 
            loginButton.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            loginButton.Location = new Point(306, 379);
            loginButton.Name = "loginButton";
            loginButton.Size = new Size(165, 47);
            loginButton.TabIndex = 7;
            loginButton.Text = "Login";
            loginButton.UseVisualStyleBackColor = true;
            loginButton.Click += loginButton_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = SystemColors.ActiveCaptionText;
            ClientSize = new Size(817, 461);
            Controls.Add(loginButton);
            Controls.Add(label1);
            Controls.Add(label2);
            Controls.Add(IPAddressLabel);
            Controls.Add(txtPassword);
            Controls.Add(txtUsername);
            Controls.Add(txtIP);
            Margin = new Padding(2, 1, 2, 1);
            Name = "Form1";
            Text = "Form1";
            Load += Form1_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private TextBox txtIP;
        private TextBox txtUsername;
        private TextBox txtPassword;
        private Label IPAddressLabel;
        private Label label2;
        private Label label1;
        private Button loginButton;
    }
}
