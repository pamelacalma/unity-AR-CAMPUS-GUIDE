using System;
using System.Collections;
using System.Collections.Generic;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System.Diagnostics;
using System.Text;
#endif
using UnityEngine;
using UnityEngine.Android;

[Serializable] public class WifiAp   { public string b; public string s; public int r; public int f; }
[Serializable] public class WifiScan { public long t; public List<WifiAp> aps = new List<WifiAp>(); }
[Serializable] public class BleAdv   { public long t; public string a; public string n; public int r; }
[Serializable] public class RadioDrain
{
    public List<WifiScan> wifiScans = new List<WifiScan>();
    public List<BleAdv> ble = new List<BleAdv>();
    public int scanRequests;
    public int scanRejected;
    public string lastError;
}
public class BleSeen { public string name; public int rssi; public float lastSeenTime; }

/// <summary>
/// Unity side of the Wi-Fi + BLE scanner. On Android it talks to RadioScannerPlugin.java;
/// everywhere else (Editor, Windows) it produces simulated data so the recorder UI can be tested.
/// </summary>
public class RadioScanner : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Comma-separated BLE device name prefixes to keep (e.g. 'BEACON_,ESP32_,ARB_'). Empty = keep ALL (noisy).")]
    [SerializeField] private string bleNamePrefix = "BEACON_,ESP32_,ARB_";
    [Tooltip("Optional: the SERVICE_UUID from the ESP32 sketch, copied exactly. Leave EMPTY to filter by name only.")]
    [SerializeField] private string bleServiceUuid = "9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d";
    [SerializeField] private int wifiScanIntervalMs = 2000;
    [SerializeField] private float drainIntervalSeconds = 0.25f;
    [SerializeField] private bool autoStart = true;
    [Tooltip("On Windows Editor, scan real nearby BLE beacons using the PC's Bluetooth adapter instead of fake simulated BLE data.")]
    [SerializeField] private bool useWindowsBleInEditor = true;

    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = "idle";
    public int AndroidSdk { get; private set; }
    public int ScanRequests { get; private set; }
    public int ScanRejected { get; private set; }
    public int LastWifiScanCount { get; private set; }
    public readonly Dictionary<string, BleSeen> BeaconsSeen = new Dictionary<string, BleSeen>();

    public event Action<RadioDrain> Drained;

    private AndroidJavaObject plugin;
    private float nextDrain, nextSimWifi, nextSimBle;
    private bool starting;

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    private Process winBleProc;
    private readonly object winBleLock = new object();
    private readonly List<BleAdv> winBleBuffer = new List<BleAdv>();
    private readonly Dictionary<string, string> winKnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool winBleActive;
