using System;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GuestResetApp
{
    public class MainForm : Form
    {
        private WebView2 webView;
        private System.Windows.Forms.Timer adbCheckTimer;
        private string activeDevice = "";
        private bool isAdbConnected = false;
        private bool isCheckingAdb = false;
        private string currentAppLang = "TH";

        // AoB Pattern for GUEST RESET
        private static readonly byte[] SEARCH_AOB = new byte[]
        {
            0x10, 0x40, 0x2D, 0xE9, 0xD0, 0x40, 0x9F, 0xE5,
            0x04, 0x40, 0x8F, 0xE0, 0x00, 0x00, 0xD4, 0xE5,
            0x00, 0x00, 0x50, 0xE3, 0x04, 0x00, 0x00, 0x1A,
            0xC0, 0x00, 0x9F, 0xE5
        };

        private static readonly byte[] REPLACE_AOB = new byte[]
        {
            0x01, 0x00, 0xA0, 0xE3, 0x1E, 0xFF, 0x2F, 0xE1,
            0x04, 0x40, 0x8F, 0xE0, 0x00, 0x00, 0xD4, 0xE5,
            0x00, 0x00, 0x50, 0xE3, 0x04, 0x00, 0x00, 0x1A,
            0xC0, 0x00, 0x9F, 0xE5
        };

        #region Win32 Native APIs
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("Gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
        private static extern IntPtr CreateRoundRectRgn(
            int nLeftRect,
            int nTopRect,
            int nRightRect,
            int nBottomRect,
            int nWidthEllipse,
            int nHeightEllipse
        );

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll")]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        public static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll")]
        public static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 0x2;
        public const int CS_DROPSHADOW = 0x00020000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                return cp;
            }
        }
        #endregion

        public MainForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.Size = new Size(660, 580);
            this.MinimumSize = new Size(620, 540);
            this.BackColor = Color.FromArgb(244, 244, 245);
            this.Text = "Guest Reset OB55";

            // Load app icon
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (!File.Exists(iconPath))
                {
                    string bundleDir = Path.Combine(Path.GetTempPath(), "GuestResetApp_Bundle");
                    Directory.CreateDirectory(bundleDir);
                    iconPath = Path.Combine(bundleDir, "app.ico");
                    ExtractResource("GuestResetApp.app.ico", iconPath);
                }
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch { }

            this.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 24, 24));
            this.Resize += (s, e) =>
            {
                this.Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 24, 24));
            };

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(244, 244, 245)
            };
            this.Controls.Add(webView);

            // Periodic auto-check ADB
            adbCheckTimer = new System.Windows.Forms.Timer();
            adbCheckTimer.Interval = 3000;
            adbCheckTimer.Tick += (s, e) => _ = CheckAdbConnectionAsync();

            InitializeWebView();
        }

        private async void InitializeWebView()
        {
            try
            {
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuestResetApp_WebView2");
                var options = new CoreWebView2EnvironmentOptions(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"));
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // Intercept external links and open in Windows default browser
                webView.CoreWebView2.NavigationStarting += (s, args) =>
                {
                    if (args.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Cancel = true;
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = args.Uri,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                };

                webView.CoreWebView2.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    if (!string.IsNullOrEmpty(args.Uri))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = args.Uri,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                };

                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui.html");
                if (!File.Exists(htmlPath))
                {
                    string bundleDir = Path.Combine(Path.GetTempPath(), "GuestResetApp_Bundle");
                    Directory.CreateDirectory(bundleDir);
                    htmlPath = Path.Combine(bundleDir, "ui.html");
                    ExtractResource("GuestResetApp.ui.html", htmlPath);
                    ExtractResource("GuestResetApp.app_icon.png", Path.Combine(bundleDir, "app_icon.png"));
                    ExtractResource("GuestResetApp.app.ico", Path.Combine(bundleDir, "app.ico"));
                }

                if (File.Exists(htmlPath))
                {
                    webView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                }
                else
                {
                    webView.CoreWebView2.NavigateToString("<h2 style='color:red;'>ui.html not found!</h2>");
                }

                // Push ADB check both on NavigationCompleted and periodic timer
                webView.CoreWebView2.NavigationCompleted += (s, args) =>
                {
                    _ = CheckAdbConnectionAsync();
                };

                adbCheckTimer.Start();
                _ = CheckAdbConnectionAsync();
            }
            catch (Exception ex)
            {
                if (ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException ||
                    ex.Message.Contains("WebView2", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("runtime", StringComparison.OrdinalIgnoreCase))
                {
                    var dr = MessageBox.Show(
                        "ไม่พบ Microsoft Edge WebView2 Runtime บนเครื่องนี้ ซึ่งจำเป็นต้องใช้ในการเปิดโปรแกรม\nต้องการดาวน์โหลดและติดตั้งอัตโนมัติทันทีหรือไม่?\n\n(Microsoft Edge WebView2 Runtime is required. Install now?)",
                        "WebView2 Runtime Required",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (dr == DialogResult.Yes)
                    {
                        try
                        {
                            string installerPath = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
                            using (var hc = new System.Net.Http.HttpClient())
                            {
                                var data = hc.GetByteArrayAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703").GetAwaiter().GetResult();
                                File.WriteAllBytes(installerPath, data);
                            }
                            var p = Process.Start(new ProcessStartInfo
                            {
                                FileName = installerPath,
                                Arguments = "/silent /install",
                                UseShellExecute = true
                            });
                            p?.WaitForExit();

                            Application.Restart();
                            Environment.Exit(0);
                            return;
                        }
                        catch
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    MessageBox.Show("WebView2 Init Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string rawJson = e.WebMessageAsJson;
                using (JsonDocument doc = JsonDocument.Parse(rawJson))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("action", out var actionProp))
                    {
                        string action = actionProp.GetString() ?? "";
                        switch (action)
                        {
                            case "drag":
                                ReleaseCapture();
                                SendMessage(this.Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                                break;
                            case "minimize":
                                this.WindowState = FormWindowState.Minimized;
                                break;
                            case "close":
                                Application.Exit();
                                break;
                            case "checkAdb":
                                _ = CheckAdbConnectionAsync();
                                break;
                            case "connectCustomAdb":
                                if (root.TryGetProperty("target", out var targetProp))
                                {
                                    string target = targetProp.GetString() ?? "";
                                    string lang = currentAppLang;
                                    if (root.TryGetProperty("lang", out var lProp))
                                        lang = lProp.GetString() ?? currentAppLang;
                                    _ = ConnectCustomAdbAsync(target, lang);
                                }
                                break;
                            case "getClipboard":
                                string clipText = "";
                                if (Clipboard.ContainsText())
                                {
                                    clipText = Clipboard.GetText();
                                }
                                SendClipboardTextToWeb(clipText);
                                break;
                            case "langChanged":
                                if (root.TryGetProperty("lang", out var langP))
                                {
                                    currentAppLang = langP.GetString() ?? "TH";
                                }
                                break;
                            case "deploy":
                                if (root.TryGetProperty("code", out var codeProp))
                                {
                                    string code = codeProp.GetString() ?? "";
                                    string lang = currentAppLang;
                                    if (root.TryGetProperty("lang", out var lProp))
                                        lang = lProp.GetString() ?? currentAppLang;
                                    _ = HandleDeployCodeAsync(code, lang);
                                }
                                break;
                            case "resetGuest":
                                {
                                    string lang = currentAppLang;
                                    if (root.TryGetProperty("lang", out var lProp))
                                        lang = lProp.GetString() ?? currentAppLang;
                                    _ = HandleResetGuestAsync(lang);
                                }
                                break;
                            case "themeChanged":
                                if (root.TryGetProperty("isDark", out var isDarkProp))
                                {
                                    bool isDark = isDarkProp.GetBoolean();
                                    SetThemeColor(isDark);
                                }
                                break;
                            case "openUrl":
                                if (root.TryGetProperty("url", out var urlProp))
                                {
                                    string url = urlProp.GetString() ?? "";
                                    if (!string.IsNullOrEmpty(url))
                                    {
                                        try
                                        {
                                            Process.Start(new ProcessStartInfo
                                            {
                                                FileName = url,
                                                UseShellExecute = true
                                            });
                                        }
                                        catch { }
                                    }
                                }
                                break;
                        }
                    }
                }
            }
            catch { }
        }

        private static void ExtractResource(string resourceName, string outputPath)
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                    }
                }
            }
            catch { }
        }

        private void SetThemeColor(bool isDark)
        {
            this.BeginInvoke(new Action(() =>
            {
                Color bg = isDark ? Color.FromArgb(18, 18, 20) : Color.FromArgb(244, 244, 245);
                this.BackColor = bg;
                if (webView != null)
                {
                    webView.DefaultBackgroundColor = bg;
                    webView.BackColor = bg;
                }
            }));
        }

        private void SendClipboardTextToWeb(string text)
        {
            this.BeginInvoke(new Action(() =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        action = "pasteClipboard",
                        text = text
                    });
                    webView.CoreWebView2.PostWebMessageAsJson(payload);
                }
                catch { }
            }));
        }

        private List<string> GetActiveEmulatorAdbPorts()
        {
            var ports = new List<string>();
            try
            {
                string activeInstance = "";
                string? runningDir = null;
                try
                {
                    Process[] procs = Process.GetProcessesByName("HD-Player");
                    if (procs.Length > 0)
                    {
                        runningDir = Path.GetDirectoryName(procs[0].MainModule?.FileName);
                    }

                    using (var searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name = 'HD-Player.exe'"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            string cmd = obj["CommandLine"]?.ToString() ?? "";
                            var m = Regex.Match(cmd, @"--instance\s+([^\s""]+)");
                            if (m.Success)
                            {
                                activeInstance = m.Groups[1].Value.Trim();
                                break;
                            }
                        }
                    }
                }
                catch { }

                // Check BlueStacks configuration files prioritizing the one matching the running executable
                var confPaths = new List<string>();
                if (!string.IsNullOrEmpty(runningDir) && runningDir.Contains("msi5", StringComparison.OrdinalIgnoreCase))
                {
                    confPaths.Add(@"C:\ProgramData\BlueStacks_msi5\bluestacks.conf");
                    confPaths.Add(@"C:\ProgramData\BlueStacks_nxt\bluestacks.conf");
                }
                else
                {
                    confPaths.Add(@"C:\ProgramData\BlueStacks_nxt\bluestacks.conf");
                    confPaths.Add(@"C:\ProgramData\BlueStacks_msi5\bluestacks.conf");
                }

                foreach (var conf in confPaths)
                {
                    if (File.Exists(conf))
                    {
                        var lines = File.ReadAllLines(conf);

                        // If active instance was found, prioritize its ADB port
                        if (!string.IsNullOrEmpty(activeInstance))
                        {
                            foreach (var line in lines)
                            {
                                if (line.Contains($"bst.instance.{activeInstance}.status.adb_port=") ||
                                    line.Contains($"bst.instance.{activeInstance}.adb_port="))
                                {
                                    var match = Regex.Match(line, "\"(\\d+)\"");
                                    if (match.Success)
                                    {
                                        string p = $"127.0.0.1:{match.Groups[1].Value}";
                                        if (!ports.Contains(p)) ports.Insert(0, p);
                                    }
                                }
                            }
                        }

                        // Also parse any other configured instances
                        foreach (var line in lines)
                        {
                            if (line.Contains(".status.adb_port=") || line.Contains(".adb_port="))
                            {
                                var match = Regex.Match(line, "\"(\\d+)\"");
                                if (match.Success)
                                {
                                    string p = $"127.0.0.1:{match.Groups[1].Value}";
                                    if (!ports.Contains(p)) ports.Add(p);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // Standard fallbacks (5554 is console port, NOT ADB, so we NEVER add 5554)
            if (!ports.Contains("127.0.0.1:5555")) ports.Add("127.0.0.1:5555");
            if (!ports.Contains("127.0.0.1:5565")) ports.Add("127.0.0.1:5565");

            return ports;
        }

        private async Task CheckAdbConnectionAsync()
        {
            if (isCheckingAdb) return;
            isCheckingAdb = true;

            await Task.Run(() =>
            {
                try
                {
                    string adbPath = FindAdb();
                    NativeAdb.EnsureServerRunning(adbPath);

                    // 1. Fast connect to standard emulator port 127.0.0.1:5555
                    NativeAdb.Connect("127.0.0.1:5555");

                    // 2. Query adb devices
                    var onlineDevices = NativeAdb.GetDevices();

                    // If not found yet, scan candidate emulator instance ports
                    if (onlineDevices.Count == 0)
                    {
                        var autoPorts = GetActiveEmulatorAdbPorts();
                        foreach (var port in autoPorts)
                        {
                            if (port != "127.0.0.1:5555") NativeAdb.Connect(port);
                        }
                        onlineDevices = NativeAdb.GetDevices();
                    }

                    bool found = onlineDevices.Count > 0;
                    string chosenDev = "";

                    if (found)
                    {
                        // Prioritize 127.0.0.1:<port> over emulator-5554 so it matches BlueStacks UI!
                        chosenDev = onlineDevices.Find(d => d.StartsWith("127.0.0.1:")) ?? "";
                        if (string.IsNullOrEmpty(chosenDev))
                        {
                            if (onlineDevices.Contains("emulator-5554"))
                            {
                                NativeAdb.Connect("127.0.0.1:5555");
                                chosenDev = "127.0.0.1:5555";
                            }
                            else
                            {
                                chosenDev = onlineDevices[0];
                            }
                        }
                    }

                    SetAdbStatus(found, chosenDev);
                }
                catch
                {
                    SetAdbStatus(false, "");
                }
                finally
                {
                    isCheckingAdb = false;
                }
            });
        }

        private async Task ConnectCustomAdbAsync(string target, string lang)
        {
            await Task.Run(() =>
            {
                string adbPath = FindAdb();
                NativeAdb.EnsureServerRunning(adbPath);

                string outStr = NativeAdb.Connect(target);
                if (outStr.Contains("connected") && !outStr.Contains("cannot"))
                {
                    string successMsg = lang == "EN" ? $"Connected to ADB: {target} successfully!" : $"เชื่อมต่อ ADB: {target} สำเร็จ!";
                    SendToastToWeb(successMsg, "success");
                    SetAdbStatus(true, target);
                }
                else
                {
                    string failMsg = lang == "EN" ? $"Failed to connect ADB: {target} ({outStr.Trim()})" : $"เชื่อมต่อ ADB: {target} ไม่สำเร็จ ({outStr.Trim()})";
                    SendToastToWeb(failMsg, "error");
                    _ = CheckAdbConnectionAsync();
                }
            });
        }

        private void SetAdbStatus(bool connected, string devName)
        {
            isAdbConnected = connected;
            activeDevice = devName;

            this.BeginInvoke(new Action(() =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        action = "adbStatus",
                        connected = connected,
                        device = devName
                    });
                    webView.CoreWebView2.PostWebMessageAsJson(payload);
                }
                catch { }
            }));
        }

        private async Task HandleResetGuestAsync(string lang)
        {
            bool success = await Task.Run(() =>
            {
                string adbPath = FindAdb();
                NativeAdb.EnsureServerRunning(adbPath);

                string dev = NativeAdb.ResolveActiveDevice(activeDevice);
                if (string.IsNullOrEmpty(dev))
                {
                    var ports = GetActiveEmulatorAdbPorts();
                    foreach (var port in ports)
                    {
                        string cOut = NativeAdb.Connect(port);
                        if (cOut.Contains("connected") && !cOut.Contains("cannot"))
                        {
                            dev = port;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(dev)) return false;
                activeDevice = dev;
                isAdbConnected = true;

                try
                {
                    // 1. Force stop Free Fire
                    NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefireth");
                    NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefiremax");

                    // 2. Wipe all login & guest tokens via root using sh -c (wildcard expansion)
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'rm -f /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk* /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk* /data/data/com.dts.freefireth/shared_prefs/com.facebook.loginManager* /data/data/com.dts.freefireth/shared_prefs/com.facebook.AccessToken* /data/data/com.dts.freefireth/shared_prefs/com.google.android.gms.signin*'");

                    // 3. Delete accounts from system accounts SQLite DB via root without syntax errors
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sqlite3 /data/system_ce/0/accounts_ce.db \"DELETE FROM authtokens; DELETE FROM extras; DELETE FROM accounts WHERE type LIKE '%garena%'; PRAGMA wal_checkpoint(TRUNCATE);\"");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sqlite3 /data/system_de/0/accounts_de.db \"DELETE FROM authtokens; DELETE FROM extras; DELETE FROM accounts WHERE type LIKE '%garena%'; PRAGMA wal_checkpoint(TRUNCATE);\"");

                    // 4. Randomize device android_id
                    string randomId = Guid.NewGuid().ToString("N").Substring(0, 16);
                    NativeAdb.ExecuteShell(activeDevice, $"settings put secure android_id {randomId}");

                    // 5. Remove sdcard and internal guest files (contentcache and game assets remain 100% untouched)
                    NativeAdb.ExecuteShell(activeDevice, "rm -f /sdcard/com.garena.msdk/guest100067.dat");
                    NativeAdb.ExecuteShell(activeDevice, "rm -f /sdcard/Android/data/com.dts.freefireth/files/guest100067.dat");
                    NativeAdb.ExecuteShell(activeDevice, "rm -f /sdcard/Android/data/com.dts.freefiremax/files/guest100067.dat");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 rm -f /data/data/com.dts.freefireth/files/guest100067.dat");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 rm -f /data/data/com.dts.freefiremax/files/guest100067.dat");

                    // 6. Remove LASTLOGIN_SUCCESSFULL to ensure game lands cleanly on Title/Login screen
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'grep -v LASTLOGIN_SUCCESSFULL /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml > /data/data/com.dts.freefireth/shared_prefs/pp.tmp && mv /data/data/com.dts.freefireth/shared_prefs/pp.tmp /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml && chmod 660 /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml && chown u0_a62:u0_a62 /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml'");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'grep -v LASTLOGIN_SUCCESSFULL /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml > /data/data/com.dts.freefiremax/shared_prefs/pp.tmp && mv /data/data/com.dts.freefiremax/shared_prefs/pp.tmp /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml && chmod 660 /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml && chown u0_a62:u0_a62 /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml'");

                    // 7. Reload Android OS AccountManager cache by restarting system_server and reconnecting
                    NativeAdb.RestartSystemServerAndReconnect(activeDevice);

                    // 8. Launch Free Fire and ensure foreground focus
                    NativeAdb.LaunchAndEnsureGameFocus(activeDevice);

                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (success)
            {
                string successMsg = lang == "EN" ? "Guest reset successfully! Free Fire is launching to login screen..." : "รีเซ็ต Guest สำเร็จ! เกมเปิดไปที่หน้าล็อกอินแล้ว...";
                SendToastToWeb(successMsg, "success");

                // Execute AoB Memory Hook with polling so it catches the game libraries as soon as loaded
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 25; attempt++)
                    {
                        await Task.Delay(1000);
                        int count = ApplyMemoryPatch();
                        if (count > 0)
                        {
                            string patchMsg = lang == "EN" ? $"Guest patched successfully ({count} offsets)! Press Guest to create account." : $"แพตช์ Guest สำเร็จ ({count} จุด)! กด Guest เพื่อสร้างรหัสใหม่ได้เลย";
                            SendToastToWeb(patchMsg, "success");
                            break;
                        }
                    }
                });
            }
            else
            {
                string errMsg = lang == "EN" ? "Emulator not found or ADB disconnected" : "ไม่พบ Emulator หรือยังไม่ได้เชื่อมต่อ ADB";
                SendToastToWeb(errMsg, "error");
            }
        }

        private async Task HandleDeployCodeAsync(string guestJson, string lang)
        {
            bool success = await Task.Run(() =>
            {
                return DeployGuestCodeToEmulator(guestJson);
            });

            if (success)
            {
                string successMsg = lang == "EN" ? "Code deployed successfully! Game is ready at login screen." : "ลงโค้ดสำเร็จ! เกมเปิดไปที่หน้าล็อกอินแล้ว กด Guest เพื่อเข้าเล่นได้เลย";
                SendToastToWeb(successMsg, "success");
            }
            else
            {
                string errMsg = lang == "EN" ? "Emulator not found or ADB disconnected" : "ไม่พบ Emulator หรือยังไม่ได้เชื่อมต่อ ADB";
                SendToastToWeb(errMsg, "error");
            }
        }

        private bool DeployGuestCodeToEmulator(string guestJson)
        {
            string dev = NativeAdb.ResolveActiveDevice(activeDevice);
            if (string.IsNullOrEmpty(dev))
            {
                var ports = GetActiveEmulatorAdbPorts();
                foreach (var port in ports)
                {
                    string cOut = NativeAdb.Connect(port);
                    if (cOut.Contains("connected") && !cOut.Contains("cannot"))
                    {
                        dev = port;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(dev)) return false;
            activeDevice = dev;
            isAdbConnected = true;

            try
            {
                // Parse guest JSON for uid and password
                string uid = "";
                string pwd = "";
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(guestJson))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("guest_account_info", out var info))
                        {
                            if (info.TryGetProperty("com.garena.msdk.guest_uid", out var u)) uid = u.GetString() ?? "";
                            if (info.TryGetProperty("com.garena.msdk.guest_password", out var p)) pwd = p.GetString() ?? "";
                        }
                        else
                        {
                            if (root.TryGetProperty("com.garena.msdk.guest_uid", out var u)) uid = u.GetString() ?? "";
                            if (root.TryGetProperty("com.garena.msdk.guest_password", out var p)) pwd = p.GetString() ?? "";
                        }
                    }
                }
                catch { }

                // 1. Force stop Free Fire first to flush any file handles
                NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefireth");
                NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefiremax");

                // 2. Insert or replace Android OS accounts database and extras
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(pwd))
                {
                    string sqlCe = $"/system/xbin/bstk/su 0 sqlite3 /data/system_ce/0/accounts_ce.db \"DELETE FROM authtokens; INSERT OR REPLACE INTO accounts (_id, name, type, password) VALUES (11, '{uid}', 'com.garena.msdk100067th', '{pwd}'); INSERT OR REPLACE INTO extras (accounts_id, key, value) VALUES (11, 'com.garena.msdk.guest_uid', '{uid}'); INSERT OR REPLACE INTO extras (accounts_id, key, value) VALUES (11, 'com.garena.msdk.guest_password', '{pwd}'); PRAGMA wal_checkpoint(TRUNCATE);\"";
                    NativeAdb.ExecuteShell(activeDevice, sqlCe);

                    string sqlDe = $"/system/xbin/bstk/su 0 sqlite3 /data/system_de/0/accounts_de.db \"INSERT OR REPLACE INTO accounts (_id, name, type) VALUES (11, '{uid}', 'com.garena.msdk100067th'); PRAGMA wal_checkpoint(TRUNCATE);\"";
                    NativeAdb.ExecuteShell(activeDevice, sqlDe);
                }

                // 3. Write guest token directly into all possible guest storage locations via base64
                NativeAdb.WriteFileViaBase64(activeDevice, "/sdcard/com.garena.msdk/guest100067.dat", guestJson.Trim());
                NativeAdb.WriteFileViaBase64(activeDevice, "/sdcard/Android/data/com.dts.freefireth/files/guest100067.dat", guestJson.Trim());
                NativeAdb.WriteFileViaBase64(activeDevice, "/sdcard/Android/data/com.dts.freefiremax/files/guest100067.dat", guestJson.Trim());
                NativeAdb.WriteFileViaBase64(activeDevice, "/data/data/com.dts.freefireth/files/guest100067.dat", guestJson.Trim());
                NativeAdb.WriteFileViaBase64(activeDevice, "/data/data/com.dts.freefiremax/files/guest100067.dat", guestJson.Trim());

                // 4. Write persist.fallback.xml directly to app shared_prefs
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(pwd))
                {
                    string fallbackXml = "<?xml version='1.0' encoding='utf-8' standalone='yes' ?>\n<map>\n" +
                                         $"    <string name=\"com.garena.msdk.guest_password\">{pwd}</string>\n" +
                                         $"    <string name=\"com.garena.msdk.guest_uid\">{uid}</string>\n</map>";
                    NativeAdb.WriteFileViaBase64(activeDevice, "/data/data/com.dts.freefireth/shared_prefs/com.garena.msdk.persist.fallback.xml", fallbackXml);
                    NativeAdb.WriteFileViaBase64(activeDevice, "/data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk.persist.fallback.xml", fallbackXml);
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 chmod 660 /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk.persist.fallback.xml");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 chown u0_a62:u0_a62 /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk.persist.fallback.xml");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 chmod 660 /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk.persist.fallback.xml");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 chown u0_a62:u0_a62 /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk.persist.fallback.xml");
                }

                // 5. Remove any cached OAuth tokens and ensure logout so Free Fire is forced to authenticate the new guest
                NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'rm -f /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk.token* /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk*crypto* /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk.SERVICE_PREF* /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk.token* /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk*crypto* /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk.SERVICE_PREF*'");
                NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'grep -v LASTLOGIN_SUCCESSFULL /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml > /data/data/com.dts.freefireth/shared_prefs/pp.tmp && mv /data/data/com.dts.freefireth/shared_prefs/pp.tmp /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml && chmod 660 /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml && chown u0_a62:u0_a62 /data/data/com.dts.freefireth/shared_prefs/com.dts.freefireth.v2.playerprefs.xml'");
                NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'grep -v LASTLOGIN_SUCCESSFULL /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml > /data/data/com.dts.freefiremax/shared_prefs/pp.tmp && mv /data/data/com.dts.freefiremax/shared_prefs/pp.tmp /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml && chmod 660 /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml && chown u0_a62:u0_a62 /data/data/com.dts.freefiremax/shared_prefs/com.dts.freefiremax.v2.playerprefs.xml'");

                // 6. Reload Android OS AccountManager by soft restarting system_server and reconnecting
                NativeAdb.RestartSystemServerAndReconnect(activeDevice);

                // 7. Launch Free Fire and ensure foreground focus
                NativeAdb.LaunchAndEnsureGameFocus(activeDevice);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private int ApplyMemoryPatch()
        {
            int patchedCount = 0;
            try
            {
                Process[] procs = Process.GetProcessesByName("HD-Player");
                if (procs.Length == 0) return 0;

                // OpenProcess with PROCESS_VM_READ (0x0010) | PROCESS_VM_WRITE (0x0020) | PROCESS_VM_OPERATION (0x0008)
                IntPtr hProcess = OpenProcess(0x0010 | 0x0020 | 0x0008, false, procs[0].Id);
                if (hProcess == IntPtr.Zero) return 0;

                try
                {
                    long current = 0;
                    long maxAddress = 0x7FFFFFFF0000;
                    int chunkSize = 16 * 1024 * 1024; // 16 MB chunks
                    byte[] chunkBuf = new byte[chunkSize];
                    int pLen = SEARCH_AOB.Length;
                    byte firstByte = SEARCH_AOB[0]; // 0x10

                    MEMORY_BASIC_INFORMATION mbi;
                    int mbiSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION));
                    var targetAddresses = new List<IntPtr>();

                    while (current < maxAddress && VirtualQueryEx(hProcess, (IntPtr)current, out mbi, (uint)mbiSize) != 0)
                    {
                        long regionSize = (long)mbi.RegionSize;
                        long baseAddr = (long)mbi.BaseAddress;

                        // Scannable: committed, not guard/noaccess, and executable or readwrite
                        bool isScannable = (mbi.State == 0x1000) &&
                                           ((mbi.Protect & 0x100) == 0) &&
                                           ((mbi.Protect & 0x01) == 0) &&
                                           ((mbi.Protect & 0x04) != 0 || // PAGE_READWRITE
                                            (mbi.Protect & 0x40) != 0 || // PAGE_EXECUTE_READWRITE
                                            (mbi.Protect & 0x20) != 0);   // PAGE_EXECUTE_READ

                        if (isScannable && regionSize > 0)
                        {
                            long regionOffset = 0;
                            byte[] overlap = Array.Empty<byte>();

                            while (regionOffset < regionSize)
                            {
                                int toRead = (int)Math.Min((long)chunkSize, regionSize - regionOffset);
                                IntPtr chunkPtr = (IntPtr)(baseAddr + regionOffset);

                                if (ReadProcessMemory(hProcess, chunkPtr, chunkBuf, toRead, out IntPtr bytesRead) && (int)bytesRead > 0)
                                {
                                    int readCount = (int)bytesRead;
                                    int overlapLen = overlap.Length;
                                    int totalDataLen = overlapLen + readCount;

                                    byte[] data = new byte[totalDataLen];
                                    if (overlapLen > 0)
                                    {
                                        Buffer.BlockCopy(overlap, 0, data, 0, overlapLen);
                                    }
                                    Buffer.BlockCopy(chunkBuf, 0, data, overlapLen, readCount);

                                    int limit = totalDataLen - pLen;
                                    for (int i = 0; i <= limit; i++)
                                    {
                                        // Fast skip on first byte
                                        if (data[i] != firstByte) continue;

                                        bool match = true;
                                        for (int j = 1; j < pLen; j++)
                                        {
                                            if (data[i + j] != SEARCH_AOB[j])
                                            {
                                                match = false;
                                                break;
                                            }
                                        }

                                        if (match)
                                        {
                                            long matchAddr = baseAddr + regionOffset - overlapLen + i;
                                            targetAddresses.Add((IntPtr)matchAddr);
                                        }
                                    }

                                    if (totalDataLen >= pLen)
                                    {
                                        overlap = new byte[pLen - 1];
                                        Buffer.BlockCopy(data, totalDataLen - (pLen - 1), overlap, 0, pLen - 1);
                                    }
                                    else
                                    {
                                        overlap = Array.Empty<byte>();
                                    }
                                }
                                else
                                {
                                    overlap = Array.Empty<byte>();
                                }

                                regionOffset += toRead;
                            }
                        }

                        current = baseAddr + regionSize;
                        if (current <= baseAddr) break;
                    }

                    // Step 3: WriteProcessMemory with REPLACE_AOB
                    foreach (var addr in targetAddresses)
                    {
                        uint oldProtect;
                        VirtualProtectEx(hProcess, addr, (UIntPtr)REPLACE_AOB.Length, 0x40, out oldProtect);
                        IntPtr written;
                        if (WriteProcessMemory(hProcess, addr, REPLACE_AOB, REPLACE_AOB.Length, out written))
                        {
                            patchedCount++;
                        }
                        VirtualProtectEx(hProcess, addr, (UIntPtr)REPLACE_AOB.Length, oldProtect, out oldProtect);
                    }
                }
                finally
                {
                    CloseHandle(hProcess);
                }
            }
            catch { }

            return patchedCount;
        }

        private string FindAdb()
        {
            // 1. Prioritize currently running HD-Player emulator process directory
            try
            {
                Process[] procs = Process.GetProcessesByName("HD-Player");
                if (procs.Length > 0)
                {
                    string? procPath = procs[0].MainModule?.FileName;
                    if (!string.IsNullOrEmpty(procPath))
                    {
                        string dir = Path.GetDirectoryName(procPath) ?? "";
                        string candidate = Path.Combine(dir, "HD-Adb.exe");
                        if (File.Exists(candidate)) return candidate;
                        candidate = Path.Combine(dir, "adb.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { }

            // 2. Fallback to standard installation paths
            string[] paths = new string[]
            {
                @"C:\Program Files\BlueStacks_msi5\HD-Adb.exe",
                @"C:\Program Files\BlueStacks_nxt\HD-Adb.exe",
                @"C:\Program Files (x86)\BlueStacks_msi5\HD-Adb.exe",
                @"C:\Program Files (x86)\BlueStacks_nxt\HD-Adb.exe",
                @"C:\Program Files\Nox\bin\nox_adb.exe",
                @"C:\Program Files\LDPlayer\LDPlayer9\adb.exe",
                @"adb.exe"
            };
            foreach (var p in paths)
            {
                if (File.Exists(p)) return p;
            }
            return "adb";
        }

        private void SendToastToWeb(string message, string type)
        {
            this.BeginInvoke(new Action(() =>
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new { toast = message, type = type });
                    webView.CoreWebView2.PostWebMessageAsJson(payload);
                }
                catch { }
            }));
        }
    }

    public static class NativeAdb
    {
        private const string HOST = "127.0.0.1";
        private const int PORT = 5037;

        public static bool EnsureServerRunning(string adbFallbackPath)
        {
            try
            {
                using var test = new TcpClient();
                var ar = test.BeginConnect(HOST, PORT, null, null);
                if (ar.AsyncWaitHandle.WaitOne(300))
                {
                    test.EndConnect(ar);
                    return true;
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(adbFallbackPath) && File.Exists(adbFallbackPath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = adbFallbackPath,
                        Arguments = "start-server",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(3000);
                }
                catch { }
            }

            try
            {
                using var test = new TcpClient();
                var ar = test.BeginConnect(HOST, PORT, null, null);
                if (ar.AsyncWaitHandle.WaitOne(1000))
                {
                    test.EndConnect(ar);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void SendRequest(NetworkStream stream, string req)
        {
            byte[] payload = Encoding.UTF8.GetBytes(req);
            string lenHex = payload.Length.ToString("X4");
            byte[] lenBytes = Encoding.ASCII.GetBytes(lenHex);
            stream.Write(lenBytes, 0, lenBytes.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static string ReadStatus(NetworkStream stream)
        {
            byte[] statusBuf = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = stream.Read(statusBuf, read, 4 - read);
                if (r <= 0) break;
                read += r;
            }
            return Encoding.ASCII.GetString(statusBuf, 0, read);
        }

        private static string ReadHexPrefixedString(NetworkStream stream)
        {
            byte[] lenBuf = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int r = stream.Read(lenBuf, read, 4 - read);
                if (r <= 0) break;
                read += r;
            }
            if (read < 4) return "";
            string hex = Encoding.ASCII.GetString(lenBuf);
            if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int len) || len <= 0)
            {
                return "";
            }
            byte[] dataBuf = new byte[len];
            int dataRead = 0;
            while (dataRead < len)
            {
                int r = stream.Read(dataBuf, dataRead, len - dataRead);
                if (r <= 0) break;
                dataRead += r;
            }
            return Encoding.UTF8.GetString(dataBuf, 0, dataRead);
        }

        public static List<string> GetDevices(int timeoutMs = 2000)
        {
            var devices = new List<string>();
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(HOST, PORT, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return devices;
                client.EndConnect(ar);

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                SendRequest(stream, "host:devices");
                string status = ReadStatus(stream);
                if (status != "OKAY") return devices;

                string list = ReadHexPrefixedString(stream);
                var lines = list.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Contains("device") && !line.Contains("offline") && !line.Contains("unauthorized"))
                    {
                        var parts = line.Split('\t', ' ');
                        string dev = parts[0].Trim();
                        if (!string.IsNullOrEmpty(dev) && !devices.Contains(dev))
                        {
                            devices.Add(dev);
                        }
                    }
                }
            }
            catch { }
            return devices;
        }

        public static string ResolveActiveDevice(string preferred)
        {
            var devices = GetDevices();
            if (devices.Count == 0)
            {
                Connect("127.0.0.1:5555");
                devices = GetDevices();
            }
            if (devices.Count == 0) return "";
            if (!string.IsNullOrEmpty(preferred) && devices.Contains(preferred))
            {
                return preferred;
            }
            var tcpDev = devices.Find(d => d.StartsWith("127.0.0.1:"));
            if (!string.IsNullOrEmpty(tcpDev)) return tcpDev;
            return devices[0];
        }

        public static string Connect(string target, int timeoutMs = 2500)
        {
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(HOST, PORT, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return "connect timeout";
                client.EndConnect(ar);

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                SendRequest(stream, $"host:connect:{target}");
                string status = ReadStatus(stream);
                if (status == "OKAY")
                {
                    return ReadHexPrefixedString(stream);
                }
                return status;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string ExecuteShell(string device, string cmd, int timeoutMs = 5000)
        {
            string target = !string.IsNullOrEmpty(device) ? device : ResolveActiveDevice("");
            if (string.IsNullOrEmpty(target)) return "";

            string? res = ExecuteShellInternal(target, cmd, timeoutMs);
            if (res == null)
            {
                string alt = ResolveActiveDevice(target == "127.0.0.1:5555" ? "emulator-5554" : "127.0.0.1:5555");
                if (!string.IsNullOrEmpty(alt) && alt != target)
                {
                    res = ExecuteShellInternal(alt, cmd, timeoutMs);
                }
            }
            return res ?? "";
        }

        private static string? ExecuteShellInternal(string device, string cmd, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(HOST, PORT, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return null;
                client.EndConnect(ar);

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                SendRequest(stream, $"host:transport:{device}");
                string status = ReadStatus(stream);
                if (status != "OKAY") return null;

                SendRequest(stream, $"shell:{cmd}");
                string shellStatus = ReadStatus(stream);
                if (shellStatus != "OKAY") return null;

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }

        public static bool WriteFileViaBase64(string device, string remotePath, string content, int timeoutMs = 5000)
        {
            try
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content.Trim()));
                string dir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/') ?? "";
                
                // 1. mkdir
                ExecuteShell(device, $"mkdir -p \"{dir}\"", timeoutMs);
                ExecuteShell(device, $"/system/xbin/bstk/su 0 mkdir -p \"{dir}\"", timeoutMs);

                // 2. Decode base64 directly without quote nesting issues
                ExecuteShell(device, $"sh -c 'echo {b64} | base64 -d > \"{remotePath}\"'", timeoutMs);
                ExecuteShell(device, $"/system/xbin/bstk/su 0 sh -c 'echo {b64} | base64 -d > \"{remotePath}\"'", timeoutMs);

                // 3. Ensure permissions
                ExecuteShell(device, $"chmod 666 \"{remotePath}\"", timeoutMs);
                ExecuteShell(device, $"/system/xbin/bstk/su 0 chmod 666 \"{remotePath}\"", timeoutMs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool RestartSystemServerAndReconnect(string device, int maxWaitSeconds = 20)
        {
            try
            {
                string oldPid = ExecuteShell(device, "pidof system_server").Trim();
                ExecuteShell(device, "/system/xbin/bstk/su 0 killall -9 system_server");
                System.Threading.Thread.Sleep(2500);

                for (int i = 0; i < maxWaitSeconds; i++)
                {
                    Connect("127.0.0.1:5555", 1500);
                    string newPid = ExecuteShell(device, "pidof system_server", 1500).Trim();
                    if (!string.IsNullOrEmpty(newPid) && newPid != oldPid)
                    {
                        System.Threading.Thread.Sleep(1500);
                        return true;
                    }
                    System.Threading.Thread.Sleep(1000);
                }
            }
            catch { }
            return false;
        }

        public static bool LaunchAndEnsureGameFocus(string device, int maxAttempts = 15)
        {
            try
            {
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    ExecuteShell(device, "am start -n com.dts.freefireth/com.dts.freefireth.FFMainActivity");
                    ExecuteShell(device, "am start -n com.dts.freefiremax/com.dts.freefireth.FFMainActivity");
                    System.Threading.Thread.Sleep(1500);

                    string focus = ExecuteShell(device, "dumpsys window | grep mCurrentFocus");
                    if (focus.Contains("com.dts.freefireth") || focus.Contains("com.dts.freefiremax"))
                    {
                        return true;
                    }

                    ExecuteShell(device, "monkey -p com.dts.freefireth -c android.intent.category.LAUNCHER 1");
                    ExecuteShell(device, "monkey -p com.dts.freefiremax -c android.intent.category.LAUNCHER 1");
                    System.Threading.Thread.Sleep(1000);
                }
            }
            catch { }
            return false;
        }
    }
}
