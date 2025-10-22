using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GUI
{
    public partial class Form2 : Form
    {
        private Client client;
        public Form2(Client _client)
        {
            InitializeComponent();
            client = _client;
            this.Text = client.Name;
            client.MessageReceived += (sender, msg) => 
            { 
                rtxtMessages.SelectionColor = Color.White; 
                rtxtMessages.AppendText($"{sender}: {msg}\n"); 
                rtxtMessages.ScrollToCaret(); 
            };
            client.Notification += (type, message) =>
            {
                rtxtMessages.SelectionColor = type switch
                {
                    Client.NotificationType.Info => Color.Green,
                    Client.NotificationType.Warning => Color.Yellow,
                    Client.NotificationType.Error => Color.Red,
                    _ => Color.White
                };
                rtxtMessages.AppendText($"{message}\n");
                rtxtMessages.ScrollToCaret();
            };

            this.FormClosing += new FormClosingEventHandler(Form2_FormClosing);
            this.txtMessage.KeyDown += txtMessage_KeyDown;
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            await client.SendMessageAsync(txtMessage.Text);
            rtxtMessages.SelectionColor = Color.White;
            rtxtMessages.AppendText($"You: {txtMessage.Text}\n");
            rtxtMessages.ScrollToCaret();
            txtMessage.Clear();
        }

        private void Form2_FormClosing(object sender, FormClosingEventArgs e)
        {
            Application.Exit();
        }

        private async void btnSendFile_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog())
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string filePath = ofd.FileName;
                    await client.SendFileAsync(filePath);
                }
            }
        }

        private async void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) 
            {
                button1_Click(sender, e);
            }
        }
    }
}
