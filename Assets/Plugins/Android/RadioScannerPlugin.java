import android.annotation.SuppressLint;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothManager;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanFilter;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.location.LocationManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelUuid;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;

/**
 * Wi-Fi + BLE scanner for the AR Campus Guide thesis (free, no third-party plugin).
 * Called from Unity through AndroidJavaObject("RadioScannerPlugin", activity).
 * Default package on purpose, so Unity compiles it straight from Assets/Plugins/Android.
 */
@SuppressWarnings("deprecation")
public class RadioScannerPlugin {
    private static final int MAX_BLE_BUFFER = 6000;

    private final Context ctx;
    private final WifiManager wifi;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Object lock = new Object();
    private final ArrayList<JSONObject> wifiScans = new ArrayList<>();
    private final ArrayList<JSONObject> bleAdvs = new ArrayList<>();
    private final Map<String, String> knownNames = new HashMap<>();

    private BroadcastReceiver wifiReceiver;
    private BluetoothLeScanner bleScanner;
    private ScanCallback bleCallback;
    private boolean running;
    private int wifiIntervalMs = 2000;
    private String[] blePrefixes = new String[0];
    private UUID serviceUuid = null;   // when set, Android filters scan results by this advertised service
    private long lastWifiTs = 0;
    private int scanRequests = 0;
    private int scanRejected = 0;
    private String lastError = "";

    public RadioScannerPlugin(Activity activity) {
        ctx = activity.getApplicationContext();
        wifi = (WifiManager) ctx.getSystemService(Context.WIFI_SERVICE);
    }

    /** Old signature, kept so older Unity code still works (name filter only). */
    public String start(int wifiScanIntervalMs, String bleNamePrefix) {
        return start(wifiScanIntervalMs, bleNamePrefix, "");
    }

