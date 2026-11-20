using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Real-beacon position source. Each ESP32 is a known re-anchor point at a CAD position.
///
/// How it decides to snap (v2):
///  * Per beacon it smooths RSSI over a short window.
///  * Only the STRONGEST beacon is considered, and another beacon must beat it by
///    switchMarginDb to take over (no flip-flopping between two neighbours).
///  * It waits for you to pass the beacon: it snaps at the RSSI PEAK (closest approach),
///    i.e. when RSSI has dropped peakDropDb below the peak, or immediately when you are
///    right at it (closeEnoughRssi), or when you linger near it (maxTrackSeconds).
///    The peak must reach snapRssiThreshold, so distant beacons never snap.
///  * Walking-lag correction: by the time the peak is detected you have walked on. ARCore
///    tracks that motion accurately, so the fix is anchor + (distance walked since the peak).
///  * Fixes carry POSITION ONLY (no heading). Heading is calibrated once at the entrance
///    with the MockPositionProvider button; later snaps keep it.
/// </summary>
public class BlePositionProvider : MonoBehaviour, IPositionProvider
{
    [Serializable]
    public struct BeaconEntry
    {
        [Tooltip("Exactly the name the ESP32 advertises (BEACON_NAME in the firmware), e.g. ESP32_ENTRANCE.")]
        public string advertisedName;
        [Tooltip("Empty GameObject on the CAD floor where people pass the beacon. Blue arrow = direction the user faces.")]
        public Transform anchor;
        [Tooltip("Optional: the board's BLE MAC (shown in nRF Connect). Not needed if its name is ever received; the address is learned automatically.")]
        public string macAddress;
    }

    public event Action<PositionFix> FixReceived;

    [Header("References")]
    [SerializeField] private RadioScanner scanner;
    [Tooltip("Main Camera (XR Origin > Camera Offset > Main Camera). Used for the walking-lag correction. Leave empty to turn that correction off.")]
    [SerializeField] private Transform userTransform;
    [SerializeField] private List<BeaconEntry> beacons = new List<BeaconEntry>();

    [Header("Snap rules (tune from the range test)")]
    [Tooltip("Smoothed RSSI (dBm) the beacon must REACH while you pass it. About the RSSI measured at the farthest pass-by distance you still want to snap, minus ~3 dB.")]
    [SerializeField] private float snapRssiThreshold = -65f;
    [Tooltip("Snap immediately at or above this RSSI (you are right at the beacon). About the RSSI measured at 1 m, line of sight.")]
    [SerializeField] private float closeEnoughRssi = -50f;
    [Tooltip("Start watching for the peak this many dB below the snap threshold.")]
    [SerializeField] private float trackStartMarginDb = 5f;
    [Tooltip("Snap when RSSI has fallen this many dB below the peak (you have passed the beacon).")]
    [SerializeField] private float peakDropDb = 3f;
    [Tooltip("If you stay near the beacon this long without RSSI dropping, snap anyway.")]
    [SerializeField] private float maxTrackSeconds = 6f;
    [Tooltip("A different beacon must be this many dB stronger to replace the current candidate.")]
    [SerializeField] private float switchMarginDb = 6f;
    [Tooltip("After a snap, the beacon re-arms once RSSI falls this far below the snap threshold.")]
    [SerializeField] private float rearmDropDb = 8f;
    [Tooltip("...or after this many seconds, whichever comes first.")]
    [SerializeField] private float resnapCooldownSeconds = 30f;

    [Header("Smoothing")]
    [SerializeField] private float windowSeconds = 1.2f;
    [SerializeField] private int minSamples = 3;

    [Header("Fix")]
    [SerializeField] private float fixAccuracyMeters = 2.0f;
    [SerializeField] private float maxCompensationMeters = 4f;
    [Tooltip("BLE has no direction. Leave OFF for real use (calibrate heading with the mock button). ON = for the FIRST fix only, assume the user faces the anchor's blue arrow.")]
    [SerializeField] private bool firstFixUsesAnchorHeading = false;

    [Header("Debug")]
    [SerializeField] private bool showDebugLabel = true;

    private struct Sample { public float time; public int rssi; }
    private struct HistoryPoint { public float time; public Vector3 localPos; }

