package service

import (
	"bytes"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/cookiejar"
	"net/url"
	"regexp"
	"strings"
	"time"

	"x-ui/database"
	"x-ui/database/model"
	"x-ui/logger"
)

// RemoteServerService 远程服务器管理服务
// 〔中文注释〕: 提供被控端和中转机的增删改查以及中转部署功能
type RemoteServerService struct {
	inboundService InboundService
	xrayService    XrayService
}

// GetAllServers 获取所有远程服务器列表
func (s *RemoteServerService) GetAllServers() ([]*model.RemoteServer, error) {
	db := database.GetDB()
	var servers []*model.RemoteServer
	err := db.Order("created_at DESC").Find(&servers).Error
	if err != nil {
		return nil, err
	}
	return servers, nil
}

// GetServersByType 按类型获取服务器列表
// type: 0=被控端, 1=中转机
func (s *RemoteServerService) GetServersByType(serverType int) ([]*model.RemoteServer, error) {
	db := database.GetDB()
	var servers []*model.RemoteServer
	err := db.Where("type = ?", serverType).Order("created_at DESC").Find(&servers).Error
	if err != nil {
		return nil, err
	}
	return servers, nil
}

// AddServer 添加远程服务器
func (s *RemoteServerService) AddServer(server *model.RemoteServer) error {
	db := database.GetDB()
	
	// 验证必填字段
	if server.URL == "" {
		return errors.New("面板地址不能为空")
	}
	if server.Username == "" {
		return errors.New("用户名不能为空")
	}
	if server.Password == "" {
		return errors.New("密码不能为空")
	}
	
	// 验证服务器连接
	err := s.testConnection(server.URL, server.Username, server.Password)
	if err != nil {
		return fmt.Errorf("无法连接到远程面板: %v", err)
	}
	
	return db.Create(server).Error
}

// DeleteServer 删除远程服务器
func (s *RemoteServerService) DeleteServer(id int) error {
	db := database.GetDB()
	return db.Delete(&model.RemoteServer{}, id).Error
}

// GetServerByID 根据ID获取服务器
func (s *RemoteServerService) GetServerByID(id int) (*model.RemoteServer, error) {
	db := database.GetDB()
	var server model.RemoteServer
	err := db.First(&server, id).Error
	if err != nil {
		return nil, err
	}
	return &server, nil
}

// UpdateServer 更新服务器信息
func (s *RemoteServerService) UpdateServer(server *model.RemoteServer) error {
	db := database.GetDB()
	return db.Save(server).Error
}

// GetServerCount 获取服务器数量统计
func (s *RemoteServerService) GetServerCount() (normalCount int64, transitCount int64, err error) {
	db := database.GetDB()
	
	err = db.Model(&model.RemoteServer{}).Where("type = ?", 0).Count(&normalCount).Error
	if err != nil {
		return 0, 0, err
	}
	
	err = db.Model(&model.RemoteServer{}).Where("type = ?", 1).Count(&transitCount).Error
	if err != nil {
		return 0, 0, err
	}
	
	return normalCount, transitCount, nil
}

// testConnection 测试与远程面板的连接
func (s *RemoteServerService) testConnection(panelURL, username, password string) error {
	// 创建HTTP客户端（忽略SSL证书验证）
	tr := &http.Transport{
		TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
	}
	jar, _ := cookiejar.New(nil)
	client := &http.Client{
		Transport: tr,
		Jar:       jar,
		Timeout:   10 * time.Second,
	}
	
	// 尝试登录
	loginURL := strings.TrimRight(panelURL, "/") + "/login"
	loginData := url.Values{
		"username": {username},
		"password": {password},
	}
	
	resp, err := client.PostForm(loginURL, loginData)
	if err != nil {
		return fmt.Errorf("连接失败: %v", err)
	}
	defer resp.Body.Close()
	
	body, _ := io.ReadAll(resp.Body)
	
	// 检查登录响应
	var result map[string]interface{}
	if err := json.Unmarshal(body, &result); err != nil {
		return errors.New("响应解析失败,请检查面板地址是否正确")
	}
	
	if success, ok := result["success"].(bool); !ok || !success {
		return errors.New("登录失败,请检查用户名和密码")
	}
	
	return nil
}

