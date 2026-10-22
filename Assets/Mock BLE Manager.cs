using System;
using System.Collections.Generic;
using UnityEngine;

public class MockBLEManager : MonoBehaviour
{
    [System.Serializable]
    public struct BeaconAnchor
    {
        public string beaconName;
        public Transform beaconAnchorTransform;
    }

    [Header("User Reference")]
    public Transform userTransform;

    [Header("Simulated ESP32 Beacons")]
    public List<BeaconAnchor> beacons = new List<BeaconAnchor>();

    public void RelocateUserToBeacon(string beaconName)
    {
        BeaconAnchor anchor = beacons.Find(b => b.beaconName.Equals(beaconName, StringComparison.OrdinalIgnoreCase));
        if (anchor.beaconAnchorTransform != null && userTransform != null)
        {
            userTransform.position = anchor.beaconAnchorTransform.position;
        }
    }
}