    private class BeaconState
    {
        public readonly Queue<Sample> samples = new Queue<Sample>();
        public float smoothed = float.NaN;
        public bool tracking;
        public float trackStart, peak, peakTime;
        public bool armed = true;
        public float lastSnapTime = -999f;
    }

    private readonly Dictionary<string, int> nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> addrToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private readonly List<HistoryPoint> history = new List<HistoryPoint>();
    private BeaconState[] states = new BeaconState[0];
    private int candidate = -1;
    private float nextEval, nextHistory;
    private bool hasSnapped;
    private string lastFixText = "no snap yet";

    private void Awake() { Rebuild(); }

    private void Rebuild()
    {
        states = new BeaconState[beacons.Count];
        nameToIndex.Clear();
        addrToIndex.Clear();
        for (int i = 0; i < beacons.Count; i++)
        {
            states[i] = new BeaconState();
            string n = (beacons[i].advertisedName ?? "").Trim();
            if (n.Length > 0) nameToIndex[n] = i;
            string m = (beacons[i].macAddress ?? "").Trim();
            if (m.Length > 0) addrToIndex[m] = i;
        }
        candidate = -1;
    }

    private void OnEnable()
    {
        if (scanner == null) { Debug.LogWarning("[BlePositionProvider] Scanner is not assigned."); return; }
        scanner.Drained += OnDrained;
    }

    private void OnDisable() { if (scanner != null) scanner.Drained -= OnDrained; }

    // A packet may carry the name or not (depends on scan-response timing). The first named packet
    // teaches us the address, so later nameless packets from the same board are still counted.
    private int IndexFor(BleAdv adv)
    {
        int i;
        if (!string.IsNullOrEmpty(adv.n) && nameToIndex.TryGetValue(adv.n, out i))
        {
            if (!string.IsNullOrEmpty(adv.a)) addrToIndex[adv.a] = i;
            return i;
        }
        if (!string.IsNullOrEmpty(adv.a) && addrToIndex.TryGetValue(adv.a, out i)) return i;
        return -1;
    }

    private void OnDrained(RadioDrain d)
    {
        if (states == null || states.Length != beacons.Count) Rebuild();
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float now = Time.time;
        foreach (BleAdv adv in d.ble)
        {
            int i = IndexFor(adv);
            if (i < 0) continue;
            float age = Mathf.Clamp((nowMs - adv.t) / 1000f, 0f, 5f);
            states[i].samples.Enqueue(new Sample { time = now - age, rssi = adv.r });
        }
    }

    private void Update()
    {
        float now = Time.time;
        RecordHistory(now);
        if (now < nextEval) return;
        nextEval = now + 0.2f;
        Evaluate(now);
    }

    private void Evaluate(float now)
    {
        int best = -1;
        float bestVal = float.NegativeInfinity;
        float rearmBelow = snapRssiThreshold - rearmDropDb;

        for (int i = 0; i < states.Length; i++)
        {
            BeaconState s = states[i];
            while (s.samples.Count > 0 && now - s.samples.Peek().time > windowSeconds) s.samples.Dequeue();

            if (s.samples.Count < minSamples) s.smoothed = float.NaN;
            else
            {
                float sum = 0f;
                foreach (Sample smp in s.samples) sum += smp.rssi;
                s.smoothed = sum / s.samples.Count;
                if (s.smoothed > bestVal) { bestVal = s.smoothed; best = i; }
            }

            if (!s.armed && (float.IsNaN(s.smoothed) || s.smoothed < rearmBelow || now - s.lastSnapTime >= resnapCooldownSeconds))
                s.armed = true;
        }

        // Candidate selection with hysteresis: keep the current beacon unless another is clearly stronger.
        if (candidate >= states.Length) candidate = -1;
        if (candidate >= 0 && float.IsNaN(states[candidate].smoothed)) { states[candidate].tracking = false; candidate = -1; }
        if (candidate < 0) candidate = best;
        else if (best >= 0 && best != candidate && states[best].smoothed >= states[candidate].smoothed + switchMarginDb)
        {
            states[candidate].tracking = false;
            candidate = best;
        }
        if (candidate < 0) return;

        BeaconState c = states[candidate];
        float sm = c.smoothed;
        if (sm < snapRssiThreshold - trackStartMarginDb) { c.tracking = false; return; }

        if (!c.tracking) { c.tracking = true; c.trackStart = now; c.peak = sm; c.peakTime = now; }
        else if (sm > c.peak) { c.peak = sm; c.peakTime = now; }

        if (!c.armed || c.peak < snapRssiThreshold) return;

        bool atBeacon = sm >= closeEnoughRssi;
        bool passed = c.peak - sm >= peakDropDb;
        bool lingering = now - c.trackStart >= maxTrackSeconds;
        if (atBeacon || passed || lingering) Fire(candidate, now);
    }

