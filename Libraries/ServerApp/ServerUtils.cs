using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace ServerApp;

//Several Server-unique functions that aren't directly related to sending/receiving data from the network
internal static class ServerUtils {
    internal static bool IsSameMachine(Socket socket) {
        if (socket.RemoteEndPoint is not IPEndPoint remote) {
            return false;
        }

        if (IPAddress.IsLoopback(remote.Address)) {
            return true;
        }

        try {
            var localAddresses = Dns.GetHostAddresses(Dns.GetHostName());
            return localAddresses.Any(addr => addr.Equals(remote.Address));
        }
        catch {
            return false;
        }
    }

    internal static string BuildProgessBar(double percent, int width = 40) {
        percent = Math.Clamp(percent, 0.0, 100.0);
        int filled = (int)Math.Round(percent / 100.0 * width);
        if (filled > width) filled = width;

        string filledPart = new string('\u2588', filled);
        string emptyPart = new string('-', width - filled);
        return $"[{filledPart}{emptyPart}] {percent,5:0.0}%";
    }

    // === Certificate Loading ===
    internal static X509Certificate2 LoadServerCertificate() {
        var thumb = (Environment.GetEnvironmentVariable("SERVER_CERT_THUMBPRINT") ?? "");
        if (string.IsNullOrEmpty(thumb)) {
            throw new InvalidOperationException("SERVER_CERT_THUMBPRINT environment variable not set.");
        }

        //First check local machine store
        var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var cert = store.Certificates
            .Find(X509FindType.FindByThumbprint, thumb, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(c => c.HasPrivateKey);

        //If it's not in local machine, check current user store
        if (cert == null) {
            store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            cert = store.Certificates
            .Find(X509FindType.FindByThumbprint, thumb, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault(c => c.HasPrivateKey)
            //if it's still null, it means it genuinely doesn't exist
            ?? throw new InvalidOperationException("Certificate with specified thumbprint not found.");
        }
        return cert;
    }
}
