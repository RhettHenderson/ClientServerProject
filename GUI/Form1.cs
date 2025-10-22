using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace GUI
{
    public partial class Form1 : Form
    {
        private Client client;

        public Form1()
        {
            InitializeComponent();
            client = new Client();

            this.FormClosing += new FormClosingEventHandler(Form1_FormClosing);
        }

        private void Form1_Load(object sender, EventArgs e)
        {

        }

        private void IPAddressLabel_Click(object sender, EventArgs e)
        {

        }

        private async void loginButton_Click(object sender, EventArgs e)
        {
            string ip = txtIP.Text;
            string username = txtUsername.Text;
            string passwordHash = SHA256Hash(txtPassword.Text);
            try
            {
                await client.ConnectAsync(ip, 11111, username, passwordHash);
                MessageBox.Show("Connected successfully");

                //Open the new window
                Form2 chatForm = new Form2(client);
                chatForm.Show();
                this.Hide();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Application.Exit();
        }

        public string SHA256Hash(string input)
        {
            SHA256 hasher = SHA256.Create();
            byte[] hashValue = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hashValue)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
