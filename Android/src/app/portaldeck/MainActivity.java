package app.portaldeck;

import android.app.Activity;
import android.graphics.Color;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.view.View;
import android.view.WindowManager;
import android.webkit.CookieManager;
import android.webkit.JavascriptInterface;
import android.webkit.RenderProcessGoneDetail;
import android.webkit.ServiceWorkerClient;
import android.webkit.ServiceWorkerController;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import javax.net.ssl.SSLException;

public final class MainActivity extends Activity {
    private static final long RETRY_MS = 2500;
    private static final int MAX_TOKEN_BYTES = 4096;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Runnable probeTask = this::probe;
    private FrameLayout content;
    private View waiting;
    private TextView status;
    private WebView dashboard;
    private DashboardClient dashboardClient;
    private boolean foreground;
    private boolean probing;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        immerse();

        WebView.setWebContentsDebuggingEnabled(false);
        CookieManager.getInstance().setAcceptCookie(false);
        ServiceWorkerController workers = ServiceWorkerController.getInstance();
        workers.getServiceWorkerWebSettings().setBlockNetworkLoads(true);
        workers.getServiceWorkerWebSettings().setAllowFileAccess(false);
        workers.getServiceWorkerWebSettings().setAllowContentAccess(false);
        workers.setServiceWorkerClient(new ServiceWorkerClient() {
            @Override
            public WebResourceResponse shouldInterceptRequest(WebResourceRequest request) {
                return denied();
            }
        });

