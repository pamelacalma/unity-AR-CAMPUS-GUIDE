using System;
using UnityEngine;

/// <summary>
/// Registers the CAD model to the real room by moving the XR Origin.
/// (Never move the camera: ARCore overwrites its pose every frame.)
///
/// Given a fix "user is at P, facing yaw H", it solves for the XR Origin
/// pose that makes the AR camera land at P with yaw H in CAD space.
/// ARCore then keeps tracking relative motion until the next fix.
/// Accepts up to two position sources (e.g. real BLE + mock buttons).
/// </summary>
public class SpatialAnchorAligner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Any component implementing IPositionProvider (e.g. KnnPositionProvider).")]
    [SerializeField] private MonoBehaviour providerBehaviour;
    [Tooltip("Optional second source (e.g. MockPositionProvider for debug buttons).")]
    [SerializeField] private MonoBehaviour secondaryProviderBehaviour;
    [Tooltip("Optional third source (e.g. BlePositionProvider alongside KnnPositionProvider and MockPositionProvider).")]
    [SerializeField] private MonoBehaviour tertiaryProviderBehaviour;
    [Tooltip("The XR Origin root transform.")]
    [SerializeField] private Transform xrOrigin;
    [Tooltip("The AR camera (XR Origin > Camera Offset > Main Camera).")]
    [SerializeField] private Transform arCamera;

    [Header("Settings")]
    [Tooltip("Phone height above floor while held, in meters.")]
    [SerializeField] private float assumedHoldHeight = 1.3f;
    [Tooltip("Ignore fixes less accurate than this (meters).")]
    [SerializeField] private float maxAcceptedAccuracy = 3.0f;

    public bool HasAligned { get; private set; }
    public event Action<PositionFix> Aligned;

    private IPositionProvider provider;
    private IPositionProvider secondary;
    private IPositionProvider tertiary;

    private void OnEnable()
    {
        provider = providerBehaviour as IPositionProvider;
        secondary = secondaryProviderBehaviour as IPositionProvider;
        tertiary = tertiaryProviderBehaviour as IPositionProvider;
        if (provider == null && secondary == null && tertiary == null)
        {
            Debug.LogError("[SpatialAnchorAligner] No provider assigned or it doesn't implement IPositionProvider.");
            return;
        }
        if (provider != null) provider.FixReceived += ApplyFix;
        if (secondary != null) secondary.FixReceived += ApplyFix;
        if (tertiary != null) tertiary.FixReceived += ApplyFix;
    }

    private void OnDisable()
    {
        if (provider != null) provider.FixReceived -= ApplyFix;
        if (secondary != null) secondary.FixReceived -= ApplyFix;
        if (tertiary != null) tertiary.FixReceived -= ApplyFix;
    }

    public void ApplyFix(PositionFix fix)
    {
        if (xrOrigin == null || arCamera == null) return;
        if (fix.accuracyMeters > maxAcceptedAccuracy) return;

        Vector3 camLocalPos = xrOrigin.InverseTransformPoint(arCamera.position);
        Vector3 fwdLocal = xrOrigin.InverseTransformDirection(arCamera.forward);
        fwdLocal.y = 0f;

        float newOriginYaw = xrOrigin.eulerAngles.y;            // keep yaw if no heading
        if (fix.hasHeading && fwdLocal.sqrMagnitude > 1e-4f)
        {
            float camLocalYaw = Mathf.Atan2(fwdLocal.x, fwdLocal.z) * Mathf.Rad2Deg;
            newOriginYaw = fix.headingDeg - camLocalYaw;
        }

        Quaternion newRot = Quaternion.Euler(0f, newOriginYaw, 0f);
        Vector3 targetCamWorld = fix.position + Vector3.up * assumedHoldHeight;

        xrOrigin.SetPositionAndRotation(targetCamWorld - newRot * camLocalPos, newRot);

        HasAligned = true;
        Aligned?.Invoke(fix);
        Debug.Log($"[SpatialAnchorAligner] Aligned via {fix.sourceId} at {fix.position}, heading {(fix.hasHeading ? fix.headingDeg.ToString("0") : "kept")}");
    }
}
