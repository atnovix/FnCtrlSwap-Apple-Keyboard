using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace FnCtrlSwap
{
    public static class Program
    {
        static readonly object Sync = new object();
        static readonly Dictionary<string, DeviceReader> Readers = new Dictionary<string, DeviceReader>(StringComparer.OrdinalIgnoreCase);
        public static volatile bool Active = true;
        public static volatile bool FnDown;
        public static bool DebugMode;
        static string _logPath;
        static NotifyIcon _icon;
        static volatile bool _ctrlInjected;
        static Hid.LowLevelKeyboardProc _hookProc;
        static IntPtr _hookHandle;
        static readonly bool[] _swallowUp = new bool[256];
        static bool _eqDown;       // fysieke "="-toets is ingedrukt
        static bool _eqPending;    // ingeslikte "=" die bij loslaten alsnog getypt wordt
        static bool _eqAsModifier; // "=" is als Delete-modifier gebruikt: up inslikken

        public static bool CtrlInjected { get { return _ctrlInjected; } }

        [STAThread]
        public static void Main(string[] args)
        {
            bool createdNew;
            Mutex mutex = new Mutex(true, @"Local\FnCtrlSwapSingleton", out createdNew);
            if (!createdNew) return;

            DebugMode = Array.IndexOf(args, "--debug") >= 0;
            bool showTray = Array.IndexOf(args, "--tray") >= 0;
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            _logPath = Path.Combine(dir, "FnCtrlSwap.log");
            try { if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 512 * 1024) File.Delete(_logPath); } catch { }
            Log("gestart, debug=" + DebugMode + ", tray=" + showTray);

            System.Threading.Timer rescanTimer = new System.Threading.Timer(delegate(object s) { Rescan(); }, null, 0, 3000);

            _hookProc = HookCallback;
            _hookHandle = Hid.SetWindowsHookEx(13, _hookProc, Hid.GetModuleHandle(null), 0);
            if (_hookHandle == IntPtr.Zero) Log("toetsenbord-hook mislukt: " + Marshal.GetLastWin32Error());

            _icon = new NotifyIcon();
            _icon.Icon = System.Drawing.SystemIcons.Application;
            _icon.Text = "Fn = Ctrl (Apple toetsenbord)";
            ContextMenu menu = new ContextMenu();
            MenuItem miActive = new MenuItem("Actief");
            miActive.Checked = true;
            miActive.Click += delegate(object s, EventArgs e)
            {
                Active = !Active;
                miActive.Checked = Active;
                if (!Active) SendCtrl(false);
            };
            MenuItem miExit = new MenuItem("Afsluiten");
            miExit.Click += delegate(object s, EventArgs e)
            {
                _icon.Visible = false;
                SendCtrl(false);
                Application.Exit();
            };
            menu.MenuItems.Add(miActive);
            menu.MenuItems.Add(miExit);
            _icon.ContextMenu = menu;
            _icon.Visible = showTray;

            Application.ApplicationExit += delegate(object s, EventArgs e) { SendCtrl(false); };
            Application.Run();
            GC.KeepAlive(mutex);
            GC.KeepAlive(rescanTimer);
        }

        static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                bool down = (msg == 0x0100 || msg == 0x0104);
                bool up = (msg == 0x0101 || msg == 0x0105);
                Hid.KBDLLHOOKSTRUCT info = (Hid.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Hid.KBDLLHOOKSTRUCT));
                bool injected = (info.flags & 0x10) != 0;
                int vk = (int)info.vkCode;
                if (!injected && vk < 256)
                {
                    if (up && _swallowUp[vk])
                    {
                        _swallowUp[vk] = false;
                        return (IntPtr)1;
                    }
                    // "=" (naast Backspace) is een vasthoudtoets: samen met Backspace -> Delete.
                    // Een losse tik "=" wordt pas bij het loslaten getypt; auto-repeat van "=" vervalt.
                    if (vk == 0xBB && Active)
                    {
                        if (down)
                        {
                            if (!_eqDown)
                            {
                                _eqDown = true;
                                if ((Hid.GetAsyncKeyState(0x08) & 0x8000) != 0)
                                {
                                    _eqAsModifier = true;
                                    QueueAction(delegate { Hid.TapKey(0x2E, 0x53, true); }); // Delete
                                }
                                else _eqPending = true;
                            }
                            return (IntPtr)1; // ook auto-repeat inslikken
                        }
                        if (up && _eqDown)
                        {
                            _eqDown = false;
                            bool wasMod = _eqAsModifier, wasPending = _eqPending;
                            _eqAsModifier = false;
                            _eqPending = false;
                            if (wasPending)
                                QueueAction(delegate { Hid.TapKey(0xBB, 0x0D, false); }); // alsnog "="
                            if (wasMod || wasPending) return (IntPtr)1;
                        }
                    }
                    if (down && Active && vk == 0x08 && _eqDown)
                    {
                        _eqPending = false;
                        _eqAsModifier = true;
                        _swallowUp[vk] = true;
                        QueueAction(delegate { Hid.TapKey(0x2E, 0x53, true); }); // Delete (herhaalt met Backspace-repeat)
                        return (IntPtr)1;
                    }
                    if (down && Active)
                    {
                        if (FnDown && FnAction(vk))
                        {
                            _swallowUp[vk] = true;
                            return (IntPtr)1;
                        }
                        // Ctrl+Backspace (dus ook Fn+Backspace) -> Delete
                        if (vk == 0x08 && (Hid.GetAsyncKeyState(0x11) & 0x8000) != 0)
                        {
                            _swallowUp[vk] = true;
                            QueueAction(delegate { Hid.TapKeyWithoutCtrl(0x2E, 0x53, true); });
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return Hid.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // Fn + F-toets -> functie zoals de opdruk op het Apple-toetsenbord
        static bool FnAction(int vk)
        {
            switch (vk)
            {
                case 0x70: QueueAction(delegate { Hid.ChangeBrightness(-10); }); return true;          // F1 helderheid -
                case 0x71: QueueAction(delegate { Hid.ChangeBrightness(10); }); return true;           // F2 helderheid +
                case 0x72: QueueAction(delegate { Hid.TapWinComboWithoutCtrl(0x09, 0x0F); }); return true; // F3 taakweergave (Win+Tab)
                case 0x73: QueueAction(delegate { Hid.TapWinComboWithoutCtrl(0x57, 0x11); }); return true; // F4 widgets (Win+W)
                case 0x76: QueueAction(delegate { Hid.TapKeyWithoutCtrl(0xB1, 0x10, true); }); return true; // F7 vorige
                case 0x77: QueueAction(delegate { Hid.TapKeyWithoutCtrl(0xB3, 0x22, true); }); return true; // F8 play/pauze
                case 0x78: QueueAction(delegate { Hid.TapKeyWithoutCtrl(0xB0, 0x19, true); }); return true; // F9 volgende
                case 0x79: QueueAction(delegate { Hid.TapKeyWithoutCtrl(0xAD, 0x20, true); }); return true; // F10 mute
                case 0x7A: QueueAction(delegate { Hid.TapKeyWithoutCtrl(0xAE, 0x2E, true); }); return true; // F11 volume -
                case 0x7B: QueueAction(delegate { Hid.TapKeyWithoutCtrl(0xAF, 0x30, true); }); return true; // F12 volume +
                default: return false;
            }
        }

        static void QueueAction(Action a)
        {
            ThreadPool.QueueUserWorkItem(delegate(object s)
            {
                try { a(); }
                catch (Exception ex) { Log("actie-fout: " + ex.Message); }
            });
        }

        static void Rescan()
        {
            try
            {
                List<string> paths = Hid.EnumerateDevicePaths();
                foreach (string path in paths)
                {
                    // Apple: 05AC via USB, 000205AC via Bluetooth
                    if (path.IndexOf("vid&000205ac", StringComparison.OrdinalIgnoreCase) < 0 &&
                        path.IndexOf("vid_05ac", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    lock (Sync)
                    {
                        if (Readers.ContainsKey(path)) continue;
                        DeviceReader r = new DeviceReader(path);
                        if (r.Start()) Readers[path] = r;
                    }
                }
            }
            catch (Exception ex) { Log("rescan-fout: " + ex.Message); }
        }

        static DateTime _lastClaudeLaunch = DateTime.MinValue;

        // Eject-toets: open Claude in een cmd-venster. Staat een Verkenner-venster op de
        // voorgrond, dan in de map die daar open is; anders in de Documenten-map.
        public static void LaunchClaude()
        {
            if ((DateTime.Now - _lastClaudeLaunch).TotalMilliseconds < 1500) return;
            _lastClaudeLaunch = DateTime.Now;
            IntPtr fg = Hid.GetForegroundWindow();
            Log("Eject ingedrukt -> Claude starten");
            Thread t = new Thread(delegate()
            {
                try
                {
                    string workDir = GetExplorerFolder(fg);
                    if (workDir == null)
                        workDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                    Log("Claude-werkmap: " + workDir);
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                    psi.FileName = "cmd.exe";
                    psi.Arguments = "/K claude";
                    psi.WorkingDirectory = workDir;
                    psi.UseShellExecute = false;
                    // Geerfde Claude Code-sessiemarkers niet doorgeven, anders denkt
                    // de nieuwe claude dat hij een subsessie is
                    List<string> remove = new List<string>();
                    foreach (System.Collections.DictionaryEntry e in psi.EnvironmentVariables)
                    {
                        string k = (string)e.Key;
                        if (k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) ||
                            k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase))
                            remove.Add(k);
                    }
                    foreach (string k in remove) psi.EnvironmentVariables.Remove(k);
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex) { Log("claude-start-fout: " + ex.Message); }
            });
            t.SetApartmentState(ApartmentState.STA); // Shell.Application (COM) wil STA
            t.IsBackground = true;
            t.Start();
        }

        // Pad van de map die openstaat in het Verkenner-venster hwnd, of null als
        // hwnd geen Verkenner is of geen echte map toont (Deze pc, Prullenbak, ...)
        static string GetExplorerFolder(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
                Hid.GetClassName(hwnd, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls != "CabinetWClass" && cls != "ExploreWClass") return null;

                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                object shell = Activator.CreateInstance(shellType);
                object windows = shellType.InvokeMember("Windows", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                try
                {
                    int count = Convert.ToInt32(windows.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, windows, null));
                    for (int i = 0; i < count; i++)
                    {
                        object win = null;
                        try
                        {
                            win = windows.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, windows, new object[] { i });
                            if (win == null) continue;
                            long h = Convert.ToInt64(win.GetType().InvokeMember("HWND", System.Reflection.BindingFlags.GetProperty, null, win, null));
                            if (h != hwnd.ToInt64()) continue;
                            object doc = win.GetType().InvokeMember("Document", System.Reflection.BindingFlags.GetProperty, null, win, null);
                            object folder = doc.GetType().InvokeMember("Folder", System.Reflection.BindingFlags.GetProperty, null, doc, null);
                            object self = folder.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
                            string path = self.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, self, null) as string;
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                            return null; // virtuele locatie zonder echt pad
                        }
                        catch (Exception ex) { Log("verkenner-venster-fout: " + ex.Message); }
                        finally { if (win != null) Marshal.ReleaseComObject(win); }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(windows);
                    Marshal.ReleaseComObject(shell);
                }
            }
            catch (Exception ex) { Log("verkenner-map-fout: " + ex.Message); }
            return null;
        }

        public static void ReaderStopped(DeviceReader r)
        {
            lock (Sync) Readers.Remove(r.DevicePath);
        }

        public static void SendCtrl(bool down)
        {
            lock (Sync)
            {
                if (down == _ctrlInjected) return;
                _ctrlInjected = down;
                Hid.SendKeyExt(0xA2, 0x1D, false, down); // VK_LCONTROL
            }
        }

        public static void Log(string msg)
        {
            try
            {
                lock (Sync)
                    File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n");
            }
            catch { }
        }

        public static string Tail(string s)
        {
            return s.Length <= 60 ? s : "..." + s.Substring(s.Length - 60);
        }
    }

    public class DeviceReader
    {
        public readonly string DevicePath;
        SafeFileHandle _handle;
        FileStream _stream;
        int _reportLen;
        bool _fnDown;
        bool _ejectDown;
        readonly HashSet<byte> _seenIds = new HashSet<byte>();
        static readonly HashSet<string> LoggedFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DeviceReader(string path) { DevicePath = path; }

        public bool Start()
        {
            _handle = Hid.Open(DevicePath);
            if (_handle == null || _handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                lock (LoggedFailures)
                {
                    if (LoggedFailures.Add(DevicePath))
                        Program.Log("openen mislukt (fout " + err + "): " + Program.Tail(DevicePath));
                }
                return false;
            }
            Hid.HIDP_CAPS caps;
            if (Hid.GetCaps(_handle, out caps) && caps.InputReportByteLength > 0)
                _reportLen = caps.InputReportByteLength;
            else
                _reportLen = 64;
            Program.Log("open ok, usagePage=0x" + caps.UsagePage.ToString("X4") +
                        " usage=0x" + caps.Usage.ToString("X2") +
                        " reportLen=" + _reportLen + ": " + Program.Tail(DevicePath));
            _stream = new FileStream(_handle, FileAccess.Read, _reportLen, false);
            Thread t = new Thread(ReadLoop);
            t.IsBackground = true;
            t.Start();
            return true;
        }

        void ReadLoop()
        {
            byte[] buf = new byte[_reportLen];
            try
            {
                while (true)
                {
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    Handle(buf, n);
                }
            }
            catch (Exception ex)
            {
                Program.Log("lezen gestopt (" + ex.Message + "): " + Program.Tail(DevicePath));
            }
            finally
            {
                if (_fnDown)
                {
                    _fnDown = false;
                    Program.FnDown = false;
                    Program.SendCtrl(false);
                }
                try { if (_stream != null) _stream.Dispose(); } catch { }
                Program.ReaderStopped(this);
            }
        }

        void Handle(byte[] buf, int n)
        {
            if (n < 2) return;
            byte id = buf[0];
            lock (_seenIds)
            {
                if (_seenIds.Add(id))
                    Program.Log("report-id 0x" + id.ToString("X2") + " gezien op " + Program.Tail(DevicePath));
            }
            if (Program.DebugMode)
                Program.Log("report: " + BitConverter.ToString(buf, 0, Math.Min(n, 16)) + " (" + Program.Tail(DevicePath) + ")");
            if (id != 0x11) return;
            bool eject = (buf[1] & 0x08) != 0;
            if (eject != _ejectDown)
            {
                _ejectDown = eject;
                if (eject) Program.LaunchClaude();
            }
            bool fn = (buf[1] & 0x10) != 0;
            if (fn == _fnDown) return;
            _fnDown = fn;
            Program.FnDown = fn;
            Program.Log("Fn " + (fn ? "ingedrukt" : "losgelaten"));
            if (Program.Active) Program.SendCtrl(fn);
            else if (!fn) Program.SendCtrl(false);
        }
    }

    public static class Hid
    {
        const int DIGCF_PRESENT = 0x02;
        const int DIGCF_DEVICEINTERFACE = 0x10;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("hid.dll")]
        static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);
        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static List<string> EnumerateDevicePaths()
        {
            List<string> result = new List<string>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devInfo = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devInfo == (IntPtr)(-1)) return result;
            try
            {
                for (int i = 0; ; i++)
                {
                    SP_DEVICE_INTERFACE_DATA did = new SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref hidGuid, i, ref did)) break;
                    SP_DEVICE_INTERFACE_DETAIL_DATA detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                    detail.cbSize = (IntPtr.Size == 8) ? 8 : (4 + Marshal.SystemDefaultCharSize);
                    int required;
                    if (SetupDiGetDeviceInterfaceDetail(devInfo, ref did, ref detail, Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DETAIL_DATA)), out required, IntPtr.Zero))
                        result.Add(detail.DevicePath);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(devInfo); }
            return result;
        }

        public static SafeFileHandle Open(string path)
        {
            // GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING
            return CreateFile(path, 0x80000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        public static bool GetCaps(SafeFileHandle handle, out HIDP_CAPS caps)
        {
            caps = new HIDP_CAPS();
            IntPtr preparsed;
            if (!HidD_GetPreparsedData(handle, out preparsed)) return false;
            try
            {
                return HidP_GetCaps(preparsed, out caps) == 0x00110000; // HIDP_STATUS_SUCCESS
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }

        public static void SendKeyExt(ushort vk, ushort scan, bool extended, bool down)
        {
            INPUT[] input = new INPUT[1];
            input[0].type = 1; // INPUT_KEYBOARD
            input[0].U.ki.wVk = vk;
            input[0].U.ki.wScan = scan;
            uint flags = down ? 0u : 2u; // KEYEVENTF_KEYUP
            if (extended) flags |= 1u;   // KEYEVENTF_EXTENDEDKEY
            input[0].U.ki.dwFlags = flags;
            SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
        }

        // Simpele toetsaanslag (down+up), met de modifiers die op dat moment ingedrukt zijn
        public static void TapKey(ushort vk, ushort scan, bool extended)
        {
            SendKeyExt(vk, scan, extended, true);
            SendKeyExt(vk, scan, extended, false);
        }

        // Toets aanslaan met tijdelijk losgelaten Ctrl, zodat de app een "kale" toets ziet
        public static void TapKeyWithoutCtrl(ushort vk, ushort scan, bool extended)
        {
            bool wasInjected = Program.CtrlInjected;
            bool l = (GetAsyncKeyState(0xA2) & 0x8000) != 0;
            bool r = (GetAsyncKeyState(0xA3) & 0x8000) != 0;
            if (l) SendKeyExt(0xA2, 0x1D, false, false);
            if (r) SendKeyExt(0xA3, 0x1D, true, false);
            SendKeyExt(vk, scan, extended, true);
            SendKeyExt(vk, scan, extended, false);
            if (r) SendKeyExt(0xA3, 0x1D, true, true);
            if (l && (!wasInjected || Program.FnDown)) SendKeyExt(0xA2, 0x1D, false, true);
        }

        public static void TapWinComboWithoutCtrl(ushort vk, ushort scan)
        {
            bool wasInjected = Program.CtrlInjected;
            bool l = (GetAsyncKeyState(0xA2) & 0x8000) != 0;
            bool r = (GetAsyncKeyState(0xA3) & 0x8000) != 0;
            if (l) SendKeyExt(0xA2, 0x1D, false, false);
            if (r) SendKeyExt(0xA3, 0x1D, true, false);
            SendKeyExt(0x5B, 0x5B, true, true); // LWin
            SendKeyExt(vk, scan, false, true);
            SendKeyExt(vk, scan, false, false);
            SendKeyExt(0x5B, 0x5B, true, false);
            if (r) SendKeyExt(0xA3, 0x1D, true, true);
            if (l && (!wasInjected || Program.FnDown)) SendKeyExt(0xA2, 0x1D, false, true);
        }

        public static void ChangeBrightness(int delta)
        {
            try
            {
                int current = -1;
                System.Management.ManagementObjectSearcher get = new System.Management.ManagementObjectSearcher("root\\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                try
                {
                    foreach (System.Management.ManagementObject o in get.Get())
                    {
                        current = Convert.ToInt32(o["CurrentBrightness"]);
                        o.Dispose();
                        break;
                    }
                }
                finally { get.Dispose(); }
                if (current < 0)
                {
                    Program.Log("helderheid: geen WMI-waarde beschikbaar");
                    return;
                }
                int nv = Math.Max(0, Math.Min(100, current + delta));
                System.Management.ManagementObjectSearcher set = new System.Management.ManagementObjectSearcher("root\\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
                try
                {
                    foreach (System.Management.ManagementObject o in set.Get())
                    {
                        o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)nv });
                        o.Dispose();
                        break;
                    }
                }
                finally { set.Dispose(); }
            }
            catch (Exception ex) { Program.Log("helderheid-fout: " + ex.Message); }
        }
    }
}