        content = new FrameLayout(this);
        LinearLayout panel = new LinearLayout(this);
        panel.setBackgroundColor(Color.rgb(20, 24, 32));
        panel.setGravity(Gravity.CENTER);
        panel.setOrientation(LinearLayout.VERTICAL);
        int padding = Math.round(24 * getResources().getDisplayMetrics().density);
        panel.setPadding(padding, padding, padding, padding);
        status = new TextView(this);
        status.setTextColor(Color.WHITE);
        status.setTextSize(20);
        status.setGravity(Gravity.CENTER);
        panel.addView(status);
        Button retry = new Button(this);
        retry.setText(R.string.retry);
        retry.setOnClickListener(v -> probe());
        panel.addView(retry);
        waiting = panel;
        content.addView(waiting, fullScreen());
        setContentView(content);
        showWaiting(R.string.waiting_for_companion);
    }

    @Override
    protected void onResume() {
        super.onResume();
        foreground = true;
        immerse();
        if (dashboard != null) {
            dashboard.onResume();
        }
        probe();
    }

    @Override
    protected void onPause() {
        foreground = false;
        ui.removeCallbacks(probeTask);
        if (dashboard != null) {
            dashboard.onPause();
        }
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        foreground = false;
        ui.removeCallbacks(probeTask);
        worker.shutdownNow();
        removeDashboard();
        super.onDestroy();
    }

    @Override
    public void onWindowFocusChanged(boolean focused) {
        super.onWindowFocusChanged(focused);
        if (focused) {
            immerse();
        }
    }

    @SuppressWarnings("deprecation")  // Immersive mode on API 24.
    private void immerse() {
        getWindow().getDecorView().setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY | View.SYSTEM_UI_FLAG_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                        | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                        | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
    }

    private void probe() {
        if (!foreground || probing) {
            return;
        }
        ui.removeCallbacks(probeTask);
        probing = true;
        DashboardClient current = dashboardClient;
        worker.execute(() -> {
            CompanionHttp companion = current != null ? current.companion : readProvisioning();
            int message = R.string.waiting_for_pairing;
            if (companion != null) {
                try {
                    message = companion.isReady() ? 0 : R.string.waiting_for_companion;
                } catch (SSLException e) {
                    message = R.string.waiting_for_trusted_companion;
                } catch (IOException e) {
                    message = R.string.waiting_for_companion;
                }
            }
            int result = message;
            ui.post(() -> completeProbe(current, companion, result));
        });
    }

    private void completeProbe(DashboardClient current, CompanionHttp companion, int message) {
        probing = false;
        if (!foreground) {
            return;
        }
        if (message != 0) {
            showWaiting(message);
        } else if (current == null && dashboard == null) {
            openDashboard(companion);
        }
        ui.postDelayed(probeTask, RETRY_MS);
    }

    private CompanionHttp readProvisioning() {
        try {
            String token = readPrivateAscii("pairing-token", MAX_TOKEN_BYTES);
            for (int i = 0; i < token.length(); i++) {
                if (token.charAt(i) < '!' || token.charAt(i) > '~') {
                    return null;
                }
            }
            String pin = readPrivateAscii("companion-pin", 128);
            if (token.isEmpty() || !pin.matches("[0-9A-F]{64}")) {
                return null;
            }
            byte[] fingerprint = new byte[32];
            for (int i = 0; i < fingerprint.length; i++) {
                fingerprint[i] = (byte) Integer.parseInt(pin.substring(i * 2, i * 2 + 2), 16);
            }
            return new CompanionHttp(token, fingerprint);
        } catch (IOException | GeneralSecurityException e) {
            return null;
        }
    }

    private String readPrivateAscii(String name, int limit) throws IOException {
        try (FileInputStream input = new FileInputStream(new File(getFilesDir(), name))) {
            return new String(CompanionHttp.readLimited(input, limit),
                    StandardCharsets.US_ASCII).trim();
        }
    }

    @SuppressWarnings("deprecation")  // File URL controls still matter on older WebViews.
    private void openDashboard(CompanionHttp companion) {
        status.setText(R.string.loading_dashboard);
        waiting.setVisibility(View.VISIBLE);
        WebView view = new WebView(this);
        dashboard = view;
        view.setSaveEnabled(false);
        WebSettings settings = view.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setJavaScriptCanOpenWindowsAutomatically(false);
        settings.setSupportMultipleWindows(false);
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setAllowFileAccessFromFileURLs(false);
        settings.setAllowUniversalAccessFromFileURLs(false);
        settings.setDomStorageEnabled(false);
        settings.setDatabaseEnabled(false);
        settings.setGeolocationEnabled(false);
        settings.setSaveFormData(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        settings.setCacheMode(WebSettings.LOAD_NO_CACHE);
        settings.setBlockNetworkLoads(true);
        settings.setMediaPlaybackRequiresUserGesture(true);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            view.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
            settings.setSafeBrowsingEnabled(false);
        }
        CookieManager.getInstance().setAcceptThirdPartyCookies(view, false);
        view.setDownloadListener((url, agent, disposition, mime, size) -> { });
        dashboardClient = new DashboardClient(companion);
        view.setWebViewClient(dashboardClient);
        view.addJavascriptInterface(new ActionBridge(dashboardClient), "PortalNative");
        content.addView(view, 0, fullScreen());
        view.loadUrl(CompanionHttp.ROOT_URL);
    }

    private static FrameLayout.LayoutParams fullScreen() {
        return new FrameLayout.LayoutParams(FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT);
    }

    private void showWaiting(int message) {
        removeDashboard();
        status.setText(message);
        waiting.setVisibility(View.VISIBLE);
    }

    private void removeDashboard() {
        WebView view = dashboard;
        dashboard = null;
        if (dashboardClient != null) {
            dashboardClient.active = false;
            dashboardClient = null;
        }
        if (view != null) {
            content.removeView(view);
            view.destroy();
        }
    }

    private void dashboardFailed(WebView view) {
        ui.post(() -> {
            if (dashboard == view) {
                showWaiting(R.string.waiting_for_companion);
            }
        });
    }

    private static WebResourceResponse denied() {
        return response("text/plain", 403, new byte[0], false);
    }

    private static WebResourceResponse response(String mime, int status, byte[] body, boolean page) {
        Map<String, String> headers = new HashMap<>();
        headers.put("Cache-Control", "no-store");
        headers.put("X-Content-Type-Options", "nosniff");
        if (page) {
            headers.put("Content-Security-Policy", CompanionHttp.CSP);
            headers.put("X-Frame-Options", "DENY");
            headers.put("Referrer-Policy", "no-referrer");
            headers.put("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
        }
        return new WebResourceResponse(mime, "UTF-8", status, status < 300 ? "OK" : "Rejected",
                headers, new ByteArrayInputStream(body));
    }

    private final class DashboardClient extends WebViewClient {
        private final CompanionHttp companion;
        private volatile boolean active = true;
        private volatile boolean pageServed;

        DashboardClient(CompanionHttp companion) {
            this.companion = companion;
        }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            // The one app-initiated loadUrl is not passed to this callback.
            return true;
        }

        @Override
        @SuppressWarnings("deprecation")
        public boolean shouldOverrideUrlLoading(WebView view, String url) {
            return true;
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest request) {
            String url = request.getUrl().toString();
            CompanionHttp.Route route = CompanionHttp.route(url, request.getMethod(),
                    request.isForMainFrame());
            if (!active || route == CompanionHttp.Route.BLOCKED || request.isRedirect()) {
                return denied();
            }
            try {
                CompanionHttp.Response result = companion.get(route);
                if (route == CompanionHttp.Route.PAGE) {
                    pageServed = result.status == 200;
                }
                if (route != CompanionHttp.Route.STATE && result.status != 200) {
                    dashboardFailed(view);
                }
                return response(route.mime, result.status, result.body,
                        route == CompanionHttp.Route.PAGE);
            } catch (IOException e) {
                if (route == CompanionHttp.Route.PAGE || route == CompanionHttp.Route.SCRIPT
                        || route == CompanionHttp.Route.STYLE) {
                    dashboardFailed(view);
                }
                return response(route.mime, 503, new byte[0], route == CompanionHttp.Route.PAGE);
            }
        }

        @Override
        public void onPageFinished(WebView view, String url) {
            if (dashboard == view && pageServed && CompanionHttp.ROOT_URL.equals(url)) {
                waiting.setVisibility(View.GONE);
            }
        }

        @Override
        public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
            if (request.isForMainFrame()) {
                dashboardFailed(view);
            }
        }

        @Override
        public boolean onRenderProcessGone(WebView view, RenderProcessGoneDetail detail) {
            if (dashboard == view) {
                showWaiting(R.string.waiting_for_companion);
            }
            return true;
        }
    }

    public static final class ActionBridge {
        private static final String UNAVAILABLE =
                "{\"ok\":false,\"message\":\"Action result unavailable. Check the desktop before retrying.\"}";
        private final DashboardClient client;

        private ActionBridge(DashboardClient client) {
            this.client = client;
        }

        @JavascriptInterface
        public String sendAction(String action) {
            if (!client.active || !client.pageServed) {
                return UNAVAILABLE;
            }
            try {
                // The bridge runs off the Android UI thread. Only this fixed pinned POST is exposed.
                CompanionHttp.Response result = client.companion.sendAction(action);
                if (result.status == 200) {
                    return new String(result.body, StandardCharsets.UTF_8);
                }
            } catch (IllegalArgumentException e) {
                return "{\"ok\":false,\"message\":\"Unsupported action.\"}";
            } catch (IOException e) {
                return UNAVAILABLE;
            }
            return UNAVAILABLE;
        }
    }
}
