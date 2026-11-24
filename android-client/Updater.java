package com.example.xpanel;

import android.app.DownloadManager;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Environment;
import android.util.Log;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;

/**
 * 客户端自动更新：比对面板镜像 /download/pkg/client.json 里的 APK sha256。
 * 有新版自动下载；root 设备静默安装，普通设备拉起系统安装器（一次确认）。
 */
public final class Updater {
    private static final String TAG = "XPanelUpdate";
    private static volatile boolean started = false;

    // 面板地址候选：443 优先（国内最稳，nginx 反代到面板），80 次之，
    // 最后才直连面板端口 1111 —— 该端口在国内经常超时，旧版只写死它，
    // 检查一失败就放弃，等于手机端从来没有真正自动更新过。
    private static final String[] DEFAULT_BASES = {
            "https://888888666.xyz/clay",
            "http://888888666.xyz/clay",
            "https://888888666.xyz:1111/clay"
    };

    private Updater() {}

    // 更新检查的每一步都回报给界面日志区：只有发现新版才写 logcat 的话，
    // 「已是最新」「三个地址全失败」这些情况在日志里完全不可见，出问题无从判断。
    public interface Reporter { void onUpdateLog(String msg); }
    private static volatile Reporter reporter;
    public static void setReporter(Reporter r) { reporter = r; }

    private static void report(String msg) {
        Log.i(TAG, msg);
        Reporter r = reporter;
        if (r != null) {
            try { r.onUpdateLog(msg); } catch (Exception ignored) { }
        }
    }

    public static void checkAsync(final Context ctx, final String baseUrl) {
        if (started || baseUrl == null || baseUrl.isEmpty()) return;
        started = true;
        new Thread(() -> {
            java.util.List<String> bases = new java.util.ArrayList<>();
            for (String b : DEFAULT_BASES) bases.add(b);
            String passed = baseUrl.replaceAll("/+$", "");
            if (!bases.contains(passed)) bases.add(passed);
            for (String b : bases) {
                try {
                    if (check(ctx, b)) return;      // 已处理（无需更新或已下载完）
                } catch (Exception e) {
                    report("[update] 检查失败 " + b + ": " + e.getMessage());
                }
            }
            report("[update] 所有地址都不可用");
        }, "xpanel-updater").start();
    }

    private static void toast(final Context ctx, final String msg) {
        try {
            new android.os.Handler(ctx.getMainLooper()).post(
                    () -> android.widget.Toast.makeText(ctx, msg, android.widget.Toast.LENGTH_LONG).show());
        } catch (Exception ignored) { }
    }

    private static boolean check(Context ctx, String base) throws Exception {
        report("[update] 检查 " + base);
        String json = httpGet(base + "/download/pkg/client.json");
        if (json == null || json.isEmpty()) { report("[update] 清单为空: " + base); return false; }
        JSONObject root = new JSONObject(json);
        JSONObject a = root.optJSONObject("android");
        if (a == null) { report("[update] 清单里没有 android 段"); return true; }
        String want = a.optString("sha256", "");
        if (want.isEmpty()) { report("[update] 清单没有 sha256"); return true; }

        String self = sha256(new File(ctx.getPackageCodePath()));
        report("[update] 清单 sha=" + shortSha(want) + "  本机 sha=" + shortSha(self));
        if (want.equalsIgnoreCase(self)) { report("[update] 已是最新"); return true; }
        report("[update] 发现新版本，开始下载");
        toast(ctx, "发现新版本，正在下载…");

        DownloadManager dm = (DownloadManager) ctx.getSystemService(Context.DOWNLOAD_SERVICE);
        DownloadManager.Request req = new DownloadManager.Request(
                Uri.parse(base + "/download/pkg/xpanel-client.apk"));
        req.setTitle("X-Panel 客户端更新");
        req.setDescription("正在下载新版本…");
        req.setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED);
        req.setDestinationInExternalFilesDir(ctx, Environment.DIRECTORY_DOWNLOADS, "xpanel-client.apk");
        req.setMimeType("application/vnd.android.package-archive");
        long id = dm.enqueue(req);