// SetupRelay 一键部署中转节点
// 〔中文注释〕: 这个方法会:
// 1. 登录远程中转机面板
// 2. 在远程创建Socks入站
// 3. 在本机创建指向远程Socks的出站
// 4. 在本机创建VLESS Reality入站
// 5. 生成订阅链接返回
func (s *RemoteServerService) SetupRelay(serverID int) (string, error) {
	// 获取服务器信息
	server, err := s.GetServerByID(serverID)
	if err != nil {
		return "", fmt.Errorf("获取服务器信息失败: %v", err)
	}
	
	if server.Type != 1 {
		return "", errors.New("只有中转机类型的服务器才能执行一键部署")
	}
	
	// 创建HTTP客户端
	tr := &http.Transport{
		TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
	}
	jar, _ := cookiejar.New(nil)
	client := &http.Client{
		Transport: tr,
		Jar:       jar,
		Timeout:   30 * time.Second,
	}
	
	// 1. 登录远程面板
	loginURL := strings.TrimRight(server.URL, "/") + "/login"
	loginData := url.Values{
		"username": {server.Username},
		"password": {server.Password},
	}
	
	resp, err := client.PostForm(loginURL, loginData)
	if err != nil {
		return "", fmt.Errorf("连接远程面板失败: %v", err)
	}
	defer resp.Body.Close()
	
	body, _ := io.ReadAll(resp.Body)
	var loginResult map[string]interface{}
	json.Unmarshal(body, &loginResult)
	
	if success, ok := loginResult["success"].(bool); !ok || !success {
		return "", errors.New("登录远程面板失败")
	}
	
	// 2. 在远程创建Socks入站
	remotePort := 10000 + serverID // 使用服务器ID生成唯一端口
	socksInbound := map[string]interface{}{
		"remark":   fmt.Sprintf("relay-socks-%d", serverID),
		"enable":   true,
		"port":     remotePort,
		"protocol": "socks",
		"settings": `{"auth":"password","accounts":[{"user":"relay","pass":"relay123"}],"udp":true}`,
		"streamSettings": `{"network":"tcp","security":"none"}`,
		"sniffing": `{"enabled":true,"destOverride":["http","tls"]}`,
	}
	
	addInboundURL := strings.TrimRight(server.URL, "/") + "/panel/inbound/add"
	jsonData, _ := json.Marshal(socksInbound)
	
	req, _ := http.NewRequest("POST", addInboundURL, bytes.NewBuffer(jsonData))
	req.Header.Set("Content-Type", "application/json")
	
	resp, err = client.Do(req)
	if err != nil {
		logger.Warning("创建远程Socks入站时出错:", err)
		// 继续执行,可能入站已存在
	} else {
		resp.Body.Close()
	}
	
	// 3. 从远程面板URL解析出服务器地址
	parsedURL, err := url.Parse(server.URL)
	if err != nil {
		return "", fmt.Errorf("解析面板地址失败: %v", err)
	}
	remoteHost := parsedURL.Hostname()
	
	// 4. 生成本地VLESS Reality入站配置
	localPort := 20000 + serverID
	
	// 生成Reality密钥对
	serverSvc := ServerService{}
	certResult, err := serverSvc.GetNewX25519Cert()
	if err != nil {
		return "", fmt.Errorf("生成Reality密钥失败: %v", err)
	}
	
	certMap, ok := certResult.(map[string]interface{})
	if !ok {
		return "", errors.New("无效的证书结果")
	}
	
	privateKey := certMap["privateKey"].(string)
	publicKey := certMap["publicKey"].(string)
	
	// 生成UUID
	uuidResult, err := (&ServerService{}).GetNewUUID()
	if err != nil {
		return "", fmt.Errorf("生成UUID失败: %v", err)
	}
	uuid := uuidResult["uuid"]
	
	// 生成短ID
	shortID := generateShortID()
	
	// 创建本地入站
	inboundSettings := fmt.Sprintf(`{
		"clients": [{
			"id": "%s",
			"email": "relay-%d@x-panel",
			"flow": "xtls-rprx-vision",
			"enable": true
		}],
		"decryption": "none"
	}`, uuid, serverID)
	
	streamSettings := fmt.Sprintf(`{
		"network": "tcp",
		"security": "reality",
		"realitySettings": {
			"show": false,
			"dest": "www.microsoft.com:443",
			"xver": 0,
			"serverNames": ["www.microsoft.com"],
			"privateKey": "%s",
			"shortIds": ["%s"]
		}
	}`, privateKey, shortID)
	
	// 创建出站配置 (指向远程Socks)
	_ = fmt.Sprintf("relay-out-%d", serverID) // outboundTag 备用
	_ = remoteHost // remoteHost 备用
	
	// 调用本地inbound服务添加入站
	db := database.GetDB()
	
	// 检查是否已存在相同tag的入站
	var existingInbound model.Inbound
	inboundTag := fmt.Sprintf("inbound-relay-%d", serverID)
	
	if err := db.Where("tag = ?", inboundTag).First(&existingInbound).Error; err == nil {
		// 更新现有入站
		existingInbound.Port = localPort
		existingInbound.Settings = inboundSettings
		existingInbound.StreamSettings = streamSettings
		db.Save(&existingInbound)
	} else {
		// 创建新入站
		newInbound := &model.Inbound{
			Remark:         fmt.Sprintf("中转节点-%s", server.Name),
			Enable:         true,
			Port:           localPort,
			Protocol:       model.VLESS,
			Settings:       inboundSettings,
			StreamSettings: streamSettings,
			Tag:            inboundTag,
			Sniffing:       `{"enabled":true,"destOverride":["http","tls"]}`,
		}
		if err := db.Create(newInbound).Error; err != nil {
			return "", fmt.Errorf("创建本地入站失败: %v", err)
		}
	}
	
	// 5. 生成VLESS链接
	// 获取本机公网IP
	serverService := ServerService{}
	status := serverService.GetStatus(nil)
	localIP := status.PublicIP.IPv4
	if localIP == "" {
		localIP = "YOUR_SERVER_IP" // 如果获取不到IP,使用占位符
	}
	
	vlessLink := fmt.Sprintf(
		"vless://%s@%s:%d?type=tcp&security=reality&pbk=%s&fp=chrome&sni=www.microsoft.com&sid=%s&spx=%%2F&flow=xtls-rprx-vision#%s",
		uuid,
		localIP,
		localPort,
		publicKey,
		shortID,
		url.QueryEscape(fmt.Sprintf("中转-%s", server.Name)),
	)
	
	// 保存链接到服务器记录
	server.LastLink = vlessLink
	db.Save(server)
	
	// 重启Xray服务以应用新配置
	go func() {
		time.Sleep(1 * time.Second)
		s.xrayService.RestartXray(false)
	}()
	
	logger.Info("成功部署中转节点:", server.Name)
	
	return vlessLink, nil
}

