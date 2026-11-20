package app.portaldeck;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.Proxy;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

final class CompanionHttp {
    static final String ORIGIN = "https://127.0.0.1:8731";
    static final String ROOT_URL = ORIGIN + "/";
    static final String CSP = "default-src 'none'; "
            + "script-src " + ORIGIN + "/deck.js; style-src " + ORIGIN + "/deck.css; "
            + "connect-src " + ORIGIN + "/api/state; "
            + "frame-src 'none'; child-src 'none'; worker-src 'none'; "
            + "frame-ancestors 'none'; form-action 'none'; base-uri 'none'; object-src 'none'";
    private static final int CONNECT_TIMEOUT_MS = 1000;
    private static final int READ_TIMEOUT_MS = 2000;
    private static final int MAX_RESPONSE_BYTES = 2 * 1024 * 1024;
    private static final int MAX_ACTION_RESPONSE_BYTES = 8 * 1024;
    private final String authorization;
    private final SSLSocketFactory socketFactory;

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
        if (!"GET".equals(method)) {
            return Route.BLOCKED;
        }
        if (mainFrame) {
            return ROOT_URL.equals(url) ? Route.PAGE : Route.BLOCKED;
        }
        if ((ORIGIN + Route.SCRIPT.path).equals(url)) {
            return Route.SCRIPT;
        }
        if ((ORIGIN + Route.STYLE.path).equals(url)) {
            return Route.STYLE;
        }
        return (ORIGIN + Route.STATE.path).equals(url) ? Route.STATE : Route.BLOCKED;
    }

    CompanionHttp(String token, byte[] pin) throws GeneralSecurityException {
        authorization = "Bearer " + token;
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[] {new LeafPinTrustManager(pin)}, null);
        socketFactory = context.getSocketFactory();
    }

    boolean isReady() throws IOException {
        HttpsURLConnection connection = open(Route.PAGE);
        try {
            return connection.getResponseCode() == HttpsURLConnection.HTTP_OK;
        } finally {
            connection.disconnect();
        }
    }

    Response get(Route route) throws IOException {
        if (route == Route.ACTION) {
            throw new IllegalArgumentException("Not a GET route");
        }
        return request(route, null, MAX_RESPONSE_BYTES);
    }

    Response sendAction(String action) throws IOException {
        // Even trusted dashboard code cannot use this bridge to send arbitrary payloads.
        if (action == null) {
            throw new IllegalArgumentException("Invalid action");
        }
        switch (action) {
            case "mic": case "outlook": case "teams": case "chrome": case "edge":
            case "youtube": case "terminal": case "camera": case "lock":
                byte[] body = ("{\"action\":\"" + action + "\"}").getBytes(StandardCharsets.UTF_8);
                return request(Route.ACTION, body, MAX_ACTION_RESPONSE_BYTES);
            default:
                throw new IllegalArgumentException("Invalid action");
        }
    }

    private Response request(Route route, byte[] payload, int maxBytes) throws IOException {
        HttpsURLConnection connection = open(route);
        try {
            if (payload != null) {
                connection.setDoOutput(true);
                connection.setFixedLengthStreamingMode(payload.length);
                connection.setRequestProperty("Content-Type", "application/json");
                connection.setRequestProperty("X-PortalDeck-Native", "1");
                try (OutputStream output = connection.getOutputStream()) {
                    output.write(payload);
                }
            }
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
                return new Response(status, readLimited(stream, maxBytes));
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

    private HttpsURLConnection open(Route route) throws IOException {
        if (route == Route.BLOCKED) {
            throw new IllegalArgumentException("Not a companion route");
        }
        HttpsURLConnection connection = (HttpsURLConnection) new URL(ORIGIN + route.path)
                .openConnection(Proxy.NO_PROXY);
        // Retain HttpsURLConnection's platform verifier, including IP SAN checking.
        connection.setSSLSocketFactory(socketFactory);
        connection.setRequestMethod(route == Route.ACTION ? "POST" : "GET");
        connection.setInstanceFollowRedirects(false);
        connection.setConnectTimeout(CONNECT_TIMEOUT_MS);
        connection.setReadTimeout(READ_TIMEOUT_MS);
        connection.setUseCaches(false);
        if (route == Route.STATE || route == Route.ACTION) {
            connection.setRequestProperty("Authorization", authorization);
        }
        return connection;
    }

    private static final class LeafPinTrustManager implements X509TrustManager {
        private final byte[] pin;

        LeafPinTrustManager(byte[] pin) {
            if (pin.length != 32) {
                throw new IllegalArgumentException("Expected SHA-256 certificate pin");
            }
            this.pin = pin.clone();
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType)
                throws CertificateException {
            if (chain == null || chain.length == 0) {
                throw new CertificateException("Missing companion certificate");
            }
            X509Certificate leaf = chain[0];
            leaf.checkValidity();
            try {
                byte[] actual = MessageDigest.getInstance("SHA-256").digest(leaf.getEncoded());
                if (!MessageDigest.isEqual(pin, actual)) {
                    throw new CertificateException("Companion certificate pin mismatch");
                }
            } catch (java.security.NoSuchAlgorithmException e) {
                throw new CertificateException(e);
            }
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType)
                throws CertificateException {
            throw new CertificateException("Client authentication not supported");
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return new X509Certificate[0];
        }
    }

    static final class Response {
        final int status;
        final byte[] body;

        Response(int status, byte[] body) {
            this.status = status;
            this.body = body;
        }
    }

}
