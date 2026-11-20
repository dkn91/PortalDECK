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

import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

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
        boolean needsToken = dashboard == null;
        worker.execute(() -> {
            String token = needsToken ? readToken() : null;
            boolean ready = (!needsToken || token != null) && CompanionHttp.isReady();
            ui.post(() -> {
                probing = false;
                if (!foreground) {
                    return;
                }
                if (!ready) {
                    showWaiting(needsToken && token == null ? R.string.waiting_for_pairing
                            : R.string.waiting_for_companion);
                } else if (dashboard == null && token != null) {
                    openDashboard(token);
                }
                ui.postDelayed(probeTask, RETRY_MS);
            });
        });
    }

    private String readToken() {
        try (FileInputStream input = new FileInputStream(new File(getFilesDir(), "pairing-token"))) {
            String token = new String(CompanionHttp.readLimited(input, MAX_TOKEN_BYTES),
                    StandardCharsets.US_ASCII).trim();
            if (token.isEmpty()) {
                return null;
            }
            for (int i = 0; i < token.length(); i++) {
                if (token.charAt(i) < '!' || token.charAt(i) > '~') {
                    return null;
                }
            }
            return token;
        } catch (IOException e) {
            return null;
        }
    }

    @SuppressWarnings("deprecation")  // File URL controls still matter on older WebViews.
    private void openDashboard(String token) {
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
        settings.setMediaPlaybackRequiresUserGesture(true);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            view.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
            settings.setSafeBrowsingEnabled(false);
        }
        CookieManager.getInstance().setAcceptThirdPartyCookies(view, false);
        view.setDownloadListener((url, agent, disposition, mime, size) -> { });
        view.setWebViewClient(new DashboardClient(token));
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
        }
        return new WebResourceResponse(mime, "UTF-8", status, status < 300 ? "OK" : "Rejected",
                headers, new ByteArrayInputStream(body));
    }

    private final class DashboardClient extends WebViewClient {
        private String token;
        private volatile boolean pageServed;

        DashboardClient(String token) {
            this.token = token;
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
            if (route == CompanionHttp.Route.BLOCKED || request.isRedirect()) {
                return denied();
            }
            if (route == CompanionHttp.Route.ACTION) {
                // Android does not expose a POST body here. The dashboard fetch uses
                // redirect:'error', and the document CSP also blocks off-origin connections.
                // Framework WebView uses Chromium's implicit proxy bypass for loopback.
                return pageServed ? null : denied();
            }
            try {
                String authorization = null;
                if (route == CompanionHttp.Route.STATE) {
                    for (Map.Entry<String, String> entry : request.getRequestHeaders().entrySet()) {
                        if ("Authorization".equalsIgnoreCase(entry.getKey())) {
                            authorization = entry.getValue();
                            break;
                        }
                    }
                }
                CompanionHttp.Response result = CompanionHttp.get(route, authorization);
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
            if (dashboard != view || !pageServed || !CompanionHttp.ROOT_URL.equals(url)
                    || token == null) {
                return;
            }
            // evaluateJavascript runs in the main document and exposes no native Java methods.
            String quoted = JSONObject.quote(token);
            token = null;
            view.evaluateJavascript("(function(){"
                    + "if(window!==window.top||location.href!=='" + CompanionHttp.ROOT_URL
                    + "')return false;"
                    + "Object.defineProperty(window,'PortalPairing',{value:Object.freeze({"
                    + "token:function(){return " + quoted + ";}})});"
                    + "window.dispatchEvent(new Event('portal-pairing-ready'));return true;})()",
                    result -> {
                        if (dashboard == view && "true".equals(result)) {
                            waiting.setVisibility(View.GONE);
                        } else {
                            dashboardFailed(view);
                        }
                    });
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
}
