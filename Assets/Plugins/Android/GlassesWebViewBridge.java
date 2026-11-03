package com.glassesxr.cardboardxr;

import android.app.Activity;
import android.app.Presentation;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.Looper;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import android.view.Display;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.webkit.CookieManager;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebViewClient;

import com.unity3d.player.UnityPlayer;

import java.nio.ByteBuffer;
import java.util.ArrayList;
import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;

/** Android browser bridge. A hardware-rendered WebView display is copied into RGBA frames for Unity. */
public final class GlassesWebViewBridge {
    private static Activity activity;
    private static FrameLayout container;
    private static android.webkit.WebView webView;
    private static Presentation presentation;
    private static VirtualDisplay virtualDisplay;
    private static ImageReader imageReader;
    private static SpeechRecognizer speechRecognizer;
    private static String latestVoiceResult;
    private static Handler handler;
    private static Runnable captureTask;
    private static byte[] latestRgba;
    private static long frameNumber;
    private static long copiedFrameNumber;
    private static int width;
    private static int height;
    private static volatile boolean loading;
    private static final Object frameLock = new Object();
    private static final int CAPTURE_INTERVAL_MS = 50;

    private GlassesWebViewBridge() { }

    private static <T> T onUiThread(Callable<T> action, T fallback) {
        Activity current = UnityPlayer.currentActivity;
        if (current == null) return fallback;
        if (Looper.myLooper() == Looper.getMainLooper()) {
            try { return action.call(); }
            catch (Exception e) { android.util.Log.e("CardboardXR.Web", "WebView UI operation failed", e); return fallback; }
        }
        FutureTask<T> task = new FutureTask<>(action);
        current.runOnUiThread(task);
        try { return task.get(); }
        catch (Exception e) { android.util.Log.e("CardboardXR.Web", "Waiting for WebView UI thread failed", e); return fallback; }
    }

