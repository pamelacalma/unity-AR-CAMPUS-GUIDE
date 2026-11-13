using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Data-collection mode for the Wi-Fi/BLE radio map (SO2).
/// Stand on a marked waypoint, press RECORD, hold the phone still. It averages the
/// scans over the recording window and appends one block of rows per trial to
/// fingerprints.csv (long format: one row per access point / beacon per trial).
/// Ground-truth position comes from the waypoint's CAD position, so AR tracking
/// and alignment are NOT needed while collecting.
/// </summary>
public class FingerprintRecorder : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private RadioScanner scanner;
    [Tooltip("Empty parent; each CHILD is one waypoint placed at its CAD position (name = waypoint ID).")]
    [SerializeField] private Transform waypointRoot;
    [Tooltip("Optional: Main Camera. Its CAD-space yaw is saved as facing_deg.")]
    [SerializeField] private Transform cameraTransform;

    [Header("Recording")]
    [SerializeField] private string floorLabel = "F1";
    [SerializeField] private float recordSeconds = 15f;
    [SerializeField] private int targetTrialsPerWaypoint = 5;

    [Header("Debug overlay")]
    [SerializeField] private bool showOverlay = true;
    [SerializeField] private float overlayScale = 2.5f;

    private class Agg
    {
        public string name; public int freq;
        public readonly List<int> rssi = new List<int>();
        public double Mean() { double s = 0; foreach (var v in rssi) s += v; return s / rssi.Count; }
        public int Min() { int m = int.MaxValue; foreach (var v in rssi) m = Math.Min(m, v); return m; }
        public int Max() { int m = int.MinValue; foreach (var v in rssi) m = Math.Max(m, v); return m; }
    }

    private readonly List<Transform> waypoints = new List<Transform>();
    private readonly List<WifiScan> recScans = new List<WifiScan>();
    private readonly List<BleAdv> recBle = new List<BleAdv>();
    private int index;
    private bool recording;
    private float elapsed;
    private string status = "Ready";
    private string lastTrialId;

    private string FilePath => Path.Combine(Application.persistentDataPath, "fingerprints.csv");
    private string DiscardPath => Path.Combine(Application.persistentDataPath, "discarded_trials.txt");
    private static string TrialKey(Transform wp) => "fp_trials_" + wp.name;

    private void Awake() { RefreshWaypoints(); }
    private void OnEnable()  { if (scanner != null) scanner.Drained += OnDrained; }
    private void OnDisable() { if (scanner != null) scanner.Drained -= OnDrained; }

    public void RefreshWaypoints()
    {
        waypoints.Clear();
        if (waypointRoot != null) foreach (Transform c in waypointRoot) waypoints.Add(c);
        index = Mathf.Clamp(index, 0, Mathf.Max(0, waypoints.Count - 1));
    }

    private void OnDrained(RadioDrain d)
    {
        if (!recording) return;
        recScans.AddRange(d.wifiScans);
        recBle.AddRange(d.ble);
    }

    public void NextWaypoint() { if (!recording && waypoints.Count > 0) index = (index + 1) % waypoints.Count; }
    public void PrevWaypoint() { if (!recording && waypoints.Count > 0) index = (index - 1 + waypoints.Count) % waypoints.Count; }

    public void StartRecording()
    {
        if (recording || waypoints.Count == 0 || scanner == null) { status = "No waypoints or scanner."; return; }
        if (!scanner.IsRunning) { status = "Scanner not running: " + scanner.Status; return; }
        recScans.Clear(); recBle.Clear();
        elapsed = 0f;
        recording = true;
        status = "Recording… hold still";
    }

    private void Update()
    {
        if (!recording) return;
        elapsed += Time.deltaTime;
        if (elapsed >= recordSeconds) FinishRecording();
    }

    private void FinishRecording()
    {
        recording = false;
        Transform wp = waypoints[index];
        int scanCount = recScans.Count;

        var wifi = new Dictionary<string, Agg>();
        foreach (var scan in recScans)
            foreach (var ap in scan.aps)
            {
                if (string.IsNullOrEmpty(ap.b)) continue;
                if (!wifi.TryGetValue(ap.b, out var a)) { a = new Agg { name = ap.s, freq = ap.f }; wifi[ap.b] = a; }
                a.rssi.Add(ap.r);
            }
        var ble = new Dictionary<string, Agg>();
        foreach (var adv in recBle)
        {
            if (!ble.TryGetValue(adv.a, out var a)) { a = new Agg { name = adv.n }; ble[adv.a] = a; }
            a.rssi.Add(adv.r);
        }

        if (wifi.Count == 0 && ble.Count == 0)
        {
            status = "No radio data captured. NOT saved. Check scanner status.";
            return;
        }

        int trial = PlayerPrefs.GetInt(TrialKey(wp), 0) + 1;
        string trialId = $"{wp.name}_T{trial:00}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        string facing = cameraTransform != null ? cameraTransform.eulerAngles.y.ToString("F0", CultureInfo.InvariantCulture) : "";
        string common = string.Join(",",
            Csv(trialId), Csv(wp.name), Csv(floorLabel),
            wp.position.x.ToString("F2", CultureInfo.InvariantCulture),
            wp.position.y.ToString("F2", CultureInfo.InvariantCulture),
            wp.position.z.ToString("F2", CultureInfo.InvariantCulture),
            facing,
            DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Csv(SystemInfo.deviceModel), scanner.AndroidSdk.ToString(),
            recordSeconds.ToString("F0", CultureInfo.InvariantCulture),
            scanCount.ToString());

        var sb = new StringBuilder();
        if (!File.Exists(FilePath))
            sb.AppendLine("trial_id,waypoint_id,floor,x,y,z,facing_deg,timestamp_utc,device,android_sdk,duration_s,wifi_scans,type,id,name,freq_mhz,rssi_mean,rssi_min,rssi_max,n,detect_rate");
        foreach (var kv in wifi)
            sb.AppendLine(string.Join(",", common, "wifi", Csv(kv.Key), Csv(kv.Value.name), kv.Value.freq.ToString(),
                kv.Value.Mean().ToString("F1", CultureInfo.InvariantCulture), kv.Value.Min().ToString(), kv.Value.Max().ToString(),
                kv.Value.rssi.Count.ToString(),
                scanCount > 0 ? ((float)kv.Value.rssi.Count / scanCount).ToString("F2", CultureInfo.InvariantCulture) : ""));
        foreach (var kv in ble)
            sb.AppendLine(string.Join(",", common, "ble", Csv(kv.Key), Csv(kv.Value.name), "",
                kv.Value.Mean().ToString("F1", CultureInfo.InvariantCulture), kv.Value.Min().ToString(), kv.Value.Max().ToString(),
                kv.Value.rssi.Count.ToString(), ""));

        try
        {
            File.AppendAllText(FilePath, sb.ToString());
            PlayerPrefs.SetInt(TrialKey(wp), trial);
            PlayerPrefs.Save();
            lastTrialId = trialId;
            status = $"Saved {trialId}: {wifi.Count} APs ({scanCount} scans), {ble.Count} beacons";
            if (scanCount < 3) status += "  WARNING: few scans. Turn OFF Wi-Fi scan throttling in Developer options.";
        }
        catch (Exception e) { status = "Write failed: " + e.Message; }
    }

    public void DiscardLastTrial()
    {
        if (string.IsNullOrEmpty(lastTrialId)) { status = "Nothing to discard."; return; }
        File.AppendAllText(DiscardPath, lastTrialId + Environment.NewLine);
        status = "Marked as discarded: " + lastTrialId;
        lastTrialId = null;
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private void OnGUI()
    {
        if (!showOverlay) return;
        GUI.matrix = Matrix4x4.Scale(new Vector3(overlayScale, overlayScale, 1f));
        float w = Screen.width / overlayScale, h = Screen.height / overlayScale;
        GUILayout.BeginArea(new Rect(8, h - 280, Mathf.Min(w - 16, 280), 272), GUI.skin.box);

        if (waypoints.Count == 0) GUILayout.Label("No waypoints under Waypoint Root.");
        else
        {
            Transform wp = waypoints[index];
            int done = PlayerPrefs.GetInt(TrialKey(wp), 0);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(40))) PrevWaypoint();
            GUILayout.Label($"{wp.name}  ({index + 1}/{waypoints.Count})");
            if (GUILayout.Button(">", GUILayout.Width(40))) NextWaypoint();
            GUILayout.EndHorizontal();
            GUILayout.Label($"Trials: {done}/{targetTrialsPerWaypoint}   Floor: {floorLabel}");
        }

        if (scanner != null)
            GUILayout.Label($"Scanner: {scanner.Status}\nWi-Fi APs: {scanner.LastWifiScanCount}  Beacons: {scanner.BeaconsSeen.Count}\nScan req/rejected: {scanner.ScanRequests}/{scanner.ScanRejected}");

        GUI.enabled = !recording;
        if (GUILayout.Button(recording ? $"Recording… {elapsed:0}/{recordSeconds:0}s" : $"RECORD ({recordSeconds:0}s)")) StartRecording();
        GUI.enabled = true;
        if (GUILayout.Button("Discard last trial")) DiscardLastTrial();
        GUILayout.Label(status);
        GUILayout.EndArea();
    }
}
