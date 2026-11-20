using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// SO2 Deliverable: Weighted k-Nearest Neighbors (Wk-NN) indoor localization provider
/// fusing Wi-Fi RSSI fingerprints and ESP32 BLE beacon RSSI from fingerprints.csv.
/// Implements IPositionProvider so SpatialAnchorAligner and LocalizationAccuracyEvaluator
/// can consume continuous indoor position fixes alongside BlePositionProvider.
/// </summary>
public class KnnPositionProvider : MonoBehaviour, IPositionProvider
{
    public event Action<PositionFix> FixReceived;

    [Header("References")]
    [SerializeField] private RadioScanner scanner;
    [Tooltip("Optional fallback CSV asset if fingerprints.csv does not exist in Application.persistentDataPath yet.")]
    [SerializeField] private TextAsset fallbackFingerprintsCsv = null;

    [Header("Weighted k-NN Parameters (SO2)")]
    [Range(1, 9)]
    [SerializeField] private int kNeighbors = 3;
    [Tooltip("Higher weight for ESP32 BLE beacons relative to Wi-Fi APs during RSSI distance calculation.")]
    [SerializeField] private float bleFeatureWeight = 1.5f;
    [Tooltip("Default RSSI (dBm) substituted when an AP/beacon present in a fingerprint is not heard in the current scan.")]
    [SerializeField] private float missingSignalRssi = -95f;
    [Tooltip("Minimum matching APs/beacons required before emitting a k-NN fix.")]
    [SerializeField] private int minMatchedSignals = 2;
    [SerializeField] private float evalIntervalSeconds = 1.0f;

    [Header("EMA Signal & Position Smoothing")]
    [Range(0.1f, 1.0f)]
    [SerializeField] private float rssiEmaAlpha = 0.45f;
    [SerializeField] private float signalExpirySeconds = 6.0f;
    [Range(0.1f, 1.0f)]
    [SerializeField] private float positionSmoothAlpha = 0.55f;
    [Tooltip("When false, k-NN computes and logs continuous position estimates for SO2/SO5 without overriding BLE peak snaps on SpatialAnchorAligner unless enabled.")]
    [SerializeField] private bool emitFixesToAligner = true;

    [Header("Debug")]
    [SerializeField] private bool showDebugLabel = true;

    private class ReferenceFingerprint
    {
        public string trialId;
        public string waypointId;
        public string floor;
        public Vector3 position;
        public readonly Dictionary<string, float> rssiById = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, bool> isBleById = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    }

    private class LiveSignal
    {
        public float smoothedRssi;
        public float lastSeenTime;
        public bool isBle;
    }

    private readonly List<ReferenceFingerprint> referenceMap = new List<ReferenceFingerprint>();
    private readonly Dictionary<string, LiveSignal> liveSignals = new Dictionary<string, LiveSignal>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> bleNameToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private float nextEvalTime;
    private bool hasSmoothedPos;
    private Vector3 smoothedPosition;

    public int ReferenceFingerprintCount => referenceMap.Count;
    public int ActiveSignalCount => liveSignals.Count;
    public bool HasEstimate { get; private set; }
    public Vector3 LastEstimatedPosition { get; private set; }
    public string LastNearestWaypoint { get; private set; } = "none";
    public float LastEstimatedAccuracyMeters { get; private set; } = 99f;
    public float LastComputeLatencyMs { get; private set; }
    public int LastMatchedSignalCount { get; private set; }

    private string PersistentCsvPath => Path.Combine(Application.persistentDataPath, "fingerprints.csv");
    private string DiscardFilePath => Path.Combine(Application.persistentDataPath, "discarded_trials.txt");

    private void Awake()
    {
        if (scanner == null)
            scanner = FindAnyObjectByType<RadioScanner>();
        ReloadFingerprintDatabase();
    }

    private void OnEnable()
    {
        if (scanner == null)
            scanner = FindAnyObjectByType<RadioScanner>();
        if (scanner != null)
            scanner.Drained += OnRadioDrained;
    }

    private void OnDisable()
    {
        if (scanner != null)
            scanner.Drained -= OnRadioDrained;
    }

    public void ReloadFingerprintDatabase()
    {
        referenceMap.Clear();
        bleNameToKey.Clear();

        var discarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(DiscardFilePath))
        {
            try
            {
                foreach (string line in File.ReadAllLines(DiscardFilePath))
                {
                    string t = line.Trim();
                    if (!string.IsNullOrEmpty(t)) discarded.Add(t);
                }
            }
            catch (Exception) { }
        }

        string csvContent = null;
        if (File.Exists(PersistentCsvPath))
        {
            try { csvContent = File.ReadAllText(PersistentCsvPath); }
            catch (Exception) { }
        }

