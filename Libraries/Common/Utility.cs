using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Common;
public class Utility {
    public static string SHA256Hash(string input) {
        SHA256 hasher = SHA256.Create();
        byte[] hashValue = hasher.ComputeHash(Encoding.UTF8.GetBytes(input));
        StringBuilder sb = new StringBuilder();
        foreach (byte b in hashValue) {
            //x2 is the format specifier for hex with 2 digits
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }
    public static int GenerateAuthCode(ref int outCode, int digits = 6) {
        //min is 10^(digits-1), max is 10^digits
        //since rand.Next is exclusive on the upper bound, we let max go up to 10^digits without subtracting the 1
        int min = (int)Math.Pow(10, digits - 1);
        int max = (int)Math.Pow(10, digits);
        byte[] bytes = new byte[4];
        int value;
        do {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToInt32(bytes, 0) & int.MaxValue; // makes it non-negative
            value = (value % (max - min + 1)) + min; // scale to desired range
        }
        while (value > max || value < min);

        outCode = value;
        return value;
    }
    public static string ResolveSaveDir(string input, string defaultSaveDir) {
        // 1) Treat null/blank or "=default" (case-insensitive) as the default
        if (string.IsNullOrWhiteSpace(input) ||
            string.Equals(input.Trim(), "=default", StringComparison.OrdinalIgnoreCase)) {
            return Path.GetFullPath(defaultSaveDir);
        }

        // 2) Trim whitespace and expand %ENV% (Windows) / $ENV (PowerShell won't expand here, but %ENV% will)
        var path = Environment.ExpandEnvironmentVariables(input.Trim());

        // 3) Expand "~" to the user home (useful on Unix/macOS; harmless on Windows if present)
        if (path.StartsWith("~")) {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var tail = path.TrimStart('~').TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            path = string.IsNullOrEmpty(tail) ? home : Path.Combine(home, tail);
        }

        // 4) If not fully qualified, make it relative to defaultSaveDir
        if (!Path.IsPathFullyQualified(path)) {
            path = Path.Combine(defaultSaveDir, path);
        }

        // 5) Normalize (removes ., .., mixed separators) and return
        return Path.GetFullPath(path);
    }

    public static string GetLocalIP() {
        foreach (var ni in Dns.GetHostEntry(Dns.GetHostName()).AddressList) {
            //Find the first IPv4 that isn't loopback and return it
            if (ni.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ni)) {
                return ni.ToString();
            }
        }
        //Fallback
        return "127.0.0.1";
    }

    public static string GetPlatformName() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            return "Windows";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
            return "macOS";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            return "Linux";
        }

        return "Unknown";
    }

    public enum NotificationType { Info, Warning, Error }
}
