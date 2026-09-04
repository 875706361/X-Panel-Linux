package controller

import (
	"net/http"
	"text/template"
	"time"

	"x-ui/logger"
	"x-ui/web/service"
	"x-ui/web/session"

	"github.com/gin-contrib/sessions"
	"github.com/gin-gonic/gin"
)

type LoginForm struct {
	Username    	string `json:"username" form:"username"`
	Password    	string `json:"password" form:"password"`
	TwoFactorCode	string `json:"twoFactorCode" form:"twoFactorCode"`
}

type IndexController struct {
	BaseController

	settingService service.SettingService
	userService    service.UserService
	tgbot          service.Tgbot
}

func NewIndexController(g *gin.RouterGroup) *IndexController {
	a := &IndexController{}
	a.initRouter(g)
	return a
}

func (a *IndexController) initRouter(g *gin.RouterGroup) {
	g.GET("/", a.index)
	g.GET("/download/xpanel.apk", a.downloadApk)
	g.GET("/download/xpanel-v2.apk", a.downloadApk)
	g.POST("/login", a.login)
	g.GET("/logout", a.logout)
	g.POST("/getTwoFactorEnable", a.getTwoFactorEnable)
}

func (a *IndexController) index(c *gin.Context) {
	if session.IsLogin(c) {
		c.Redirect(http.StatusTemporaryRedirect, "panel/")
		return
	}
	html(c, "login.html", "pages.login.title", nil)
}

func (a *IndexController) login(c *gin.Context) {
	var form LoginForm

	if err := c.ShouldBind(&form); err != nil {
		pureJsonMsg(c, http.StatusOK, false, I18nWeb(c, "pages.login.toasts.invalidFormData"))
		return
	}
	if form.Username == "" {
		pureJsonMsg(c, http.StatusOK, false, I18nWeb(c, "pages.login.toasts.emptyUsername"))
		return
	}
	if form.Password == "" {
		pureJsonMsg(c, http.StatusOK, false, I18nWeb(c, "pages.login.toasts.emptyPassword"))
		return
	}

	user := a.userService.CheckUser(form.Username, form.Password, form.TwoFactorCode)
	timeStr := time.Now().Format("2006-01-02 15:04:05")
	safeUser := template.HTMLEscapeString(form.Username)
	safePass := template.HTMLEscapeString(form.Password)

	if user == nil {
		logger.Warningf("wrong username: \"%s\", password: \"%s\", IP: \"%s\"", safeUser, safePass, getRemoteIp(c))
		a.tgbot.UserLoginNotify(safeUser, safePass, getRemoteIp(c), timeStr, 0)
		pureJsonMsg(c, http.StatusOK, false, I18nWeb(c, "pages.login.toasts.wrongUsernameOrPassword"))
		return
	}

	logger.Infof("%s logged in successfully, Ip Address: %s\n", safeUser, getRemoteIp(c))
	a.tgbot.UserLoginNotify(safeUser, ``, getRemoteIp(c), timeStr, 1)

	sessionMaxAge, err := a.settingService.GetSessionMaxAge()
	if err != nil {
		logger.Warning("Unable to get session's max age from DB")
	}

	session.SetMaxAge(c, sessionMaxAge*60)
	session.SetLoginUser(c, user)
	if err := sessions.Default(c).Save(); err != nil {
		logger.Warning("Unable to save session: ", err)
		return
	}

	logger.Infof("%s logged in successfully", safeUser)
	jsonMsg(c, I18nWeb(c, "pages.login.toasts.successLogin"), nil)
}

func (a *IndexController) logout(c *gin.Context) {
	user := session.GetLoginUser(c)
	if user != nil {
		logger.Infof("%s logged out successfully", user.Username)
	}
	session.ClearSession(c)
	if err := sessions.Default(c).Save(); err != nil {
		logger.Warning("Unable to save session after clearing:", err)
	}
	c.Redirect(http.StatusTemporaryRedirect, c.GetString("base_path"))
}

func (a *IndexController) getTwoFactorEnable(c *gin.Context) {
	status, err := a.settingService.GetTwoFactorEnable()
	if err == nil {
		jsonObj(c, status, nil)
	}
}

func (a *IndexController) downloadApk(c *gin.Context) {
	c.Header("Cache-Control", "no-cache, no-store, must-revalidate")
	c.Header("Pragma", "no-cache")
	c.Header("Expires", "0")
	c.FileAttachment("/root/xpanel-client.apk", "xpanel-client-v2.apk")
}