#endif

    private void Start() { if (autoStart) StartScanning(); }

    public void StartScanning()
    {
        if (IsRunning || starting) return;
        StartCoroutine(StartRoutine());
    }

    public void RestartScanning()
    {
        if (starting) return;
        StopScanning();
        StartScanning();
    }

    public void StopScanning()
    {
        if (plugin != null) { try { plugin.Call("stop"); } catch (Exception) { } plugin.Dispose(); plugin = null; }
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        StopWindowsBle();
#endif
        IsRunning = false;
        Status = "stopped";
    }

    private IEnumerator StartRoutine()
    {
        starting = true;
        if (Application.platform != RuntimePlatform.Android)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (useWindowsBleInEditor && StartWindowsBle())
            {
                Status = "ok (Windows BLE live)";
                IsRunning = true;
                starting = false;
                yield break;
            }
#endif
            Status = "simulated (not on Android)";
            IsRunning = true;
            starting = false;
            yield break;
        }

        using (var v = new AndroidJavaClass("android.os.Build$VERSION")) AndroidSdk = v.GetStatic<int>("SDK_INT");

        // Request Camera alongside BLE/Location in one batch so Android does not drop
        // overlapping startup permission dialogs when SkipPermissionsDialog is enabled.
        var all = new List<string> { Permission.Camera, "android.permission.ACCESS_FINE_LOCATION" };
        var required = new List<string> { "android.permission.ACCESS_FINE_LOCATION" };
        if (AndroidSdk >= 31)
        {
            all.Add("android.permission.BLUETOOTH_SCAN");
            all.Add("android.permission.BLUETOOTH_CONNECT");
            required.Add("android.permission.BLUETOOTH_SCAN");
        }
        if (AndroidSdk >= 33) all.Add("android.permission.NEARBY_WIFI_DEVICES");

        var missing = all.FindAll(p => !Permission.HasUserAuthorizedPermission(p));
        if (missing.Count > 0)
        {
            Status = "requesting permissions";
            Permission.RequestUserPermissions(missing.ToArray());
            float waited = 0f;
            while (waited < 60f && !required.TrueForAll(Permission.HasUserAuthorizedPermission))
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
        }
        if (!required.TrueForAll(Permission.HasUserAuthorizedPermission))
        {
            Status = "permission denied (Location / Nearby devices)";
            starting = false;
            yield break;
        }

        try
        {
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
                plugin = new AndroidJavaObject("RadioScannerPlugin", act);
            Status = plugin.Call<string>("start", wifiScanIntervalMs, bleNamePrefix, bleServiceUuid ?? "");
            IsRunning = true;
        }
        catch (Exception e)
        {
            Status = "plugin error: " + e.Message;
        }
        starting = false;
    }

    private void Update()
    {
        if (!IsRunning || Time.time < nextDrain) return;
        nextDrain = Time.time + drainIntervalSeconds;

        RadioDrain d;
        if (plugin != null)
        {
            try { d = JsonUtility.FromJson<RadioDrain>(plugin.Call<string>("drain")); }
            catch (Exception e) { Status = "drain error: " + e.Message; return; }
            if (d == null) return;
            ScanRequests = d.scanRequests;
            ScanRejected = d.scanRejected;
            if (!string.IsNullOrEmpty(d.lastError)) Status = d.lastError;
        }
        else d = Simulate();

        if (d.wifiScans.Count > 0) LastWifiScanCount = d.wifiScans[d.wifiScans.Count - 1].aps.Count;
        foreach (var b in d.ble)
        {
            if (!BeaconsSeen.TryGetValue(b.a, out var s)) { s = new BleSeen(); BeaconsSeen[b.a] = s; }
            if (!string.IsNullOrEmpty(b.n)) s.name = b.n;
            s.rssi = b.r; s.lastSeenTime = Time.time;
        }
        Drained?.Invoke(d);
    }

    // ---- Editor / non-Android simulation & Windows BLE ----
    private static readonly int[] SimWifiBase = { -45, -55, -62, -70, -78, -85 };
    private static readonly int[] SimBleBase = { -60, -72, -80 };
    private static readonly string[] SimBleNames = { "BEACON_02", "ESP32_ENTRANCE", "BEACON_03" };

    private RadioDrain Simulate()
    {
        var d = new RadioDrain();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (Time.time >= nextSimWifi)
        {
            nextSimWifi = Time.time + wifiScanIntervalMs / 1000f;
            var scan = new WifiScan { t = now };
            for (int i = 0; i < SimWifiBase.Length; i++)
                scan.aps.Add(new WifiAp { b = $"00:11:22:33:44:{i:00}", s = "SIM_AP_" + i, r = SimWifiBase[i] + UnityEngine.Random.Range(-3, 4), f = 2412 + i * 5 });
            d.wifiScans.Add(scan);
        }
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        if (winBleActive)
        {
            lock (winBleLock)
            {
                if (winBleBuffer.Count > 0)
                {
                    d.ble.AddRange(winBleBuffer);
                    winBleBuffer.Clear();
                }
            }
            return d;
        }
#endif
        if (Time.time >= nextSimBle)
        {
            nextSimBle = Time.time + 0.3f;
            for (int i = 0; i < SimBleBase.Length; i++)
                d.ble.Add(new BleAdv { t = now, a = $"AA:BB:CC:DD:EE:{i:00}", n = SimBleNames[i % SimBleNames.Length], r = SimBleBase[i] + UnityEngine.Random.Range(-4, 5) });
        }
        return d;
    }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    private bool StartWindowsBle()
    {
        StopWindowsBle();
        try
        {
            const string psScript = @"
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
$null = [Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementReceivedEventArgs, Windows.Devices.Bluetooth, ContentType=WindowsRuntime]
$null = [Windows.Foundation.TypedEventHandler`2, Windows.Foundation, ContentType=WindowsRuntime]
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
public static class WinBleBridge {
    public static ConcurrentQueue<object> Queue = new ConcurrentQueue<object>();
    public static void OnReceived(object sender, object args) { Queue.Enqueue(args); }
}
'@
$w = [Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher]::new()
$w.ScanningMode = [Windows.Devices.Bluetooth.Advertisement.BluetoothLEScanningMode]::Active
$ht = [Windows.Foundation.TypedEventHandler[Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher, Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementReceivedEventArgs]]
$del = [Delegate]::CreateDelegate($ht, [WinBleBridge].GetMethod('OnReceived'))
$addM = $w.GetType().GetMethod('add_Received')
$remM = $w.GetType().GetMethod('remove_Received')
$f = [Func[object, System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken]]{ param($d) $addM.Invoke($w, @($d)) }
$a = [Action[System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken]]{ param($t) $remM.Invoke($w, @($t)) }
[System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal]::AddEventHandler($f, $a, $del)
$w.Start()
[Console]::WriteLine('STATUS|' + $w.Status)
[Console]::Out.Flush()
while ($w.Status -eq 'Started') {
    $item = $null
    while ([WinBleBridge]::Queue.TryDequeue([ref]$item)) {
        $mac = ('{0:X12}' -f $item.BluetoothAddress) -replace '(..)(?!$)', '$1:'
        $name = $item.Advertisement.LocalName
        $rssi = $item.RawSignalStrengthInDBm
        $uuids = ($item.Advertisement.ServiceUuids | ForEach-Object { $_.ToString() }) -join ';'
        [Console]::WriteLine('BLE|' + $mac + '|' + $name + '|' + $rssi + '|' + $uuids)
    }
    [Console]::Out.Flush()
    Start-Sleep -Milliseconds 100
}
";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            winBleProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            winBleProc.OutputDataReceived += OnWindowsBleLine;
            winBleProc.Start();
            winBleProc.BeginOutputReadLine();
            winBleActive = true;
            return true;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning("[RadioScanner] Windows BLE start failed: " + e.Message);
            winBleActive = false;
            return false;
        }
    }

    private void OnWindowsBleLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data)) return;
        if (e.Data.StartsWith("STATUS|"))
        {
            string st = e.Data.Substring("STATUS|".Length).Trim();
            Status = st == "Started" ? "ok (Windows BLE live)" : "win_ble_" + st;
            return;
        }
        if (!e.Data.StartsWith("BLE|")) return;
        string[] parts = e.Data.Split('|');
        if (parts.Length < 5) return;

        string mac = parts[1].Trim();
        string name = parts[2].Trim();
        if (!int.TryParse(parts[3].Trim(), out int rssi)) return;
        string uuids = parts[4].Trim();

        lock (winBleLock)
        {
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(mac))
                winKnownNames[mac] = name;
            else if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(mac) && winKnownNames.TryGetValue(mac, out string cached))
                name = cached;

            bool uuidMatch = !string.IsNullOrEmpty(bleServiceUuid) &&
                             uuids.IndexOf(bleServiceUuid.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
            bool prefixMatch = MatchesPrefix(name);

            if (!string.IsNullOrEmpty(bleServiceUuid))
            {
                if (!uuidMatch && !prefixMatch) return;
            }
            else if (!string.IsNullOrEmpty(bleNamePrefix))
            {
                if (!prefixMatch) return;
            }

            if (winBleBuffer.Count < 6000)
            {
                winBleBuffer.Add(new BleAdv
                {
                    t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    a = mac,
                    n = name ?? "",
                    r = rssi
                });
            }
        }
    }

    private bool MatchesPrefix(string name)
    {
        if (string.IsNullOrEmpty(bleNamePrefix)) return true;
        if (string.IsNullOrEmpty(name)) return false;
        string[] prefixes = bleNamePrefix.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string p in prefixes)
        {
            string trimmed = p.Trim();
            if (trimmed.Length > 0 && name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void StopWindowsBle()
    {
        winBleActive = false;
        if (winBleProc != null)
        {
            try { if (!winBleProc.HasExited) winBleProc.Kill(); } catch (Exception) { }
            try { winBleProc.Dispose(); } catch (Exception) { }
            winBleProc = null;
        }
    }
#endif

    // Android can stop scans/receivers while the app is in the background or the screen is locked.
    private void OnApplicationPause(bool paused)
    {
        if (paused || plugin == null) return;
        RestartScanning();
    }

    // Re-check when returning from Android quick-settings shade (e.g. toggling Bluetooth/Wi-Fi) or permission dialogs.
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus || starting || Application.platform != RuntimePlatform.Android) return;
        bool needsRetry = !IsRunning ||
                          Status.IndexOf("ble_off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          Status.IndexOf("wifi_off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          Status.IndexOf("location_off", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          Status.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0;
        if (needsRetry)
            RestartScanning();
    }

    private void OnDisable() { StopScanning(); }

    private void OnDestroy() { StopScanning(); }
}
