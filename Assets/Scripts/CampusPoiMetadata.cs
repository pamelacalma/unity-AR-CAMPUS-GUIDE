using System;
using UnityEngine;

public enum CampusBuildingCode
{
    All = 0,
    M = 1,   // St. Miguel Hall
    SJ = 2,  // St. Joseph Hall
    GK = 3   // Gokongwei Hall
}

public enum CampusPoiCategory
{
    Classroom = 0,
    Restroom = 1,
    Laboratory = 2,
    Office = 3,
    Stairs = 4,
    Elevator = 5,
    Ramp = 6,
    Entrance = 7,
    Other = 8
}

[Serializable]
public struct CampusPoiInfo
{
    public CampusBuildingCode building;
    public int floorNumber;
    public string roomCode;
    public string displayName;
    public CampusPoiCategory category;
    public bool isPwdAccessible;

    public string BuildingShortCode => CampusPoiMetadata.GetBuildingShortCode(building);
    public string BuildingFullName => CampusPoiMetadata.GetBuildingFullName(building);
    public string FloorLabel => floorNumber > 0 ? $"{floorNumber}F" : "1F";

    public string FormattedButtonLabel
    {
        get
        {
            if (string.IsNullOrEmpty(roomCode))
                return $"{BuildingShortCode}{FloorLabel} \u00B7 {displayName}";
            if (string.Equals(roomCode, displayName, StringComparison.OrdinalIgnoreCase))
                return roomCode;
            return $"{roomCode} \u00B7 {displayName}";
        }
    }

