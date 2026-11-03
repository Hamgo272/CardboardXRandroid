package com.glassesxr.cardboardxr;

import android.app.Activity;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.os.Handler;
import android.os.Looper;
import android.view.MotionEvent;
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

/** Android browser bridge. Hidden WebView renders into bottom-up RGBA frames for Unity. */
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
            View content = activity.findViewById(android.R.id.content);
            if (!(content instanceof ViewGroup)) return 0;

            container = new FrameLayout(activity);
            container.setBackgroundColor(Color.BLACK);
            webView = new android.webkit.WebView(activity);
            webView.setFocusable(true);
            webView.setFocusableInTouchMode(true);
            webView.setLayerType(View.LAYER_TYPE_SOFTWARE, null);
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
            webView.setWebChromeClient(new WebChromeClient());
            container.addView(webView, new FrameLayout.LayoutParams(width, height));
            ((ViewGroup) content).addView(container, 0, new ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
            container.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO_HIDE_DESCENDANTS);

            bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
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
        if (webView == null || bitmap == null || !webView.isAttachedToWindow()) return;
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
            for (int y = 0; y < height; y++) {
                int source = (height - 1 - y) * width;
                int target = y * width * 4;
                for (int x = 0; x < width; x++) {
                    int color = argb[source + x];
                    rgba[target++] = (byte) ((color >> 16) & 0xff);
                    rgba[target++] = (byte) ((color >> 8) & 0xff);
                    rgba[target++] = (byte) (color & 0xff);
                    rgba[target++] = (byte) ((color >>> 24) & 0xff);
                }
            }
            synchronized (frameLock) { latestRgba = rgba; frameNumber++; }
        } catch (RuntimeException e) {
            android.util.Log.w("CardboardXR.Web", "Could not capture WebView frame", e);
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

    public static void load(final String url) {
        onUiThread(() -> { if (webView != null && url != null) webView.loadUrl(url); return null; }, null);
    }
    public static void evaluate(final String javascript) {
        onUiThread(() -> { if (webView != null && javascript != null) webView.evaluateJavascript(javascript, null); return null; }, null);
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
        if (webView != null) {
            webView.stopLoading();
            webView.loadUrl("about:blank");
            webView.destroy();
            webView = null;
        }
        if (container != null && container.getParent() instanceof ViewGroup)
            ((ViewGroup) container.getParent()).removeView(container);
        container = null;
        if (bitmap != null) { bitmap.recycle(); bitmap = null; }
        synchronized (frameLock) { latestRgba = null; frameNumber = copiedFrameNumber = 0; }
        activity = null;
    }
}
