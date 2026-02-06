package controller

import (
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
}

func (a *APIController) BackuptoTgbot(c *gin.Context) {
	a.Tgbot.SendBackupToAdmins()
}
