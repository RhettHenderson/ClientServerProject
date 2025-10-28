namespace GUI
{
    partial class Form2
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            rtxtMessages = new RichTextBox();
            txtMessage = new TextBox();
            btnSendMessage = new Button();
            btnSendFile = new Button();
            label1 = new Label();
            SuspendLayout();
            // 
            // rtxtMessages
            // 
            rtxtMessages.BackColor = SystemColors.ActiveCaptionText;
            rtxtMessages.ForeColor = SystemColors.ControlLightLight;
            rtxtMessages.Location = new Point(12, 12);
            rtxtMessages.Name = "rtxtMessages";
            rtxtMessages.ReadOnly = true;
            rtxtMessages.Size = new Size(776, 341);
            rtxtMessages.TabIndex = 0;
            rtxtMessages.Text = "";
            // 
            // txtMessage
            // 
            txtMessage.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtMessage.Location = new Point(12, 395);
            txtMessage.Name = "txtMessage";
            txtMessage.Size = new Size(656, 23);
            txtMessage.TabIndex = 1;
            // 
            // btnSendMessage
            // 
            btnSendMessage.Location = new Point(692, 394);
            btnSendMessage.Name = "btnSendMessage";
            btnSendMessage.Size = new Size(75, 23);
            btnSendMessage.TabIndex = 2;
            btnSendMessage.Text = "Send";
            btnSendMessage.UseVisualStyleBackColor = true;
            btnSendMessage.Click += button1_Click;
            // 
            // btnSendFile
            // 
            btnSendFile.Location = new Point(773, 394);
            btnSendFile.Name = "btnSendFile";
            btnSendFile.Size = new Size(75, 23);
            btnSendFile.TabIndex = 3;
            btnSendFile.Text = "Send File";
            btnSendFile.UseVisualStyleBackColor = true;
            btnSendFile.Click += btnSendFile_Click;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.BackColor = SystemColors.ActiveCaptionText;
            label1.Font = new Font("Segoe UI", 12F);
            label1.ForeColor = SystemColors.AppWorkspace;
            label1.Location = new Point(12, 371);
            label1.Name = "label1";
            label1.Size = new Size(150, 21);
            label1.TabIndex = 4;
            label1.Text = "Enter your message:";
            // 
            // Form2
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = SystemColors.ActiveCaptionText;
            ClientSize = new Size(860, 465);
            Controls.Add(label1);
            Controls.Add(btnSendFile);
            Controls.Add(btnSendMessage);
            Controls.Add(txtMessage);
            Controls.Add(rtxtMessages);
            Name = "Form2";
            Text = "Form2";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private RichTextBox rtxtMessages;
        private TextBox txtMessage;
        private Button btnSendMessage;
        private Button btnSendFile;
        private Label label1;
    }
}