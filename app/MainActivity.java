package com.example.xpanel;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.net.VpnService;
import android.os.Bundle;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import android.graphics.Color;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.ImageButton;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.net.URLEncoder;
import java.util.ArrayList;
import org.json.JSONArray;
import org.json.JSONObject;

public class MainActivity extends Activity {
    private EditText configInput;
    private View inputPanel, resultPanel, logPanel;
    private TextView logView, tvEmail, tvUsed, tvUpDown, tvExpiry, tvStatus, tvConnState;
    private ImageButton btnPower;
    private Button btnQuery, btnLog1, btnLog2, btnBackFromLog;
    private ScrollView scrollView;
    private android.widget.Switch swBypassCN, swPerApp;
    private boolean vpnRunning = false;
    private String lastPage = "input";
    private Handler handler = new Handler(Looper.getMainLooper());
    private static final String SB_DIR = "/data/data/com.example.xpanel/files/sb";
    private static final String SB_CONF = SB_DIR + "/config.json";
    private static final String SB_BIN = "/data/data/com.example.xpanel/files/sing-box";
    private static final String PANEL_BASE = "https://23.144.20.121:1111/clay";
    private Process vpnProcess = null;
    private ParcelFileDescriptor vpnFd = null;
    private static final int VPN_REQUEST_CODE = 1001;

