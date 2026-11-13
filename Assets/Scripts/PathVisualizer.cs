using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public enum NavState { NoDestination, OffMesh, NoPath, Navigating, Arrived }

/// <summary>
/// Computes a NavMesh route from the user to the active destination and draws it
/// as a flat, scrolling chevron line on the floor.
/// Recalculates on a throttle (time AND distance moved), hides the line instead of
/// snapping when the user is off the NavMesh, and detects path deviation (SO4).
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class PathVisualizer : MonoBehaviour
{
    [Header("Target References")]
    public Transform userTransform;        // Main Camera
    public Transform destinationTarget;    // set by DestinationManager
    public Transform fallbackEntrance;     // used if destinationTarget is null

    [Header("Optional")]
    [Tooltip("If assigned, the route refreshes immediately after every alignment fix.")]
    [SerializeField] private SpatialAnchorAligner aligner;

    [Header("Line Look")]
    [SerializeField] private bool showFloorLine = true;
    [SerializeField] private float pathHeightOffset = 0.05f;
    [SerializeField] private float lineWidth = 0.35f;
    [Tooltip("Meters between chevrons.")]
    [SerializeField] private float chevronSpacing = 0.6f;
    [Tooltip("How fast chevrons move toward the destination (m/s).")]
    [SerializeField] private float scrollMetersPerSecond = 0.8f;
    [Tooltip("Leave empty to use the built-in generated chevron texture.")]
    [SerializeField] private Texture2D chevronTextureOverride;

    [Header("3D Navigation Arrows")]
    [SerializeField] private bool show3DArrows = true;
    [Tooltip("Optional custom 3D arrow prefab pointing along +Z. Leave empty to use the built-in 3D arrow.")]
    [SerializeField] private GameObject arrowPrefab;
    [Tooltip("Optional material for built-in 3D arrows. Leave empty to auto-create a bright cyan unlit material.")]
    [SerializeField] private Material arrowMaterialOverride;
    [SerializeField] private Color arrowColor = new Color(0f, 0.95f, 1f, 0.95f);
    [SerializeField] private float arrowHeightOffset = 0.11f;
    [SerializeField] private float arrowScale = 0.4f;
    [SerializeField] private float arrowSpacing = 1.4f;
    [SerializeField] private float arrowScrollSpeed = 0.9f;
    [SerializeField] private float arrowStartOffset = 0.8f;
    [SerializeField] private float arrowEndPadding = 0.8f;
    [SerializeField] private float maxArrowDistance = 30f;
    [SerializeField] private int maxArrowCount = 32;

    [Header("Destination Marker")]
    [SerializeField] private bool showDestinationMarker = true;
    [Tooltip("Optional custom 3D marker prefab placed at the active destination. Leave empty to use the built-in animated pin + floor ring.")]
    [SerializeField] private GameObject destinationMarkerPrefab;
    [SerializeField] private Color markerNavigatingColor = new Color(0f, 0.95f, 1f, 0.95f);
    [SerializeField] private Color markerArrivedColor = new Color(0.2f, 1f, 0.45f, 0.98f);
    [SerializeField] private float markerPinHeight = 1.25f;
    [SerializeField] private float markerRingRadius = 0.55f;

    [Header("Navigation")]
    [Tooltip("Max distance from the camera to the NavMesh. Beyond this the line hides (user is 'off map').")]
    [SerializeField] private float startSampleRadius = 2.5f;
    [SerializeField] private float targetSampleRadius = 2.0f;
    [SerializeField] private float recalcInterval = 0.25f;
    [SerializeField] private float recalcMoveDistance = 0.3f;
    [SerializeField] private float arrivalRadius = 1.5f;
    [Tooltip("Distance from the committed route that counts as 'deviated'.")]
    [SerializeField] private float deviationDistance = 2.0f;

    public NavState State { get; private set; } = NavState.NoDestination;
    public float DistanceRemaining { get; private set; }
    public Transform CurrentDestination => destinationTarget != null ? destinationTarget : fallbackEntrance;
    public IReadOnlyList<Vector3> CurrentPathCorners => activePolyline.Corners;

    public event Action<NavState> StateChanged;
    public event Action Deviated;
    public event Action Arrived;

    private LineRenderer lineRenderer;
    private NavMeshPath path;
    private Vector3[] cornerBuffer = new Vector3[64];
    private readonly PathPolyline activePolyline = new PathPolyline();
    private readonly PathArrowPool arrowPool = new PathArrowPool();
    private readonly DestinationMarkerVisual destinationMarker = new DestinationMarkerVisual();
    private List<Vector3> committedRoute;
    private Transform lastTarget;
    private Transform lastMarkerTarget;
    private Vector3 cachedMarkerFloorPos;
    private bool hasCachedMarkerFloorPos;
    private Vector3 lastCalcPos;
    private float lastCalcTime = -999f;
    private bool forceRecalc = true;
    private bool arrivedLatched;
    private Material lineMat;

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        path = new NavMeshPath();

        // Flat-on-floor setup. Alignment TransformZ makes the ribbon face this transform's +Z,
        // so rotate so +Z points up. Positions are world space, so this doesn't move the line.
        transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
        lineRenderer.useWorldSpace = true;
        lineRenderer.alignment = LineAlignment.TransformZ;
        lineRenderer.textureMode = LineTextureMode.Tile;
        lineRenderer.widthCurve = AnimationCurve.Constant(0f, 1f, 1f);
        lineRenderer.widthMultiplier = lineWidth;
        lineRenderer.numCornerVertices = 2;
        lineRenderer.numCapVertices = 0;
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
        lineRenderer.textureScale = new Vector2(1f / Mathf.Max(0.05f, chevronSpacing), 1f);
        lineRenderer.positionCount = 0;

        lineMat = lineRenderer.material; // instance
        lineMat.mainTexture = chevronTextureOverride != null ? chevronTextureOverride : BuildChevronTexture();
        Shader fallbackShader = lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.shader : null;
        arrowPool.Configure(gameObject.scene, arrowPrefab, arrowMaterialOverride, arrowColor, fallbackShader);
    }

    private void OnEnable()
    {
        if (aligner != null) aligner.Aligned += OnAligned;
    }

    private void OnDisable()
    {
        if (aligner != null) aligner.Aligned -= OnAligned;
        arrowPool.HideAll();
        destinationMarker.Hide();
    }

    private void OnDestroy()
    {
        arrowPool.Dispose();
        destinationMarker.Dispose();
    }

    private void OnAligned(PositionFix _) { forceRecalc = true; committedRoute = null; }

    public void SetDestination(Transform newTarget) { destinationTarget = newTarget; }

    /// <summary>
    /// Computes the next look-ahead target point along the active path and the next upcoming turn.
    /// Returns false when not actively navigating.
    /// </summary>
    public bool TryGetNavigationGuidance(
        float lookAheadMeters,
        float turnThresholdDeg,
        out Vector3 targetPoint,
        out bool hasUpcomingTurn,
        out float distanceToTurn,
        out float turnAngleDeg)
    {
        targetPoint = Vector3.zero;
        hasUpcomingTurn = false;
        distanceToTurn = 0f;
        turnAngleDeg = 0f;

        if (State != NavState.Navigating || userTransform == null || activePolyline.Count < 2)
            return false;

        float userDist = activePolyline.ProjectPointOntoPath(userTransform.position);
        float aimDist = Mathf.Min(activePolyline.TotalLength, userDist + Mathf.Max(0.4f, lookAheadMeters));
        if (!activePolyline.TrySampleAtDistance(aimDist, out targetPoint, out _))
            return false;

        hasUpcomingTurn = activePolyline.TryFindNextTurn(userDist, turnThresholdDeg, out distanceToTurn, out turnAngleDeg);
        return true;
    }

    private void Update()
    {
        AnimateChevrons();

        Transform target = CurrentDestination;
        if (userTransform == null || target == null)
        {
            SetState(NavState.NoDestination);
            ClearLine();
            UpdateDestinationMarker();
            return;
        }

        if (target != lastTarget)
        {
            lastTarget = target;
            arrivedLatched = false;
            committedRoute = null;
            forceRecalc = true;
        }

        CheckDeviation();

        bool timeOk = Time.time - lastCalcTime >= recalcInterval;
        bool moved = (userTransform.position - lastCalcPos).sqrMagnitude >= recalcMoveDistance * recalcMoveDistance;
        if (forceRecalc || (timeOk && moved)) Recalculate(target);

        Update3DArrows();
        UpdateDestinationMarker();
    }

    private void UpdateDestinationMarker()
    {
        if (!showDestinationMarker || destinationTarget == null)
        {
            destinationMarker.Hide();
            lastMarkerTarget = null;
            hasCachedMarkerFloorPos = false;
            return;
        }

        if (destinationTarget != lastMarkerTarget)
        {
            lastMarkerTarget = destinationTarget;
            hasCachedMarkerFloorPos = NavMesh.SamplePosition(
                destinationTarget.position,
                out NavMeshHit hit,
                targetSampleRadius,
                NavMesh.AllAreas);
            cachedMarkerFloorPos = hasCachedMarkerFloorPos ? hit.position : destinationTarget.position;
        }

        Shader fallbackShader = lineRenderer != null && lineRenderer.sharedMaterial != null
            ? lineRenderer.sharedMaterial.shader
            : null;
        destinationMarker.Configure(gameObject.scene, destinationMarkerPrefab, fallbackShader);

        bool arrived = State == NavState.Arrived;
        Vector3 floorPos = hasCachedMarkerFloorPos ? cachedMarkerFloorPos : destinationTarget.position;
        destinationMarker.UpdateVisual(
            floorPos,
            arrived ? markerArrivedColor : markerNavigatingColor,
            arrived,
            markerPinHeight,
            markerRingRadius,
            Time.time);
    }

    private void Update3DArrows()
    {
        if (!show3DArrows || State != NavState.Navigating || userTransform == null || activePolyline.Count < 2)
        {
            arrowPool.HideAll();
            return;
        }

        Shader fallbackShader = lineRenderer != null && lineRenderer.sharedMaterial != null ? lineRenderer.sharedMaterial.shader : null;
        arrowPool.Configure(gameObject.scene, arrowPrefab, arrowMaterialOverride, arrowColor, fallbackShader);
        float userDist = activePolyline.ProjectPointOntoPath(userTransform.position);
        arrowPool.UpdateArrows(
            activePolyline,
            userDist,
            arrowStartOffset,
            arrowEndPadding,
            arrowSpacing,
            arrowScrollSpeed,
            maxArrowDistance,
            maxArrowCount,
            arrowHeightOffset,
            arrowScale,
            Time.time);
    }

    private void Recalculate(Transform target)
    {
        forceRecalc = false;
        lastCalcTime = Time.time;
        lastCalcPos = userTransform.position;

        Vector3 flat = target.position - userTransform.position;
        flat.y = 0f;
        float flatDist = flat.magnitude;

        if (arrivedLatched)
        {
            if (flatDist > arrivalRadius + 2f) arrivedLatched = false;
            else { SetState(NavState.Arrived); ClearLine(); return; }
        }
        if (flatDist <= arrivalRadius)
        {
            arrivedLatched = true;
            SetState(NavState.Arrived);
            ClearLine();
            Arrived?.Invoke();
            return;
        }

        if (!NavMesh.SamplePosition(userTransform.position, out NavMeshHit startHit, startSampleRadius, NavMesh.AllAreas))
        {
            SetState(NavState.OffMesh);
            ClearLine();
            return;
        }
        if (!NavMesh.SamplePosition(target.position, out NavMeshHit targetHit, targetSampleRadius, NavMesh.AllAreas) ||
            !NavMesh.CalculatePath(startHit.position, targetHit.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete)
        {
            SetState(NavState.NoPath);
            ClearLine();
            return;
        }

        if (target == destinationTarget)
        {
            cachedMarkerFloorPos = targetHit.position;
            hasCachedMarkerFloorPos = true;
        }

        int n = path.GetCornersNonAlloc(cornerBuffer);
        if (n == cornerBuffer.Length) { cornerBuffer = new Vector3[n * 2]; n = path.GetCornersNonAlloc(cornerBuffer); }
        if (n < 2) { SetState(NavState.NoPath); ClearLine(); return; }

        activePolyline.UpdateFrom(cornerBuffer, n);
        DistanceRemaining = activePolyline.TotalLength;

        if (showFloorLine)
        {
            Vector3 up = Vector3.up * pathHeightOffset;
            lineRenderer.positionCount = n;
            for (int i = 0; i < n; i++)
                lineRenderer.SetPosition(i, cornerBuffer[i] + up);
        }
        else
        {
            lineRenderer.positionCount = 0;
        }

        if (committedRoute == null)
        {
            committedRoute = new List<Vector3>(n);
            for (int i = 0; i < n; i++) committedRoute.Add(cornerBuffer[i]);
        }
        SetState(NavState.Navigating);
    }

    /// <summary>User is farther than deviationDistance from the route they were given.</summary>
    private void CheckDeviation()
    {
        if (State != NavState.Navigating || committedRoute == null || committedRoute.Count < 2) return;

        Vector2 u = new Vector2(userTransform.position.x, userTransform.position.z);
        float best = float.MaxValue;
        for (int i = 0; i < committedRoute.Count - 1; i++)
        {
            Vector2 a = new Vector2(committedRoute[i].x, committedRoute[i].z);
            Vector2 b = new Vector2(committedRoute[i + 1].x, committedRoute[i + 1].z);
            Vector2 ab = b - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(u - a, ab) / ab.sqrMagnitude) : 0f;
            best = Mathf.Min(best, Vector2.Distance(u, a + ab * t));
        }

        if (best > deviationDistance)
        {
            committedRoute = null;   // next Recalculate commits the new route
            forceRecalc = true;
            Deviated?.Invoke();
        }
    }

    private void AnimateChevrons()
    {
        if (lineMat == null) return;
        float tiles = Time.time * scrollMetersPerSecond / Mathf.Max(0.05f, chevronSpacing);
        lineMat.mainTextureOffset = new Vector2(-(tiles % 1f), 0f);
    }

    private void ClearLine()
    {
        lineRenderer.positionCount = 0;
        activePolyline.Clear();
        arrowPool.HideAll();
        DistanceRemaining = 0f;
    }

    private void SetState(NavState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>White '>' chevron with alpha, tileable along U. Tint comes from the material color.</summary>
    private static Texture2D BuildChevronTexture()
    {
        const int W = 128, H = 128;
        var tex = new Texture2D(W, H, TextureFormat.RGBA32, true)
        {
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 8
        };
        var px = new Color32[W * H];
        const float tipU = 0.78f, thick = 0.07f, aa = 0.02f;
        for (int y = 0; y < H; y++)
        {
            float v = (y + 0.5f) / H;
            float armU = tipU - Mathf.Abs(v - 0.5f);
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W;
                float a = Mathf.Clamp01((thick - Mathf.Abs(u - armU)) / aa);
                px[y * W + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }
}
