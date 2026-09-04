package main

import (
	"bytes"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/xjasonlyu/tun2socks/v2/engine"
	"github.com/xtls/xray-core/core"
	_ "github.com/xtls/xray-core/main/distro/all"
)

func recvFd(sockPath string) (int, error) {
	targetName := sockPath
	if !strings.HasPrefix(targetName, "@") && !strings.HasPrefix(targetName, "/") {
		targetName = "@" + sockPath
	}
	log.Printf("[XPANEL-CORE] dialing unix socket: %s", targetName)

	var c *net.UnixConn
	var err error
	for i := 0; i < 50; i++ {
		addr := &net.UnixAddr{Name: targetName, Net: "unix"}
		c, err = net.DialUnix("unix", nil, addr)
		if err == nil {
			break
		}
		time.Sleep(100 * time.Millisecond)
	}
	if err != nil {
		return -1, fmt.Errorf("dial unix %s failed: %w", targetName, err)
	}
	defer c.Close()

	buf := make([]byte, 32)
	oob := make([]byte, 128)
	_, oobn, _, _, err := c.ReadMsgUnix(buf, oob)
	if err != nil {
		return -1, fmt.Errorf("read msg unix failed: %w", err)
	}

	scms, err := syscall.ParseSocketControlMessage(oob[:oobn])
	if err != nil {
		return -1, fmt.Errorf("parse control msg failed: %w", err)
	}
	if len(scms) == 0 {
		return -1, fmt.Errorf("no socket control message received")
	}

	fds, err := syscall.ParseUnixRights(&scms[0])
	if err != nil {
		return -1, fmt.Errorf("parse unix rights failed: %w", err)
	}
	if len(fds) == 0 {
		return -1, fmt.Errorf("no fds received in rights")
	}

		// 回复确认信号
	c.Write([]byte{1})
	return fds[0], nil
}

func main() {
	sock := flag.String("sock", "", "unix domain socket path for receiving tun fd")
	conf := flag.String("conf", "", "xray config json file path")
	flag.Parse()

	log.Printf("[XPANEL-CORE] starting with sock=%s, conf=%s", *sock, *conf)

	// 1. 读取并启动 Xray 核心
	confData, err := os.ReadFile(*conf)
	if err != nil {
		log.Fatalf("[XPANEL-CORE] read config failed: %v", err)
	}

	xrayConfig, err := core.LoadConfig("json", bytes.NewReader(confData))
	if err != nil {
		log.Fatalf("[XPANEL-CORE] load xray config failed: %v", err)
	}

	server, err := core.New(xrayConfig)
	if err != nil {
		log.Fatalf("[XPANEL-CORE] init xray core failed: %v", err)
	}

	if err := server.Start(); err != nil {
		log.Fatalf("[XPANEL-CORE] start xray server failed: %v", err)
	}
	log.Printf("[XPANEL-CORE] xray server started on 127.0.0.1:10808")

	// 2. 从 Unix Socket 接收 Tun FD
	tunFd, err := recvFd(*sock)
	if err != nil {
		log.Fatalf("[XPANEL-CORE] recv tun fd failed: %v", err)
	}
	log.Printf("[XPANEL-CORE] successfully received tun fd: %d", tunFd)

	// 3. 启动 tun2socks
	key := new(engine.Key)
	key.Device = fmt.Sprintf("fd://%d", tunFd)
	key.Proxy = "socks5://127.0.0.1:10808"

	engine.Insert(key)
	engine.Start()
	log.Printf("[XPANEL-CORE] tun2socks connected: tun0 (fd %d) <-> 127.0.0.1:10808", tunFd)

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	engine.Stop()
	server.Close()
	log.Printf("[XPANEL-CORE] stopped cleanly")
}