        String path = null;
        for (int i = 0; i < 300; i++) {
            Thread.sleep(2000);
            DownloadManager.Query q = new DownloadManager.Query().setFilterById(id);
            try (Cursor c = dm.query(q)) {
                if (c == null || !c.moveToFirst()) continue;
                int status = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS));
                if (status == DownloadManager.STATUS_SUCCESSFUL) {
                    path = c.getString(c.getColumnIndexOrThrow(DownloadManager.COLUMN_LOCAL_URI));
                    break;
                }
                if (status == DownloadManager.STATUS_FAILED) { toast(ctx, "更新下载失败，稍后重试"); return false; }
            }
        }
        if (path == null) return false;
        if (path.startsWith("file://")) path = Uri.parse(path).getPath();
        if (path == null) return false;

        if (!want.equalsIgnoreCase(sha256(new File(path)))) {
            report("[update] 下载内容校验失败，换地址重试");
            new File(path).delete();
            return false;                      // 内容不对，换下一个 base 重下
        }
        report("[update] 下载完成，校验通过");

        if (rootInstall(path)) {
            report("[update] root 静默安装成功");
            toast(ctx, "已自动更新到新版本");
            return true;
        }
        Uri apk = dm.getUriForDownloadedFile(id);
        if (apk == null) return true;
        toast(ctx, "更新已下载，请按提示安装");
        Intent i = new Intent(Intent.ACTION_VIEW);
        i.setDataAndType(apk, "application/vnd.android.package-archive");
        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
        ctx.startActivity(i);
        return true;
    }

    private static boolean rootInstall(String path) {
        try {
            Process p = Runtime.getRuntime().exec(new String[]{"su", "-c", "pm install -r \"" + path + "\""});
            BufferedReader r = new BufferedReader(new InputStreamReader(p.getInputStream()));
            StringBuilder sb = new StringBuilder();
            String line;
            while ((line = r.readLine()) != null) sb.append(line);
            int code = p.waitFor();
            Log.i(TAG, "su install rc=" + code + " " + sb);
            return code == 0 && sb.toString().toLowerCase().contains("success");
        } catch (Exception e) {
            return false;
        }
    }

    private static String httpGet(String urlStr) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(urlStr).openConnection(java.net.Proxy.NO_PROXY);
        if (c instanceof javax.net.ssl.HttpsURLConnection) {
            javax.net.ssl.HttpsURLConnection h = (javax.net.ssl.HttpsURLConnection) c;
            h.setSSLSocketFactory((javax.net.ssl.SSLSocketFactory) javax.net.ssl.SSLSocketFactory.getDefault());
            h.setHostnameVerifier((host, session) ->
                    javax.net.ssl.HttpsURLConnection.getDefaultHostnameVerifier().verify(host, session));
        }
        c.setConnectTimeout(10000);
        c.setReadTimeout(15000);
        c.setRequestMethod("GET");
        if (c.getResponseCode() / 100 != 2) { c.disconnect(); return null; }
        InputStream in = c.getInputStream();
        BufferedReader r = new BufferedReader(new InputStreamReader(in, "UTF-8"));
        StringBuilder sb = new StringBuilder();
        String line;
        while ((line = r.readLine()) != null) sb.append(line);
        r.close();
        c.disconnect();
        return sb.toString();
    }

    private static String shortSha(String s) {
        if (s == null || s.length() < 8) return String.valueOf(s);
        return s.substring(0, 8);
    }

    private static String sha256(File f) {
        try (FileInputStream in = new FileInputStream(f)) {
            MessageDigest md = MessageDigest.getInstance("SHA-256");
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) > 0) md.update(buf, 0, n);
            StringBuilder sb = new StringBuilder();
            for (byte b : md.digest()) sb.append(String.format("%02x", b & 0xff));
            return sb.toString();
        } catch (Exception e) {
            return "";
        }
    }
}
