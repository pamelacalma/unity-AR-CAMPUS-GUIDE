using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;
#endif

/// <summary>
/// Editor-only first-person walk and look controller placed on XR Origin so you
/// can walk through St. Miguel Hall inside the Unity Game tab during Play Mode.
/// Automatically disables itself in device builds (Android/iOS) so ARCore tracking
/// is never affected.
/// </summary>
[DisallowMultipleComponent]
public class EditorWalkTester : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Child Camera Offset under XR Origin (used for clamped vertical pitch without fighting TrackedPoseDriver).")]
    public Transform cameraOffset;

    [Tooltip("Optional starting anchor (e.g. Entrance_1) used by the R reset shortcut.")]
    public Transform startAnchor;

#if !UNITY_EDITOR
#pragma warning disable 0414
#endif
    [Header("Movement & Look")]
    [SerializeField] private float walkSpeed = 2.4f;
    [SerializeField] private float sprintSpeed = 5.5f;
    [SerializeField] private float turnSpeedDeg = 80f;
    [SerializeField] private float mouseSensitivity = 0.22f;
    [SerializeField] private float floorStepMeters = 3.77f;
    [SerializeField] private float eyeHeightAboveFloor = 1.35f;

    [Header("Overlay")]
    [SerializeField] private bool showControlsOverlay = false;
#if !UNITY_EDITOR
#pragma warning restore 0414
#endif

#if UNITY_EDITOR
    private static readonly string[] FloorNames =
    {
        "MIGUEL_1st_Floor",
        "MIGUEL_2nd_Floor",
        "MIGUEL_3rd_Floor",
        "MIGUEL_4th_Floor"
    };

    private Vector3 startOriginPos;
    private Quaternion startOriginRot;
    private float pitchDeg;
    private readonly float[] floorHeights = new float[4];
    private readonly bool[] hasFloorHeight = new bool[4];
    private float baseFloorY = -2.68f;
    private bool loggedControls;
    private Camera mainCam;
    private TrackedPoseDriver trackedPoseDriver;
    private ARCameraBackground arCameraBackground;
#endif

    private void Awake()
    {
        if (!Application.isEditor)
        {
            enabled = false;
            return;
        }

        if (cameraOffset == null)
        {
            Transform child = transform.Find("Camera Offset");
            if (child != null)
                cameraOffset = child;
        }

#if UNITY_EDITOR
        DisableEditorXrSimulationPoseOverride();
#endif
    }

