using System;
using UnityEngine;

/// <summary>
/// One position estimate in CAD/world space (1 unit = 1 m).
/// Any source (mock button, BLE snap, Wi-Fi k-NN) produces these.
/// </summary>
public struct PositionFix
{
    /// <summary>Floor-level point in CAD/world coordinates.</summary>
    public Vector3 position;

    /// <summary>True if the source also knows which way the user faces.</summary>
    public bool hasHeading;

    /// <summary>World yaw in degrees (0 = +Z, 90 = +X). Only used if hasHeading.</summary>
    public float headingDeg;

    /// <summary>Estimated error radius in meters (BLE snap ~1, Wi-Fi k-NN ~1.5-3).</summary>
    public float accuracyMeters;

    /// <summary>Who produced it, e.g. "mock:ENTRANCE", "ble:AA:BB..", "knn".</summary>
    public string sourceId;
}

/// <summary>
/// Implemented by every positioning source. The rest of the app only
/// talks to this, so swapping mock -> real ESP32 BLE -> Wi-Fi k-NN
/// needs no changes to alignment or navigation code.
/// </summary>
public interface IPositionProvider
{
    event Action<PositionFix> FixReceived;
}
