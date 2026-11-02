package com.glassesxr.cardboardxr;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.webkit.CookieManager;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebViewClient;

import com.unity3d.player.UnityPlayer;

import java.util.concurrent.Callable;
import java.util.concurrent.FutureTask;

/**
 * Android counterpart to the iOS GlassesWebView plugin. The WebView is attached behind Unity and
 * periodically drawn into an RGBA bitmap for the floating browser screen's Unity texture.
 * All Android WebView operations run on the Activity UI thread.
 */
public final class GlassesWebViewBridge {
    private static Activity activity;
    private static FrameLayout container;
    private static android.webkit.WebView webView;
    private static Bitmap bitmap;
    private static Handler handler;
    private static Runnable captureTask;
    private static byte[] latestRgba;
    private static long frameNumber;
    private static long copiedFrameNumber;
    private static int width;
    private static int height;
    private static boolean loading;
    private static final Object frameLock = new Object();
    private static final int CAPTURE_INTERVAL_MS = 50;

    private GlassesWebViewBridge() { }

    private static <T> T onUiThread(Callable<T> action, T fallback) {
        Activity current = UnityPlayer.currentActivity;
        if (current == null)
            return fallback;
        if (Looper.myLooper() == Looper.getMainLooper()) {
            try { return action.call(); }
            catch (Exception e) { android.util.Log.e("CardboardXR.Web", "WebView UI operation failed", e); return fallback; }
        }
        FutureTask<T> task = new FutureTask<>(action);
        current.runOnUiThread(task);
        try { return task.get(); }
        catch (Exception e) { android.util.Log.e("CardboardXR.Web", "Timed out waiting for WebView UI thread", e); return fallback; }
    }

    public static int create(final int requestedWidth, final int requestedHeight) {
        return onUiThread(() -> {
            if (webView != null)
                destroyOnUiThread();
            activity = UnityPlayer.currentActivity;
            if (activity == null)
                return 0;
            width = requestedWidth;
            height = requestedHeight;
            frameNumber = 0;
            copiedFrameNumber = 0;
            latestRgba = null;

            View content = activity.findViewById(android.R.id.content);
            if (!(content instanceof ViewGroup))
                return 0;
            container = new FrameLayout(activity);
            container.setBackgroundColor(Color.BLACK);
            webView = new android.webkit.WebView(activity);
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
            webView.setWebViewClient(new WebViewClient());
            webView.setWebChromeClient(new WebChromeClient());
            container.addView(webView, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            // Insert at the back of Unity's content view. This keeps it attached and rendering while
            // Unity remains the visible, touch-handling view above it.
            ((ViewGroup) content).addView(container, 0, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            container.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);

            bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
            handler = new Handler(Looper.getMainLooper());
            captureTask = new Runnable() {
                @Override public void run() {
                    captureOnUiThread();
                    if (handler != null && webView != null)
                        handler.postDelayed(this, CAPTURE_INTERVAL_MS);
                }
            };
            handler.post(captureTask);
            android.util.Log.i("CardboardXR.Web", "Android WebView created " + width + "x" + height);
            return 1;
        }, 0);
    }

    private static void captureOnUiThread() {
        if (webView == null || bitmap == null || !webView.isAttachedToWindow())
            return;
        try {
            int w = View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY);
            int h = View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY);
            webView.measure(w, h);
            webView.layout(0, 0, width, height);
            Canvas canvas = new Canvas(bitmap);
            canvas.drawColor(Color.BLACK);
            webView.draw(canvas);
            int[] argb = new int[width * height];
            bitmap.getPixels(argb, 0, width, 0, 0, width, height);
            byte[] rgba = new byte[width * height * 4];
            for (int i = 0, p = 0; i < argb.length; i++) {
                int color = argb[i];
                rgba[p++] = (byte) ((color >> 16) & 0xff);
                rgba[p++] = (byte) ((color >> 8) & 0xff);
                rgba[p++] = (byte) (color & 0xff);
                rgba[p++] = (byte) ((color >>> 24) & 0xff);
            }
            synchronized (frameLock) {
                latestRgba = rgba;
                frameNumber++;
            }
        } catch (RuntimeException e) {
            android.util.Log.w("CardboardXR.Web", "Could not capture WebView frame", e);
        }
    }

    public static void load(final String url) {
        onUiThread(() -> { if (webView != null && url != null) webView.loadUrl(url); return null; }, null);
    }

    public static void evaluate(final String javascript) {
        onUiThread(() -> { if (webView != null && javascript != null) webView.evaluateJavascript(javascript, null); return null; }, null);
    }

    public static void back() {
        onUiThread(() -> { if (webView != null && webView.canGoBack()) webView.goBack(); return null; }, null);
    }

    public static void reload() {
        onUiThread(() -> { if (webView != null) webView.reload(); return null; }, null);
    }

    public static boolean isLoading() {
        return onUiThread(() -> loading, false);
    }

    /** Returns one fresh RGBA frame, or null until the next captured frame is available. */
    public static byte[] copyFrame() {
        synchronized (frameLock) {
            if (latestRgba == null || copiedFrameNumber == frameNumber)
                return null;
            copiedFrameNumber = frameNumber;
            return latestRgba.clone();
        }
    }

    public static void destroy() {
        onUiThread(() -> { destroyOnUiThread(); return null; }, null);
    }

    private static void destroyOnUiThread() {
        if (handler != null && captureTask != null)
            handler.removeCallbacks(captureTask);
        handler = null;
        captureTask = null;
        if (webView != null) {
            webView.stopLoading();
            webView.loadUrl("about:blank");
            webView.destroy();
            webView = null;
        }
        if (container != null && container.getParent() instanceof ViewGroup)
            ((ViewGroup) container.getParent()).removeView(container);
        container = null;
        if (bitmap != null) {
            bitmap.recycle();
            bitmap = null;
        }
        synchronized (frameLock) {
            latestRgba = null;
            frameNumber = 0;
            copiedFrameNumber = 0;
        }
        activity = null;
    }
}