    private void Fire(int index, float now)
    {
        BeaconState c = states[index];
        c.armed = false;
        c.tracking = false;
        c.lastSnapTime = now;

        BeaconEntry e = beacons[index];
        if (e.anchor == null)
        {
            Debug.LogWarning("[BlePositionProvider] Beacon '" + e.advertisedName + "' has no Anchor assigned.");
            return;
        }

        Vector3 pos = e.anchor.position;
        float walked = 0f;
        if (userTransform != null && userTransform.parent != null &&
            TryGetLocalAt(c.peakTime - windowSeconds * 0.5f, out Vector3 localAtPeak))
        {
            Vector3 delta = userTransform.parent.TransformDirection(userTransform.localPosition - localAtPeak);
            delta.y = 0f;
            delta = Vector3.ClampMagnitude(delta, maxCompensationMeters);
            pos += delta;
            walked = delta.magnitude;
        }

        bool first = !hasSnapped;
        hasSnapped = true;

        Vector3 f = e.anchor.forward; f.y = 0f;
        float yaw = f.sqrMagnitude > 1e-4f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : e.anchor.eulerAngles.y;

        lastFixText = $"Snap: {e.advertisedName} peak {c.peak:0} dBm, walked {walked:0.0} m ({Time.time:0}s)";
        FixReceived?.Invoke(new PositionFix
        {
            position = pos,
            hasHeading = first && firstFixUsesAnchorHeading,
            headingDeg = yaw,
            accuracyMeters = fixAccuracyMeters,
            sourceId = "ble:" + e.advertisedName
        });
    }

    // ---- short history of the camera's position (in its parent's space, so alignment jumps don't matter) ----
    private void RecordHistory(float now)
    {
        if (userTransform == null || now < nextHistory) return;
        nextHistory = now + 0.1f;
        history.Add(new HistoryPoint { time = now, localPos = userTransform.localPosition });
        float keep = maxTrackSeconds + windowSeconds + 2f;
        int remove = 0;
        while (remove < history.Count && now - history[remove].time > keep) remove++;
        if (remove > 0) history.RemoveRange(0, remove);
    }

    private bool TryGetLocalAt(float time, out Vector3 p)
    {
        p = Vector3.zero;
        if (history.Count == 0 || time < history[0].time - 0.3f) return false;
        int bestI = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i < history.Count; i++)
        {
            float d = Mathf.Abs(history[i].time - time);
            if (d < bestD) { bestD = d; bestI = i; }
        }
        p = history[bestI].localPos;
        return true;
    }

    private void OnGUI()
    {
        if (!showDebugLabel || UIManager.IsMainMenuActive || !NavigationHUD.DebugOverlaysVisible) return;
        float s = 2.5f;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));

        string line;
        if (candidate >= 0 && candidate < states.Length && !float.IsNaN(states[candidate].smoothed))
        {
            BeaconState c = states[candidate];
            line = $"BLE: {beacons[candidate].advertisedName} {c.smoothed:0} dBm";
            if (c.tracking) line += $"  tracking, peak {c.peak:0}";
            if (!c.armed) line += "  (locked)";
        }
        else
        {
            string scannerState = scanner != null ? scanner.Status : "no scanner";
            line = $"BLE: no known beacon ({scannerState})";
            if (scanner != null)
            {
                foreach (var kv in scanner.BeaconsSeen)
                {
                    if (Time.time - kv.Value.lastSeenTime < 3f)
                    {
                        string seenName = string.IsNullOrEmpty(kv.Value.name) ? kv.Key : kv.Value.name;
                        line += $"\nSeen: {seenName} ({kv.Value.rssi} dBm)";
                        break;
                    }
                }
            }
        }
        line += $"\nsnap {snapRssiThreshold:0} / close {closeEnoughRssi:0}\n{lastFixText}";
        GUI.Box(new Rect(8, 246, 280, 96), line);
    }
}
