using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Hardware-free stand-in for ESP32 beacons. Stand at a real spot, press
/// that beacon's button, and the aligner registers the CAD model to the
/// real room. Replaces MockBLEManager (which moved the camera, which
/// ARCore overwrites every frame).
/// </summary>
public class MockPositionProvider : MonoBehaviour, IPositionProvider
{
    [Serializable]
    public struct BeaconAnchor
    {
        public string beaconName;
        [Tooltip("Place on the floor at the beacon's CAD position. Its blue (forward) arrow = the direction the user should face.")]
        public Transform anchor;
    }

    public event Action<PositionFix> FixReceived;

    [SerializeField] private List<BeaconAnchor> beacons = new List<BeaconAnchor>();
    [SerializeField] private float simulatedAccuracy = 1.0f;

    [Header("Auto-align on start")]
    [SerializeField] private bool alignFirstBeaconOnStart = true;
    [SerializeField] private float startAlignTimeoutSeconds = 3.0f;

    [Header("Compass heading (Android)")]
    [Tooltip("When true on Android, aligns to the phone's real-world compass direction instead of assuming the user faces the anchor's blue arrow.")]
    [SerializeField] private bool usePhoneCompass = true;
    [Tooltip("Real-world compass bearing (deg clockwise from North: 0=N, 90=E, 180=S, 270=W) that Unity's +Z axis points to in your building.")]
    [SerializeField] private float unityZCompassBearingDeg = 340f;

    [Header("Debug overlay (on-device test buttons)")]
    [SerializeField] private bool showDebugOverlay = true;
    [SerializeField] private float overlayScale = 2.5f;

    private const string CompassBearingPrefKey = "ARCampusGuide.UnityZCompassBearingDeg";

    private float defaultUnityZCompassBearingDeg;
    private float facingTweakDeg;   // fine-tune facing with the +/- buttons
    private string lastSource = "none";
    private string lastBeaconName;

    private void Awake()
    {
        defaultUnityZCompassBearingDeg = unityZCompassBearingDeg;
        if (PlayerPrefs.HasKey(CompassBearingPrefKey))
            unityZCompassBearingDeg = Mathf.Repeat(PlayerPrefs.GetFloat(CompassBearingPrefKey), 360f);
    }

