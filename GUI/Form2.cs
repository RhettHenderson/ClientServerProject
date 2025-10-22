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

            this.FormClosing += new FormClosingEventHandler(Form2_FormClosing);
        }

        private async void button1_Click(object sender, EventArgs e)
        {
            await client.SendMessageAsync(txtMessage.Text);
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
    }
}
