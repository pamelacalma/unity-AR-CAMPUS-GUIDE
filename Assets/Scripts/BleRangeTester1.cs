using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Field tool for measuring real BLE beacon range and placement.
/// Stand at a measured distance from a beacon, pick the distance + condition, press RECORD.
/// For every expected beacon it logs RSSI mean/std/min/max, advertisement rate and the % of
/// seconds with at least one packet. Note: adv_rate_hz is the RECEIVED packet rate, not the beacon's advertising rate (a beacon not heard = a row with n=0, i.e. out of range).
/// Output: ble_range_test.csv in Application.persistentDataPath (adb pull it).
/// </summary>
public class BleRangeTester : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private RadioScanner scanner;

    [Header("Test setup")]
    [Tooltip("Names of the beacons under test. Missing ones are logged as 'not detected'.")]
    [SerializeField] private List<string> expectedBeacons = new List<string> { "ESP32_ENTRANCE" };
    [SerializeField] private float[] distancesMeters = { 1f, 2f, 3f, 5f, 7f, 10f, 15f, 20f };
    [SerializeField] private string[] conditions = { "LOS", "NLOS_door", "NLOS_wall", "crowded" };
    [SerializeField] private float recordSeconds = 20f;
    [Tooltip("Phone height above floor while measuring (m). Keep it the same every time.")]
    [SerializeField] private float phoneHeightM = 1.2f;
    [Tooltip("Free note saved on every row, e.g. 'beacon on wall 2.0 m high, TX default, 100 ms interval'.")]
    [SerializeField] private string setupNote = "";

    [Header("Debug overlay")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private float overlayScale = 2.5f;

    private int distIndex, condIndex;
    private bool recording;
    private float elapsed;
    private long startMs;
    private string status = "Ready";
    private readonly List<BleAdv> recAdv = new List<BleAdv>();

    private string FilePath => Path.Combine(Application.persistentDataPath, "ble_range_test.csv");

    private void OnEnable()  { if (scanner != null) scanner.Drained += OnDrained; }
    private void OnDisable() { if (scanner != null) scanner.Drained -= OnDrained; }

    private void OnDrained(RadioDrain d) { if (recording) recAdv.AddRange(d.ble); }

    public void StartRecording()
    {
        if (recording || scanner == null) return;
        if (!scanner.IsRunning) { status = "Scanner not running: " + scanner.Status; return; }
        recAdv.Clear();
        startMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        elapsed = 0f;
        recording = true;
        status = "Recording… hold still";
    }

    private void Update()
    {
        if (!recording) return;
        elapsed += Time.deltaTime;
        if (elapsed >= recordSeconds) Finish();
    }

    private void Finish()
    {
        recording = false;
        float dist = distancesMeters[distIndex];
        string cond = conditions[condIndex];
        int secs = Mathf.Max(1, Mathf.RoundToInt(recordSeconds));
        string testId = $"R_{DateTime.UtcNow:yyyyMMddHHmmss}";

        // Some packets carry no name (it can sit in the scan response). Resolve those by address.
        var addrName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in recAdv)
            if (!string.IsNullOrEmpty(a.n) && !string.IsNullOrEmpty(a.a)) addrName[a.a] = a.n;

        var byName = new Dictionary<string, List<BleAdv>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in recAdv)
        {
            string n = a.n;
            if (string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(a.a) && addrName.TryGetValue(a.a, out var resolved)) n = resolved;
            n = n ?? "";
            if (!byName.TryGetValue(n, out var l)) { l = new List<BleAdv>(); byName[n] = l; }
            l.Add(a);
        }

        var names = new List<string>(expectedBeacons);
        foreach (var k in byName.Keys) if (!names.Exists(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase))) names.Add(k);

        var sb = new StringBuilder();
        if (!File.Exists(FilePath))
            sb.AppendLine("test_id,timestamp_utc,device,android_sdk,beacon_name,beacon_addr,distance_m,condition,phone_height_m,duration_s,n,adv_rate_hz,detect_secs_pct,rssi_mean,rssi_std,rssi_min,rssi_max,note");

        var inv = CultureInfo.InvariantCulture;
        string summary = "";
        foreach (var name in names)
        {
            byName.TryGetValue(name, out var list);
            int n = list?.Count ?? 0;
            string addr = n > 0 ? list[0].a : "";
            string mean = "", std = "", min = "", max = "";
            float detectPct = 0f;
            if (n > 0)
            {
                double sum = 0; int mn = int.MaxValue, mx = int.MinValue;
                var bins = new HashSet<int>();
                foreach (var a in list)
                {
                    sum += a.r; mn = Math.Min(mn, a.r); mx = Math.Max(mx, a.r);
                    int b = (int)((a.t - startMs) / 1000);
                    if (b >= 0 && b < secs) bins.Add(b);
                }
                double m = sum / n, v = 0;
                foreach (var a in list) v += (a.r - m) * (a.r - m);
                mean = m.ToString("F1", inv); std = Math.Sqrt(v / n).ToString("F1", inv);
                min = mn.ToString(); max = mx.ToString();
                detectPct = 100f * bins.Count / secs;
                summary += $"\n{name}: {mean} dBm, {detectPct:0}% secs";
            }
            else summary += $"\n{name}: NOT heard";

            sb.AppendLine(string.Join(",", testId, DateTime.UtcNow.ToString("o", inv), Csv(SystemInfo.deviceModel), scanner.AndroidSdk.ToString(),
                Csv(name), Csv(addr), dist.ToString("F1", inv), Csv(cond), phoneHeightM.ToString("F1", inv), secs.ToString(),
                n.ToString(), (n / (float)secs).ToString("F1", inv), detectPct.ToString("F0", inv), mean, std, min, max, Csv(setupNote)));
        }

        try { File.AppendAllText(FilePath, sb.ToString()); status = $"Saved {dist:0.#} m / {cond}:" + summary; }
        catch (Exception e) { status = "Write failed: " + e.Message; }
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private void OnGUI()
    {
        if (!showOverlay || UIManager.IsMainMenuActive || !NavigationHUD.DebugOverlaysVisible ||
            NavigationHUD.ActiveDebugTab != NavigationHUD.DebugOverlayTab.Bench) return;
        GUI.matrix = Matrix4x4.Scale(new Vector3(overlayScale, overlayScale, 1f));
        float w = Screen.width / overlayScale;
        GUILayout.BeginArea(new Rect(8, 236, Mathf.Min(w - 16, 300), 240), GUI.skin.box);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("<", GUILayout.Width(36)) && !recording) distIndex = (distIndex - 1 + distancesMeters.Length) % distancesMeters.Length;
        GUILayout.Label($"Distance: {distancesMeters[distIndex]:0.#} m");
        if (GUILayout.Button(">", GUILayout.Width(36)) && !recording) distIndex = (distIndex + 1) % distancesMeters.Length;
        GUILayout.EndHorizontal();
        if (GUILayout.Button("Condition: " + conditions[condIndex]) && !recording) condIndex = (condIndex + 1) % conditions.Length;

        if (scanner != null)
        {
            string live = "";
            foreach (var kv in scanner.BeaconsSeen)
                if (Time.time - kv.Value.lastSeenTime < 3f) live += $"\n{kv.Value.name}  {kv.Value.rssi} dBm";
            GUILayout.Label("Scanner: " + scanner.Status + (live.Length > 0 ? "\nLive:" + live : "\nLive: nothing heard"));
        }

        GUI.enabled = !recording;
        if (GUILayout.Button(recording ? $"Recording… {elapsed:0}/{recordSeconds:0}s" : $"RECORD ({recordSeconds:0}s)")) StartRecording();
        GUI.enabled = true;
        GUILayout.Label(status);
        GUILayout.EndArea();
    }
}