    private IEnumerator Start()
    {
        if (usePhoneCompass)
            CompassHeading.EnsureStarted();

        if (!alignFirstBeaconOnStart || beacons.Count == 0 || beacons[0].anchor == null)
            yield break;

        // Wait until the user enters the AR view so menu/permission time doesn't lock a stale pose.
        while (UIManager.IsMainMenuActive)
            yield return null;

        // Wait until ARCore is tracking (so TrackedPoseDriver has initialized Main Camera's
        // local pose) or until timeout, so the initial XR Origin snap isn't offset by startup drift.
        float elapsed = 0f;
        float maxWait = Application.platform == RuntimePlatform.Android ? startAlignTimeoutSeconds : 0.2f;
        while (elapsed < maxWait)
        {
            bool arReady = ARSession.state == ARSessionState.SessionTracking;
            bool compassReady = !usePhoneCompass || Application.platform != RuntimePlatform.Android || CompassHeading.HasReading;
            if (arReady && compassReady)
                break;
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // Allow one extra frame for TrackedPoseDriver to apply the first tracked pose.
        yield return null;

        SpatialAnchorAligner aligner = FindAnyObjectByType<SpatialAnchorAligner>();
        if (aligner != null && aligner.HasAligned)
            yield break;

        SimulateAtBeacon(beacons[0].beaconName);
    }

    private void OnDestroy()
    {
        CompassHeading.Stop();
    }

    /// <summary>Hook this to a UI button's OnClick (string argument).</summary>
    public void SimulateAtBeacon(string beaconName)
    {
        int i = beacons.FindIndex(b => string.Equals(b.beaconName, beaconName, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || beacons[i].anchor == null)
        {
            Debug.LogWarning($"[MockPositionProvider] Beacon '{beaconName}' not found or has no anchor.");
            return;
        }

        Transform a = beacons[i].anchor;
        lastBeaconName = beacons[i].beaconName;

        float yaw;
        if (usePhoneCompass && CompassHeading.TryGetAzimuthDeg(out float compassBearingDeg))
        {
            yaw = Mathf.DeltaAngle(unityZCompassBearingDeg, compassBearingDeg);
            lastSource = $"compass:{beaconName} ({compassBearingDeg:0}°)";
        }
        else
        {
            Vector3 f = a.forward; f.y = 0f;
            yaw = f.sqrMagnitude > 1e-4f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : a.eulerAngles.y;
            lastSource = "mock:" + beaconName;
        }

        FixReceived?.Invoke(new PositionFix
        {
            position = a.position,
            hasHeading = true,
            headingDeg = yaw + facingTweakDeg,
            accuracyMeters = simulatedAccuracy,
            sourceId = lastSource
        });
    }

    public void NudgeFacing(float deltaDeg)
    {
        facingTweakDeg += deltaDeg;
        if (!string.IsNullOrEmpty(lastBeaconName))
            SimulateAtBeacon(lastBeaconName);
    }

    public void CalibrateCompassStraightAhead()
    {
        string targetName = !string.IsNullOrEmpty(lastBeaconName)
            ? lastBeaconName
            : (beacons.Count > 0 ? beacons[0].beaconName : null);
        if (string.IsNullOrEmpty(targetName)) return;

        int i = beacons.FindIndex(b => string.Equals(b.beaconName, targetName, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || beacons[i].anchor == null) return;

        if (!CompassHeading.TryGetAzimuthDeg(out float liveCompassDeg))
        {
            Debug.LogWarning("[MockPositionProvider] Cannot calibrate: compass reading unavailable.");
            return;
        }

        Transform a = beacons[i].anchor;
        Vector3 f = a.forward;
        f.y = 0f;
        float anchorYaw = f.sqrMagnitude > 1e-4f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : a.eulerAngles.y;

        unityZCompassBearingDeg = Mathf.Repeat(liveCompassDeg - anchorYaw, 360f);
        facingTweakDeg = 0f;
        PlayerPrefs.SetFloat(CompassBearingPrefKey, unityZCompassBearingDeg);
        PlayerPrefs.Save();

        SimulateAtBeacon(targetName);
        Debug.Log($"[MockPositionProvider] Calibrated unityZCompassBearingDeg={unityZCompassBearingDeg:0.0}° from compass={liveCompassDeg:0.0}°, anchorYaw={anchorYaw:0.0}°");
    }

    public void ResetCompassCalibration()
    {
        PlayerPrefs.DeleteKey(CompassBearingPrefKey);
        PlayerPrefs.Save();
        unityZCompassBearingDeg = defaultUnityZCompassBearingDeg;
        facingTweakDeg = 0f;
        if (!string.IsNullOrEmpty(lastBeaconName))
            SimulateAtBeacon(lastBeaconName);
    }

    private void OnGUI()
    {
        if (!showDebugOverlay || UIManager.IsMainMenuActive || !NavigationHUD.DebugOverlaysVisible ||
            NavigationHUD.ActiveDebugTab != NavigationHUD.DebugOverlayTab.Align) return;
        GUI.matrix = Matrix4x4.Scale(Vector3.one * overlayScale);
        GUILayout.BeginArea(new Rect(8, 96, 300, 250), GUI.skin.box);
        GUILayout.Label($"Align: {lastSource}  (tweak {facingTweakDeg:0}°)");
        if (usePhoneCompass && CompassHeading.TryGetAzimuthDeg(out float liveDeg))
            GUILayout.Label($"Compass: {liveDeg:0}° ({CardinalFor(liveDeg)}) | +Z={unityZCompassBearingDeg:0}°");
        foreach (var b in beacons)
            if (GUILayout.Button("I'm at " + b.beaconName)) SimulateAtBeacon(b.beaconName);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("-15°")) NudgeFacing(-15f);
        if (GUILayout.Button("-5°")) NudgeFacing(-5f);
        if (GUILayout.Button("+5°")) NudgeFacing(5f);
        if (GUILayout.Button("+15°")) NudgeFacing(15f);
        GUILayout.EndHorizontal();
        if (usePhoneCompass)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Calibrate Straight")) CalibrateCompassStraightAhead();
            if (GUILayout.Button("Reset +Z")) ResetCompassCalibration();
            GUILayout.EndHorizontal();
        }
        GUILayout.EndArea();
    }

    private static string CardinalFor(float deg)
    {
        string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        int idx = Mathf.RoundToInt(Mathf.Repeat(deg, 360f) / 45f) % 8;
        return dirs[idx];
    }
}
