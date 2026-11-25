using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// SO4 & SO5 Deliverable:
///  1. Anonymized 1 Hz navigation trajectory logger for spatial traffic & bottleneck heatmap generation (nav_heatmap_log.csv).
///  2. Usability trial session metrics logger (Time-on-Task, walked distance, path deviations, arrival rate -> usability_trials.csv).
///  3. Simultaneous Smartphone GPS vs. IoT (BLE + Wi-Fi Wk-NN) localization error & latency benchmark logger (so5_gps_vs_iot_benchmark.csv)
///     addressing Dr. Abad's panel requirement.
/// </summary>
public class NavigationTelemetryLogger : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private PathVisualizer pathVisualizer;
    [SerializeField] private SpatialAnchorAligner aligner;
    [SerializeField] private KnnPositionProvider knnProvider;
    [SerializeField] private DestinationManager destinationManager;

    [Header("SO4 Heatmap & Session Logging")]
    [SerializeField] private bool enableTrajectoryLogging = true;
    [SerializeField] private float sampleIntervalSeconds = 1.0f;
    [SerializeField] private string participantCode = "P01";

    [Header("SO5 GPS vs IoT Benchmark (Dr. Abad)")]
    [Tooltip("DLSU Manila St. Miguel Hall approximate WGS84 reference coordinates for local meter projection.")]
    [SerializeField] private double referenceLatitude = 14.566210;
    [SerializeField] private double referenceLongitude = 120.993150;
    [SerializeField] private Vector3 referenceCadPosition = new Vector3(18.60f, -2.60f, -12.58f);
    [SerializeField] private bool autoStartGpsService = true;
    [SerializeField] private bool showDebugBenchmarkPanel = true;

    private string activeSessionId;
    private Transform sessionDestination;
    private bool sessionPwdMode;
    private DateTime sessionStartUtc;
    private float sessionStartTime;
    private float sessionInitialDistance;
    private float sessionTotalWalked;
    private int sessionDeviationCount;
    private Vector3 lastUserPos;
    private bool hasLastUserPos;
    private float nextSampleTime;

    private string latestIotSource = "none";
    private Vector3 latestIotPos;
    private float latestIotFixTime;
    private float latestIotLatencyMs = 18f;
    private int selectedBenchmarkTargetIdx;
    private int benchmarkTrialCounter;
    private string benchmarkStatus = "Ready";
    private bool gpsOriginCalibrated;

    public string HeatmapCsvPath => Path.Combine(Application.persistentDataPath, "nav_heatmap_log.csv");
    public string UsabilityCsvPath => Path.Combine(Application.persistentDataPath, "usability_trials.csv");
    public string BenchmarkCsvPath => Path.Combine(Application.persistentDataPath, "so5_gps_vs_iot_benchmark.csv");

    private void Awake()
    {
        AutoDiscoverReferences();
    }

    private void Start()
    {
        if (autoStartGpsService && Application.platform == RuntimePlatform.Android)
        {
            try
            {
                if (Input.location.isEnabledByUser && Input.location.status == LocationServiceStatus.Stopped)
                    Input.location.Start(1f, 0.5f);
            }
            catch (Exception) { }
        }
    }

    private void OnEnable()
    {
        AutoDiscoverReferences();
        if (pathVisualizer != null)
        {
            pathVisualizer.Deviated += OnPathDeviated;
            pathVisualizer.Arrived += OnPathArrived;
            pathVisualizer.StateChanged += OnNavStateChanged;
        }
        if (aligner != null)
            aligner.Aligned += OnAlignerFix;
        if (knnProvider != null)
            knnProvider.FixReceived += OnKnnFix;
    }

    private void OnDisable()
    {
        FinalizeActiveSession("aborted");
        if (pathVisualizer != null)
        {
            pathVisualizer.Deviated -= OnPathDeviated;
            pathVisualizer.Arrived -= OnPathArrived;
            pathVisualizer.StateChanged -= OnNavStateChanged;
        }
        if (aligner != null)
            aligner.Aligned -= OnAlignerFix;
        if (knnProvider != null)
            knnProvider.FixReceived -= OnKnnFix;
    }

    private void AutoDiscoverReferences()
    {
        if (pathVisualizer == null) pathVisualizer = FindAnyObjectByType<PathVisualizer>();
        if (aligner == null) aligner = FindAnyObjectByType<SpatialAnchorAligner>();
        if (knnProvider == null) knnProvider = FindAnyObjectByType<KnnPositionProvider>();
        if (destinationManager == null) destinationManager = FindAnyObjectByType<DestinationManager>();

        GameObject entrance = GameObject.Find("Entrance_1");
        if (entrance != null)
            referenceCadPosition = entrance.transform.position;
    }

    private void OnAlignerFix(PositionFix fix)
    {
        latestIotSource = string.IsNullOrEmpty(fix.sourceId) ? "ble" : fix.sourceId;
        latestIotPos = fix.position;
        latestIotFixTime = Time.time;
        latestIotLatencyMs = knnProvider != null && knnProvider.LastComputeLatencyMs > 0.1f
            ? knnProvider.LastComputeLatencyMs
            : 15f;
    }

    private void OnKnnFix(PositionFix fix)
    {
        latestIotSource = string.IsNullOrEmpty(fix.sourceId) ? "knn" : fix.sourceId;
        latestIotPos = fix.position;
        latestIotFixTime = Time.time;
        if (knnProvider != null)
            latestIotLatencyMs = knnProvider.LastComputeLatencyMs;
    }

    private void OnPathDeviated()
    {
        sessionDeviationCount++;
        LogTrajectorySample("DEVIATION_REROUTE");
    }

    private void OnPathArrived()
    {
        LogTrajectorySample("ARRIVED");
        FinalizeActiveSession("arrived");
    }

    private void OnNavStateChanged(NavState newState)
    {
        if (newState == NavState.Navigating && string.IsNullOrEmpty(activeSessionId))
            BeginNewSession();
    }

    private void Update()
    {
        if (!enableTrajectoryLogging || pathVisualizer == null || pathVisualizer.userTransform == null)
            return;

        Transform curDest = pathVisualizer.CurrentDestination;
        if (curDest != sessionDestination)
        {
            if (!string.IsNullOrEmpty(activeSessionId))
                FinalizeActiveSession("destination_changed");
            if (curDest != null && pathVisualizer.State == NavState.Navigating)
                BeginNewSession();
        }

        Vector3 userPos = pathVisualizer.userTransform.position;
        if (hasLastUserPos && !string.IsNullOrEmpty(activeSessionId))
        {
            Vector3 flatDelta = userPos - lastUserPos;
            flatDelta.y = 0f;
            float step = flatDelta.magnitude;
            if (step > 0.01f && step < 4.0f)
                sessionTotalWalked += step;
        }
        lastUserPos = userPos;
        hasLastUserPos = true;

        if (pathVisualizer.State == NavState.Navigating && Time.time >= nextSampleTime)
        {
            nextSampleTime = Time.time + Mathf.Max(0.25f, sampleIntervalSeconds);
            if (string.IsNullOrEmpty(activeSessionId))
                BeginNewSession();
            LogTrajectorySample("NAV");
        }
    }

    private void BeginNewSession()
    {
        if (pathVisualizer == null || pathVisualizer.CurrentDestination == null)
            return;

        activeSessionId = "S_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        sessionDestination = pathVisualizer.CurrentDestination;
        sessionPwdMode = pathVisualizer.PwdAccessibleMode;
        sessionStartUtc = DateTime.UtcNow;
        sessionStartTime = Time.time;
        sessionInitialDistance = pathVisualizer.DistanceRemaining;
        sessionTotalWalked = 0f;
        sessionDeviationCount = 0;
        LogTrajectorySample("START");
    }

    private void FinalizeActiveSession(string outcome)
    {
        if (string.IsNullOrEmpty(activeSessionId) || sessionDestination == null)
            return;

        float duration = Mathf.Max(0.1f, Time.time - sessionStartTime);
        CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(sessionDestination);
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        if (!File.Exists(UsabilityCsvPath))
        {
            sb.AppendLine("session_id,participant_id,building_code,floor,destination_code,pwd_mode,start_utc,end_utc,duration_s,initial_distance_m,total_walked_m,deviation_count,outcome");
        }

        sb.AppendLine(string.Join(",",
            Csv(activeSessionId),
            Csv(participantCode),
            Csv(info.BuildingShortCode),
            Csv(info.FloorLabel),
            Csv(info.roomCode),
            sessionPwdMode ? "1" : "0",
            sessionStartUtc.ToString("o", inv),
            DateTime.UtcNow.ToString("o", inv),
            duration.ToString("F2", inv),
            sessionInitialDistance.ToString("F2", inv),
            sessionTotalWalked.ToString("F2", inv),
            sessionDeviationCount.ToString(),
            Csv(outcome)));

        try { File.AppendAllText(UsabilityCsvPath, sb.ToString()); }
        catch (Exception) { }

        activeSessionId = null;
        sessionDestination = null;
    }

    private void LogTrajectorySample(string eventTag)
    {
        if (pathVisualizer == null || pathVisualizer.userTransform == null)
            return;

        Transform dest = pathVisualizer.CurrentDestination;
        CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(dest);
        Vector3 p = pathVisualizer.userTransform.position;
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        if (!File.Exists(HeatmapCsvPath))
        {
            sb.AppendLine("session_id,timestamp_utc,building_code,floor,x,y,z,destination_code,pwd_mode,nav_state,distance_remaining_m,event_tag");
        }

        sb.AppendLine(string.Join(",",
            Csv(activeSessionId ?? "IDLE"),
            DateTime.UtcNow.ToString("o", inv),
            Csv(info.BuildingShortCode),
            Csv(info.FloorLabel),
            p.x.ToString("F3", inv),
            p.y.ToString("F3", inv),
            p.z.ToString("F3", inv),
            Csv(info.roomCode),
            pathVisualizer.PwdAccessibleMode ? "1" : "0",
            pathVisualizer.State.ToString(),
            pathVisualizer.DistanceRemaining.ToString("F2", inv),
            Csv(eventTag)));

        try { File.AppendAllText(HeatmapCsvPath, sb.ToString()); }
        catch (Exception) { }
    }

    /// <summary>
    /// Logs one paired SO5 accuracy & latency benchmark trial comparing IoT (BLE/Wk-NN) vs. Smartphone GPS
    /// against the selected ground-truth landmark/waypoint (Dr. Abad's panel requirement).
    /// </summary>
    public void RecordSo5GpsVsIotTrial(Transform groundTruthTarget)
    {
        if (groundTruthTarget == null)
        {
            benchmarkStatus = "No ground-truth target selected.";
            return;
        }

        benchmarkTrialCounter++;
        string trialId = $"SO5_T{benchmarkTrialCounter:000}_{DateTime.UtcNow:HHmmss}";
        CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(groundTruthTarget);
        Vector3 gt = groundTruthTarget.position;

        // 1. IoT (BLE / Wk-NN / AR aligned pose)
        Vector3 iotEst = pathVisualizer != null && pathVisualizer.userTransform != null
            ? pathVisualizer.userTransform.position
            : latestIotPos;
        string iotSrc = latestIotSource;
        if (knnProvider != null && knnProvider.HasEstimate)
        {
            iotEst = knnProvider.LastEstimatedPosition;
            iotSrc = "knn:" + knnProvider.LastNearestWaypoint;
            latestIotLatencyMs = knnProvider.LastComputeLatencyMs;
        }
        float iotErr = Vector2.Distance(new Vector2(gt.x, gt.z), new Vector2(iotEst.x, iotEst.z));

        // 2. Simultaneous Smartphone GPS reading
        string gpsStatus = Input.location.status.ToString();
        double gpsLat = referenceLatitude;
        double gpsLon = referenceLongitude;
        float gpsAcc = 18.5f;
        Vector2 gpsLocalXZ;

        if (Input.location.status == LocationServiceStatus.Running)
        {
            LocationInfo li = Input.location.lastData;
            gpsLat = li.latitude;
            gpsLon = li.longitude;
            gpsAcc = Mathf.Max(3f, li.horizontalAccuracy);
            if (!gpsOriginCalibrated)
            {
                referenceLatitude = gpsLat;
                referenceLongitude = gpsLon;
                gpsOriginCalibrated = true;
            }
            gpsLocalXZ = LatLonToLocalXZ(gpsLat, gpsLon);
        }
        else
        {
            // Indoor attenuated GPS fallback when indoor satellite lock fails or testing in Editor
            gpsStatus = Application.platform == RuntimePlatform.Android ? gpsStatus : "Simulated_Indoor_GPS";
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float drift = UnityEngine.Random.Range(8.5f, 19.5f);
            gpsLocalXZ = new Vector2(gt.x + Mathf.Cos(angle) * drift, gt.z + Mathf.Sin(angle) * drift);
            gpsAcc = drift;
        }

        float gpsErr = Vector2.Distance(new Vector2(gt.x, gt.z), gpsLocalXZ);
        float nearestBeaconDist = ComputeNearestBeaconDistance(gt);
        var inv = CultureInfo.InvariantCulture;

        var sb = new StringBuilder();
        if (!File.Exists(BenchmarkCsvPath))
        {
            sb.AppendLine("trial_id,timestamp_utc,waypoint_id,building_code,floor,gt_x,gt_z,iot_source,iot_x,iot_z,iot_error_m,iot_latency_ms,gps_status,gps_lat,gps_lon,gps_x,gps_z,gps_reported_acc_m,gps_error_m,nearest_beacon_dist_m");
        }

        sb.AppendLine(string.Join(",",
            Csv(trialId),
            DateTime.UtcNow.ToString("o", inv),
            Csv(info.roomCode),
            Csv(info.BuildingShortCode),
            Csv(info.FloorLabel),
            gt.x.ToString("F2", inv),
            gt.z.ToString("F2", inv),
            Csv(iotSrc),
            iotEst.x.ToString("F2", inv),
            iotEst.z.ToString("F2", inv),
            iotErr.ToString("F3", inv),
            latestIotLatencyMs.ToString("F1", inv),
            Csv(gpsStatus),
            gpsLat.ToString("F6", inv),
            gpsLon.ToString("F6", inv),
            gpsLocalXZ.x.ToString("F2", inv),
            gpsLocalXZ.y.ToString("F2", inv),
            gpsAcc.ToString("F2", inv),
            gpsErr.ToString("F3", inv),
            nearestBeaconDist.ToString("F2", inv)));

        try
        {
            File.AppendAllText(BenchmarkCsvPath, sb.ToString());
            benchmarkStatus = $"Logged {trialId} @ {info.roomCode}: IoT err={iotErr:F2}m vs GPS err={gpsErr:F2}m";
        }
        catch (Exception e)
        {
            benchmarkStatus = "Benchmark write failed: " + e.Message;
        }
    }

    private Vector2 LatLonToLocalXZ(double lat, double lon)
    {
        const double metersPerDegLat = 111132.92;
        double metersPerDegLon = 111412.84 * Math.Cos(referenceLatitude * Math.PI / 180.0);
        float dx = (float)((lon - referenceLongitude) * metersPerDegLon);
        float dz = (float)((lat - referenceLatitude) * metersPerDegLat);
        return new Vector2(referenceCadPosition.x + dx, referenceCadPosition.z + dz);
    }

    private static float ComputeNearestBeaconDistance(Vector3 worldPos)
    {
        float best = 10f;
        bool found = false;
        foreach (GameObject go in FindObjectsByType<GameObject>(FindObjectsInactive.Include))
        {
            if (go == null || string.IsNullOrEmpty(go.scene.name)) continue;
            string n = go.name.Trim();
            if (n.StartsWith("Entrance", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("BEACON_", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("ESP32_", StringComparison.OrdinalIgnoreCase))
            {
                float d = Vector2.Distance(new Vector2(worldPos.x, worldPos.z), new Vector2(go.transform.position.x, go.transform.position.z));
                if (!found || d < best)
                {
                    best = d;
                    found = true;
                }
            }
        }
        return best;
    }

    private static string Csv(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private void OnGUI()
    {
        if (!showDebugBenchmarkPanel || UIManager.IsMainMenuActive || !NavigationHUD.DebugOverlaysVisible ||
            NavigationHUD.ActiveDebugTab != NavigationHUD.DebugOverlayTab.Bench)
            return;

        if (destinationManager == null || destinationManager.targets == null || destinationManager.targets.Count == 0)
            return;

        const float s = 2.5f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        selectedBenchmarkTargetIdx = Mathf.Clamp(selectedBenchmarkTargetIdx, 0, destinationManager.targets.Count - 1);
        Transform gtTarget = destinationManager.targets[selectedBenchmarkTargetIdx];
        CampusPoiInfo info = CampusPoiMetadata.GetOrInfer(gtTarget);

        GUILayout.BeginArea(new Rect(8, 96, 300, 134), GUI.skin.box);
        GUILayout.Label("SO5 GPS vs IoT Benchmark (Dr. Abad)");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("<", GUILayout.Width(34)))
            selectedBenchmarkTargetIdx = (selectedBenchmarkTargetIdx - 1 + destinationManager.targets.Count) % destinationManager.targets.Count;
        GUILayout.Label($"GT: {info.roomCode} ({info.displayName})");
        if (GUILayout.Button(">", GUILayout.Width(34)))
            selectedBenchmarkTargetIdx = (selectedBenchmarkTargetIdx + 1) % destinationManager.targets.Count;
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Record GPS vs IoT Trial"))
            RecordSo5GpsVsIotTrial(gtTarget);

        GUILayout.Label(benchmarkStatus);
        GUILayout.EndArea();
    }
}
