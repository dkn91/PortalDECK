package app.portaldeck;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.Proxy;
import java.net.URL;

final class CompanionHttp {
    static final String ORIGIN = "http://127.0.0.1:8731";
    static final String ROOT_URL = ORIGIN + "/";
    static final String CSP = "default-src 'none'; "
            + "script-src " + ORIGIN + "/deck.js; style-src " + ORIGIN + "/deck.css; "
            + "connect-src " + ORIGIN + "/api/state " + ORIGIN + "/api/action; "
            + "frame-src 'none'; child-src 'none'; worker-src 'none'; "
            + "frame-ancestors 'none'; form-action 'none'; base-uri 'none'; object-src 'none'";
    private static final int TIMEOUT_MS = 1500;
    private static final int MAX_RESPONSE_BYTES = 2 * 1024 * 1024;

    enum Route {
        PAGE("/", "text/html"),
        SCRIPT("/deck.js", "application/javascript"),
        STYLE("/deck.css", "text/css"),
        STATE("/api/state", "application/json"),
        ACTION("/api/action", "application/json"),
        BLOCKED("", "text/plain");

        final String path;
        final String mime;

        Route(String path, String mime) {
            this.path = path;
            this.mime = mime;
        }
    }

    static Route route(String url, String method, boolean mainFrame) {
        if (mainFrame) {
            return "GET".equals(method) && ROOT_URL.equals(url) ? Route.PAGE : Route.BLOCKED;
        }
        if ("GET".equals(method)) {
            for (Route route : new Route[] {Route.SCRIPT, Route.STYLE, Route.STATE}) {
                if ((ORIGIN + route.path).equals(url)) {
                    return route;
                }
            }
        } else if ("POST".equals(method) && (ORIGIN + Route.ACTION.path).equals(url)) {
            return Route.ACTION;
        }
        return Route.BLOCKED;
    }

    static boolean isReady() {
        HttpURLConnection connection = null;
        try {
            connection = open(Route.PAGE, null);
            return connection.getResponseCode() == HttpURLConnection.HTTP_OK;
        } catch (IOException e) {
            return false;
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    static Response get(Route route, String authorization) throws IOException {
        HttpURLConnection connection = open(route, authorization);
        try {
            int status = connection.getResponseCode();
            // WebView cannot represent a 3xx WebResourceResponse; never follow or forward one.
            if (status < 200 || (status >= 300 && status < 400) || status > 599) {
                throw new IOException("Unexpected response status");
            }
            InputStream body = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
            if (body == null) {
                return new Response(status, new byte[0]);
            }
            try (InputStream stream = body) {
                return new Response(status, readLimited(stream, MAX_RESPONSE_BYTES));
            }
        } finally {
            connection.disconnect();
        }
    }

    static byte[] readLimited(InputStream stream, int limit) throws IOException {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = stream.read(buffer)) != -1) {
            if (count > limit - bytes.size()) {
                throw new IOException("Response too large");
            }
            bytes.write(buffer, 0, count);
        }
        return bytes.toByteArray();
    }

    private static HttpURLConnection open(Route route, String authorization) throws IOException {
        if (route == Route.ACTION || route == Route.BLOCKED) {
            throw new IllegalArgumentException("Not a GET route");
        }
        HttpURLConnection connection = (HttpURLConnection) new URL(ORIGIN + route.path)
                .openConnection(Proxy.NO_PROXY);
        connection.setRequestMethod("GET");
        connection.setInstanceFollowRedirects(false);
        connection.setConnectTimeout(TIMEOUT_MS);
        connection.setReadTimeout(TIMEOUT_MS);
        connection.setUseCaches(false);
        if (route == Route.STATE && authorization != null) {
            connection.setRequestProperty("Authorization", authorization);
        }
        return connection;
    }

    static final class Response {
        final int status;
        final byte[] body;

        Response(int status, byte[] body) {
            this.status = status;
            this.body = body;
        }
    }

    private CompanionHttp() { }
}
