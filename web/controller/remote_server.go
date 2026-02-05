package controller

import (
	"net"
	"strconv"
	"time"

	"x-ui/database/model"
	"x-ui/logger"
	"x-ui/web/service"

	"github.com/gin-gonic/gin"
)

// RemoteServerController 远程服务器管理控制器
type RemoteServerController struct {
	BaseController
	remoteServerService service.RemoteServerService
}

// NewRemoteServerController 创建远程服务器控制器
func NewRemoteServerController(g *gin.RouterGroup) *RemoteServerController {
	a := &RemoteServerController{}
	a.initRouter(g)
	return a
}

func (a *RemoteServerController) initRouter(g *gin.RouterGroup) {
	g.GET("/list", a.getServers)
	g.POST("/add", a.addServer)
	g.POST("/delete/:id", a.deleteServer)
	g.GET("/:id/lastlink", a.getLastLink)
	g.POST("/:id/deploy", a.setupRelay)
	g.POST("/:id/ping", a.pingServer)
	g.GET("/stats", a.getStats)
}

// getServers 获取所有服务器列表
func (a *RemoteServerController) getServers(c *gin.Context) {
	servers, err := a.remoteServerService.GetAllServers()
	if err != nil {
		logger.Warning("获取服务器列表失败:", err)
		jsonMsg(c, "获取服务器列表失败", err)
		return
	}
	
	// 隐藏密码
	for _, server := range servers {
		server.Password = "******"
	}
	
	jsonObj(c, servers, nil)
}

// getStats 获取服务器统计信息
func (a *RemoteServerController) getStats(c *gin.Context) {
	normalCount, transitCount, err := a.remoteServerService.GetServerCount()
	if err != nil {
		jsonMsg(c, "获取统计信息失败", err)
		return
	}
	
	maxLimit := a.remoteServerService.GetMaxLimit()
	
	jsonObj(c, map[string]interface{}{
		"normalCount":  normalCount,
		"transitCount": transitCount,
		"maxLimit":     maxLimit,
	}, nil)
}

// addServer 添加服务器
func (a *RemoteServerController) addServer(c *gin.Context) {
	var server model.RemoteServer
	
	if err := c.ShouldBind(&server); err != nil {
		jsonMsg(c, "参数解析失败", err)
		return
	}
	
	err := a.remoteServerService.AddServer(&server)
	if err != nil {
		logger.Warning("添加服务器失败:", err)
		jsonMsg(c, err.Error(), err)
		return
	}
	
	logger.Info("成功添加远程服务器:", server.Name)
	jsonMsg(c, "添加成功", nil)
}

// deleteServer 删除服务器
func (a *RemoteServerController) deleteServer(c *gin.Context) {
	idStr := c.Param("id")
	id, err := strconv.Atoi(idStr)
	if err != nil {
		jsonMsg(c, "无效的服务器ID", err)
		return
	}
	
	err = a.remoteServerService.DeleteServer(id)
	if err != nil {
		logger.Warning("删除服务器失败:", err)
		jsonMsg(c, "删除失败", err)
		return
	}
	
	logger.Info("成功删除远程服务器, ID:", id)
	jsonMsg(c, "删除成功", nil)
}

// getLastLink 获取上次部署的链接
func (a *RemoteServerController) getLastLink(c *gin.Context) {
	idStr := c.Param("id")
	id, err := strconv.Atoi(idStr)
	if err != nil {
		jsonMsg(c, "无效的服务器ID", err)
		return
	}
	
	link, err := a.remoteServerService.GetLastLink(id)
	if err != nil {
		jsonMsg(c, err.Error(), err)
		return
	}
	
	jsonObj(c, link, nil)
}

// setupRelay 一键部署中转
func (a *RemoteServerController) setupRelay(c *gin.Context) {
	idStr := c.Param("id")
	id, err := strconv.Atoi(idStr)
	if err != nil {
		jsonMsg(c, "无效的服务器ID", err)
		return
	}
	
	link, err := a.remoteServerService.SetupRelay(id)
	if err != nil {
		logger.Warning("部署中转节点失败:", err)
		jsonMsg(c, err.Error(), err)
		return
	}
	
	jsonObj(c, link, nil)
}

// pingServer 检测服务器连通性
func (a *RemoteServerController) pingServer(c *gin.Context) {
	idStr := c.Param("id")
	id, err := strconv.Atoi(idStr)
	if err != nil {
		jsonMsg(c, "无效的服务器ID", err)
		return
	}
	
	// 获取服务器信息以进行Ping测试
	server, err := a.remoteServerService.GetServerByID(id)
	if err != nil {
		jsonMsg(c, "获取服务器信息失败", err)
		return
	}
	
	if server.LastLink == "" {
		jsonObj(c, map[string]interface{}{
			"result": -1,
			"msg":    "尚未部署",
		}, nil)
		return
	}
	
	// 使用简单的TCP Ping
	latency, err := a.remoteServerService.PingServer(id)
	if err != nil {
		jsonObj(c, map[string]interface{}{
			"result": -1,
			"msg":    err.Error(),
		}, nil)
		return
	}
	
	jsonObj(c, map[string]interface{}{
		"result": latency,
		"msg":    "连接正常",
	}, nil)
}

// TcpPing 执行TCP Ping测试
func TcpPing(host string, port int, timeout time.Duration) (int64, error) {
	start := time.Now()
	conn, err := net.DialTimeout("tcp", host+":"+strconv.Itoa(port), timeout)
	if err != nil {
		return -1, err
	}
	defer conn.Close()
	return time.Since(start).Milliseconds(), nil
}
