using System;
using UnityEngine;

/// <summary>
/// Cross-platform wrapper around Android's CompassHelper (tilt-compensated
/// rotation-vector compass). Returns false in Editor/non-Android platforms.
/// </summary>
public static class CompassHeading
{
    private static AndroidJavaObject helper;
    private static bool initAttempted;
    private static bool available;
    private static float smoothedAzimuth;
    private static bool hasSmoothed;

    public static bool IsAvailable
    {
        get
        {
            EnsureStarted();
            return available;
        }
    }

    public static bool HasReading
    {
        get
        {
            EnsureStarted();
            if (!available || helper == null) return false;
            try { return helper.Call<bool>("hasReading"); }
            catch (Exception) { return false; }
        }
    }

    public static void EnsureStarted()
    {
        if (initAttempted) return;
        initAttempted = true;

        if (Application.platform != RuntimePlatform.Android)
        {
            available = false;
            return;
        }

        try
        {
            using (var up = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var act = up.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                helper = new AndroidJavaObject("CompassHelper", act);
                available = helper.Call<bool>("isAvailable") && helper.Call<bool>("start");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[CompassHeading] Failed to start Android compass: " + e.Message);
            available = false;
        }
    }

    /// <summary>
    /// Returns smoothed compass azimuth in degrees clockwise from Magnetic North
    /// (0 = North, 90 = East, 180 = South, 270 = West).
    /// </summary>
    public static bool TryGetAzimuthDeg(out float azimuthDeg)
    {
        azimuthDeg = 0f;
        EnsureStarted();
        if (!available || helper == null) return false;

        try
        {
            if (!helper.Call<bool>("hasReading")) return false;
            float raw = helper.Call<float>("getAzimuthDeg");
            if (!hasSmoothed)
            {
                smoothedAzimuth = raw;
                hasSmoothed = true;
            }
            else
            {
                smoothedAzimuth = Mathf.Repeat(Mathf.LerpAngle(smoothedAzimuth, raw, 0.25f), 360f);
            }
            azimuthDeg = smoothedAzimuth;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void Stop()
    {
        if (helper != null)
        {
            try { helper.Call("stop"); } catch (Exception) { }
            helper.Dispose();
            helper = null;
        }
        initAttempted = false;
        available = false;
        hasSmoothed = false;
    }
}