    /**
     * Returns "ok", or a comma-separated list of problems (e.g. "ble_off,wifi_off,bad_uuid").
     * bleServiceUuid: optional. If valid, only packets advertising that service are reported
     * (names may then be empty on some packets; Unity matches those by address).
     */
    @SuppressLint("MissingPermission")
    public String start(int wifiScanIntervalMs, String bleNamePrefix, String bleServiceUuid) {
        if (running) return "ok";
        wifiIntervalMs = Math.max(500, wifiScanIntervalMs);
        ArrayList<String> prefixes = new ArrayList<>();
        if (bleNamePrefix != null) {
            for (String p : bleNamePrefix.split("[,;|]")) {
                String trimmed = p.trim();
                if (trimmed.length() > 0) prefixes.add(trimmed.toUpperCase(Locale.ROOT));
            }
        }
        blePrefixes = prefixes.toArray(new String[0]);
        ArrayList<String> problems = new ArrayList<>();
        running = true;

        try {
            LocationManager lm = (LocationManager) ctx.getSystemService(Context.LOCATION_SERVICE);
            if (lm != null && Build.VERSION.SDK_INT >= 28 && !lm.isLocationEnabled()) {
                problems.add("location_off");
            }
        } catch (Exception ignored) {}

        serviceUuid = null;
        if (bleServiceUuid != null && bleServiceUuid.trim().length() > 0) {
            try { serviceUuid = UUID.fromString(bleServiceUuid.trim()); }
            catch (Exception e) { lastError = "bad_uuid:" + e.getMessage(); problems.add("bad_uuid"); }
        }

        // ---- Wi-Fi ----
        if (wifi == null || !wifi.isWifiEnabled()) {
            problems.add("wifi_off");
        }
        if (wifi != null) {
            wifiReceiver = new BroadcastReceiver() {
                @Override public void onReceive(Context c, Intent i) { handleWifiResults(); }
            };
            IntentFilter f = new IntentFilter(WifiManager.SCAN_RESULTS_AVAILABLE_ACTION);
            try {
                if (Build.VERSION.SDK_INT >= 33) {
                    ctx.registerReceiver(wifiReceiver, f, Context.RECEIVER_NOT_EXPORTED);
                } else {
                    ctx.registerReceiver(wifiReceiver, f);
                }
            } catch (Exception e) { lastError = "wifi_receiver:" + e.getMessage(); problems.add("wifi_receiver"); }
            handler.post(scanLoop);
        }

        // ---- BLE ----
        try {
            BluetoothManager bm = (BluetoothManager) ctx.getSystemService(Context.BLUETOOTH_SERVICE);
            BluetoothAdapter ad = bm != null ? bm.getAdapter() : null;
            if (ad == null || !ad.isEnabled()) {
                problems.add("ble_off");
            } else {
                bleScanner = ad.getBluetoothLeScanner();
                if (bleScanner == null) {
                    problems.add("ble_scanner_null");
                } else {
                    ScanSettings s = new ScanSettings.Builder()
                            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build();
                    bleCallback = new ScanCallback() {
                        @Override public void onScanResult(int type, ScanResult r) { handleBle(r); }
                        @Override public void onBatchScanResults(List<ScanResult> list) { for (ScanResult r : list) handleBle(r); }
                        @Override public void onScanFailed(int code) { lastError = "ble_scan_failed_" + code; }
                    };
                    List<ScanFilter> filters = new ArrayList<>();
                    if (serviceUuid != null) {
                        filters.add(new ScanFilter.Builder().setServiceUuid(new ParcelUuid(serviceUuid)).build());
                    } else {
                        filters.add(new ScanFilter.Builder().build());
                    }
                    bleScanner.startScan(filters, s, bleCallback);
                }
            }
        } catch (Exception e) { lastError = "ble_start:" + e.getMessage(); problems.add("ble_error"); }

        if (problems.isEmpty()) return "ok";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < problems.size(); i++) { if (i > 0) sb.append(','); sb.append(problems.get(i)); }
        return sb.toString();
    }

    @SuppressLint("MissingPermission")
    public void stop() {
        running = false;
        handler.removeCallbacks(scanLoop);
        try { if (wifiReceiver != null) ctx.unregisterReceiver(wifiReceiver); } catch (Exception ignored) {}
        wifiReceiver = null;
        try { if (bleScanner != null && bleCallback != null) bleScanner.stopScan(bleCallback); } catch (Exception ignored) {}
        bleScanner = null;
        bleCallback = null;
    }

    /** Returns everything collected since the last call as JSON, then clears the buffers. */
    public String drain() {
        JSONObject root = new JSONObject();
        JSONArray ws = new JSONArray();
        JSONArray bs = new JSONArray();
        synchronized (lock) {
            for (JSONObject o : wifiScans) ws.put(o);
            for (JSONObject o : bleAdvs) bs.put(o);
            wifiScans.clear();
            bleAdvs.clear();
        }
        try {
            root.put("wifiScans", ws);
            root.put("ble", bs);
            root.put("scanRequests", scanRequests);
            root.put("scanRejected", scanRejected);
            root.put("lastError", lastError);
        } catch (Exception ignored) {}
        return root.toString();
    }

    private final Runnable scanLoop = new Runnable() {
        @Override public void run() {
            if (!running) return;
            scanRequests++;
            boolean ok = false;
            try { ok = wifi.startScan(); } catch (Exception e) { lastError = "startScan:" + e.getMessage(); }
            if (!ok) scanRejected++;   // rejected = Android throttling (4 scans / 2 min in foreground)
            handler.postDelayed(this, wifiIntervalMs);
        }
    };

    @SuppressLint("MissingPermission")
    private void handleWifiResults() {
        try {
            List<android.net.wifi.ScanResult> results = wifi.getScanResults();
            if (results == null || results.isEmpty()) return;
            long maxTs = 0;
            for (android.net.wifi.ScanResult r : results) maxTs = Math.max(maxTs, r.timestamp);
            if (maxTs <= lastWifiTs) return;          // cached results from an earlier scan
            lastWifiTs = maxTs;

            JSONArray aps = new JSONArray();
            for (android.net.wifi.ScanResult r : results) {
                JSONObject ap = new JSONObject();
                ap.put("b", r.BSSID);
                ap.put("s", r.SSID);
                ap.put("r", r.level);
                ap.put("f", r.frequency);
                aps.put(ap);
            }
            JSONObject scan = new JSONObject();
            scan.put("t", System.currentTimeMillis());
            scan.put("aps", aps);
            synchronized (lock) { wifiScans.add(scan); }
        } catch (Exception e) { lastError = "wifi_results:" + e.getMessage(); }
    }

    @SuppressLint("MissingPermission")
    private void handleBle(ScanResult r) {
        try {
            String addr = r.getDevice() != null ? r.getDevice().getAddress() : "";
            String name = r.getScanRecord() != null ? r.getScanRecord().getDeviceName() : null;
            if (name == null || name.length() == 0) {
                try { name = r.getDevice() != null ? r.getDevice().getName() : null; } catch (Exception ignored) {}
            }
            synchronized (lock) {
                if (name != null && name.length() > 0 && addr != null && addr.length() > 0) {
                    knownNames.put(addr, name);
                } else if ((name == null || name.length() == 0) && addr != null) {
                    name = knownNames.get(addr);
                }
            }
            if (blePrefixes.length > 0) {
                if (name != null && name.length() > 0) {
                    String upper = name.toUpperCase(Locale.ROOT);
                    boolean matched = false;
                    for (String p : blePrefixes) {
                        if (upper.startsWith(p)) { matched = true; break; }
                    }
                    if (!matched) return;
                } else if (serviceUuid == null) {
                    return; // nameless and no UUID filter to vouch for it
                }
            }
            JSONObject o = new JSONObject();
            o.put("t", System.currentTimeMillis());
            o.put("a", addr == null ? "" : addr);
            o.put("n", name == null ? "" : name);
            o.put("r", r.getRssi());
            synchronized (lock) { if (bleAdvs.size() < MAX_BLE_BUFFER) bleAdvs.add(o); }
        } catch (Exception e) { lastError = "ble_result:" + e.getMessage(); }
    }
}