    public string FormattedHudTitle
    {
        get
        {
            if (string.IsNullOrEmpty(roomCode))
                return $"{displayName} ({BuildingShortCode} {FloorLabel})";
            if (displayName.IndexOf(roomCode, StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{displayName} ({BuildingFullName} {FloorLabel})";
            return $"{roomCode} \u00B7 {displayName}";
        }
    }
}

/// <summary>
/// Optional metadata component for any Destination_* or landmark Transform.
/// If not explicitly attached, CampusPoiMetadata.GetOrInfer(Transform) automatically
/// infers DLSU building codes (M = St. Miguel Hall, SJ = St. Joseph Hall, GK = Gokongwei Hall),
/// floor level, and room code from the object's name and hierarchy.
/// </summary>
public class CampusPoiMetadata : MonoBehaviour
{
    [Header("DLSU Building & Floor")]
    public CampusBuildingCode building = CampusBuildingCode.M;
    [Range(1, 10)]
    public int floorNumber = 1;

    [Header("Room / Landmark Identity")]
    [Tooltip("Official DLSU room code, e.g. M101, M102, SJ201, GK302. Leave empty to auto-infer.")]
    public string roomCode = "";
    [Tooltip("Human-friendly room name, e.g. Room 101, Men's CR. Leave empty to auto-infer.")]
    public string displayName = "";
    public CampusPoiCategory category = CampusPoiCategory.Classroom;
    public bool isPwdAccessible = true;

    public CampusPoiInfo GetResolvedInfo()
    {
        CampusPoiInfo inferred = InferFromTransform(transform);
        return new CampusPoiInfo
        {
            building = building != CampusBuildingCode.All ? building : inferred.building,
            floorNumber = floorNumber > 0 ? floorNumber : inferred.floorNumber,
            roomCode = !string.IsNullOrWhiteSpace(roomCode) ? roomCode.Trim() : inferred.roomCode,
            displayName = !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim() : inferred.displayName,
            category = category,
            isPwdAccessible = isPwdAccessible
        };
    }

    public static CampusPoiInfo GetOrInfer(Transform target)
    {
        if (target == null)
        {
            return new CampusPoiInfo
            {
                building = CampusBuildingCode.M,
                floorNumber = 1,
                roomCode = "",
                displayName = "",
                category = CampusPoiCategory.Other,
                isPwdAccessible = true
            };
        }

        CampusPoiMetadata meta = target.GetComponent<CampusPoiMetadata>();
        if (meta != null)
            return meta.GetResolvedInfo();

        return InferFromTransform(target);
    }

    public static string GetBuildingShortCode(CampusBuildingCode code)
    {
        switch (code)
        {
            case CampusBuildingCode.M:  return "M";
            case CampusBuildingCode.SJ: return "SJ";
            case CampusBuildingCode.GK: return "GK";
            default:                    return "ALL";
        }
    }

    public static string GetBuildingFullName(CampusBuildingCode code)
    {
        switch (code)
        {
            case CampusBuildingCode.M:  return "St. Miguel Hall";
            case CampusBuildingCode.SJ: return "St. Joseph Hall";
            case CampusBuildingCode.GK: return "Gokongwei Hall";
            default:                    return "All Campus Buildings";
        }
    }

    public static bool TryDecodeBuildingQuery(string query, out CampusBuildingCode code, out string explanation)
    {
        code = CampusBuildingCode.All;
        explanation = "";
        if (string.IsNullOrWhiteSpace(query))
            return false;

        string q = query.Trim().ToUpperInvariant();
        if (q == "M" || q.StartsWith("M1") || q.StartsWith("M2") || q.StartsWith("M3") || q.StartsWith("M4") ||
            q.StartsWith("M-") || q.Contains("MIGUEL"))
        {
            code = CampusBuildingCode.M;
            explanation = "M = St. Miguel Hall";
            return true;
        }

        if (q == "SJ" || q.StartsWith("SJ1") || q.StartsWith("SJ2") || q.StartsWith("SJ3") || q.StartsWith("SJ-") ||
            q.Contains("JOSEPH"))
        {
            code = CampusBuildingCode.SJ;
            explanation = "SJ = St. Joseph Hall";
            return true;
        }

        if (q == "GK" || q == "G" || q.StartsWith("GK1") || q.StartsWith("GK2") || q.StartsWith("GK3") ||
            q.StartsWith("GK4") || q.StartsWith("GK-") || q.Contains("GOKONGWEI") || q.Contains("GOKS"))
        {
            code = CampusBuildingCode.GK;
            explanation = "GK = Gokongwei Hall";
            return true;
        }

        return false;
    }

    public static bool MatchesFilter(Transform target, CampusBuildingCode buildingFilter, string searchQuery, bool requirePwdAccessible = false)
    {
        if (target == null) return false;
        CampusPoiInfo info = GetOrInfer(target);

        if (buildingFilter != CampusBuildingCode.All && info.building != buildingFilter)
            return false;

        if (requirePwdAccessible && !info.isPwdAccessible)
            return false;

        if (string.IsNullOrWhiteSpace(searchQuery))
            return true;

        string q = searchQuery.Trim();
        string qCompact = q.Replace(" ", "").Replace("-", "").Replace("_", "");
        string bCode = info.BuildingShortCode;
        string bName = info.BuildingFullName;
        string rCode = (info.roomCode ?? "").Replace(" ", "").Replace("-", "");
        string dName = info.displayName ?? "";
        string dCompact = dName.Replace(" ", "").Replace("-", "");

        if (string.Equals(q, bCode, StringComparison.OrdinalIgnoreCase))
            return true;

        if (bName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (!string.IsNullOrEmpty(rCode) && rCode.IndexOf(qCompact, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        if (dName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
            dCompact.IndexOf(qCompact, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Match queries like "M 101" or "Miguel 101"
        string combined = (bCode + dCompact).Replace("ROOM", "");
        if (combined.IndexOf(qCompact, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    public static CampusPoiInfo InferFromTransform(Transform t)
    {
        var info = new CampusPoiInfo
        {
            building = CampusBuildingCode.M,
            floorNumber = 1,
            roomCode = "",
            displayName = "",
            category = CampusPoiCategory.Classroom,
            isPwdAccessible = true
        };

        if (t == null) return info;

        string raw = t.name.Trim();
        if (raw.StartsWith("Destination_", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring("Destination_".Length).Trim();

        string clean = raw.Replace('_', ' ').Trim();
        string upper = clean.ToUpperInvariant();

        // Check parent names for building/floor hints (e.g., mig1, mig2, sj1, gk2)
        Transform cur = t.parent;
        while (cur != null)
        {
            string pn = cur.name.ToUpperInvariant();
            if (pn.StartsWith("SJ") || pn.Contains("JOSEPH"))
                info.building = CampusBuildingCode.SJ;
            else if (pn.StartsWith("GK") || pn.Contains("GOK"))
                info.building = CampusBuildingCode.GK;
            else if (pn.StartsWith("MIG") || pn.StartsWith("M_"))
                info.building = CampusBuildingCode.M;

            for (int f = 1; f <= 6; f++)
            {
                if (pn.Contains(f + "F") || pn.EndsWith(f.ToString()))
                    info.floorNumber = f;
            }
            cur = cur.parent;
        }

        // Prefix check on destination name itself (SJ..., GK..., M...)
        if (upper.StartsWith("SJ"))
        {
            info.building = CampusBuildingCode.SJ;
            string rest = clean.Substring(2).Trim(' ', '-', '_');
            if (rest.Length >= 3 && char.IsDigit(rest[0]) && char.IsDigit(rest[1]) && char.IsDigit(rest[2]))
            {
                info.floorNumber = Mathf.Clamp(rest[0] - '0', 1, 9);
                info.roomCode = "SJ" + rest.Substring(0, 3);
                info.displayName = "Room " + rest.Substring(0, 3);
            }
            else
            {
                info.roomCode = "SJ-" + rest.ToUpperInvariant().Replace(" ", "");
                info.displayName = string.IsNullOrEmpty(rest) ? "St. Joseph POI" : rest;
            }
        }
        else if (upper.StartsWith("GK"))
        {
            info.building = CampusBuildingCode.GK;
            string rest = clean.Substring(2).Trim(' ', '-', '_');
            if (rest.Length >= 3 && char.IsDigit(rest[0]) && char.IsDigit(rest[1]) && char.IsDigit(rest[2]))
            {
                info.floorNumber = Mathf.Clamp(rest[0] - '0', 1, 9);
                info.roomCode = "GK" + rest.Substring(0, 3);
                info.displayName = "Room " + rest.Substring(0, 3);
            }
            else
            {
                info.roomCode = "GK-" + rest.ToUpperInvariant().Replace(" ", "");
                info.displayName = string.IsNullOrEmpty(rest) ? "Gokongwei POI" : rest;
            }
        }
        else if (upper.StartsWith("ROOM "))
        {
            string numPart = clean.Substring(5).Trim();
            if (numPart.Length >= 3 && char.IsDigit(numPart[0]))
                info.floorNumber = Mathf.Clamp(numPart[0] - '0', 1, 9);

            string bPrefix = GetBuildingShortCode(info.building);
            info.roomCode = bPrefix + numPart.Replace(" ", "");
            info.displayName = "Room " + numPart;
        }
        else if ((upper.StartsWith("M1") || upper.StartsWith("M2") || upper.StartsWith("M3") || upper.StartsWith("M4")) &&
                 upper.Length >= 4 && char.IsDigit(upper[2]) && char.IsDigit(upper[3]))
        {
            info.building = CampusBuildingCode.M;
            info.floorNumber = Mathf.Clamp(upper[1] - '0', 1, 9);
            info.roomCode = upper.Substring(0, 4);
            info.displayName = "Room " + upper.Substring(1, 3);
        }
        else
        {
            string bPrefix = GetBuildingShortCode(info.building);
            info.displayName = clean;
            if (upper.Contains("CR") || upper.Contains("RESTROOM") || upper.Contains("TOILET"))
            {
                info.category = CampusPoiCategory.Restroom;
                info.roomCode = $"{bPrefix}{info.floorNumber}-CR";
            }
            else if (upper.Contains("STAIR"))
            {
                info.category = CampusPoiCategory.Stairs;
                info.isPwdAccessible = false;
                info.roomCode = $"{bPrefix}{info.floorNumber}-STAIRS";
            }
            else if (upper.Contains("ELEVATOR") || upper.Contains("LIFT"))
            {
                info.category = CampusPoiCategory.Elevator;
                info.roomCode = $"{bPrefix}{info.floorNumber}-ELEV";
            }
            else if (upper.Contains("RAMP"))
            {
                info.category = CampusPoiCategory.Ramp;
                info.roomCode = $"{bPrefix}{info.floorNumber}-RAMP";
            }
            else if (upper.Contains("ENTRANCE") || upper.Contains("EXIT"))
            {
                info.category = CampusPoiCategory.Entrance;
                info.roomCode = $"{bPrefix}{info.floorNumber}-ENT";
            }
            else
            {
                info.roomCode = $"{bPrefix}{info.floorNumber}-{upper.Replace(" ", "")}";
            }
        }

        // Also elevate floor number based on world Y if multi-floor models are stacked vertically
        if (t.position.y > 2.2f && info.floorNumber == 1)
            info.floorNumber = 2 + Mathf.FloorToInt((t.position.y - 2.2f) / 3.5f);

        return info;
    }
}
