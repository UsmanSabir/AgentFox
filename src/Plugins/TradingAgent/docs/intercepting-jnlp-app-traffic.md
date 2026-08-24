# Reverse-Engineering a Java Web Start Trading App's Network Traffic

Guide for capturing and decoding traffic from a JNLP-launched Java application (e.g. `https://online.ahletrade.com/TradeCast/launch.jnlp`) in order to build a custom client against its API.

---

## 0. Before you start

- Check whether the broker offers an official API for algo/API trading. Some brokers provide this on request even if it isn't advertised publicly. This saves the reverse-engineering work and avoids ToS ambiguity.
- Everything below is for understanding a protocol you're already authorized to access (your own trading account). Don't use this against systems or accounts that aren't yours.

---

## 1. Inspect the JNLP file and jars

The `.jnlp` file is just XML — it tells you the codebase URL, jars, and main class.

```bash
curl -o launch.jnlp "https://online.ahletrade.com/TradeCast/launch.jnlp"
cat launch.jnlp
```

Look for:
- `<jar href="...">` entries under `<resources>` — download each one
- `<application-desc main-class="...">` — this is your entry point for decompiling

Download all referenced jars:

```bash
mkdir tradecast-jars && cd tradecast-jars
# repeat for each jar href found in the jnlp
curl -O "https://online.ahletrade.com/TradeCast/<jarname>.jar"
```

### Decompile

Use one of:
- [vineflower](https://github.com/Vineflower/vineflower) (actively maintained fork of Fernflower)
- [cfr](https://www.benf.org/other/cfr/)
- [jd-gui](https://github.com/java-decompiler/jd-gui) (GUI, good for browsing)

```bash
java -jar vineflower.jar tradecast.jar output-src/
```

Read through the decompiled source for:
- API base URLs / hostnames
- Request/response classes (field names = your future JSON/params)
- Whether it's REST/HTTP or a raw socket protocol
- Any hardcoded auth tokens, session logic, or checksum/signing routines

This step alone often tells you most of what you need before you even capture a packet.

---

## 2. Capture decrypted traffic with SSLKEYLOGFILE (recommended)

Works since Java 8u261+. No MITM proxy, no cert-pinning issues, no touching the JVM's truststore.

**Step 1 — set the env var and launch the app:**

```bash
export SSLKEYLOGFILE=/tmp/sslkeys.log
javaws launch.jnlp
```

On Windows (PowerShell):

```powershell
$env:SSLKEYLOGFILE = "C:\temp\sslkeys.log"
javaws launch.jnlp
```

**Step 2 — find what port/interface it's using (optional sanity check):**

```bash
netstat -ano | grep java      # Linux/macOS
netstat -ano | findstr java   # Windows
```

**Step 3 — capture in Wireshark:**

1. Start capturing on the relevant interface
2. `Edit → Preferences → Protocols → TLS`
3. Set **(Pre)-Master-Secret log filename** to `/tmp/sslkeys.log`
4. Filter: `tcp.port == 443` (adjust if TradeCast uses a different port)
5. Use the JNLP app normally — login, place a test order, check quotes, etc.

Wireshark will now show decrypted HTTP or raw TLS payload. Right-click a stream → **Follow → TLS Stream** to read the full conversation.

This method survives certificate pinning because it doesn't intercept the handshake at all — it just gives Wireshark the session keys after the fact.

---

## 3. Alternative: MITM proxy (mitmproxy / Burp Suite)

Simpler UI for reading HTTP requests/responses, but breaks if the app does certificate pinning.

**Step 1 — trust your proxy's CA in the JRE's truststore** (Java Web Start doesn't use the OS cert store):

```bash
keytool -import -alias mitmproxy -file mitmproxy-ca-cert.pem \
  -keystore "$JAVA_HOME/lib/security/cacerts" -storepass changeit
```

**Step 2 — point the JVM at your proxy.** Either via `deployment.properties`:

```
# usually at ~/.java/deployment/deployment.properties (Linux/macOS)
# or %APPDATA%\Sun\Java\Deployment\deployment.properties (Windows)
deployment.proxy.type=1
deployment.proxy.http.host=127.0.0.1
deployment.proxy.http.port=8080
deployment.proxy.https.host=127.0.0.1
deployment.proxy.https.port=8080
```

Or via environment variable before launch:

```bash
export JAVA_TOOL_OPTIONS="-Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=8080 -Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=8080"
javaws launch.jnlp
```

**Step 3 — run mitmproxy and capture:**

```bash
mitmproxy -p 8080
# or mitmweb for a browser UI
mitmweb -p 8080
```

Every request/response body, header, and endpoint will show up in the mitmproxy log.

**If this doesn't work (blank/failed connections):** the app likely pins certificates. Fall back to Method 2 (SSLKEYLOGFILE).

---

## 4. If it's not HTTP at all

Some trading terminals use a raw binary protocol (FIX-like or proprietary) directly over a TLS socket rather than REST/HTTP. Signs of this in Wireshark:

- A TLS stream on a non-standard port
- Decrypted payload that isn't readable HTTP text — looks like binary/structured bytes instead

In this case:
1. Go back to the decompiled source (Step 1) and find the serialization/deserialization classes — search for `readObject`, `ByteBuffer`, `DataInputStream`, or custom `Message`/`Packet` classes.
2. Map out the message framing: header format, message type codes, length prefixes, checksums, auth handshake sequence.
3. You'll likely need to reimplement this protocol in your own client rather than replay simple HTTP calls — plan for more work here than a REST API would require.

---

## 5. Building your custom client

Once you understand the protocol:

1. Replicate the login/auth handshake first, in isolation, before anything else.
2. Implement one read-only call (e.g. fetch quote/balance) before attempting anything that places orders.
3. Keep a "safe mode" / dry-run flag in your client that logs intended actions instead of sending them, until you're confident in the protocol implementation — mistakes here have real financial consequences.
4. Watch for session expiry, heartbeat/keepalive messages, and reconnect logic in the decompiled source — these are easy to miss and will cause silent failures in your own client.

---

## Tools referenced

| Tool | Purpose |
|---|---|
| [Wireshark](https://www.wireshark.org/) | Packet capture + TLS decryption via SSLKEYLOGFILE |
| [vineflower](https://github.com/Vineflower/vineflower) / [cfr](https://www.benf.org/other/cfr/) | Java decompilers |
| [jd-gui](https://github.com/java-decompiler/jd-gui) | GUI Java decompiler for browsing |
| [mitmproxy](https://mitmproxy.org/) | HTTP(S) intercepting proxy |
| `keytool` | Import CA cert into JRE truststore |