    private BroadcastReceiver logReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context c, Intent i) {
            String msg = i.getStringExtra("msg");
            if (msg != null) {
                log(msg);
                if (msg.contains("核心进程退出") || msg.contains("已断开")) {
                    vpnRunning = false;
                    handler.post(() -> {
                        tvConnState.setText("○ 未连接 (点击开启)");
                        tvConnState.setTextColor(Color.parseColor("#94a3b8"));
                        tvConnState.setTextSize(13);
                        btnPower.setBackgroundResource(R.drawable.btn_power);
                        btnPower.setColorFilter(Color.parseColor("#64748b"));
                    });
                }
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        try {
            javax.net.ssl.TrustManager[] trustAllCerts = new javax.net.ssl.TrustManager[]{
                new javax.net.ssl.X509TrustManager() {
                    public java.security.cert.X509Certificate[] getAcceptedIssuers() { return null; }
                    public void checkClientTrusted(java.security.cert.X509Certificate[] certs, String authType) {}
                    public void checkServerTrusted(java.security.cert.X509Certificate[] certs, String authType) {}
                }
            };
            javax.net.ssl.SSLContext sc = javax.net.ssl.SSLContext.getInstance("SSL");
            sc.init(null, trustAllCerts, new java.security.SecureRandom());
            javax.net.ssl.HttpsURLConnection.setDefaultSSLSocketFactory(sc.getSocketFactory());
            javax.net.ssl.HttpsURLConnection.setDefaultHostnameVerifier((hostname, session) -> true);
        } catch (Exception ignored) {}
        registerReceiver(logReceiver, new IntentFilter("com.example.xpanel.LOG"), Context.RECEIVER_NOT_EXPORTED);
        setContentView(R.layout.activity_main);

        configInput = findViewById(R.id.configInput);
        String saved = getSharedPreferences("cfg", 0).getString("link", "");
        if (!saved.isEmpty()) configInput.setText(saved);
        logView = findViewById(R.id.logView);
        scrollView = findViewById(R.id.scrollView);
        inputPanel = findViewById(R.id.inputPanel);
        resultPanel = findViewById(R.id.resultPanel);
        logPanel = findViewById(R.id.logPanel);
        tvEmail = findViewById(R.id.tvEmail);
        tvUsed = findViewById(R.id.tvUsed);
        tvUpDown = findViewById(R.id.tvUpDown);
        tvExpiry = findViewById(R.id.tvExpiry);
        tvStatus = findViewById(R.id.tvStatus);
        tvConnState = findViewById(R.id.tvConnState);
        btnPower = findViewById(R.id.btnPower);
        btnPower.setColorFilter(Color.parseColor("#64748b"));
        btnQuery = findViewById(R.id.btnQuery);
        btnLog1 = findViewById(R.id.btnLog1);
        btnLog2 = findViewById(R.id.btnLog2);
        btnBackFromLog = findViewById(R.id.btnBackFromLog);
        Button btnBack = findViewById(R.id.btnBack);
        Button btnCopy1 = findViewById(R.id.btnCopy1);
        Button btnCopyLog = findViewById(R.id.btnCopyLog);

        swBypassCN = findViewById(R.id.swBypassCN);
        swPerApp = findViewById(R.id.swPerApp);
        Button btnPickApps = findViewById(R.id.btnPickApps);
        // 恢复开关状态
        android.content.SharedPreferences prefs = getSharedPreferences("cfg", 0);
        swBypassCN.setChecked(prefs.getBoolean("bypass_cn", true));
        swPerApp.setChecked(prefs.getBoolean("per_app", false));
        swBypassCN.setOnCheckedChangeListener((v, c) -> getSharedPreferences("cfg", 0).edit().putBoolean("bypass_cn", c).apply());
        swPerApp.setOnCheckedChangeListener((v, c) -> getSharedPreferences("cfg", 0).edit().putBoolean("per_app", c).apply());
        btnPickApps.setOnClickListener(v -> startActivity(new Intent(this, AppPickerActivity.class)));

        btnQuery.setOnClickListener(v -> {
            log("[点击] 登录按钮");
            Toast.makeText(this, "正在登录...", Toast.LENGTH_SHORT).show();
            queryPanel();
        });
        btnPower.setOnClickListener(v -> { if (vpnRunning) stopVpn(); else startVpn(); });
        btnBack.setOnClickListener(v -> showInput());
        btnCopy1.setOnClickListener(v -> copyLog());
        btnCopyLog.setOnClickListener(v -> copyLog());
        btnLog1.setOnClickListener(v -> showLog());
        btnLog2.setOnClickListener(v -> showLog());
        btnBackFromLog.setOnClickListener(v -> { if ("result".equals(lastPage)) showResult(); else showInput(); });



        // ponytail: 恢复上次结果页（强制退出后重启直接到节点信息）
        if (prefs.contains("r_email")) {
            tvEmail.setText(prefs.getString("r_email", ""));
            tvUsed.setText(prefs.getString("r_used", ""));
            tvUpDown.setText(prefs.getString("r_updown", ""));
            tvExpiry.setText(prefs.getString("r_exp", ""));
            tvExpiry.setTextColor(prefs.getInt("r_exp_color", Color.parseColor("#7a7a82")));
            tvStatus.setText(prefs.getString("r_status", ""));
            tvStatus.setTextColor("启用".equals(prefs.getString("r_status","")) ? Color.parseColor("#06d6a0") : Color.parseColor("#ef5350"));
            showResult();
            queryPanelSilent();
        }

        // 首次初始化：解压 sing-box
        new Thread(() -> {
            try {
                File dir = new File(SB_DIR);
                dir.mkdirs();
                File bin = new File(SB_BIN);
                if (!bin.exists()) {
                    log("[init] 解压 sing-box...");
                    InputStream is = getAssets().open("sing-box.gz");
                    File tmp = new File(getFilesDir(), "sing-box.gz");
                    FileOutputStream fos = new FileOutputStream(tmp);
                    byte[] buf = new byte[65536]; int n;
                    while ((n = is.read(buf)) > 0) fos.write(buf, 0, n);
                    fos.close(); is.close();
                    String[] cmd = {"sh", "-c",
                        "gzip -d -c " + tmp.getAbsolutePath() + " > " + SB_BIN +
                        " && chmod 755 " + SB_BIN + " && echo OK"};
                    Process p = Runtime.getRuntime().exec(cmd);
                    p.waitFor();
                    BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream()));
                    String out = r.readLine();
                    log("[init] " + (out != null && out.contains("OK") ? "安装完成" : "解压失败"));
                    tmp.delete();
                } else {
                    log("[init] sing-box 已就绪");
                }
            } catch (Exception e) {
                log("[init] 异常: " + e.getMessage());
            }
        }).start();
    }

    // ---- VpnService 建立 tun fd ----
    private void startVpn() {
        Intent intent = VpnService.prepare(this);
        if (intent != null) {
            startActivityForResult(intent, VPN_REQUEST_CODE);
            return;
        }
        setupVpn();
    }

    @Override
    protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == VPN_REQUEST_CODE && result == RESULT_OK) setupVpn();
    }

    private void setupVpn() {
        new Thread(() -> {
            try {
                boolean bypassCN = getSharedPreferences("cfg", 0).getBoolean("bypass_cn", true);
                boolean perApp = getSharedPreferences("cfg", 0).getBoolean("per_app", false);
                String cfg = buildConfig(configInput.getText().toString(), bypassCN);
                if (cfg == null) { log("[错误] 不支持的链接"); return; }
                log("[VPN] 启动 VpnService...\n[配置详情]\n" + cfg);
                Intent svc = new Intent(this, XrayVpnService.class);
                svc.putExtra("config", cfg);
                if (perApp) {
                    ArrayList<String> bypass = new ArrayList<>();
                    try {
                        org.json.JSONArray arr = new org.json.JSONArray(getSharedPreferences("cfg", 0).getString("bypass_apps", ""));
                        for (int i = 0; i < arr.length(); i++) bypass.add(arr.getString(i));
                    } catch (Exception ignored) {}
                    svc.putStringArrayListExtra("bypass_apps", bypass);
                    log("[VPN] 分应用代理，绕过 " + bypass.size() + " 个应用");
                }
                startService(svc);
                vpnRunning = true;
                handler.post(() -> {
                    tvConnState.setText("● 已连接 (安全加速中)");
                    tvConnState.setTextColor(Color.parseColor("#10b981"));
                    tvConnState.setTextSize(15);
                    btnPower.setBackgroundResource(R.drawable.btn_power_on);
                    btnPower.setColorFilter(Color.WHITE);

                });
                log("[VPN] VpnService 已启动");
            } catch (Exception e) {
                log("[VPN] 异常: " + e.getMessage());
            }
        }).start();
    }

    // ---- 解析链接 ----
    private String[] parseLink(String raw) {
        String url = raw.trim();
        String[] info = new String[4];
        try {
            if (url.startsWith("vless://")) {
                String body = url.substring(8);
                if (body.contains("?")) body = body.substring(0, body.indexOf("?"));
                if (body.contains("#")) body = body.substring(0, body.indexOf("#"));
                info[0] = "vless"; info[1] = body.substring(0, body.indexOf("@"));
                String hp = body.substring(body.indexOf("@") + 1); info[2] = hp; info[3] = "443";
                if (hp.contains(":")) { info[2] = hp.substring(0, hp.lastIndexOf(":")); info[3] = hp.substring(hp.lastIndexOf(":") + 1); }
            } else if (url.startsWith("vmess://")) {
                info[0] = "vmess";
                String b64 = url.substring(8);
                if (b64.contains("#")) b64 = b64.substring(0, b64.indexOf("#"));
                JSONObject o = new JSONObject(new String(android.util.Base64.decode(b64, android.util.Base64.DEFAULT)));
                info[1] = o.optString("id"); info[2] = o.optString("add"); info[3] = o.optString("port");
            } else if (url.startsWith("trojan://")) {
                String body = url.substring(9);
                if (body.contains("?")) body = body.substring(0, body.indexOf("?"));
                if (body.contains("#")) body = body.substring(0, body.indexOf("#"));
                info[0] = "trojan"; info[1] = body.substring(0, body.indexOf("@"));
                String hp = body.substring(body.indexOf("@") + 1); info[2] = hp; info[3] = "443";
                if (hp.contains(":")) { info[2] = hp.substring(0, hp.lastIndexOf(":")); info[3] = hp.substring(hp.lastIndexOf(":") + 1); }
            }
        } catch (Exception e) { return null; }
        return info;
    }

    // ---- 查询面板 ----（独立子线程 + 2秒自驱动高频轮询，永不断线）
    private boolean autoRefresh = false;
    private volatile boolean querying = false;
    private final Runnable queryTask = new Runnable() {
        @Override public void run() {
            if (autoRefresh) {
                doQuery(true);
                handler.postDelayed(this, 2000);
            }
        }
    };

    private void queryPanel() {
        log("[登录] 开始查询...");
        autoRefresh = true;
        handler.removeCallbacks(queryTask);
        handler.post(queryTask);
    }

    private void queryPanelSilent() {
        doQuery(true);
    }

    private void doQuery(final boolean silent) {
        if (querying) return;
        final String linkFromUi = configInput != null ? configInput.getText().toString().trim() : "";
        new Thread(() -> {
            querying = true;
            try {
                String base = PANEL_BASE;
                String link = linkFromUi;
                if (link.isEmpty()) {
                    link = getSharedPreferences("cfg", 0).getString("link", "").trim();
                }
                if (link.isEmpty()) return;
                getSharedPreferences("cfg", 0).edit().putString("link", link).apply();
                if (link.startsWith("http://") || link.startsWith("https://")) {
                    querySubscription(link);
                    return;
                }
                String[] info = parseLink(link);
                if (info == null || info[1] == null || info[1].isEmpty()) {
                    if (!silent) { log("[查询] 协议链接格式不匹配"); toast("链接格式不对"); }
                    return;
                }
                if (info[2] != null && !info[2].isEmpty()) {
                    base = "https://" + info[2] + ":1111/clay";
                }
                boolean ok = queryPublic(base, info[1], silent);
                if (!ok && !base.endsWith("/clay")) {
                    queryPublic("https://" + info[2] + ":1111/clay", info[1], silent);
                } else if (!ok) {
                    queryPublic("https://" + info[2] + ":1111", info[1], silent);
                }
            } catch (Exception e) {
                if (!silent) log("[查询] 异常: " + e.getMessage());
            } finally {
                querying = false;
            }
        }).start();
    }

    // 公开接口: GET /panel/pub/traffic/:uuid
    private boolean queryPublic(String base, String uuid, boolean silent) {
        for (int attempt = 1; attempt <= 2; attempt++) {
            try {
                return queryPublicOnce(base, uuid, silent);
            } catch (Exception e) {
                if (!silent) log("[查询] 第" + attempt + "次异常: " + e.getMessage());
                if (attempt == 2) return false;
                try { Thread.sleep(800); } catch (InterruptedException ignored) {}
            }
        }
        return false;
    }

    private boolean queryPublicOnce(String base, String uuid, boolean silent) throws Exception {
        try {
            if (!silent) log("[查询] 请求中...");
            HttpURLConnection c = get(base + "/panel/pub/traffic/" + uuid);
            String body = readAll(c);
            if (!silent) log("[查询] 响应: HTTP " + c.getResponseCode() + ", url=" + base + "/panel/pub/traffic/" + uuid);
            JSONObject root = new JSONObject(body);
            if (!root.optBoolean("success")) {
                log("[查询] 公开接口不可用: " + root.optString("msg", ""));
                return false;
            }
            JSONObject o = root.optJSONObject("obj");
            if (o == null) { log("[查询] 公开接口未找到该客户端"); return false; }
            String upS = o.optString("up", "0"), downS = o.optString("down", "0");
            String totalS = o.optString("total", "0"), expS = o.optString("expiryTime", "0");
            long up = parseLong(upS), down = parseLong(downS), total = parseLong(totalS), exp = parseLong(expS);
            final String fEmail = o.optString("email", "");
            final String fUp = fmtBytes(upS), fDown = fmtBytes(downS), fUsed = fmtBytes(String.valueOf(up + down));
            final String fTotal = fmtBytes(totalS);
            final String fExp = fmtExp(exp);
            final int expColor;
            if (exp <= 0) {
                expColor = Color.parseColor("#7a7a82");
            } else if (exp < System.currentTimeMillis()) {
                expColor = Color.parseColor("#757575");
            } else {
                long daysLeft = (exp - System.currentTimeMillis()) / 86400000L;
                if (daysLeft > 7) expColor = Color.parseColor("#06d6a0");
                else if (daysLeft > 3) expColor = Color.parseColor("#ffb74d");
                else expColor = Color.parseColor("#ef5350");
            }
            final String fStatus = o.optBoolean("enable") ? "启用" : "停用";
            handler.post(() -> {
                tvEmail.setText(fEmail);
                tvUsed.setText(fUsed + " / " + fTotal);
                tvUpDown.setText(fUp + " / " + fDown);
                tvExpiry.setText(fExp);
                tvExpiry.setTextColor(expColor);
                tvStatus.setText(fStatus);
                tvStatus.setTextColor(o.optBoolean("enable") ? Color.parseColor("#06d6a0") : Color.parseColor("#ef5350"));
                // ponytail: 存结果以便重启恢复
                getSharedPreferences("cfg", 0).edit()
                    .putString("r_email", fEmail)
                    .putString("r_used", fUsed + " / " + fTotal)
                    .putString("r_updown", fUp + " / " + fDown)
                    .putString("r_exp", fExp)
                    .putInt("r_exp_color", expColor)
                    .putString("r_status", fStatus)
                    .apply();
                showResult();
            });
            log("=== 节点信息 ===  上行 " + fUp + " / 下行 " + fDown + " / 已用 " + fUsed + " / 到期 " + fExp);
            return true;
        } catch (Exception e) {
            log("[查询] 公开接口异常: " + e.getMessage());
            throw e;
        }
    }

    // 切换: 输入页 / 结果页 / 日志页
    private void showInput() {
        autoRefresh = false;
        handler.removeCallbacks(queryTask);
        inputPanel.setVisibility(View.VISIBLE);
        resultPanel.setVisibility(View.GONE);
        logPanel.setVisibility(View.GONE);
        lastPage = "input";
    }

    private void showResult() {
        inputPanel.setVisibility(View.GONE);
        resultPanel.setVisibility(View.VISIBLE);
        logPanel.setVisibility(View.GONE);
        lastPage = "result";
        autoRefresh = true;
    }

    private void showLog() {
        inputPanel.setVisibility(View.GONE);
        resultPanel.setVisibility(View.GONE);
        logPanel.setVisibility(View.VISIBLE);
        scrollView.post(() -> scrollView.fullScroll(ScrollView.FOCUS_DOWN));
    }

    // 登录查询（老版本面板回退）
    private void queryByLogin(String base, String user, String pass, String[] info) {
            try {
                HttpURLConnection c = postForm(base + "/login", "username=" + URLEncoder.encode(user, "UTF-8") + "&password=" + URLEncoder.encode(pass, "UTF-8"));
                String body = readAll(c);
                if (!body.contains("\"success\":true")) { log("[登录] 失败: " + body.substring(0, Math.min(100, body.length()))); return; }
                log("[登录] 成功");
                HttpURLConnection c2 = get(base + "/panel/api/inbounds/list");
                String list = readAll(c2);
                JSONObject root = new JSONObject(list);
                JSONArray inbounds = root.optJSONArray("obj");
                if (inbounds == null) { log("[查询] 接口异常"); return; }
                boolean found = false;
                for (int i = 0; i < inbounds.length(); i++) {
                    JSONObject ib = inbounds.getJSONObject(i);
                    String ss = ib.optString("settings", "{}");
                    JSONArray clients = new JSONArray();
                    try { JSONArray cs = new JSONObject(ss).optJSONArray("clients"); if (cs != null) clients = cs; } catch (Exception ignored) {}
                    for (int j = 0; j < clients.length(); j++) {
                        JSONObject cl = clients.getJSONObject(j);
                        if (info[1].equals(cl.optString("id"))) {
                            String email = cl.optString("email", "");
                            found = true;
                            String upS = "0", downS = "0", totalS = "0", expS = "0", enableS = "?";
                            if (!email.isEmpty()) {
                                HttpURLConnection c3 = get(base + "/panel/api/inbounds/getClientTraffics/" + URLEncoder.encode(email, "UTF-8"));
                                String tb = readAll(c3);
                                try {
                                    JSONObject troot = new JSONObject(tb);
                                    JSONObject tobj = troot.optJSONObject("obj");
                                    if (tobj != null) {
                                        upS = tobj.optString("up", "0"); downS = tobj.optString("down", "0");
                                        totalS = tobj.optString("total", "0"); expS = tobj.optString("expiryTime", "0");
                                        enableS = tobj.optBoolean("enable") ? "启用" : "停用";
                                    }
                                } catch (Exception ignored) {}
                            }
                            log("=== 节点信息 ===");
                            log("协议: " + info[0] + "  " + info[2] + ":" + info[3]);
                            log("email: " + email);
                            log("上行: " + fmtBytes(upS) + "  下行: " + fmtBytes(downS));
                            long up = parseLong(upS), down = parseLong(downS), total = parseLong(totalS);
                            log("已用: " + fmtBytes(String.valueOf(up + down)) + " / " + fmtBytes(totalS));
                            if (total > 0) log("剩余: " + fmtBytes(String.valueOf(Math.max(0, total - up - down))));
                            long exp = parseLong(expS);
                            log("到期: " + fmtExp(exp));
                            log("状态: " + enableS);
                        }
                    }
                }
                if (!found) log("[查询] 未找到匹配的客户端");
            } catch (Exception e) { log("[查询] 异常: " + e.getMessage()); }
    }

    private void querySubscription(String subUrl) {
        try {
            log("[订阅] " + subUrl);
            HttpURLConnection c = (HttpURLConnection) new URL(subUrl).openConnection();
            c.setRequestMethod("GET"); c.setConnectTimeout(10000); c.setReadTimeout(15000);
            String info = c.getHeaderField("Subscription-Userinfo");
            readAll(c);
            if (info != null) {
                log("=== 订阅信息 (无需密码) ===");
                long up = 0, down = 0, total = 0, exp = 0;
                for (String kv : info.split(";")) {
                    String[] p = kv.trim().split("=");
                    if (p.length != 2) continue;
                    try {
                        if (p[0].equals("upload")) up = Long.parseLong(p[1]);
                        else if (p[0].equals("download")) down = Long.parseLong(p[1]);
                        else if (p[0].equals("total")) total = Long.parseLong(p[1]);
                        else if (p[0].equals("expire")) exp = Long.parseLong(p[1]);
                    } catch (Exception ignored) {}
                }
                log("上行: " + fmtBytes(String.valueOf(up)) + "  下行: " + fmtBytes(String.valueOf(down)));
                log("已用: " + fmtBytes(String.valueOf(up + down)) + " / " + fmtBytes(String.valueOf(total)));
                if (total > 0) log("剩余: " + fmtBytes(String.valueOf(Math.max(0, total - up - down))));
                log("到期: " + (exp > 0 ? new java.text.SimpleDateFormat("yyyy/MM/dd HH:mm").format(new java.util.Date(exp * 1000)) : "不限"));
            } else {
                log("[订阅] 该链接未返回流量信息，请确认是订阅链接而非节点链接");
            }
        } catch (Exception e) { log("[订阅] 异常: " + e.getMessage()); }
    }

    private HttpURLConnection postForm(String urlStr, String body) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(urlStr).openConnection(java.net.Proxy.NO_PROXY);
        c.setRequestMethod("POST"); c.setDoOutput(true); c.setConnectTimeout(10000); c.setReadTimeout(15000);
        c.setRequestProperty("Content-Type", "application/x-www-form-urlencoded");
        OutputStream os = c.getOutputStream(); os.write(body.getBytes()); os.flush(); os.close();
        return c;
    }

    private HttpURLConnection get(String urlStr) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(urlStr).openConnection(java.net.Proxy.NO_PROXY);
        c.setRequestMethod("GET"); c.setConnectTimeout(8000); c.setReadTimeout(12000);
        return c;
    }

    private String readAll(HttpURLConnection c) throws Exception {
        InputStream is = c.getResponseCode() >= 400 ? c.getErrorStream() : c.getInputStream();
        if (is == null) return "";
        BufferedReader br = new BufferedReader(new InputStreamReader(is));
        StringBuilder sb = new StringBuilder(); String line;
        while ((line = br.readLine()) != null) sb.append(line);
        return sb.toString();
    }

    private long parseLong(String s) { try { return Long.parseLong(s); } catch (Exception e) { return 0; } }

    // 面板接口的 expiryTime 单位是毫秒；0=不限, 负数=延迟生效占位, 过去=已过期
    private String fmtExp(long ms) {
        if (ms <= 0) return "不限";
        if (ms < System.currentTimeMillis()) return "已过期";
        return new java.text.SimpleDateFormat("yyyy/M/d HH:mm").format(new java.util.Date(ms));
    }

    private String fmtBytes(String s) {
        long b = parseLong(s);
        if (b < 1024) return b + " B";
        double kb = b / 1024.0;
        if (kb < 1024) return String.format("%.1f KB", kb);
        double mb = kb / 1024.0;
        if (mb < 1024) return String.format("%.1f MB", mb);
        double gb = mb / 1024.0;
        if (gb < 1024) return String.format("%.2f GB", gb);
        return String.format("%.2f TB", gb / 1024.0);
    }

    private static String lookupHost(String domain) {
        try {
            java.net.InetAddress[] all = java.net.InetAddress.getAllByName(domain);
            if (all.length > 0) return all[0].getHostAddress();
        } catch (Exception ignored) {}
        return null;
    }

    // ---- Xray 配置生成 ----
    private String buildConfig(String raw, boolean bypassCN) {
        String url = raw.trim();
        if (!url.startsWith("vless://")) return null;
        
        String body = url.substring(8); String query = "";
        if (body.contains("?")) { query = body.substring(body.indexOf("?") + 1); body = body.substring(0, body.indexOf("?")); }
        if (body.contains("#")) body = body.substring(0, body.indexOf("#"));
        String uuid = body.substring(0, body.indexOf("@"));
        String hp = body.substring(body.indexOf("@") + 1); String host = hp, port = "443";
        if (hp.contains(":")) { host = hp.substring(0, hp.lastIndexOf(":")); port = hp.substring(hp.lastIndexOf(":") + 1); }
        String sni = getParam(query, "sni", host); String flow = getParam(query, "flow", "");
        String fp = getParam(query, "fp", "chrome"); String pbk = getParam(query, "pbk", ""); String sid = getParam(query, "sid", "");

        String hostIp = host.matches(".*[a-zA-Z].*") ? lookupHost(host) : host;

        StringBuilder sb = new StringBuilder();
        sb.append("{\n");
        sb.append("  \"log\": {\"loglevel\": \"warning\"},\n");

        // DNS: 配置内置 DoH 并且静态解析节点域名，打破 DNS 解析死循环
        sb.append("  \"dns\": {\n");
        sb.append("    \"servers\": [\n");
        sb.append("      {\"address\": \"https://1.1.1.1/dns-query\", \"tag\": \"dns_query\"},\n");
        sb.append("      {\"address\": \"https://8.8.8.8/dns-query\", \"tag\": \"dns_query\"}\n");
        sb.append("    ]");
        if (hostIp != null && !hostIp.equals(host)) {
            sb.append(",\n    \"hosts\": {\"").append(host).append("\": \"").append(hostIp).append("\"}\n");
        } else {
            sb.append("\n");
        }
        sb.append("  },\n");

        sb.append("  \"inbounds\": [{\n");
        sb.append("    \"port\": 10808,\n");
        sb.append("    \"listen\": \"127.0.0.1\",\n");
        sb.append("    \"protocol\": \"socks\",\n");
        sb.append("    \"settings\": {\"auth\": \"noauth\", \"udp\": true}\n");
        sb.append("  }],\n");

        sb.append("  \"outbounds\": [\n");
        // proxy 出站
        sb.append("    {\"protocol\":\"vless\",\"tag\":\"proxy\",\"settings\":{\"vnext\":[{\"address\":\"").append(host).append("\",\"port\":").append(port)
          .append(",\"users\":[{\"id\":\"").append(uuid).append("\",\"encryption\":\"none\",\"flow\":\"").append(flow).append("\"}]}]}")
          .append(",\"streamSettings\":{\"network\":\"tcp\",\"security\":\"reality\",\"realitySettings\":{\"serverName\":\"").append(sni)
          .append("\",\"publicKey\":\"").append(pbk).append("\",\"shortId\":\"").append(sid).append("\",\"fingerprint\":\"").append(fp).append("\"}")
          .append(",\"sockopt\":{\"domainStrategy\":\"UseIP\"}}},\n");

        // direct 直连
        sb.append("    {\"protocol\":\"freedom\",\"tag\":\"direct\",\"settings\":{\"domainStrategy\":\"UseIP\"}},\n");
        // block 阻断
        sb.append("    {\"protocol\":\"blackhole\",\"tag\":\"block\"}\n");
        sb.append("  ],\n");

        // routing 路由规则
        sb.append("  \"routing\": {\n");
        sb.append("    \"domainStrategy\": \"IPIfNonMatch\",\n");
        sb.append("    \"rules\": [\n");
        // DNS 查询带 dns_query 走代理出站（防本地 DNS 劫持污染）
        sb.append("      {\"type\":\"field\",\"inboundTag\":[\"dns_query\"],\"outboundTag\":\"proxy\"},\n");
        // XTLS Vision 只能承载 TCP，阻断 UDP 443 (QUIC) 让浏览器回退 TCP
        if (flow != null && !flow.isEmpty()) {
            sb.append("      {\"type\":\"field\",\"network\":\"udp\",\"port\":\"443\",\"outboundTag\":\"block\"},\n");
        }
        // 节点自身的 IP 必须直连，防止出站回环
        if (hostIp != null) {
            sb.append("      {\"type\":\"field\",\"ip\":[\"").append(hostIp).append("\"],\"outboundTag\":\"direct\"},\n");
        }
        // 私网 IP 直连
        sb.append("      {\"type\":\"field\",\"ip\":[\"10.0.0.0/8\",\"172.16.0.0/12\",\"192.168.0.0/16\",\"127.0.0.0/8\"],\"outboundTag\":\"direct\"}\n");
        sb.append("    ]\n");
        sb.append("  }\n");
        sb.append("}\n");

        return sb.toString();
    }

    private String getParam(String query, String key, String def) {
        if (query == null || query.isEmpty()) return def;
        if (query.contains("#")) query = query.substring(0, query.indexOf("#"));
        for (String kv : query.split("&")) {
            String[] p = kv.split("=", 2);
            if (p.length == 2 && p[0].equals(key)) {
                String val = p[1];
                if (val.contains("#")) val = val.substring(0, val.indexOf("#"));
                return val;
            }
        }
        return def;
    }

    private void stopVpn() {
        autoRefresh = false;
        handler.removeCallbacks(queryTask);
        vpnRunning = false;
        handler.post(() -> {
            tvConnState.setText("○ 未连接 (点击开启)");
            tvConnState.setTextColor(Color.parseColor("#94a3b8"));
            tvConnState.setTextSize(13);
            btnPower.setBackgroundResource(R.drawable.btn_power);
            btnPower.setColorFilter(Color.parseColor("#64748b"));

        });
        XrayVpnService.shutdown();
        log("[VPN] 已断开");
    }

    @Override
    protected void onResume() {
        super.onResume();
        autoRefresh = true;
        handler.removeCallbacks(queryTask);
        handler.post(queryTask);
    }

    @Override
    protected void onPause() {
        super.onPause();
        handler.removeCallbacks(queryTask);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        try { unregisterReceiver(logReceiver); } catch (Exception ignored) {}
    }

    private void log(final String msg) {
        handler.post(() -> {
            logView.append(msg + "\n");
            scrollView.post(() -> scrollView.fullScroll(ScrollView.FOCUS_DOWN));
        });
    }

    private void toast(final String msg) {
        handler.post(() -> Toast.makeText(this, msg, Toast.LENGTH_SHORT).show());
    }

    private void copyLog() {
        String text = logView.getText().toString();
        if (text.isEmpty()) { log("[复制] 日志为空"); return; }
        try {
            ClipboardManager cm = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
            cm.setPrimaryClip(ClipData.newPlainText("log", text));
            log("[复制] 日志已复制到剪贴板 (" + text.length() + " 字符)");
        } catch (Exception e) {
            log("[复制] 失败: " + e.getMessage() + "，改用文件保存");
            saveLogFile(text);
        }
    }

    private void saveLogFile(String text) {
        try {
            File f = new File(getExternalFilesDir(null), "xpanel_log.txt");
            FileOutputStream fos = new FileOutputStream(f);
            fos.write(text.getBytes()); fos.close();
            log("[复制] 已保存到文件: " + f.getAbsolutePath());
            // 复制文件路径到剪贴板，方便分享
            ClipboardManager cm = (ClipboardManager) getSystemService(CLIPBOARD_SERVICE);
            cm.setPrimaryClip(ClipData.newPlainText("path", f.getAbsolutePath()));
        } catch (Exception e) {
            log("[复制] 文件保存失败: " + e.getMessage());
        }
    }
}