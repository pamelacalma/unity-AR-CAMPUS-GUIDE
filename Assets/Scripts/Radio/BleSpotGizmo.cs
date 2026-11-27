using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Editor-only Scene View visualizer for planned BLE beacon placement spots.
/// Shows the floor reference point, recommended overhead mounting height (2.4 m),
/// and estimated RSSI snap / k-NN coverage rings.
/// </summary>
public class BleSpotGizmo : MonoBehaviour
{
#if !UNITY_EDITOR
#pragma warning disable 0414
#endif
    [Header("Spot Identity")]
    public string spotLabel = "BLE Spot";
    public string floorLabel = "M 1F";
    public string roleNote = "Overhead mount (2.4 m)";

    [Header("Mounting & Coverage Rings (meters)")]
    [SerializeField] public float mountHeightMeters = 2.4f;
    [SerializeField] public float closeSnapRadius = 1.5f;
    [SerializeField] public float peakSnapRadius = 4.5f;
    [SerializeField] public float knnOverlapRadius = 10.0f;
    [SerializeField] public bool showKnnRange = true;
    [SerializeField] public Color accentColor = new Color(0.15f, 0.90f, 1f, 0.95f);
#if !UNITY_EDITOR
#pragma warning restore 0414
#endif

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        Vector3 floorPos = transform.position;
        Vector3 mountPos = floorPos + Vector3.up * Mathf.Max(0f, mountHeightMeters);

        Color prevGizmo = Gizmos.color;
        Color prevHandle = Handles.color;

        // Close-snap immediate threshold zone (~ -50 dBm)
        Handles.color = accentColor;
        Handles.DrawWireDisc(floorPos, Vector3.up, Mathf.Max(0.25f, closeSnapRadius));

        // Peak-drop snap zone (~ -65 dBm)
        Handles.color = new Color(0.22f, 0.96f, 0.48f, 0.85f);
        Handles.DrawWireDisc(floorPos, Vector3.up, Mathf.Max(0.5f, peakSnapRadius));

        // Multi-beacon k-NN overlap radius (~ 10 m)
        if (showKnnRange && knnOverlapRadius > peakSnapRadius)
        {
            Handles.color = new Color(1f, 0.86f, 0.25f, 0.25f);
            Handles.DrawWireDisc(floorPos, Vector3.up, knnOverlapRadius);
        }

        // Floor point, mounting pole, and overhead ESP32-H2 board sphere
        Gizmos.color = accentColor;
        Gizmos.DrawWireSphere(floorPos, 0.14f);
        Handles.color = accentColor;
        Handles.DrawLine(floorPos, mountPos);
        Gizmos.DrawSphere(mountPos, 0.16f);

        var style = new GUIStyle(EditorStyles.boldLabel);
        style.normal.textColor = Color.white;
        Handles.Label(
            mountPos + Vector3.up * 0.28f,
            $"{spotLabel}\n{floorLabel} ({mountHeightMeters:0.0}m) · {roleNote}",
            style);

        Gizmos.color = prevGizmo;
        Handles.color = prevHandle;
    }
#endif
}
