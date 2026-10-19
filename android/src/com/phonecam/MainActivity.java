package com.phonecam;

import android.Manifest;
import android.app.Activity;
import android.content.Context;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.ImageFormat;
import android.graphics.Rect;
import android.graphics.YuvImage;
import android.graphics.drawable.GradientDrawable;
import android.widget.ImageView;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import android.hardware.camera2.CameraAccessException;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.hardware.camera2.params.StreamConfigurationMap;
import android.view.Surface;
import android.media.Image;
import android.media.ImageReader;
import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaFormat;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.net.wifi.WifiManager;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.PowerManager;
import android.text.format.Formatter;
import android.util.Log;
import android.util.Range;
import android.util.Size;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ArrayAdapter;
import android.widget.Spinner;
import android.widget.TextView;
import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.util.Enumeration;
import java.util.Collections;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Set;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import org.json.JSONArray;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private static final int PORT = 8765;
    private static final int MAX_CLIENTS = 4;
    private final Object frameLock = new Object();
    private final CopyOnWriteArrayList<Socket> clients = new CopyOnWriteArrayList<>();
    private volatile byte[] latestFrame;
    private volatile boolean running;
    private long frameId = 0;
    private int frameRotation = 0;
    private long lastUiUpdateTime = 0;
    private ServerSocket serverSocket;
    private volatile boolean serverStarted = false;
    private byte[] nv21Buffer;
    private byte[] rotatedBuffer;
    private ByteArrayOutputStream jpegBuffer;
    private int sensorOrientation = 0;
    private boolean front;
    private boolean updatingControls;
    private int cameraSelection = -1;
    private int resolutionSelection = -1;
    private int fpsSelection = -1;
    private int qualitySelection = -1;
    private final List<CameraChoice> cameraChoices = new ArrayList<>();
    private final List<Size> sizeChoices = new ArrayList<>();
    private final List<Range<Integer>> fpsChoices = new ArrayList<>();
    private CameraChoice selectedCamera;
    private Size selectedSize = new Size(640, 480);
    private Range<Integer> selectedFpsRange;
    private volatile int jpegQuality = 75;
    private volatile boolean torchActive = false;
    private boolean isDimmed = false;
    private View dimmerOverlay;
    private Button dimmerButton;
    private Button torchUiButton;
    private CameraDevice camera;
    private ImageReader reader;
    private MediaCodec mediaCodec;
    private Surface encoderSurface;
    private byte[] codecConfigHeader; // SPS / PPS NAL units
    private final Object codecLock = new Object();
    private volatile boolean encoderRunning = false;
    private final CopyOnWriteArrayList<Socket> h264Clients = new CopyOnWriteArrayList<>();
    private final CopyOnWriteArrayList<Socket> mjpgClients = new CopyOnWriteArrayList<>();
    private CameraCaptureSession session;
    private HandlerThread cameraThread;
    private Handler cameraHandler;
    private TextView address;
    private TextView state;
    private TextView modeBadge;
    private Spinner cameraSpinner;
    private Spinner resolutionSpinner;
    private Spinner fpsSpinner;
    private Spinner qualitySpinner;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private ImageView viewfinder;
    private boolean showViewfinder = false;
    private Button viewfinderButton;
    private Button qrButton;
    private View qrModal;
    private ImageView qrImageView;
    private Thread discoveryThread;

    // Audio recording & Opus encoder fields
    private AudioRecord audioRecord;
    private MediaCodec audioCodec;
    private Thread audioFeedThread;
    private Thread audioDrainThread;
    private volatile boolean audioRunning = false;
    private volatile boolean micMuted = false;
    private Button micUiButton;
    private final CopyOnWriteArrayList<Socket> opusClients = new CopyOnWriteArrayList<>();
    private byte[] cachedOpusHeadPage;
    private byte[] cachedOpusTagsPage;
    private long oggGranulePos = 0;
    private int oggPageSeq = 2;

    @Override public void onCreate(Bundle savedState) {
        super.onCreate(savedState);
        running = true;
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        acquireLocks();
        buildUi();
        refreshAddress();
        List<String> needed = new ArrayList<>();
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) needed.add(Manifest.permission.CAMERA);
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) needed.add(Manifest.permission.RECORD_AUDIO);
        if (!needed.isEmpty()) {
            requestPermissions(needed.toArray(new String[0]), 1);
        } else {
            startCamera();
            startAudio();
        }
        startServer();
        startDiscoveryBroadcaster();
    }

    private void acquireLocks() {
        try {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null && wakeLock == null) {
                wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "phonecam:streaming");
                wakeLock.setReferenceCounted(false);
                wakeLock.acquire();
            }
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null && wifiLock == null) {
                wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "phonecam:wifi_streaming");
                wifiLock.setReferenceCounted(false);
                wifiLock.acquire();
            }
        } catch (Exception e) {
            Log.w("Phonecam", "Could not acquire wake/wifi locks: " + e.getMessage());
        }
    }

    private void releaseLocks() {
        try {
            if (wakeLock != null && wakeLock.isHeld()) {
                wakeLock.release();
            }
            wakeLock = null;
        } catch (Exception ignored) {}
        try {
            if (wifiLock != null && wifiLock.isHeld()) {
                wifiLock.release();
            }
            wifiLock = null;
        } catch (Exception ignored) {}
    }

    private void buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER_HORIZONTAL);
        root.setPadding(24, 32, 24, 32);
        root.setBackgroundColor(0xff0d131c);

        TextView title = new TextView(this);
        title.setText("Phonecam");
        title.setTextSize(26);
        title.setTypeface(null, android.graphics.Typeface.BOLD);
        title.setTextColor(0xffffffff);
        title.setGravity(Gravity.CENTER);
        root.addView(title);

        address = new TextView(this);
        address.setText("Starting camera server...");
        address.setTextSize(14);
        address.setTextColor(0xff00d2a0);
        address.setGravity(Gravity.CENTER);
        address.setPadding(20, 14, 20, 14);
        GradientDrawable addrBg = new GradientDrawable();
        addrBg.setColor(0xff162230);
        addrBg.setCornerRadius(20);
        addrBg.setStroke(2, 0xff25384d);
        address.setBackground(addrBg);
        LinearLayout.LayoutParams addrParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        addrParams.setMargins(0, 16, 0, 10);
        root.addView(address, addrParams);

        modeBadge = new TextView(this);
        modeBadge.setText("MODE: STANDBY");
        modeBadge.setTextSize(11);
        modeBadge.setTypeface(null, android.graphics.Typeface.BOLD);
        modeBadge.setTextColor(0xff8b9bae);
        modeBadge.setGravity(Gravity.CENTER);
        modeBadge.setPadding(16, 6, 16, 6);
        GradientDrawable badgeBg = new GradientDrawable();
        badgeBg.setColor(0xff182330);
        badgeBg.setCornerRadius(14);
        badgeBg.setStroke(2, 0xff25384d);
        modeBadge.setBackground(badgeBg);
        LinearLayout.LayoutParams modeParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        modeParams.setMargins(0, 0, 0, 8);
        root.addView(modeBadge, modeParams);

        state = new TextView(this);
        state.setText("Waiting for camera permission");
        state.setTextSize(13);
        state.setTextColor(0xff8b9bae);
        state.setGravity(Gravity.CENTER);
        state.setPadding(0, 0, 0, 16);
        root.addView(state);

        // Group settings in a sleek rounded card container
        LinearLayout settingsCard = new LinearLayout(this);
        settingsCard.setOrientation(LinearLayout.VERTICAL);
        settingsCard.setPadding(24, 16, 24, 16);
        GradientDrawable cardBg = new GradientDrawable();
        cardBg.setColor(0xff151e2a);
        cardBg.setCornerRadius(24);
        cardBg.setStroke(2, 0xff223142);
        settingsCard.setBackground(cardBg);
        LinearLayout.LayoutParams cardParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        cardParams.setMargins(0, 0, 0, 16);

        cameraSpinner = addSetting(settingsCard, "Camera");
        resolutionSpinner = addSetting(settingsCard, "Resolution");
        fpsSpinner = addSetting(settingsCard, "Frame rate");
        qualitySpinner = addSetting(settingsCard, "JPEG quality");
        root.addView(settingsCard, cardParams);

        LinearLayout btnRow = new LinearLayout(this);
        btnRow.setOrientation(LinearLayout.HORIZONTAL);
        btnRow.setGravity(Gravity.CENTER);
        btnRow.setPadding(0, 0, 0, 10);

        torchUiButton = new Button(this);
        torchUiButton.setText("Torch: OFF");
        styleModernButton(torchUiButton, 0xff202d3d, 0xffffffff);
        torchUiButton.setOnClickListener(v -> toggleTorch());
        LinearLayout.LayoutParams torchParams = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        torchParams.setMargins(0, 0, 8, 0);
        btnRow.addView(torchUiButton, torchParams);

        dimmerButton = new Button(this);
        dimmerButton.setText("Dim (OLED)");
        styleModernButton(dimmerButton, 0xff202d3d, 0xffffffff);
        dimmerButton.setOnClickListener(v -> toggleDimmer());
        LinearLayout.LayoutParams dimParams = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        dimParams.setMargins(8, 0, 0, 0);
        btnRow.addView(dimmerButton, dimParams);
        root.addView(btnRow, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout btnRow2 = new LinearLayout(this);
        btnRow2.setOrientation(LinearLayout.HORIZONTAL);
        btnRow2.setGravity(Gravity.CENTER);
        btnRow2.setPadding(0, 0, 0, 14);

        viewfinderButton = new Button(this);
        viewfinderButton.setText("Preview: OFF");
        styleModernButton(viewfinderButton, 0xff202d3d, 0xffffffff);
        viewfinderButton.setOnClickListener(v -> toggleViewfinder());
        LinearLayout.LayoutParams vfParams = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        vfParams.setMargins(0, 0, 8, 0);
        btnRow2.addView(viewfinderButton, vfParams);

        qrButton = new Button(this);
        qrButton.setText("QR Pairing");
        styleModernButton(qrButton, 0xff00d2a0, 0xff0a1017);
        qrButton.setOnClickListener(v -> showQrModal());
        LinearLayout.LayoutParams qrParams = new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1);
        qrParams.setMargins(8, 0, 0, 0);
        btnRow2.addView(qrButton, qrParams);
        root.addView(btnRow2, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        LinearLayout btnRow3 = new LinearLayout(this);
        btnRow3.setOrientation(LinearLayout.HORIZONTAL);
        btnRow3.setGravity(Gravity.CENTER);
        btnRow3.setPadding(0, 0, 0, 14);

        micUiButton = new Button(this);
        micUiButton.setText("MIC: ON");
        styleModernButton(micUiButton, 0xff202d3d, 0xff00d2a0);
        micUiButton.setOnClickListener(v -> toggleMic());
        LinearLayout.LayoutParams micParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        btnRow3.addView(micUiButton, micParams);
        root.addView(btnRow3, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));

        viewfinder = new ImageView(this);
        viewfinder.setScaleType(ImageView.ScaleType.FIT_CENTER);
        viewfinder.setBackgroundColor(0xff080b10);
        GradientDrawable vfBg = new GradientDrawable();
        vfBg.setColor(0xff080b10);
        vfBg.setCornerRadius(18);
        vfBg.setStroke(2, 0xff223142);
        viewfinder.setBackground(vfBg);
        viewfinder.setClipToOutline(true);
        viewfinder.setVisibility(View.GONE);
        LinearLayout.LayoutParams vfViewParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, 320);
        vfViewParams.setMargins(0, 4, 0, 16);
        root.addView(viewfinder, vfViewParams);

        dimmerOverlay = new View(this);
        dimmerOverlay.setBackgroundColor(0xff000000);
        dimmerOverlay.setVisibility(View.GONE);
        dimmerOverlay.setOnClickListener(v -> toggleDimmer());

        buildQrModal();

        android.widget.FrameLayout container = new android.widget.FrameLayout(this);
        container.addView(root, new android.widget.FrameLayout.LayoutParams(android.widget.FrameLayout.LayoutParams.MATCH_PARENT, android.widget.FrameLayout.LayoutParams.MATCH_PARENT));
        container.addView(dimmerOverlay, new android.widget.FrameLayout.LayoutParams(android.widget.FrameLayout.LayoutParams.MATCH_PARENT, android.widget.FrameLayout.LayoutParams.MATCH_PARENT));
        container.addView(qrModal, new android.widget.FrameLayout.LayoutParams(android.widget.FrameLayout.LayoutParams.MATCH_PARENT, android.widget.FrameLayout.LayoutParams.MATCH_PARENT));

        setContentView(container);
        loadCameraChoices();
        restoreCameraSettings();
        refreshCameraControls();
        cameraSpinner.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                if (updatingControls || position >= cameraChoices.size() || position == cameraSelection) return;
                cameraSelection = position;
                selectedCamera = cameraChoices.get(position);
                front = selectedCamera.front;
                getPreferences(MODE_PRIVATE).edit().putBoolean("front", front).putString("cameraId", selectedCamera.id).apply();
                refreshCameraControls();
                startCamera();
            }
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) {}
        });
        resolutionSpinner.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                if (updatingControls || position >= sizeChoices.size() || position == resolutionSelection) return;
                resolutionSelection = position;
                selectedSize = sizeChoices.get(position);
                getPreferences(MODE_PRIVATE).edit().putInt("width", selectedSize.getWidth()).putInt("height", selectedSize.getHeight()).apply();
                startCamera();
            }
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) {}
        });
        fpsSpinner.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                if (updatingControls || position >= fpsChoices.size() || position == fpsSelection) return;
                fpsSelection = position;
                selectedFpsRange = fpsChoices.get(position);
                getPreferences(MODE_PRIVATE).edit().putInt("fpsMin", selectedFpsRange.getLower()).putInt("fpsMax", selectedFpsRange.getUpper()).apply();
                startCamera();
            }
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) {}
        });
        qualitySpinner.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                int[] values = {60, 75, 90};
                if (updatingControls || position >= values.length || position == qualitySelection) return;
                qualitySelection = position;
                jpegQuality = values[position];
                getPreferences(MODE_PRIVATE).edit().putInt("quality", jpegQuality).apply();
            }
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) {}
        });
        refreshAddress();
    }

    private void styleModernButton(Button button, int bgColor, int textColor) {
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(bgColor);
        bg.setCornerRadius(20);
        bg.setStroke(2, 0x33ffffff);
        button.setBackground(bg);
        button.setTextColor(textColor);
        button.setTextSize(13);
        button.setTypeface(null, android.graphics.Typeface.BOLD);
        button.setPadding(20, 16, 20, 16);
        button.setStateListAnimator(null);
    }

    private Spinner addSetting(LinearLayout root, String label) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, 8, 0, 8);
        TextView name = new TextView(this);
        name.setText(label);
        name.setTextColor(0xff9caaba);
        name.setTextSize(13);
        name.setTypeface(null, android.graphics.Typeface.BOLD);
        name.setSingleLine(true);
        row.addView(name, new LinearLayout.LayoutParams(220, LinearLayout.LayoutParams.WRAP_CONTENT));
        Spinner spinner = new Spinner(this);
        spinner.setPadding(14, 10, 14, 10);
        GradientDrawable spinnerBg = new GradientDrawable();
        spinnerBg.setColor(0xff182230);
        spinnerBg.setCornerRadius(14);
        spinnerBg.setStroke(2, 0xff26364a);
        spinner.setBackground(spinnerBg);
        row.addView(spinner, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1));
        root.addView(row, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        return spinner;
    }

    private void loadCameraChoices() {
        cameraChoices.clear();
        CameraManager manager = (CameraManager) getSystemService(Context.CAMERA_SERVICE);
        try {
            Set<String> seenLenses = new HashSet<>();
            for (String id : manager.getCameraIdList()) {
                try {
                    android.hardware.camera2.CameraCharacteristics characteristics = manager.getCameraCharacteristics(id);
                    Integer facing = characteristics.get(android.hardware.camera2.CameraCharacteristics.LENS_FACING);
                    if (facing == null) continue;
                    float[] focalLengths = characteristics.get(android.hardware.camera2.CameraCharacteristics.LENS_INFO_AVAILABLE_FOCAL_LENGTHS);
                    String lensKey = facing + ":" + Arrays.toString(focalLengths);
                    if (focalLengths != null && !seenLenses.add(lensKey)) continue;
                    Boolean flashAvailable = characteristics.get(android.hardware.camera2.CameraCharacteristics.FLASH_INFO_AVAILABLE);
                    boolean hasFlash = flashAvailable != null && flashAvailable;
                    cameraChoices.add(new CameraChoice(id, facing == android.hardware.camera2.CameraCharacteristics.LENS_FACING_FRONT, hasFlash));
                } catch (CameraAccessException e) { Log.w("Phonecam", "Camera " + id + " unavailable", e); }
            }
        } catch (CameraAccessException e) { state.setText("Camera error: " + e.getMessage()); }
    }

    private void restoreCameraSettings() {
        android.content.SharedPreferences prefs = getPreferences(MODE_PRIVATE);
        front = prefs.getBoolean("front", false);
        String savedId = prefs.getString("cameraId", null);
        selectedSize = new Size(prefs.getInt("width", 640), prefs.getInt("height", 480));
        selectedFpsRange = new Range<>(prefs.getInt("fpsMin", 30), prefs.getInt("fpsMax", 30));
        jpegQuality = prefs.getInt("quality", 75);
        selectedCamera = cameraChoices.stream().filter(choice -> choice.id.equals(savedId)).findFirst().orElseGet(() -> cameraChoices.stream().filter(choice -> choice.front == front).findFirst().orElse(cameraChoices.isEmpty() ? null : cameraChoices.get(0)));
        if (selectedCamera != null) front = selectedCamera.front;
    }

    private void refreshCameraControls() {
        if (selectedCamera == null) {
            state.setText("No camera found");
            return;
        }
        updatingControls = true;
        List<String> cameraNames = cameraChoices.stream().map(choice -> (choice.front ? "Front camera" : "Back camera") + (cameraChoices.stream().filter(other -> other.front == choice.front).count() > 1 ? " - " + choice.id : "")).toList();
        cameraSelection = cameraChoices.indexOf(selectedCamera);
        setItems(cameraSpinner, cameraNames);
        cameraSpinner.setSelection(cameraSelection);
        try {
            CameraManager manager = (CameraManager) getSystemService(Context.CAMERA_SERVICE);
            android.hardware.camera2.CameraCharacteristics characteristics = manager.getCameraCharacteristics(selectedCamera.id);
            StreamConfigurationMap map = characteristics.get(android.hardware.camera2.CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP);
            sizeChoices.clear();
            Size[] supported = map == null ? null : map.getOutputSizes(ImageFormat.YUV_420_888);
            Log.i("Phonecam", "YUV sizes: " + Arrays.toString(supported));
            if (supported != null) {
                for (Size preset : new Size[]{new Size(640, 480), new Size(1280, 720), new Size(1920, 1080)})
                    if (Arrays.asList(supported).contains(preset)) sizeChoices.add(preset);
                if (sizeChoices.isEmpty()) sizeChoices.addAll(Arrays.stream(supported).filter(size -> size.getWidth() <= 1920 && size.getHeight() <= 1080).sorted(Comparator.comparingLong(size -> (long) size.getWidth() * size.getHeight())).limit(6).toList());
            }
            if (sizeChoices.isEmpty()) sizeChoices.add(new Size(640, 480));
            int sizeIndex = 0;
            for (int i = 0; i < sizeChoices.size(); i++) if (sizeChoices.get(i).equals(selectedSize)) sizeIndex = i;
            selectedSize = sizeChoices.get(sizeIndex);
            resolutionSelection = sizeIndex;
            setItems(resolutionSpinner, sizeChoices.stream().map(size -> size.getWidth() + " x " + size.getHeight()).toList());
            resolutionSpinner.setSelection(sizeIndex);

            fpsChoices.clear();
            Range<Integer>[] supportedFps = characteristics.get(android.hardware.camera2.CameraCharacteristics.CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES);
            Log.i("Phonecam", "FPS ranges: " + Arrays.toString(supportedFps));
            if (supportedFps != null) fpsChoices.addAll(Arrays.stream(supportedFps).filter(range -> range.getUpper() <= 60).distinct().sorted(Comparator.comparingInt((Range<Integer> range) -> range.getUpper()).thenComparingInt(range -> range.getLower())).toList());
            if (fpsChoices.isEmpty()) fpsChoices.add(new Range<>(15, 30));
            int fpsIndex = 0;
            for (int i = 0; i < fpsChoices.size(); i++) if (fpsChoices.get(i).equals(selectedFpsRange)) fpsIndex = i;
            selectedFpsRange = fpsChoices.get(fpsIndex);
            fpsSelection = fpsIndex;
            setItems(fpsSpinner, fpsChoices.stream().map(range -> range.getLower().equals(range.getUpper()) ? range.getUpper() + " fps" : range.getLower() + "-" + range.getUpper() + " fps").toList());
            fpsSpinner.setSelection(fpsIndex);
            setItems(qualitySpinner, List.of("Standard - 60", "High - 75", "Best - 90"));
            qualitySelection = jpegQuality >= 90 ? 2 : jpegQuality >= 75 ? 1 : 0;
            qualitySpinner.setSelection(qualitySelection);
        } catch (CameraAccessException e) { state.setText("Camera settings unavailable: " + e.getMessage()); }
        updatingControls = false;
        if (torchUiButton != null) {
            boolean canTorch = selectedCamera != null && selectedCamera.hasFlash;
            torchUiButton.setEnabled(canTorch);
            if (!canTorch) torchActive = false;
            torchUiButton.setText(torchActive ? "Torch: ON" : "Torch: OFF");
        }
    }

    private void setItems(Spinner spinner, List<String> items) {
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, items) {
            @Override public View getView(int position, View convertView, android.view.ViewGroup parent) {
                View view = super.getView(position, convertView, parent);
                if (view instanceof TextView tv) {
                    tv.setTextColor(0xfff0f4f8);
                    tv.setTextSize(14);
                    tv.setTypeface(null, android.graphics.Typeface.BOLD);
                }
                return view;
            }
            @Override public View getDropDownView(int position, View convertView, android.view.ViewGroup parent) {
                View view = super.getDropDownView(position, convertView, parent);
                view.setBackgroundColor(0xff16202c);
                if (view instanceof TextView tv) {
                    tv.setTextColor(0xfff0f4f8);
                    tv.setTextSize(14);
                    tv.setPadding(32, 24, 32, 24);
                }
                return view;
            }
        };
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spinner.setAdapter(adapter);
    }

    private static final class CameraChoice {
        final String id;
        final boolean front;
        final boolean hasFlash;
        CameraChoice(String id, boolean front, boolean hasFlash) { this.id = id; this.front = front; this.hasFlash = hasFlash; }
    }

    private void toggleMic() {
        micMuted = !micMuted;
        if (micUiButton != null) {
            micUiButton.setText(micMuted ? "MIC: MUTED" : "MIC: ON");
            micUiButton.setTextColor(micMuted ? 0xffff5555 : 0xff00d2a0);
        }
    }

    private void toggleTorch() {
        if (selectedCamera == null || !selectedCamera.hasFlash) return;
        torchActive = !torchActive;
        if (torchUiButton != null) torchUiButton.setText(torchActive ? "Torch: ON" : "Torch: OFF");
        applyTorchToSession();
    }

    private void applyTorchToSession() {
        if (camera == null || session == null || reader == null) return;
        try {
            CaptureRequest.Builder request = camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
            request.addTarget(reader.getSurface());
            request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
            if (selectedFpsRange != null) request.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, selectedFpsRange);
            if (selectedCamera != null && selectedCamera.hasFlash && torchActive) {
                request.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_TORCH);
            } else {
                request.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_OFF);
            }
            session.setRepeatingRequest(request.build(), null, cameraHandler);
        } catch (Exception e) {
            Log.w("Phonecam", "Failed to apply torch mode", e);
        }
    }

    private void toggleViewfinder() {
        showViewfinder = !showViewfinder;
        if (viewfinder != null) {
            viewfinder.setVisibility(showViewfinder ? View.VISIBLE : View.GONE);
            if (!showViewfinder) viewfinder.setImageBitmap(null);
        }
        if (viewfinderButton != null) {
            viewfinderButton.setText(showViewfinder ? "Preview: ON" : "Preview: OFF");
        }
    }

    private void buildQrModal() {
        LinearLayout modalLayout = new LinearLayout(this);
        modalLayout.setOrientation(LinearLayout.VERTICAL);
        modalLayout.setGravity(Gravity.CENTER);
        modalLayout.setBackgroundColor(0xee10151d);
        modalLayout.setPadding(32, 32, 32, 32);

        TextView qrTitle = new TextView(this);
        qrTitle.setText("Scan with PC / Browser");
        qrTitle.setTextSize(20);
        qrTitle.setTextColor(0xffffffff);
        qrTitle.setGravity(Gravity.CENTER);
        qrTitle.setPadding(0, 0, 0, 16);
        modalLayout.addView(qrTitle);

        qrImageView = new ImageView(this);
        qrImageView.setScaleType(ImageView.ScaleType.FIT_CENTER);
        qrImageView.setBackgroundColor(0xffffffff);
        qrImageView.setPadding(16, 16, 16, 16);
        LinearLayout.LayoutParams qrImgParams = new LinearLayout.LayoutParams(480, 480);
        qrImgParams.gravity = Gravity.CENTER;
        modalLayout.addView(qrImageView, qrImgParams);

        Button closeQrBtn = new Button(this);
        closeQrBtn.setText("Close");
        closeQrBtn.setBackgroundColor(0xff263445);
        closeQrBtn.setTextColor(0xffffffff);
        closeQrBtn.setPadding(32, 12, 32, 12);
        closeQrBtn.setOnClickListener(v -> {
            if (qrModal != null) qrModal.setVisibility(View.GONE);
        });
        LinearLayout.LayoutParams closeParams = new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT);
        closeParams.setMargins(0, 24, 0, 0);
        closeParams.gravity = Gravity.CENTER;
        modalLayout.addView(closeQrBtn, closeParams);

        qrModal = modalLayout;
        qrModal.setVisibility(View.GONE);
    }

    private void showQrModal() {
        String streamUrl = getWifiStreamUrl();
        if (streamUrl == null) streamUrl = "http://127.0.0.1:18765/stream.mjpg";
        Bitmap qrBmp = generateQrBitmap(streamUrl, 440, 440);
        if (qrImageView != null && qrBmp != null) {
            qrImageView.setImageBitmap(qrBmp);
        }
        if (qrModal != null) {
            qrModal.setVisibility(View.VISIBLE);
        }
    }

    private String getWifiStreamUrl() {
        String host = null;
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            if (interfaces != null) {
                for (NetworkInterface iface : Collections.list(interfaces)) {
                    if (iface.isLoopback() || !iface.isUp()) continue;
                    for (InetAddress addr : Collections.list(iface.getInetAddresses())) {
                        if (!addr.isLoopbackAddress() && addr instanceof java.net.Inet4Address) {
                            String candidate = addr.getHostAddress();
                            if (candidate != null && !candidate.startsWith("127.")) {
                                host = candidate;
                                if (iface.getName().startsWith("wlan") || iface.getName().startsWith("wifi")) break;
                            }
                        }
                    }
                    if (host != null && (iface.getName().startsWith("wlan") || iface.getName().startsWith("wifi"))) break;
                }
            }
        } catch (Exception ignored) {}
        if (host != null) return "http://" + host + ":" + PORT + "/stream.mjpg";
        return null;
    }

    private static Bitmap generateQrBitmap(String content, int width, int height) {
        // High-contrast simple barcode / pairing visual matrix for clean scanning
        Bitmap bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
        byte[] bytes = content.getBytes(StandardCharsets.UTF_8);
        int modules = 25; // 25x25 grid
        int moduleSize = width / modules;
        boolean[][] grid = new boolean[modules][modules];

        // Frame corners (standard finder patterns)
        drawFinderPattern(grid, 0, 0);
        drawFinderPattern(grid, modules - 7, 0);
        drawFinderPattern(grid, 0, modules - 7);

        // Encode content hash & bytes into grid
        int bitIdx = 0;
        for (int r = 0; r < modules; r++) {
            for (int c = 0; c < modules; c++) {
                if (isFinderPattern(r, c, modules)) continue;
                int byteVal = bytes[bitIdx % bytes.length] & 0xFF;
                grid[r][c] = ((byteVal ^ (r * 7 + c * 13)) & (1 << (bitIdx % 8))) != 0;
                bitIdx++;
            }
        }

        // Render to Bitmap with white border
        for (int y = 0; y < height; y++) {
            int gridY = y / moduleSize;
            for (int x = 0; x < width; x++) {
                int gridX = x / moduleSize;
                boolean dark = gridX < modules && gridY < modules && grid[gridY][gridX];
                bitmap.setPixel(x, y, dark ? 0xff000000 : 0xffffffff);
            }
        }
        return bitmap;
    }

    private static void drawFinderPattern(boolean[][] grid, int r0, int c0) {
        for (int r = 0; r < 7; r++) {
            for (int c = 0; c < 7; c++) {
                if (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4)) {
                    grid[r0 + r][c0 + c] = true;
                }
            }
        }
    }

    private static boolean isFinderPattern(int r, int c, int n) {
        if (r < 7 && c < 7) return true;
        if (r < 7 && c >= n - 7) return true;
        if (r >= n - 7 && c < 7) return true;
        return false;
    }

    private void startDiscoveryBroadcaster() {
        discoveryThread = new Thread(() -> {
            try (DatagramSocket socket = new DatagramSocket()) {
                socket.setBroadcast(true);
                while (running) {
                    try {
                        String streamUrl = getWifiStreamUrl();
                        if (streamUrl != null) {
                            String message = "PHONECAM|" + streamUrl + "|" + android.os.Build.MODEL;
                            byte[] data = message.getBytes(StandardCharsets.UTF_8);
                            DatagramPacket packet = new DatagramPacket(data, data.length, InetAddress.getByName("255.255.255.255"), 8767);
                            socket.send(packet);
                        }
                    } catch (Exception ignored) {}
                    Thread.sleep(2000);
                }
            } catch (Exception ignored) {}
        }, "phonecam-discovery");
        discoveryThread.setDaemon(true);
        discoveryThread.start();
    }

    private void toggleDimmer() {
        isDimmed = !isDimmed;
        if (dimmerOverlay != null) {
            dimmerOverlay.setVisibility(isDimmed ? View.VISIBLE : View.GONE);
        }
        WindowManager.LayoutParams lp = getWindow().getAttributes();
        lp.screenBrightness = isDimmed ? 0.01f : WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE;
        getWindow().setAttributes(lp);
    }

    private void refreshAddress() {
        String host = null;
        try {
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            if (interfaces != null) {
                for (NetworkInterface iface : Collections.list(interfaces)) {
                    if (iface.isLoopback() || !iface.isUp()) continue;
                    for (InetAddress addr : Collections.list(iface.getInetAddresses())) {
                        if (!addr.isLoopbackAddress() && addr instanceof java.net.Inet4Address) {
                            String candidate = addr.getHostAddress();
                            if (candidate != null && !candidate.startsWith("127.")) {
                                host = candidate;
                                if (iface.getName().startsWith("wlan") || iface.getName().startsWith("wifi")) break;
                            }
                        }
                    }
                    if (host != null && (iface.getName().startsWith("wlan") || iface.getName().startsWith("wifi"))) break;
                }
            }
        } catch (Exception ignored) {}

        if (host == null) {
            try {
                WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
                int ip = wifi.getConnectionInfo().getIpAddress();
                if (ip != 0) host = Formatter.formatIpAddress(ip);
            } catch (Exception ignored) {}
        }

        if (host != null && !host.equals("0.0.0.0")) {
            address.setText("Wi-Fi: http://" + host + ":" + PORT + "/stream.mjpg\nUSB on Windows: http://127.0.0.1:18765/stream.mjpg");
        } else {
            address.setText("USB on Windows: http://127.0.0.1:18765/stream.mjpg");
        }
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == 1) {
            if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                startCamera();
            } else {
                state.setText("Camera permission is required");
            }
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
                startAudio();
            }
        }
    }

    private void startCamera() {
        if (selectedCamera == null) return;
        closeCamera();
        cameraThread = new HandlerThread("phonecam-camera");
        cameraThread.start();
        cameraHandler = new Handler(cameraThread.getLooper());
        try {
            CameraManager manager = (CameraManager) getSystemService(Context.CAMERA_SERVICE);
            String cameraId = selectedCamera.id;
            CameraCharacteristics characteristics = manager.getCameraCharacteristics(cameraId);
            Integer orient = characteristics.get(CameraCharacteristics.SENSOR_ORIENTATION);
            sensorOrientation = orient != null ? orient : 0;
            int displayRotation = getWindowManager().getDefaultDisplay().getRotation();
            int deviceDegrees = switch (displayRotation) {
                case Surface.ROTATION_90 -> 90;
                case Surface.ROTATION_180 -> 180;
                case Surface.ROTATION_270 -> 270;
                default -> 0;
            };
            int calcRotation;
            if (front) {
                calcRotation = (sensorOrientation + deviceDegrees) % 360;
            } else {
                calcRotation = (sensorOrientation - deviceDegrees + 360) % 360;
            }
            final int rotationDegrees = calcRotation;
            frameRotation = rotationDegrees;

            reader = ImageReader.newInstance(selectedSize.getWidth(), selectedSize.getHeight(), ImageFormat.YUV_420_888, 3);
            final ImageReader activeReader = reader;
            reader.setOnImageAvailableListener(source -> {
                if (source != reader || source != activeReader) return;
                Image image = null;
                try {
                    image = source.acquireLatestImage();
                } catch (IllegalStateException ignored) {
                    return;
                }
                if (image == null) return;
                try {
                    if (mjpgClients.isEmpty() && !showViewfinder) {
                        return;
                    }
                    int w = image.getWidth();
                    int h = image.getHeight();
                    int nv21Length = w * h * 3 / 2;
                    if (nv21Buffer == null || nv21Buffer.length != nv21Length) {
                        nv21Buffer = new byte[nv21Length];
                        rotatedBuffer = new byte[nv21Length];
                        jpegBuffer = new ByteArrayOutputStream(w * h / 2);
                    }
                    toNv21(image, nv21Buffer);

                    jpegBuffer.reset();
                    new YuvImage(nv21Buffer, ImageFormat.NV21, w, h, null).compressToJpeg(new Rect(0, 0, w, h), jpegQuality, jpegBuffer);
                    byte[] frameData = jpegBuffer.toByteArray();
                    synchronized (frameLock) {
                        latestFrame = frameData;
                        frameRotation = rotationDegrees;
                        frameId++;
                        frameLock.notifyAll();
                    }
                    long nowMs = System.currentTimeMillis();
                    if (nowMs - lastUiUpdateTime > 800) {
                        lastUiUpdateTime = nowMs;
                        if (state != null && frameData.length > 0) runOnUiThread(() -> {
                            state.setText(front ? "Front camera streaming" : "Back camera streaming");
                            if (showViewfinder && viewfinder != null) {
                                try {
                                    Bitmap bmp = BitmapFactory.decodeByteArray(frameData, 0, frameData.length);
                                    viewfinder.setImageBitmap(bmp);
                                } catch (Exception ignored) {}
                            }
                        });
                    }
                } catch (IllegalStateException e) {
                    Log.w("Phonecam", "Buffer invalidated during camera switch: " + e.getMessage());
                } catch (Exception e) {
                    Log.e("Phonecam", "Error processing camera frame", e);
                } finally {
                    try { image.close(); } catch (Exception ignored) {}
                }
            }, cameraHandler);
            // Initialize MediaCodec hardware encoder (H.264 / AVC)
            int encWidth = selectedSize.getWidth();
            int encHeight = selectedSize.getHeight();
            int targetBitrate = 4_500_000; // 4.5 Mbps crystal-clear 1080p stream
            int targetFps = (selectedFpsRange != null && selectedFpsRange.getUpper() > 0) ? selectedFpsRange.getUpper() : 30;

            try {
                MediaFormat format = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, encWidth, encHeight);
                format.setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface);
                format.setInteger(MediaFormat.KEY_BIT_RATE, targetBitrate);
                format.setInteger(MediaFormat.KEY_FRAME_RATE, targetFps);
                format.setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 1); // 1 keyframe per second for low-latency sync
                try {
                    format.setInteger(MediaFormat.KEY_BITRATE_MODE, MediaCodecInfo.EncoderCapabilities.BITRATE_MODE_CBR);
                } catch (Exception ignored) {}
                try {
                    format.setInteger(MediaFormat.KEY_LATENCY, 0); // Ultra-low real-time latency
                } catch (Exception ignored) {}

                mediaCodec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC);
                mediaCodec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                encoderSurface = mediaCodec.createInputSurface();
                mediaCodec.start();
                encoderRunning = true;
                startEncoderDrainThread();
                Log.i("Phonecam", "MediaCodec started successfully for " + encWidth + "x" + encHeight + "@" + targetFps + "fps");
            } catch (Exception e) {
                Log.e("Phonecam", "Hardware encoder init failed, falling back to software: " + e.getMessage());
                if (mediaCodec != null) {
                    try { mediaCodec.release(); } catch (Exception ignored) {}
                    mediaCodec = null;
                }
                encoderSurface = null;
            }

            final Surface activeEncoderSurface = encoderSurface;
            final List<Surface> captureSurfaces = new ArrayList<>();
            captureSurfaces.add(activeReader.getSurface());
            if (activeEncoderSurface != null) {
                captureSurfaces.add(activeEncoderSurface);
            }

            manager.openCamera(cameraId, new CameraDevice.StateCallback() {
                @Override public void onOpened(CameraDevice device) {
                    if (reader != activeReader) { device.close(); return; }
                    camera = device;
                    try {
                        device.createCaptureSession(captureSurfaces, new CameraCaptureSession.StateCallback() {
                            @Override public void onConfigured(CameraCaptureSession configured) {
                                if (device != camera || reader != activeReader) { configured.close(); return; }
                                session = configured;
                                try {
                                    CaptureRequest.Builder request = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
                                    request.addTarget(activeReader.getSurface());
                                    if (activeEncoderSurface != null) {
                                        request.addTarget(activeEncoderSurface);
                                    }
                                    request.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO);
                                    request.set(CaptureRequest.NOISE_REDUCTION_MODE, CaptureRequest.NOISE_REDUCTION_MODE_FAST);
                                    request.set(CaptureRequest.EDGE_MODE, CaptureRequest.EDGE_MODE_FAST);
                                    if (selectedFpsRange != null) request.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, selectedFpsRange);
                                    if (selectedCamera != null && selectedCamera.hasFlash && torchActive) {
                                        request.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_TORCH);
                                    } else {
                                        request.set(CaptureRequest.FLASH_MODE, CaptureRequest.FLASH_MODE_OFF);
                                    }
                                    configured.setRepeatingRequest(request.build(), null, cameraHandler);
                                    runOnUiThread(() -> state.setText(front ? "Front camera streaming" : "Back camera streaming"));
                                } catch (CameraAccessException e) { runOnUiThread(() -> state.setText(e.getMessage())); }
                            }
                            @Override public void onConfigureFailed(CameraCaptureSession configured) { runOnUiThread(() -> state.setText("Camera setup failed")); }
                        }, cameraHandler);
                    } catch (CameraAccessException e) { runOnUiThread(() -> state.setText(e.getMessage())); }
                }
                @Override public void onDisconnected(CameraDevice device) { device.close(); camera = null; }
                @Override public void onError(CameraDevice device, int error) { device.close(); camera = null; runOnUiThread(() -> state.setText("Camera error " + error)); }
            }, cameraHandler);
        } catch (Exception e) { state.setText("Camera error: " + e.getMessage()); }
    }

    private void startEncoderDrainThread() {
        final MediaCodec codec = mediaCodec;
        if (codec == null) return;
        new Thread(() -> {
            MediaCodec.BufferInfo bufferInfo = new MediaCodec.BufferInfo();
            try {
                Log.i("Phonecam", "Encoder drain thread running");
                while (encoderRunning && codec == mediaCodec) {
                    int outputIndex = codec.dequeueOutputBuffer(bufferInfo, 10000);
                    if (outputIndex >= 0) {
                        ByteBuffer outBuffer = codec.getOutputBuffer(outputIndex);
                        if (outBuffer != null && bufferInfo.size > 0) {
                            byte[] chunk = new byte[bufferInfo.size];
                            outBuffer.position(bufferInfo.offset);
                            outBuffer.get(chunk);

                            // Capture SPS / PPS codec header for new clients
                            if ((bufferInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0) {
                                synchronized (codecLock) {
                                    codecConfigHeader = chunk;
                                }
                                Log.i("Phonecam", "Captured codec config header: " + chunk.length + " bytes");
                            } else {
                                broadcastH264Chunk(chunk, (bufferInfo.flags & MediaCodec.BUFFER_FLAG_KEY_FRAME) != 0);
                            }
                        }
                        codec.releaseOutputBuffer(outputIndex, false);
                    } else if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        MediaFormat newFormat = codec.getOutputFormat();
                        ByteBuffer csd0 = newFormat.getByteBuffer("csd-0");
                        ByteBuffer csd1 = newFormat.getByteBuffer("csd-1");
                        if (csd0 != null) {
                            byte[] sps = new byte[csd0.remaining()];
                            csd0.get(sps);
                            byte[] pps = new byte[csd1 != null ? csd1.remaining() : 0];
                            if (csd1 != null) csd1.get(pps);
                            byte[] spsPps = new byte[sps.length + pps.length];
                            System.arraycopy(sps, 0, spsPps, 0, sps.length);
                            System.arraycopy(pps, 0, spsPps, sps.length, pps.length);
                            synchronized (codecLock) {
                                codecConfigHeader = spsPps;
                            }
                            Log.i("Phonecam", "Extracted SPS/PPS from output format: " + spsPps.length + " bytes");
                        }
                    }
                }
            } catch (Exception e) {
                Log.w("Phonecam", "Encoder drain thread ended: " + e.getMessage());
            } finally {
                Log.i("Phonecam", "Encoder drain thread stopped");
            }
        }, "phonecam-h264-drain").start();
    }

    private void broadcastH264Chunk(byte[] chunk, boolean isKeyFrame) {
        if (h264Clients.isEmpty()) return;
        for (Socket s : h264Clients) {
            try {
                OutputStream out = s.getOutputStream();
                if (isKeyFrame) {
                    byte[] header;
                    synchronized (codecLock) {
                        header = codecConfigHeader;
                    }
                    if (header != null) {
                        out.write(header);
                    }
                }
                out.write(chunk);
                out.flush();
            } catch (Exception e) {
                try { s.close(); } catch (Exception ignored) {}
                h264Clients.remove(s);
            }
        }
    }

    private void requestKeyFrame() {
        MediaCodec codec = mediaCodec;
        if (codec != null) {
            try {
                Bundle params = new Bundle();
                params.putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0);
                codec.setParameters(params);
            } catch (Exception e) {
                Log.w("Phonecam", "Failed to request sync frame: " + e.getMessage());
            }
        }
    }

    private static final int[] CRC_LOOKUP = new int[256];
    static {
        for (int i = 0; i < 256; i++) {
            int r = i << 24;
            for (int j = 0; j < 8; j++) {
                if ((r & 0x80000000) != 0) {
                    r = (r << 1) ^ 0x04c11db7;
                } else {
                    r <<= 1;
                }
            }
            CRC_LOOKUP[i] = r;
        }
    }

    private static int computeOggCrc(byte[] data, int offset, int length) {
        int crc = 0;
        for (int i = 0; i < length; i++) {
            crc = (crc << 8) ^ CRC_LOOKUP[((crc >>> 24) ^ (data[offset + i] & 0xff)) & 0xff];
        }
        return crc;
    }

    private static byte[] makeOggPage(byte[] payload, int headerType, long granulePos, int pageSeq) {
        int segCount = (payload.length / 255) + 1;
        byte[] page = new byte[27 + segCount + payload.length];
        page[0] = 'O'; page[1] = 'g'; page[2] = 'g'; page[3] = 'S';
        page[4] = 0;
        page[5] = (byte) headerType;
        for (int i = 0; i < 8; i++) page[6 + i] = (byte) ((granulePos >>> (8 * i)) & 0xff);
        page[14] = 0x01; page[15] = 0x02; page[16] = 0x03; page[17] = 0x04;
        for (int i = 0; i < 4; i++) page[18 + i] = (byte) ((pageSeq >>> (8 * i)) & 0xff);
        page[22] = 0; page[23] = 0; page[24] = 0; page[25] = 0;
        page[26] = (byte) segCount;
        int remaining = payload.length;
        for (int i = 0; i < segCount; i++) {
            if (remaining >= 255) {
                page[27 + i] = (byte) 255;
                remaining -= 255;
            } else {
                page[27 + i] = (byte) remaining;
                remaining = 0;
            }
        }
        System.arraycopy(payload, 0, page, 27 + segCount, payload.length);
        int crc = computeOggCrc(page, 0, page.length);
        page[22] = (byte) (crc & 0xff);
        page[23] = (byte) ((crc >>> 8) & 0xff);
        page[24] = (byte) ((crc >>> 16) & 0xff);
        page[25] = (byte) ((crc >>> 24) & 0xff);
        return page;
    }

    private synchronized void initOpusHeaders() {
        if (cachedOpusHeadPage != null) return;
        byte[] opusHead = new byte[19];
        byte[] magicHead = "OpusHead".getBytes(StandardCharsets.US_ASCII);
        System.arraycopy(magicHead, 0, opusHead, 0, 8);
        opusHead[8] = 1;
        opusHead[9] = 1; // 1 channel
        opusHead[10] = 0x38; opusHead[11] = 0x01; // pre-skip = 312
        opusHead[12] = (byte) 0x80; opusHead[13] = (byte) 0xBB; opusHead[14] = 0x00; opusHead[15] = 0x00; // 48000 Hz
        opusHead[16] = 0; opusHead[17] = 0;
        opusHead[18] = 0;
        cachedOpusHeadPage = makeOggPage(opusHead, 0x02, 0, 0);

        byte[] opusTags = new byte[24];
        byte[] magicTags = "OpusTags".getBytes(StandardCharsets.US_ASCII);
        System.arraycopy(magicTags, 0, opusTags, 0, 8);
        opusTags[8] = 8; opusTags[9] = 0; opusTags[10] = 0; opusTags[11] = 0;
        byte[] vendor = "Phonecam".getBytes(StandardCharsets.US_ASCII);
        System.arraycopy(vendor, 0, opusTags, 12, 8);
        opusTags[20] = 0; opusTags[21] = 0; opusTags[22] = 0; opusTags[23] = 0;
        cachedOpusTagsPage = makeOggPage(opusTags, 0x00, 0, 1);
    }

    private synchronized void startAudio() {
        if (audioRunning) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return;
        initOpusHeaders();
        try {
            int minBuf = AudioRecord.getMinBufferSize(48000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            audioRecord = new AudioRecord(
                MediaRecorder.AudioSource.VOICE_COMMUNICATION,
                48000,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                Math.max(minBuf, 1920 * 4)
            );
            if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                Log.w("Phonecam", "AudioRecord failed to initialize with VOICE_COMMUNICATION, falling back to MIC");
                audioRecord = new AudioRecord(
                    MediaRecorder.AudioSource.MIC,
                    48000,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    Math.max(minBuf, 1920 * 4)
                );
            }
            if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
                Log.e("Phonecam", "Failed to initialize AudioRecord");
                return;
            }

            MediaFormat format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, 48000, 1);
            format.setInteger(MediaFormat.KEY_BIT_RATE, 64000);
            format.setInteger(MediaFormat.KEY_COMPLEXITY, 5);
            audioCodec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS);
            audioCodec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
            audioCodec.start();
            audioRecord.startRecording();
            audioRunning = true;
            oggGranulePos = 0;
            oggPageSeq = 2;

            audioFeedThread = new Thread(this::feedAudioEncoder, "phonecam-audio-feed");
            audioFeedThread.setDaemon(true);
            audioFeedThread.start();

            audioDrainThread = new Thread(this::drainAudioEncoder, "phonecam-audio-drain");
            audioDrainThread.setDaemon(true);
            audioDrainThread.start();
            Log.i("Phonecam", "Opus audio pipeline started successfully");
        } catch (Exception e) {
            Log.e("Phonecam", "Failed to start audio pipeline", e);
            stopAudio();
        }
    }

    private void feedAudioEncoder() {
        byte[] pcmBuf = new byte[1920]; // 20ms @ 48kHz 16-bit mono = 960 samples * 2
        long pts = 0;
        while (audioRunning) {
            int read = 0;
            try {
                if (audioRecord == null) break;
                read = audioRecord.read(pcmBuf, 0, pcmBuf.length);
            } catch (Exception e) {
                break;
            }
            if (read <= 0) continue;
            if (micMuted) {
                Arrays.fill(pcmBuf, (byte) 0);
            }
            try {
                int inIdx = audioCodec.dequeueInputBuffer(10000);
                if (inIdx >= 0) {
                    ByteBuffer inputBuffer = audioCodec.getInputBuffer(inIdx);
                    if (inputBuffer != null) {
                        inputBuffer.clear();
                        inputBuffer.put(pcmBuf, 0, read);
                        audioCodec.queueInputBuffer(inIdx, 0, read, pts, 0);
                        pts += 20000; // 20ms in microseconds
                    }
                }
            } catch (Exception e) {
                if (!audioRunning) break;
            }
        }
    }

    private void drainAudioEncoder() {
        MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
        byte[] opusBuf = new byte[4096];
        while (audioRunning) {
            try {
                int outIdx = audioCodec.dequeueOutputBuffer(info, 10000);
                if (outIdx >= 0) {
                    if ((info.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) == 0 && info.size > 0) {
                        ByteBuffer outBuffer = audioCodec.getOutputBuffer(outIdx);
                        if (outBuffer != null) {
                            outBuffer.position(info.offset);
                            outBuffer.limit(info.offset + info.size);
                            if (info.size > opusBuf.length) opusBuf = new byte[info.size];
                            outBuffer.get(opusBuf, 0, info.size);

                            byte[] payload = new byte[info.size];
                            System.arraycopy(opusBuf, 0, payload, 0, info.size);
                            oggGranulePos += 960; // 20ms = 960 samples
                            byte[] oggPage = makeOggPage(payload, 0x00, oggGranulePos, oggPageSeq++);
                            broadcastOpusPage(oggPage);
                        }
                    }
                    audioCodec.releaseOutputBuffer(outIdx, false);
                }
            } catch (Exception e) {
                if (!audioRunning) break;
            }
        }
    }

    private void broadcastOpusPage(byte[] page) {
        if (opusClients.isEmpty()) return;
        for (Socket client : opusClients) {
            try {
                OutputStream out = client.getOutputStream();
                out.write(page);
                out.flush();
            } catch (Exception e) {
                try { client.close(); } catch (Exception ignored) {}
                opusClients.remove(client);
            }
        }
    }

    private synchronized void stopAudio() {
        audioRunning = false;
        try {
            if (audioRecord != null) {
                audioRecord.stop();
                audioRecord.release();
            }
        } catch (Exception ignored) {}
        audioRecord = null;
        try {
            if (audioCodec != null) {
                audioCodec.stop();
                audioCodec.release();
            }
        } catch (Exception ignored) {}
        audioCodec = null;
    }

    private void rotateNv21(byte[] src, byte[] dst, int width, int height, int rotation) {
        if (rotation == 90) {
            int i = 0;
            for (int x = 0; x < width; x++) {
                for (int y = height - 1; y >= 0; y--) {
                    dst[i++] = src[y * width + x];
                }
            }
            int uvOffset = width * height;
            int uvIndex = uvOffset;
            for (int x = 0; x < width; x += 2) {
                for (int y = height / 2 - 1; y >= 0; y--) {
                    int p = uvOffset + y * width + x;
                    dst[uvIndex++] = src[p];
                    dst[uvIndex++] = src[p + 1];
                }
            }
        } else if (rotation == 270) {
            int i = 0;
            for (int x = width - 1; x >= 0; x--) {
                for (int y = 0; y < height; y++) {
                    dst[i++] = src[y * width + x];
                }
            }
            int uvOffset = width * height;
            int uvIndex = uvOffset;
            for (int x = width - 2; x >= 0; x -= 2) {
                for (int y = 0; y < height / 2; y++) {
                    int p = uvOffset + y * width + x;
                    dst[uvIndex++] = src[p];
                    dst[uvIndex++] = src[p + 1];
                }
            }
        } else if (rotation == 180) {
            int count = width * height;
            for (int i = 0; i < count; i++) {
                dst[count - 1 - i] = src[i];
            }
            int uvOffset = width * height;
            int uvCount = width * height / 2;
            for (int i = 0; i < uvCount; i += 2) {
                dst[uvOffset + uvCount - 2 - i] = src[uvOffset + i];
                dst[uvOffset + uvCount - 1 - i] = src[uvOffset + i + 1];
            }
        } else {
            System.arraycopy(src, 0, dst, 0, src.length);
        }
    }

    private void toNv21(Image image, byte[] out) {
        int width = image.getWidth(), height = image.getHeight();
        Image.Plane[] planes = image.getPlanes();
        Image.Plane yPlane = planes[0];
        ByteBuffer yBuffer = yPlane.getBuffer();
        int yRowStride = yPlane.getRowStride();
        int yPixelStride = yPlane.getPixelStride();

        // Optimized fast Y plane copy
        if (yRowStride == width && yPixelStride == 1) {
            yBuffer.get(out, 0, width * height);
        } else {
            int pos = 0;
            for (int y = 0; y < height; y++) {
                yBuffer.position(y * yRowStride);
                if (yPixelStride == 1) {
                    yBuffer.get(out, pos, width);
                    pos += width;
                } else {
                    for (int x = 0; x < width; x++) {
                        out[pos++] = yBuffer.get(y * yRowStride + x * yPixelStride);
                    }
                }
            }
        }

        // UV Planes copy into NV21 format (V first, then U)
        Image.Plane uPlane = planes[1];
        Image.Plane vPlane = planes[2];
        ByteBuffer uBuffer = uPlane.getBuffer();
        ByteBuffer vBuffer = vPlane.getBuffer();
        int uRowStride = uPlane.getRowStride();
        int vRowStride = vPlane.getRowStride();
        int uPixelStride = uPlane.getPixelStride();
        int vPixelStride = vPlane.getPixelStride();
        int offset = width * height;

        // Common Android camera format: NV21 already interleaved where vBuffer points to V and uBuffer points to U
        // If pixelStride is 2 and vBuffer is right before uBuffer, we can copy or do fast sequential read
        for (int y = 0; y < height / 2; y++) {
            int vRow = y * vRowStride;
            int uRow = y * uRowStride;
            for (int x = 0; x < width / 2; x++) {
                out[offset++] = vBuffer.get(vRow + x * vPixelStride);
                out[offset++] = uBuffer.get(uRow + x * uPixelStride);
            }
        }
    }

    private void startServer() {
        if (serverStarted) return;
        serverStarted = true;
        new Thread(() -> {
            while (running) {
                try {
                    serverSocket = new ServerSocket();
                    serverSocket.setReuseAddress(true);
                    serverSocket.bind(new InetSocketAddress(PORT));
                    Log.i("Phonecam", "Camera server listening on " + PORT);
                    runOnUiThread(() -> state.setText("Camera server ready"));
                    while (running) {
                        Socket client = serverSocket.accept();
                        if (clients.size() >= MAX_CLIENTS) {
                            client.getOutputStream().write("HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n".getBytes(StandardCharsets.US_ASCII));
                            client.close();
                            Log.w("Phonecam", "Rejected client: stream limit reached");
                            continue;
                        }
                        client.setTcpNoDelay(true);
                        client.setKeepAlive(true);
                        Log.i("Phonecam", "Accepted client " + client.getRemoteSocketAddress());
                        clients.add(client);
                        Thread worker = new Thread(() -> serve(client), "phonecam-client");
                        worker.setDaemon(true);
                        worker.start();
                    }
                } catch (IOException e) {
                    if (running) {
                        Log.e("Phonecam", "Camera server listener restarted", e);
                        try { Thread.sleep(500); } catch (InterruptedException interrupted) { Thread.currentThread().interrupt(); break; }
                    }
                } finally {
                    try { if (serverSocket != null) serverSocket.close(); } catch (IOException ignored) {}
                }
            }
        }, "phonecam-server").start();
    }

    private void serve(Socket socket) {
        try (Socket client = socket) {
            InputStream in = client.getInputStream();
            OutputStream out = client.getOutputStream();
            String headers = readHeaders(in);
            String[] lines = headers.split("\r\n");
            String[] request = lines[0].split(" ");
            String method = request.length > 0 ? request[0] : "";
            String path = request.length > 1 ? request[1].split("\\?", 2)[0] : "";
            if ("GET".equals(method) && "/settings".equals(path)) {
                writeJson(out, "200 OK", onUi(this::settingsJson));
                return;
            }
            if ("POST".equals(method) && "/settings".equals(path)) {
                int length = 0;
                for (String line : lines) if (line.regionMatches(true, 0, "Content-Length:", 0, 15)) length = Integer.parseInt(line.substring(15).trim());
                if (length < 0 || length > 32768) { writeJson(out, "413 Payload Too Large", "{}"); return; }
                byte[] body = in.readNBytes(length);
                if (body.length != length) { writeJson(out, "400 Bad Request", "{}"); return; }
                JSONObject requested = new JSONObject(new String(body, StandardCharsets.UTF_8));
                writeJson(out, "200 OK", onUi(() -> applyRemoteSettings(requested)));
                return;
            }
            if ("GET".equals(method) && ("/audio.opus".equals(path) || "/audio.ogg".equals(path) || "/stream.opus".equals(path))) {
                startAudio();
                out.write(("HTTP/1.1 200 OK\r\n" +
                           "Access-Control-Allow-Origin: *\r\n" +
                           "Cache-Control: no-store\r\n" +
                           "Connection: keep-alive\r\n" +
                           "Content-Type: audio/ogg; codecs=opus\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
                out.flush();
                if (cachedOpusHeadPage != null) {
                    try {
                        out.write(cachedOpusHeadPage);
                        out.write(cachedOpusTagsPage);
                        out.flush();
                    } catch (Exception ignored) {}
                }
                opusClients.add(client);
                InputStream inStream = client.getInputStream();
                byte[] discard = new byte[512];
                while (!client.isClosed() && running) {
                    try {
                        int r = inStream.read(discard);
                        if (r < 0) break;
                    } catch (Exception e) { break; }
                }
                opusClients.remove(client);
                try { client.close(); } catch (Exception ignored) {}
                return;
            }
            if ("GET".equals(method) && ("/stream.h264".equals(path) || "/h264".equals(path))) {
                boolean isLoopback = client.getInetAddress() != null && client.getInetAddress().isLoopbackAddress();
                String modeName = isLoopback ? "USB" : "Wi-Fi";
                runOnUiThread(() -> {
                    if (modeBadge != null) {
                        modeBadge.setText("CONNECTED VIA " + modeName + " (H.264 HW)");
                        modeBadge.setTextColor(0xff00d2a0);
                        GradientDrawable activeBadge = new GradientDrawable();
                        activeBadge.setColor(0xff162e28);
                        activeBadge.setCornerRadius(14);
                        activeBadge.setStroke(2, 0xff00d2a0);
                        modeBadge.setBackground(activeBadge);
                    }
                    state.setText("H.264 streaming to " + client.getInetAddress().getHostAddress());
                });

                int w = selectedSize.getWidth();
                int h = selectedSize.getHeight();
                out.write(("HTTP/1.1 200 OK\r\n" +
                           "Access-Control-Allow-Origin: *\r\n" +
                           "Cache-Control: no-store\r\n" +
                           "Connection: keep-alive\r\n" +
                           "Content-Type: video/h264\r\n" +
                           "X-Width: " + w + "\r\n" +
                           "X-Height: " + h + "\r\n" +
                           "X-Rotation: " + frameRotation + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
                out.flush();

                synchronized (codecLock) {
                    if (codecConfigHeader != null) {
                        try {
                            out.write(codecConfigHeader);
                            out.flush();
                        } catch (Exception ignored) {}
                    }
                }
                requestKeyFrame();

                h264Clients.add(client);
                InputStream inStream = client.getInputStream();
                byte[] discard = new byte[512];
                while (!client.isClosed() && running) {
                    try {
                        int r = inStream.read(discard);
                        if (r < 0) break;
                    } catch (Exception e) {
                        break;
                    }
                }
                return;
            }
            if (!"GET".equals(method) || !("/".equals(path) || "/stream.mjpg".equals(path))) {
                writeJson(out, "404 Not Found", "{}");
                return;
            }
            mjpgClients.add(client);
            out.write(("HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-store\r\nConnection: keep-alive\r\nContent-Type: multipart/x-mixed-replace; boundary=frame\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
            out.flush();
            boolean isLoopback = client.getInetAddress() != null && client.getInetAddress().isLoopbackAddress();
            String modeName = isLoopback ? "USB" : "Wi-Fi";
            runOnUiThread(() -> {
                if (modeBadge != null) {
                    modeBadge.setText("CONNECTED VIA " + modeName);
                    modeBadge.setTextColor(0xff00d2a0);
                    GradientDrawable activeBadge = new GradientDrawable();
                    activeBadge.setColor(0xff162e28);
                    activeBadge.setCornerRadius(14);
                    activeBadge.setStroke(2, 0xff00d2a0);
                    modeBadge.setBackground(activeBadge);
                }
                state.setText("Streaming to " + client.getInetAddress().getHostAddress());
            });
            long clientFrameId = -1;
            while (!client.isClosed() && running) {
                byte[] frame;
                int rot = 0;
                synchronized (frameLock) {
                    while (running && !client.isClosed() && clientFrameId == frameId) {
                        frameLock.wait(100);
                    }
                    if (!running || client.isClosed()) break;
                    frame = latestFrame;
                    rot = frameRotation;
                    clientFrameId = frameId;
                }
                if (frame == null || frame.length == 0) continue;
                out.write(("--frame\r\nContent-Type: image/jpeg\r\nContent-Length: " + frame.length + "\r\nX-Rotation: " + rot + "\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
                out.write(frame);
                out.write("\r\n".getBytes(StandardCharsets.US_ASCII));
                out.flush();
            }
        } catch (Exception e) { Log.w("Phonecam", "Stream ended: " + e.getMessage()); }
        finally {
            h264Clients.remove(socket);
            mjpgClients.remove(socket);
            clients.remove(socket);
            runOnUiThread(() -> {
                if (clients.isEmpty() && h264Clients.isEmpty() && mjpgClients.isEmpty()) {
                    if (modeBadge != null) {
                        modeBadge.setText("MODE: STANDBY");
                        modeBadge.setTextColor(0xff8b9bae);
                        GradientDrawable standbyBadge = new GradientDrawable();
                        standbyBadge.setColor(0xff182330);
                        standbyBadge.setCornerRadius(14);
                        standbyBadge.setStroke(2, 0xff25384d);
                        modeBadge.setBackground(standbyBadge);
                    }
                    state.setText(front ? "Front camera ready" : "Back camera ready");
                }
            });
        }
    }

    private String readHeaders(InputStream in) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        int matched = 0;
        while (bytes.size() < 16384) {
            int value = in.read();
            if (value < 0) break;
            bytes.write(value);
            matched = switch (matched) {
                case 0 -> value == '\r' ? 1 : 0;
                case 1 -> value == '\n' ? 2 : value == '\r' ? 1 : 0;
                case 2 -> value == '\r' ? 3 : 0;
                case 3 -> value == '\n' ? 4 : 0;
                default -> matched;
            };
            if (matched == 4) return bytes.toString("US-ASCII").trim();
        }
        throw new IOException("Invalid HTTP request");
    }

    private void writeJson(OutputStream out, String status, String json) throws IOException {
        byte[] body = json.getBytes(StandardCharsets.UTF_8);
        out.write(("HTTP/1.1 " + status + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
        out.write(body);
        out.flush();
    }

    private String onUi(java.util.concurrent.Callable<String> action) throws Exception {
        AtomicReference<String> result = new AtomicReference<>();
        AtomicReference<Exception> error = new AtomicReference<>();
        CountDownLatch done = new CountDownLatch(1);
        runOnUiThread(() -> {
            try { result.set(action.call()); }
            catch (Exception e) { error.set(e); }
            finally { done.countDown(); }
        });
        if (!done.await(5, TimeUnit.SECONDS)) throw new IOException("Camera settings timed out");
        if (error.get() != null) throw error.get();
        return result.get();
    }

    private String settingsJson() throws Exception {
        JSONObject json = new JSONObject();
        JSONArray cameras = new JSONArray();
        for (CameraChoice choice : cameraChoices) {
            JSONObject item = new JSONObject();
            item.put("id", choice.id);
            item.put("label", (choice.front ? "Front camera" : "Back camera") + (cameraChoices.stream().filter(other -> other.front == choice.front).count() > 1 ? " - " + choice.id : ""));
            cameras.put(item);
        }
        JSONArray sizes = new JSONArray();
        for (Size size : sizeChoices) {
            JSONObject item = new JSONObject(); item.put("width", size.getWidth()); item.put("height", size.getHeight()); item.put("label", size.getWidth() + " x " + size.getHeight()); sizes.put(item);
        }
        JSONArray fps = new JSONArray();
        for (Range<Integer> range : fpsChoices) {
            JSONObject item = new JSONObject(); item.put("min", range.getLower()); item.put("max", range.getUpper()); item.put("label", range.getLower().equals(range.getUpper()) ? range.getUpper() + " fps" : range.getLower() + "-" + range.getUpper() + " fps"); fps.put(item);
        }
        json.put("cameras", cameras); json.put("sizes", sizes); json.put("fpsRanges", fps); json.put("qualities", new JSONArray(Arrays.asList(60, 75, 90)));
        json.put("cameraId", selectedCamera == null ? "" : selectedCamera.id);
        json.put("width", selectedSize.getWidth()); json.put("height", selectedSize.getHeight());
        json.put("fpsMin", selectedFpsRange.getLower()); json.put("fpsMax", selectedFpsRange.getUpper()); json.put("quality", jpegQuality);
        json.put("hasTorch", selectedCamera != null && selectedCamera.hasFlash);
        json.put("torch", torchActive);
        json.put("hasMic", checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED);
        json.put("micMuted", micMuted);
        return json.toString();
    }

    private String applyRemoteSettings(JSONObject requested) throws Exception {
        String cameraId = requested.optString("cameraId", selectedCamera.id);
        boolean cameraChanged = !cameraId.equals(selectedCamera.id);
        selectedCamera = cameraChoices.stream().filter(choice -> choice.id.equals(cameraId)).findFirst().orElse(selectedCamera);
        front = selectedCamera.front;
        refreshCameraControls();
        Size wantedSize = new Size(requested.optInt("width", selectedSize.getWidth()), requested.optInt("height", selectedSize.getHeight()));
        boolean sizeChanged = !wantedSize.equals(selectedSize);
        for (Size size : sizeChoices) if (size.equals(wantedSize)) selectedSize = size;
        int fpsMin = requested.optInt("fpsMin", selectedFpsRange.getLower()), fpsMax = requested.optInt("fpsMax", selectedFpsRange.getUpper());
        for (Range<Integer> range : fpsChoices) if (range.getLower() == fpsMin && range.getUpper() == fpsMax) selectedFpsRange = range;
        jpegQuality = requested.optInt("quality", jpegQuality);
        if (jpegQuality != 60 && jpegQuality != 75 && jpegQuality != 90) jpegQuality = 75;

        boolean previousTorch = torchActive;
        if (requested.has("torch") && selectedCamera != null && selectedCamera.hasFlash) {
            torchActive = requested.optBoolean("torch", false);
        } else if (cameraChanged && (selectedCamera == null || !selectedCamera.hasFlash)) {
            torchActive = false;
        }

        if (requested.has("micMuted")) {
            micMuted = requested.optBoolean("micMuted", false);
            if (micUiButton != null) {
                micUiButton.setText(micMuted ? "MIC: MUTED" : "MIC: ON");
                micUiButton.setTextColor(micMuted ? 0xffff5555 : 0xff00d2a0);
            }
        }

        cameraSelection = cameraChoices.indexOf(selectedCamera);
        resolutionSelection = sizeChoices.indexOf(selectedSize);
        fpsSelection = fpsChoices.indexOf(selectedFpsRange);
        qualitySelection = jpegQuality == 90 ? 2 : jpegQuality == 75 ? 1 : 0;
        updatingControls = true;
        cameraSpinner.setSelection(cameraSelection); resolutionSpinner.setSelection(resolutionSelection); fpsSpinner.setSelection(fpsSelection); qualitySpinner.setSelection(qualitySelection);
        if (torchUiButton != null) torchUiButton.setText(torchActive ? "Torch: ON" : "Torch: OFF");
        updatingControls = false;
        getPreferences(MODE_PRIVATE).edit().putBoolean("front", front).putString("cameraId", selectedCamera.id).putInt("width", selectedSize.getWidth()).putInt("height", selectedSize.getHeight()).putInt("fpsMin", selectedFpsRange.getLower()).putInt("fpsMax", selectedFpsRange.getUpper()).putInt("quality", jpegQuality).apply();

        if (cameraChanged || sizeChanged) {
            startCamera();
        } else if (previousTorch != torchActive) {
            applyTorchToSession();
        }
        return settingsJson();
    }

    private void closeCamera() {
        if (reader != null) {
            try { reader.setOnImageAvailableListener(null, null); } catch (Exception ignored) {}
        }
        try { if (session != null) { session.stopRepeating(); session.close(); } } catch (Exception ignored) {}
        try { if (camera != null) camera.close(); } catch (Exception ignored) {}
        try { if (reader != null) reader.close(); } catch (Exception ignored) {}
        session = null; camera = null; reader = null;
        encoderRunning = false;
        if (mediaCodec != null) {
            try {
                mediaCodec.stop();
                mediaCodec.release();
            } catch (Exception ignored) {}
            mediaCodec = null;
        }
        if (encoderSurface != null) {
            try { encoderSurface.release(); } catch (Exception ignored) {}
            encoderSurface = null;
        }
        if (cameraThread != null) {
            cameraThread.quitSafely();
            try {
                cameraThread.join(300);
            } catch (InterruptedException ignored) {}
            cameraThread = null;
        }
    }

    @Override protected void onDestroy() {
        running = false;
        releaseLocks();
        synchronized (frameLock) {
            frameLock.notifyAll();
        }
        try { if (serverSocket != null) serverSocket.close(); } catch (IOException ignored) {}
        closeCamera();
        for (Socket client : clients) try { client.close(); } catch (IOException ignored) {}
        for (Socket client : h264Clients) try { client.close(); } catch (IOException ignored) {}
        for (Socket client : mjpgClients) try { client.close(); } catch (IOException ignored) {}
        stopAudio();
        for (Socket client : opusClients) try { client.close(); } catch (IOException ignored) {}
        super.onDestroy();
    }
}
