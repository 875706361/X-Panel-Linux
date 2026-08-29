package controller

import (
	"net/http"

	"x-ui/web/service"

	"github.com/gin-gonic/gin"
)

type APIController struct {
	BaseController
	inboundController      *InboundController
	serverController       *ServerController
	remoteServerController *RemoteServerController
	Tgbot                  service.Tgbot
	serverService          service.ServerService
}

func NewAPIController(g *gin.RouterGroup) *APIController {
	a := &APIController{}
	a.initRouter(g)
	return a
}

func (a *APIController) initRouter(g *gin.RouterGroup) {
	// Main API group
	api := g.Group("/panel/api")
	api.Use(a.checkLogin)

	// Inbounds API
	inbounds := api.Group("/inbounds")
	a.inboundController = NewInboundController(inbounds)

	// Server API
	server := api.Group("/server")
	a.serverController = NewServerController(server, a.serverService)

	// Remote Servers API (服务器管理功能)
	remoteServers := api.Group("/servers")
	a.remoteServerController = NewRemoteServerController(remoteServers)

	// Extra routes
	api.GET("/backuptotgbot", a.BackuptoTgbot)

	// 公开查询: 按 uuid 查流量/到期, 无需登录 (app 用)
	pub := g.Group("/panel/pub")
	pub.GET("/traffic/:id", a.PubTrafficByID)
}

// 公开查询接口: GET /panel/pub/traffic/:id  (id = 客户端 uuid)
func (a *APIController) PubTrafficByID(c *gin.Context) {
	id := c.Param("id")
	svc := &service.InboundService{}
	traffics, err := svc.GetClientTrafficByID(id)
	if err != nil || len(traffics) == 0 {
		c.JSON(http.StatusOK, gin.H{"success": false, "msg": "not found"})
		return
	}
	t := traffics[0]
	c.JSON(http.StatusOK, gin.H{"success": true, "obj": gin.H{
		"up": t.Up, "down": t.Down, "total": t.Total, "expiryTime": t.ExpiryTime, "enable": t.Enable, "email": t.Email,
	}})
}

func (a *APIController) BackuptoTgbot(c *gin.Context) {
	a.Tgbot.SendBackupToAdmins()
}