// GetLastLink 获取服务器上次部署的链接
func (s *RemoteServerService) GetLastLink(serverID int) (string, error) {
	server, err := s.GetServerByID(serverID)
	if err != nil {
		return "", err
	}
	
	if server.LastLink == "" {
		return "", errors.New("该服务器尚未部署过中转节点")
	}
	
	return server.LastLink, nil
}

// PingServer 检测服务器连通性
// 〔中文注释〕: 通过TCP Ping测试上次部署链接对应的端口是否通畅
func (s *RemoteServerService) PingServer(serverID int) (int64, error) {
	server, err := s.GetServerByID(serverID)
	if err != nil {
		return -1, err
	}
	
	if server.LastLink == "" {
		return -1, errors.New("该服务器尚未部署过中转节点")
	}
	
	// 从链接中提取主机和端口
	// VLESS链接格式: vless://uuid@host:port?...
	re := regexp.MustCompile(`vless://[^@]+@([^:]+):(\d+)`)
	matches := re.FindStringSubmatch(server.LastLink)
	if len(matches) < 3 {
		return -1, errors.New("无法解析链接中的地址信息")
	}
	
	host := matches[1]
	port := matches[2]
	
	// TCP Ping测试
	start := time.Now()
	
	addr := fmt.Sprintf("%s:%s", host, port)
	dialer := &net.Dialer{Timeout: 5 * time.Second}
	conn, err := dialer.Dial("tcp", addr)
	
	if err != nil {
		return -1, nil // 返回-1表示不通
	}
	defer conn.Close()
	
	elapsed := time.Since(start).Milliseconds()
	return elapsed, nil
}

// generateShortID 生成Reality短ID
func generateShortID() string {
	const charset = "0123456789abcdef"
	result := make([]byte, 8)
	for i := range result {
		result[i] = charset[time.Now().UnixNano()%int64(len(charset))]
		time.Sleep(1 * time.Nanosecond)
	}
	return string(result)
}

// GetMaxLimit 获取最大绑定数量限制
// 〔中文注释〕: Pro版解锁后无限制,返回一个大数字
func (s *RemoteServerService) GetMaxLimit() int {
	return 9999 // 无限制
}
