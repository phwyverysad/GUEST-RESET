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
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
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
                MessageBox.Show("WebView2 Init Error: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                                    _ = ConnectCustomAdbAsync(target);
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
                            case "deploy":
                                if (root.TryGetProperty("code", out var codeProp))
                                {
                                    string code = codeProp.GetString() ?? "";
                                    _ = HandleDeployCodeAsync(code);
                                }
                                break;
                            case "resetGuest":
                                _ = HandleResetGuestAsync();
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

        private async Task ConnectCustomAdbAsync(string target)
        {
            await Task.Run(() =>
            {
                string adbPath = FindAdb();
                NativeAdb.EnsureServerRunning(adbPath);

                string outStr = NativeAdb.Connect(target);
                if (outStr.Contains("connected") && !outStr.Contains("cannot"))
                {
                    SendToastToWeb($"เชื่อมต่อ ADB: {target} สำเร็จ!", "success");
                    SetAdbStatus(true, target);
                }
                else
                {
                    SendToastToWeb($"เชื่อมต่อ ADB: {target} ไม่สำเร็จ ({outStr.Trim()})", "error");
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

        private async Task HandleResetGuestAsync()
        {
            bool success = await Task.Run(() =>
            {
                string adbPath = FindAdb();
                NativeAdb.EnsureServerRunning(adbPath);

                if (string.IsNullOrEmpty(activeDevice) || !isAdbConnected)
                {
                    var ports = GetActiveEmulatorAdbPorts();
                    foreach (var port in ports)
                    {
                        string cOut = NativeAdb.Connect(port);
                        if (cOut.Contains("connected") && !cOut.Contains("cannot"))
                        {
                            activeDevice = port;
                            isAdbConnected = true;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(activeDevice)) return false;

                try
                {
                    // 1. Force stop Free Fire
                    NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefireth");
                    NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefiremax");

                    // 2. Wipe all login & guest tokens via root using sh -c (wildcard expansion)
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c 'rm -f /data/data/com.dts.freefireth/shared_prefs/com.garena.msdk* /data/data/com.dts.freefiremax/shared_prefs/com.garena.msdk* /data/data/com.dts.freefireth/shared_prefs/com.facebook.loginManager* /data/data/com.dts.freefireth/shared_prefs/com.facebook.AccessToken* /data/data/com.dts.freefireth/shared_prefs/com.google.android.gms.signin*'");

                    // 3. Delete accounts from system accounts SQLite DB via root using sh -c
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c \"sqlite3 /data/system_ce/0/accounts_ce.db 'DELETE FROM accounts WHERE type LIKE \'%garena%\';'\"");
                    NativeAdb.ExecuteShell(activeDevice, "/system/xbin/bstk/su 0 sh -c \"sqlite3 /data/system_de/0/accounts_de.db 'DELETE FROM accounts WHERE type LIKE \'%garena%\';'\"");

                    // 4. Randomize device android_id
                    string randomId = Guid.NewGuid().ToString("N").Substring(0, 16);
                    NativeAdb.ExecuteShell(activeDevice, $"settings put secure android_id {randomId}");

                    // 5. Remove sdcard guest files (contentcache and game assets remain 100% untouched)
                    NativeAdb.ExecuteShell(activeDevice, "rm -f /sdcard/com.garena.msdk/guest100067.dat");
                    NativeAdb.ExecuteShell(activeDevice, "rm -f /sdcard/Android/data/com.dts.freefireth/files/guest100067.dat");
                    NativeAdb.ExecuteShell(activeDevice, "rm -f /sdcard/Android/data/com.dts.freefiremax/files/guest100067.dat");

                    return true;
                }
                catch
                {
                    return false;
                }
            });

            if (success)
            {
                SendToastToWeb("รีเซ็ต Guest สำเร็จ! กำลังเปิดเกม Free Fire...", "success");

                // Auto launch Free Fire
                await Task.Delay(1000);
                NativeAdb.ExecuteShell(activeDevice, "monkey -p com.dts.freefireth -c android.intent.category.LAUNCHER 1");
                NativeAdb.ExecuteShell(activeDevice, "monkey -p com.dts.freefiremax -c android.intent.category.LAUNCHER 1");

                // Execute AoB Memory Hook with polling so it catches the game libraries as soon as loaded
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 25; attempt++)
                    {
                        await Task.Delay(1000);
                        int count = ApplyMemoryPatch();
                        if (count > 0)
                        {
                            SendToastToWeb($"แพตช์ Guest สำเร็จ ({count} จุด)! กด Guest เพื่อสร้างรหัสใหม่ได้เลย", "success");
                            break;
                        }
                    }
                });
            }
            else
            {
                SendToastToWeb("ไม่พบ Emulator หรือยังไม่ได้เชื่อมต่อ ADB", "error");
            }
        }

        private async Task HandleDeployCodeAsync(string guestJson)
        {
            bool success = await Task.Run(() =>
            {
                return DeployGuestCodeToEmulator(guestJson);
            });

            if (success)
            {
                SendToastToWeb("ลงโค้ดสำเร็จ! กำลังเปิดเกม Free Fire...", "success");

                // Auto launch Free Fire
                await Task.Delay(1000);
                NativeAdb.ExecuteShell(activeDevice, "monkey -p com.dts.freefireth -c android.intent.category.LAUNCHER 1");
                NativeAdb.ExecuteShell(activeDevice, "monkey -p com.dts.freefiremax -c android.intent.category.LAUNCHER 1");

                // Execute AoB Memory Hook with polling so it catches the game libraries as soon as loaded
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 25; attempt++)
                    {
                        await Task.Delay(1000);
                        int count = ApplyMemoryPatch();
                        if (count > 0)
                        {
                            break;
                        }
                    }
                });
            }
            else
            {
                SendToastToWeb("ไม่พบ Emulator หรือยังไม่ได้เชื่อมต่อ ADB", "error");
            }
        }

        private bool DeployGuestCodeToEmulator(string guestJson)
        {
            if (string.IsNullOrEmpty(activeDevice) || !isAdbConnected)
            {
                var ports = GetActiveEmulatorAdbPorts();
                foreach (var port in ports)
                {
                    string cOut = NativeAdb.Connect(port);
                    if (cOut.Contains("connected") && !cOut.Contains("cannot"))
                    {
                        activeDevice = port;
                        isAdbConnected = true;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(activeDevice)) return false;

            try
            {
                // 1. Force stop Free Fire first to flush any file handles
                NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefireth");
                NativeAdb.ExecuteShell(activeDevice, "am force-stop com.dts.freefiremax");

                // 2. Write guest token directly into all possible guest storage locations via base64
                NativeAdb.WriteFileViaBase64(activeDevice, "/sdcard/com.garena.msdk/guest100067.dat", guestJson.Trim());
                NativeAdb.WriteFileViaBase64(activeDevice, "/sdcard/Android/data/com.dts.freefireth/files/guest100067.dat", guestJson.Trim());
                NativeAdb.WriteFileViaBase64(activeDevice, "/sdcard/Android/data/com.dts.freefiremax/files/guest100067.dat", guestJson.Trim());

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
                                            (mbi.Protect & 0x20) != 0 || // PAGE_EXECUTE_READ
                                            (mbi.Protect & 0x02) != 0);   // PAGE_READONLY

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
            try
            {
                using var client = new TcpClient();
                var ar = client.BeginConnect(HOST, PORT, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return "";
                client.EndConnect(ar);

                using var stream = client.GetStream();
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                SendRequest(stream, $"host:transport:{device}");
                string status = ReadStatus(stream);
                if (status != "OKAY") return "";

                SendRequest(stream, $"shell:{cmd}");
                string shellStatus = ReadStatus(stream);
                if (shellStatus != "OKAY") return "";

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                return "";
            }
        }

        public static bool WriteFileViaBase64(string device, string remotePath, string content, int timeoutMs = 5000)
        {
            try
            {
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                string dir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/') ?? "";
                string cmd = $"mkdir -p \"{dir}\" && sh -c 'echo \"{b64}\" | base64 -d > \"{remotePath}\"'";
                ExecuteShell(device, cmd, timeoutMs);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
