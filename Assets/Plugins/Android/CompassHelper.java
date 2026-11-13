import android.app.Activity;
import android.content.Context;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;

/**
 * Tilt-compensated compass heading helper using Android TYPE_ROTATION_VECTOR
 * (with TYPE_GEOMAGNETIC_ROTATION_VECTOR fallback). Default package so Unity
 * compiles it directly from Assets/Plugins/Android.
 */
public class CompassHelper implements SensorEventListener {
    private final SensorManager sensorManager;
    private final Sensor rotationSensor;
    private final float[] rotMatrix = new float[9];

    private boolean listening;
    private volatile boolean hasReading;
    private volatile float azimuthDeg;
    private volatile int accuracy;

    public CompassHelper(Activity activity) {
        Context ctx = activity.getApplicationContext();
        sensorManager = (SensorManager) ctx.getSystemService(Context.SENSOR_SERVICE);
        if (sensorManager != null) {
            Sensor s = sensorManager.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR);
            if (s == null) {
                s = sensorManager.getDefaultSensor(Sensor.TYPE_GEOMAGNETIC_ROTATION_VECTOR);
            }
            rotationSensor = s;
        } else {
            rotationSensor = null;
        }
    }

    public boolean isAvailable() {
        return sensorManager != null && rotationSensor != null;
    }

    public synchronized boolean start() {
        if (sensorManager == null || rotationSensor == null) return false;
        if (listening) return true;
        listening = sensorManager.registerListener(this, rotationSensor, SensorManager.SENSOR_DELAY_UI);
        return listening;
    }

    public synchronized void stop() {
        if (!listening || sensorManager == null) return;
        sensorManager.unregisterListener(this);
        listening = false;
    }

    public boolean hasReading() {
        return hasReading;
    }

    public float getAzimuthDeg() {
        return azimuthDeg;
    }

    public int getAccuracy() {
        return accuracy;
    }

    @Override
    public void onSensorChanged(SensorEvent event) {
        if (event == null || event.values == null) return;

        SensorManager.getRotationMatrixFromVector(rotMatrix, event.values);

        // Back camera look direction in device coordinates is (0, 0, -1).
        // World coords from getRotationMatrixFromVector: X = East, Y = Magnetic North, Z = Up.
        float east = -rotMatrix[2];
        float north = -rotMatrix[5];

        // If phone is held almost flat facing straight down/up, fall back to device +Y (top edge).
        if (east * east + north * north < 0.10f) {
            east = rotMatrix[1];
            north = rotMatrix[4];
        }

        if (east * east + north * north < 1e-5f) return;

        double deg = Math.toDegrees(Math.atan2(east, north));
        if (deg < 0.0) deg += 360.0;
        azimuthDeg = (float) deg;
        hasReading = true;
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int newAccuracy) {
        accuracy = newAccuracy;
    }
}