    public static int create(final int requestedWidth, final int requestedHeight) {
        return onUiThread(() -> {
            if (webView != null) destroyOnUiThread();
            activity = UnityPlayer.currentActivity;
            if (activity == null || requestedWidth <= 0 || requestedHeight <= 0) return 0;
            width = requestedWidth;
            height = requestedHeight;
            frameNumber = copiedFrameNumber = 0;
            latestRgba = null;
            loading = false;

            imageReader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
            DisplayManager displayManager = (DisplayManager) activity.getSystemService(Activity.DISPLAY_SERVICE);
            int densityDpi = activity.getResources().getDisplayMetrics().densityDpi;
            virtualDisplay = displayManager.createVirtualDisplay(
                    "CardboardXR.Web", width, height, densityDpi, imageReader.getSurface(),
                    DisplayManager.VIRTUAL_DISPLAY_FLAG_OWN_CONTENT_ONLY |
                            DisplayManager.VIRTUAL_DISPLAY_FLAG_PRESENTATION);
            if (virtualDisplay == null) {
                imageReader.close();
                imageReader = null;
                return 0;
            }

            Display display = virtualDisplay.getDisplay();
            presentation = new Presentation(activity, display);
            container = new FrameLayout(presentation.getContext());
            container.setBackgroundColor(Color.BLACK);
            webView = new android.webkit.WebView(presentation.getContext());
            webView.setFocusable(true);
            webView.setFocusableInTouchMode(true);
            WebSettings settings = webView.getSettings();
            settings.setJavaScriptEnabled(true);
            settings.setDomStorageEnabled(true);
            settings.setMediaPlaybackRequiresUserGesture(false);
            settings.setLoadWithOverviewMode(true);
            settings.setUseWideViewPort(true);
            settings.setBuiltInZoomControls(false);
            settings.setDisplayZoomControls(false);
            CookieManager.getInstance().setAcceptCookie(true);
            webView.setWebViewClient(new WebViewClient() {
                // Do not hand YouTube/video URLs to another activity. Let WebView navigate in-app.
                @Override public void onPageStarted(android.webkit.WebView view, String url, Bitmap icon) { loading = true; }
                @Override public void onPageFinished(android.webkit.WebView view, String url) { loading = false; }
                @Override public void onReceivedError(android.webkit.WebView view, int code, String description, String failingUrl) {
                    loading = false;
                    android.util.Log.w("CardboardXR.Web", "Page load error: " + description);
                }
            });
            webView.setWebChromeClient(new WebChromeClient() {
                private View fullScreenView;
                private CustomViewCallback fullScreenCallback;

                @Override public void onShowCustomView(View view, CustomViewCallback callback) {
                    if (container == null || view == null) return;
                    if (fullScreenView != null) {
                        callback.onCustomViewHidden();
                        return;
                    }
                    fullScreenView = view;
                    fullScreenCallback = callback;
                    container.removeAllViews();
                    container.addView(view, new FrameLayout.LayoutParams(
                            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
                }

                @Override public void onHideCustomView() {
                    if (container == null || fullScreenView == null) return;
                    container.removeView(fullScreenView);
                    fullScreenView = null;
                    fullScreenCallback = null;
                    if (webView != null)
                        container.addView(webView, new FrameLayout.LayoutParams(
                                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
                }
            });
            container.addView(webView, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            presentation.setContentView(container);
            presentation.show();
            container.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);

            handler = new Handler(Looper.getMainLooper());
            captureTask = new Runnable() {
                @Override public void run() {
                    captureOnUiThread();
                    if (handler != null && webView != null) handler.postDelayed(this, CAPTURE_INTERVAL_MS);
                }
            };
            handler.post(captureTask);
            android.util.Log.i("CardboardXR.Web", "Android WebView created " + width + "x" + height);
            return 1;
        }, 0);
    }

    private static void captureOnUiThread() {
        if (imageReader == null) return;
        Image image = null;
        try {
            image = imageReader.acquireLatestImage();
            if (image == null) return;
            Image.Plane plane = image.getPlanes()[0];
            ByteBuffer pixels = plane.getBuffer();
            int base = pixels.position();
            int rowStride = plane.getRowStride();
            int pixelStride = plane.getPixelStride();
            byte[] rgba = new byte[width * height * 4];
            for (int y = 0; y < height; y++) {
                int sourceY = height - 1 - y;
                int source = base + sourceY * rowStride;
                int target = y * width * 4;
                for (int x = 0; x < width; x++) {
                    int pixel = source + x * pixelStride;
                    rgba[target++] = pixels.get(pixel);
                    rgba[target++] = pixels.get(pixel + 1);
                    rgba[target++] = pixels.get(pixel + 2);
                    rgba[target++] = pixels.get(pixel + 3);
                }
            }
            synchronized (frameLock) { latestRgba = rgba; frameNumber++; }
        } catch (RuntimeException e) {
            android.util.Log.w("CardboardXR.Web", "Could not capture WebView frame", e);
        } finally {
            if (image != null) image.close();
        }
    }

    /** Dispatch a touch in the same top-left coordinate space as the captured page. */
    public static void tap(final float x, final float y) {
        onUiThread(() -> {
            if (webView == null) return null;
            webView.requestFocus();
            long now = android.os.SystemClock.uptimeMillis();
            float sx = width == 0 ? 1f : (float) webView.getWidth() / width;
            float sy = height == 0 ? 1f : (float) webView.getHeight() / height;
            float px = Math.max(0, Math.min(webView.getWidth() - 1, x * sx));
            float py = Math.max(0, Math.min(webView.getHeight() - 1, y * sy));
            MotionEvent down = MotionEvent.obtain(now, now, MotionEvent.ACTION_DOWN, px, py, 0);
            MotionEvent up = MotionEvent.obtain(now, now + 60, MotionEvent.ACTION_UP, px, py, 0);
            webView.dispatchTouchEvent(down);
            webView.dispatchTouchEvent(up);
            down.recycle();
            up.recycle();
            return null;
        }, null);
    }

    /** Returns launchable installed apps as package-name + tab + display-label records. */
    public static String[] getLaunchableApps() {
        return onUiThread(() -> {
            if (activity == null) return new String[0];
            Intent query = new Intent(Intent.ACTION_MAIN, null);
            query.addCategory(Intent.CATEGORY_LAUNCHER);
            java.util.List<android.content.pm.ResolveInfo> matches =
                    activity.getPackageManager().queryIntentActivities(query, 0);
            java.util.Collections.sort(matches, (a, b) ->
                    String.valueOf(a.loadLabel(activity.getPackageManager())).compareToIgnoreCase(
                            String.valueOf(b.loadLabel(activity.getPackageManager()))));
            java.util.ArrayList<String> apps = new java.util.ArrayList<>();
            java.util.HashSet<String> seenPackages = new java.util.HashSet<>();
            String ownPackage = activity.getPackageName();
            for (android.content.pm.ResolveInfo info : matches) {
                if (info.activityInfo == null || ownPackage.equals(info.activityInfo.packageName)) continue;
                String packageName = info.activityInfo.packageName;
                if (!seenPackages.add(packageName)) continue;
                String label = String.valueOf(info.loadLabel(activity.getPackageManager()));
                apps.add(packageName + "\t" + label);
            }
            return apps.toArray(new String[0]);
        }, new String[0]);
    }

    /** Launches an explicitly selected installed app. */
    public static boolean launchApp(final String packageName) {
        return onUiThread(() -> {
            if (activity == null || packageName == null || packageName.isEmpty()) return false;
            Intent launch = activity.getPackageManager().getLaunchIntentForPackage(packageName);
            if (launch == null) return false;
            launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            activity.startActivity(launch);
            return true;
        }, false);
    }

    public static void load(final String url) {
        onUiThread(() -> { if (webView != null && url != null) webView.loadUrl(url); return null; }, null);
    }
    public static void evaluate(final String javascript) {
        onUiThread(() -> { if (webView != null && javascript != null) webView.evaluateJavascript(javascript, null); return null; }, null);
    }

    /** Starts Android's system voice recognizer. Results are polled by Unity with consumeVoiceResult. */
    public static void startVoiceSearch() {
        onUiThread(() -> {
            if (activity == null || activity.checkSelfPermission(android.Manifest.permission.RECORD_AUDIO)
                    != PackageManager.PERMISSION_GRANTED) {
                android.util.Log.w("CardboardXR.Web", "Microphone permission is required for voice search");
                return null;
            }
            if (!SpeechRecognizer.isRecognitionAvailable(activity)) {
                android.util.Log.w("CardboardXR.Web", "No speech recognizer is available on this device");
                return null;
            }
            if (speechRecognizer != null) {
                speechRecognizer.cancel();
                speechRecognizer.destroy();
            }
            speechRecognizer = SpeechRecognizer.createSpeechRecognizer(activity);
            speechRecognizer.setRecognitionListener(new RecognitionListener() {
                @Override public void onReadyForSpeech(android.os.Bundle params) { }
                @Override public void onBeginningOfSpeech() { }
                @Override public void onRmsChanged(float rmsdB) { }
                @Override public void onBufferReceived(byte[] buffer) { }
                @Override public void onEndOfSpeech() { }
                @Override public void onError(int error) {
                    android.util.Log.w("CardboardXR.Web", "Voice search ended: " + error);
                }
                @Override public void onResults(android.os.Bundle results) {
                    ArrayList<String> matches = results.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION);
                    if (matches != null && !matches.isEmpty()) {
                        synchronized (GlassesWebViewBridge.class) { latestVoiceResult = matches.get(0); }
                    }
                }
                @Override public void onPartialResults(android.os.Bundle partialResults) { }
                @Override public void onEvent(int eventType, android.os.Bundle params) { }
            });
            Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
            intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
            intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1);
            speechRecognizer.startListening(intent);
            return null;
        }, null);
    }

    public static String consumeVoiceResult() {
        synchronized (GlassesWebViewBridge.class) {
            String result = latestVoiceResult;
            latestVoiceResult = null;
            return result;
        }
    }
    public static void back() { onUiThread(() -> { if (webView != null && webView.canGoBack()) webView.goBack(); return null; }, null); }
    public static void reload() { onUiThread(() -> { if (webView != null) webView.reload(); return null; }, null); }
    public static boolean isLoading() { return loading; }

    public static byte[] copyFrame() {
        synchronized (frameLock) {
            if (latestRgba == null || copiedFrameNumber == frameNumber) return null;
            copiedFrameNumber = frameNumber;
            return latestRgba.clone();
        }
    }

    public static void destroy() { onUiThread(() -> { destroyOnUiThread(); return null; }, null); }
    private static void destroyOnUiThread() {
        loading = false;
        if (handler != null && captureTask != null) handler.removeCallbacks(captureTask);
        handler = null;
        captureTask = null;
        if (container != null && webView != null) container.removeView(webView);
        if (presentation != null) presentation.dismiss();
        presentation = null;
        if (webView != null) {
            webView.stopLoading();
            webView.loadUrl("about:blank");
            webView.destroy();
            webView = null;
        }
        if (speechRecognizer != null) {
            speechRecognizer.cancel();
            speechRecognizer.destroy();
            speechRecognizer = null;
        }
        container = null;
        if (virtualDisplay != null) { virtualDisplay.release(); virtualDisplay = null; }
        if (imageReader != null) { imageReader.close(); imageReader = null; }
        synchronized (frameLock) { latestRgba = null; frameNumber = copiedFrameNumber = 0; }
        activity = null;
    }
}