#if UNITY_EDITOR
    private void OnEnable()
    {
        if (!Application.isEditor)
            return;

        pitchDeg = 0f;
        if (cameraOffset != null)
            cameraOffset.localRotation = Quaternion.identity;

        DisableEditorXrSimulationPoseOverride();
    }

    private void Start()
    {
        DisableEditorXrSimulationPoseOverride();

        pitchDeg = 0f;
        if (cameraOffset != null)
            cameraOffset.localRotation = Quaternion.identity;

        startOriginPos = transform.position;
        startOriginRot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        transform.rotation = startOriginRot;

        CacheFloorHeights();

        if (!loggedControls)
        {
            loggedControls = true;
            Debug.Log("[EditorWalkTester] Editor walk controls active: WASD/Arrows move (Shift sprint), Right-Click drag or Q/E look, 1-4 or PageUp/PageDown change Miguel floor, R reset, F1 toggle help.");
        }
    }

    private void LateUpdate()
    {
        if (!Application.isEditor)
            return;

        if (trackedPoseDriver != null && trackedPoseDriver.enabled)
            trackedPoseDriver.enabled = false;

        if (mainCam != null)
        {
            mainCam.transform.localPosition = Vector3.zero;
            mainCam.transform.localRotation = Quaternion.identity;
        }

        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);

        if (cameraOffset != null)
            cameraOffset.localRotation = Quaternion.Euler(pitchDeg, 0f, 0f);
    }

    private void DisableEditorXrSimulationPoseOverride()
    {
        if (mainCam == null)
            mainCam = GetComponentInChildren<Camera>(true);

        foreach (TrackedPoseDriver tpd in GetComponentsInChildren<TrackedPoseDriver>(true))
        {
            if (tpd == null) continue;
            if (trackedPoseDriver == null)
                trackedPoseDriver = tpd;
            tpd.enabled = false;
        }

        if (mainCam != null)
        {
            if (arCameraBackground == null)
                arCameraBackground = mainCam.GetComponent<ARCameraBackground>();
            if (arCameraBackground != null)
                arCameraBackground.enabled = false;

            mainCam.clearFlags = CameraClearFlags.Skybox;
            mainCam.transform.localPosition = Vector3.zero;
            mainCam.transform.localRotation = Quaternion.identity;
        }

        transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
    }

    private void Update()
    {
        if (!Application.isEditor)
            return;

        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;

        if (kb != null && !IsTypingInInputField())
        {
            if (kb.f1Key.wasPressedThisFrame)
                showControlsOverlay = !showControlsOverlay;

            if (kb.rKey.wasPressedThisFrame)
                ResetToStartPose();

            HandleFloorShortcuts(kb);
            HandleKeyboardMovement(kb);
        }

        if (mouse != null && mouse.rightButton.isPressed)
        {
            Vector2 delta = mouse.delta.ReadValue();
            if (delta.sqrMagnitude > 0f)
            {
                transform.Rotate(0f, delta.x * mouseSensitivity, 0f, Space.World);
                pitchDeg = Mathf.Clamp(pitchDeg - delta.y * mouseSensitivity, -60f, 60f);
                ApplyPitch();
            }
        }
    }

    private void HandleKeyboardMovement(Keyboard kb)
    {
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f)
            return;

        float forward = 0f;
        float strafe = 0f;
        float yaw = 0f;

        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) forward += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) forward -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) strafe += 1f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) strafe -= 1f;

        if (kb.eKey.isPressed) yaw += 1f;
        if (kb.qKey.isPressed) yaw -= 1f;

        if (Mathf.Abs(yaw) > 0f)
            transform.Rotate(0f, yaw * turnSpeedDeg * dt, 0f, Space.World);

        Vector3 moveInput = new Vector3(strafe, 0f, forward);
        if (moveInput.sqrMagnitude > 1f)
            moveInput.Normalize();

        if (moveInput.sqrMagnitude > 0f)
        {
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 right = transform.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-4f) right = Vector3.right;
            right.Normalize();

            bool sprinting = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            float speed = sprinting ? sprintSpeed : walkSpeed;
            Vector3 displacement = (fwd * moveInput.z + right * moveInput.x) * (speed * dt);
            transform.position += displacement;
        }
    }

    private void HandleFloorShortcuts(Keyboard kb)
    {
        if (kb.pageUpKey.wasPressedThisFrame)
        {
            Vector3 pos = transform.position;
            pos.y += floorStepMeters;
            transform.position = pos;
        }
        else if (kb.pageDownKey.wasPressedThisFrame)
        {
            Vector3 pos = transform.position;
            pos.y -= floorStepMeters;
            transform.position = pos;
        }

        if (kb.digit1Key.wasPressedThisFrame) JumpToFloor(0);
        else if (kb.digit2Key.wasPressedThisFrame) JumpToFloor(1);
        else if (kb.digit3Key.wasPressedThisFrame) JumpToFloor(2);
        else if (kb.digit4Key.wasPressedThisFrame) JumpToFloor(3);
    }

    private void JumpToFloor(int floorIndex)
    {
        if (floorIndex < 0 || floorIndex >= 4)
            return;

        float targetFloorY = hasFloorHeight[floorIndex]
            ? floorHeights[floorIndex]
            : baseFloorY + floorIndex * floorStepMeters;

        float offsetY = cameraOffset != null ? cameraOffset.localPosition.y : 0f;
        Vector3 pos = transform.position;
        pos.y = (targetFloorY + eyeHeightAboveFloor) - offsetY;
        transform.position = pos;
    }

    private void ResetToStartPose()
    {
        pitchDeg = 0f;
        ApplyPitch();

        if (startAnchor != null)
        {
            float sampleY = startAnchor.position.y;
            if (NavMesh.SamplePosition(startAnchor.position, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                sampleY = hit.position.y;

            float offsetY = cameraOffset != null ? cameraOffset.localPosition.y : 0f;
            Vector3 targetCamPos = new Vector3(startAnchor.position.x, sampleY + eyeHeightAboveFloor, startAnchor.position.z);
            Vector3 camLocal = cameraOffset != null ? new Vector3(0f, offsetY, 0f) : Vector3.zero;
            Vector3 f = startAnchor.forward;
            f.y = 0f;
            Quaternion rot = f.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(f.normalized, Vector3.up)
                : startOriginRot;
            transform.SetPositionAndRotation(targetCamPos - rot * camLocal, rot);
            return;
        }

        transform.SetPositionAndRotation(startOriginPos, startOriginRot);
    }

    private void ApplyPitch()
    {
        if (cameraOffset != null)
            cameraOffset.localRotation = Quaternion.Euler(pitchDeg, 0f, 0f);
    }

    private void CacheFloorHeights()
    {
        baseFloorY = transform.position.y + (cameraOffset != null ? cameraOffset.localPosition.y : 0f) - eyeHeightAboveFloor;

        if (startAnchor != null)
        {
            if (NavMesh.SamplePosition(startAnchor.position, out NavMeshHit anchorHit, 5f, NavMesh.AllAreas))
                baseFloorY = anchorHit.position.y;
            else
                baseFloorY = startAnchor.position.y;
        }

        for (int i = 0; i < FloorNames.Length; i++)
        {
            GameObject floorObj = GameObject.Find(FloorNames[i]);
            if (floorObj == null && i == 0)
                floorObj = GameObject.Find("mig1");

            if (floorObj == null)
                continue;

            float? sampledY = TryEstimateFloorSurfaceY(floorObj, i == 0 ? baseFloorY : (float?)null);
            if (sampledY.HasValue)
            {
                floorHeights[i] = sampledY.Value;
                hasFloorHeight[i] = true;
                if (i == 0)
                    baseFloorY = sampledY.Value;
            }
        }
    }

    private float? TryEstimateFloorSurfaceY(GameObject floorObj, float? hintY)
    {
        float? floorMeshMaxY = null;
        Bounds combinedBounds = default;
        bool hasBounds = false;

        foreach (Renderer r in floorObj.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null) continue;
            if (!hasBounds)
            {
                combinedBounds = r.bounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(r.bounds);
            }

            if (r.gameObject.name.IndexOf("floor", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!floorMeshMaxY.HasValue || r.bounds.max.y > floorMeshMaxY.Value)
                    floorMeshMaxY = r.bounds.max.y;
            }
        }

        float candidateY = hintY ?? floorMeshMaxY ?? (hasBounds ? combinedBounds.min.y + 0.1f : floorObj.transform.position.y);
        Vector3 sampleOrigin = new Vector3(
            startAnchor != null ? startAnchor.position.x : transform.position.x,
            candidateY,
            startAnchor != null ? startAnchor.position.z : transform.position.z);

        if (NavMesh.SamplePosition(sampleOrigin, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            return hit.position.y;

        if (hasBounds)
        {
            Vector3 centerOrigin = new Vector3(combinedBounds.center.x, candidateY, combinedBounds.center.z);
            if (NavMesh.SamplePosition(centerOrigin, out NavMeshHit centerHit, 12f, NavMesh.AllAreas))
                return centerHit.position.y;
        }

        return candidateY;
    }

    private static bool IsTypingInInputField()
    {
        EventSystem es = EventSystem.current;
        if (es == null || es.currentSelectedGameObject == null)
            return false;

        TMP_InputField tmpInput = es.currentSelectedGameObject.GetComponent<TMP_InputField>();
        if (tmpInput != null && tmpInput.isFocused)
            return true;

        InputField legacyInput = es.currentSelectedGameObject.GetComponent<InputField>();
        return legacyInput != null && legacyInput.isFocused;
    }

    private void OnGUI()
    {
        if (!showControlsOverlay || !Application.isEditor)
            return;

        const float width = 300f;
        const float height = 148f;
        Rect area = new Rect(Screen.width - width - 12f, 12f, width, height);
        GUILayout.BeginArea(area, GUI.skin.box);
        GUILayout.Label("<b>Miguel Editor Play Mode Controls</b>");
        GUILayout.Label("WASD / Arrows : Walk (Hold Shift to sprint)");
        GUILayout.Label("Right-Click Drag or Q / E : Look / Turn");
        GUILayout.Label("1 - 4 or PgUp / PgDn : Switch floor");
        GUILayout.Label("R : Reset to Entrance | F1 : Hide help");
        GUILayout.EndArea();
    }
#endif
}
