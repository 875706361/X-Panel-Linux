package com.example.xpanel;

import android.content.Intent;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.net.VpnService;
import android.os.Handler;
import android.os.Looper;
import android.os.ParcelFileDescriptor;
import java.io.BufferedReader;
import java.io.File;
import java.io.FileDescriptor;
import java.io.FileOutputStream;
import java.io.InputStreamReader;
import java.util.ArrayList;

public class XrayVpnService extends VpnService {
    private static final String CORE_DIR = "/data/data/com.example.xpanel/files/core";
    private static final String CORE_CONF = CORE_DIR + "/config.json";
    private static final String CORE_BIN_NAME = "libxpanel.so";
    private static XrayVpnService sInstance = null;
    private Process coreProcess = null;
    private ParcelFileDescriptor vpnFd = null;
    private LocalServerSocket localServer = null;
    private Handler handler = new Handler(Looper.getMainLooper());

    @Override
    public void onCreate() {
        super.onCreate();
        sInstance = this;
    }

    public static void shutdown() {
        XrayVpnService s = sInstance;
        if (s != null) s.doShutdown();
    }

    private void doShutdown() {
        try { if (coreProcess != null) { coreProcess.destroy(); coreProcess.destroyForcibly(); } } catch (Exception ignored) {}
        try { if (localServer != null) { localServer.close(); } } catch (Exception ignored) {}
        try { if (vpnFd != null) { vpnFd.close(); } } catch (Exception ignored) {}
        coreProcess = null;
        localServer = null;
        vpnFd = null;
        stopSelf();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_NOT_STICKY;
        String configStr = intent.getStringExtra("config");
        if (configStr == null) { stopSelf(); return START_NOT_STICKY; }
        ArrayList<String> bypassApps = intent.getStringArrayListExtra("bypass_apps");
        new Thread(() -> runVpn(configStr, bypassApps)).start();
        return START_NOT_STICKY;
    }

    private void runVpn(String configStr, ArrayList<String> bypassApps) {
        try {
            Builder b = new Builder();
            b.setSession("X-Panel VPN");
            b.setMtu(1500);
            b.addAddress("172.18.0.2", 30);
            b.addRoute("0.0.0.0", 0);
            b.addDnsServer("1.1.1.1");
            b.addDnsServer("8.8.8.8");
            if (bypassApps != null) {
                for (String pkg : bypassApps) {
                    try { b.addDisallowedApplication(pkg); log("绕过: " + pkg); }
                    catch (Exception e) { log("绕过失败 " + pkg + ": " + e.getMessage()); }
                }
            }
            try {
                b.addDisallowedApplication(getPackageName());
                log("[VPN] 已排除自身包名防止路由回环");
            } catch (Exception e) {
                log("[VPN] 排除自身异常: " + e.getMessage());
            }
            if (android.os.Build.VERSION.SDK_INT >= 29) b.setMetered(false);
            if (android.os.Build.VERSION.SDK_INT >= 22) {
                try { b.setUnderlyingNetworks(null); } catch (Exception ignored) {}
            }
            vpnFd = b.establish();
            if (vpnFd == null) { log("VpnService 建立失败"); stopSelf(); return; }
            int fd = vpnFd.getFd();
            log("tun fd: " + fd);

            File dir = new File(CORE_DIR); dir.mkdirs();
            FileOutputStream fos = new FileOutputStream(new File(CORE_CONF));
            fos.write(configStr.getBytes()); fos.close();

            // 创建 LocalServerSocket 用于向核心传递文件描述符
            String sockName = "xpanel_tun_" + System.currentTimeMillis();
            localServer = new LocalServerSocket(sockName);

            String libDir = getApplicationInfo().nativeLibraryDir;
            String coreBin = libDir + "/" + CORE_BIN_NAME;

            log("启动 X-Panel 原生网络核心...");
            String[] cmd = {coreBin, "-sock", "@" + sockName, "-conf", CORE_CONF};
            ProcessBuilder pb = new ProcessBuilder(cmd);
            pb.redirectErrorStream(true);
            coreProcess = pb.start();

            // 异步读取核心日志
            new Thread(() -> {
                try {
                    BufferedReader reader = new BufferedReader(new InputStreamReader(coreProcess.getInputStream()));
                    String line;
                    while ((line = reader.readLine()) != null) {
                        log(line);
                    }
                } catch (Exception ignored) {}
            }).start();

            // 等待核心连接并发送 FD
            LocalSocket client = localServer.accept();
            log("[IPC] 核心已连接，正在注入 TUN FD...");
            client.setFileDescriptorsForSend(new FileDescriptor[]{vpnFd.getFileDescriptor()});
            client.getOutputStream().write(1);
            client.getOutputStream().flush();
            try {
                // 等待对端确认收到
                client.getInputStream().read();
            } catch (Exception ignored) {}
            try { client.close(); } catch (Exception ignored) {}
            try { localServer.close(); } catch (Exception ignored) {}
            localServer = null;
            log("[IPC] TUN FD 注入成功，网络接管就绪");

            int exit = coreProcess.waitFor();
            log("[VPN] 核心进程退出: " + exit);
            stopSelf();
        } catch (Exception e) {
            if (localServer != null || coreProcess != null) log("[VPN] 异常: " + e.getMessage());
            stopSelf();
        }
    }

    @Override
    public void onDestroy() {
        doShutdown();
        if (sInstance == this) sInstance = null;
        super.onDestroy();
    }

    private void log(final String msg) {
        android.util.Log.i("XPANEL", msg);
        handler.post(() -> {
            Intent i = new Intent("com.example.xpanel.LOG");
            i.setPackage(getPackageName());
            i.putExtra("msg", msg);
            sendBroadcast(i);
        });
    }
}