        if (string.IsNullOrWhiteSpace(csvContent) && fallbackFingerprintsCsv != null)
            csvContent = fallbackFingerprintsCsv.text;

        if (string.IsNullOrWhiteSpace(csvContent))
        {
            TextAsset resCsv = Resources.Load<TextAsset>("fingerprints");
            if (resCsv != null) csvContent = resCsv.text;
        }

        if (string.IsNullOrWhiteSpace(csvContent))
            return;

        ParseFingerprintsCsv(csvContent, discarded);
        Debug.Log($"[KnnPositionProvider] Loaded {referenceMap.Count} reference fingerprint trial(s) for Wk-NN localization.");
    }

    private void ParseFingerprintsCsv(string csvText, HashSet<string> discarded)
    {
        var byTrial = new Dictionary<string, ReferenceFingerprint>(StringComparer.OrdinalIgnoreCase);
        string[] lines = csvText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("trial_id,", StringComparison.OrdinalIgnoreCase))
                continue;

            List<string> cols = SplitCsvLine(line);
            if (cols.Count < 18) continue;

            string trialId = cols[0].Trim();
            if (string.IsNullOrEmpty(trialId) || discarded.Contains(trialId))
                continue;

            string waypointId = cols[1].Trim();
            string floor = cols[2].Trim();
            if (!float.TryParse(cols[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) continue;
            if (!float.TryParse(cols[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) continue;
            if (!float.TryParse(cols[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) continue;

            string sigType = cols[12].Trim().ToLowerInvariant();
            string sigId = cols[13].Trim();
            string sigName = cols[14].Trim();
            if (!float.TryParse(cols[16], NumberStyles.Float, CultureInfo.InvariantCulture, out float rssiMean))
                continue;

            bool isBle = sigType == "ble";
            string featureKey = !string.IsNullOrEmpty(sigId) ? sigId : sigName;
            if (string.IsNullOrEmpty(featureKey)) continue;

            if (isBle && !string.IsNullOrEmpty(sigName))
                bleNameToKey[sigName] = featureKey;

            if (!byTrial.TryGetValue(trialId, out ReferenceFingerprint fp))
            {
                fp = new ReferenceFingerprint
                {
                    trialId = trialId,
                    waypointId = waypointId,
                    floor = floor,
                    position = new Vector3(x, y, z)
                };
                byTrial[trialId] = fp;
                referenceMap.Add(fp);
            }

            fp.rssiById[featureKey] = rssiMean;
            fp.isBleById[featureKey] = isBle;
        }
    }

    private void OnRadioDrained(RadioDrain drain)
    {
        if (drain == null) return;
        float now = Time.time;

        if (drain.wifiScans != null)
        {
            foreach (WifiScan scan in drain.wifiScans)
            {
                if (scan?.aps == null) continue;
                foreach (WifiAp ap in scan.aps)
                {
                    if (ap == null || string.IsNullOrEmpty(ap.b)) continue;
                    UpdateLiveSignal(ap.b, ap.r, isBle: false, now);
                }
            }
        }

        if (drain.ble != null)
        {
            foreach (BleAdv adv in drain.ble)
            {
                if (adv == null) continue;
                string key = !string.IsNullOrEmpty(adv.a) ? adv.a : adv.n;
                if (string.IsNullOrEmpty(key)) continue;

                UpdateLiveSignal(key, adv.r, isBle: true, now);
                if (!string.IsNullOrEmpty(adv.n) && bleNameToKey.TryGetValue(adv.n, out string mappedKey) &&
                    !string.Equals(mappedKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateLiveSignal(mappedKey, adv.r, isBle: true, now);
                }
            }
        }
    }

    private void UpdateLiveSignal(string key, float rssi, bool isBle, float now)
    {
        if (!liveSignals.TryGetValue(key, out LiveSignal sig))
        {
            sig = new LiveSignal
            {
                smoothedRssi = rssi,
                lastSeenTime = now,
                isBle = isBle
            };
            liveSignals[key] = sig;
        }
        else
        {
            sig.smoothedRssi = Mathf.Lerp(sig.smoothedRssi, rssi, rssiEmaAlpha);
            sig.lastSeenTime = now;
            sig.isBle = isBle;
        }
    }

    private void Update()
    {
        float now = Time.time;
        if (now < nextEvalTime) return;
        nextEvalTime = now + Mathf.Max(0.25f, evalIntervalSeconds);

        PruneExpiredSignals(now);
        EvaluateKnn();
    }

    private void PruneExpiredSignals(float now)
    {
        if (liveSignals.Count == 0) return;
        var expired = new List<string>();
        foreach (var kv in liveSignals)
        {
            if (now - kv.Value.lastSeenTime > signalExpirySeconds)
                expired.Add(kv.Key);
        }
        for (int i = 0; i < expired.Count; i++)
            liveSignals.Remove(expired[i]);
    }

    private struct NeighborCandidate
    {
        public ReferenceFingerprint fp;
        public float distance;
        public int matchedCount;
    }

    private void EvaluateKnn()
    {
        if (referenceMap.Count == 0 || liveSignals.Count == 0)
        {
            HasEstimate = false;
            return;
        }

        float t0 = Time.realtimeSinceStartup;
        var candidates = new List<NeighborCandidate>(referenceMap.Count);
        int bestMatches = 0;

        for (int i = 0; i < referenceMap.Count; i++)
        {
            ReferenceFingerprint fp = referenceMap[i];
            float weightedSqSum = 0f;
            float weightSum = 0f;
            int matched = 0;

            foreach (var kv in fp.rssiById)
            {
                string id = kv.Key;
                float refRssi = kv.Value;
                bool isBle = fp.isBleById.TryGetValue(id, out bool b) && b;
                float w = isBle ? bleFeatureWeight : 1.0f;

                float liveRssi = missingSignalRssi;
                if (liveSignals.TryGetValue(id, out LiveSignal live))
                {
                    liveRssi = live.smoothedRssi;
                    matched++;
                }

                float diff = liveRssi - refRssi;
                weightedSqSum += w * diff * diff;
                weightSum += w;
            }

            if (matched > bestMatches) bestMatches = matched;
            if (matched < minMatchedSignals || weightSum <= 1e-4f)
                continue;

            float rmsDist = Mathf.Sqrt(weightedSqSum / weightSum);
            candidates.Add(new NeighborCandidate
            {
                fp = fp,
                distance = rmsDist,
                matchedCount = matched
            });
        }

        LastMatchedSignalCount = bestMatches;
        if (candidates.Count == 0)
        {
            HasEstimate = false;
            LastComputeLatencyMs = (Time.realtimeSinceStartup - t0) * 1000f;
            return;
        }

        candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
        int k = Mathf.Min(kNeighbors, candidates.Count);

        Vector3 weightedPos = Vector3.zero;
        float totalInvWeight = 0f;
        for (int i = 0; i < k; i++)
        {
            float d = Mathf.Max(0.35f, candidates[i].distance);
            float invW = 1f / (d * d);
            weightedPos += candidates[i].fp.position * invW;
            totalInvWeight += invW;
        }

        Vector3 rawEstimate = totalInvWeight > 1e-5f ? (weightedPos / totalInvWeight) : candidates[0].fp.position;
        if (!hasSmoothedPos)
        {
            smoothedPosition = rawEstimate;
            hasSmoothedPos = true;
        }
        else
        {
            smoothedPosition = Vector3.Lerp(smoothedPosition, rawEstimate, positionSmoothAlpha);
        }

        LastComputeLatencyMs = (Time.realtimeSinceStartup - t0) * 1000f;
        LastEstimatedPosition = smoothedPosition;
        LastNearestWaypoint = candidates[0].fp.waypointId;
        LastEstimatedAccuracyMeters = Mathf.Clamp(candidates[0].distance * 0.18f, 1.2f, 2.9f);
        HasEstimate = true;

        if (emitFixesToAligner)
        {
            FixReceived?.Invoke(new PositionFix
            {
                position = smoothedPosition,
                hasHeading = false,
                headingDeg = 0f,
                accuracyMeters = LastEstimatedAccuracyMeters,
                sourceId = "knn:" + LastNearestWaypoint
            });
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>(24);
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(Unquote(line.Substring(start, i - start)));
                start = i + 1;
            }
        }
        result.Add(Unquote(line.Substring(start)));
        return result;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
            return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
        return s;
    }

    private void OnGUI()
    {
        if (!showDebugLabel || UIManager.IsMainMenuActive || !NavigationHUD.DebugOverlaysVisible)
            return;

        const float s = 2.5f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        string msg = referenceMap.Count == 0
            ? "Wk-NN: no fingerprints.csv loaded yet"
            : (HasEstimate
                ? $"Wk-NN (k={kNeighbors}): {LastNearestWaypoint} ({LastEstimatedPosition.x:F1}, {LastEstimatedPosition.z:F1}) \u00B1{LastEstimatedAccuracyMeters:F1}m [{LastComputeLatencyMs:F1}ms]"
                : $"Wk-NN: {referenceMap.Count} refs, matched {LastMatchedSignalCount}/{minMatchedSignals} signals");
        GUI.Box(new Rect(8, 346, 280, 32), msg);
    }
}
