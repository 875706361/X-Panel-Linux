package model

import "time"

// RemoteServer 远程服务器管理模型
// 〔中文注释〕: 用于存储被控端VPS和中转机VPS的信息
type RemoteServer struct {
	ID        int       `json:"ID" gorm:"primaryKey;autoIncrement"`
	Name      string    `json:"name" form:"name"`           // 备注名称
	URL       string    `json:"url" form:"url"`             // 面板地址
	Username  string    `json:"username" form:"username"`   // 登录用户名
	Password  string    `json:"password" form:"password"`   // 登录密码
	Type      int       `json:"type" form:"type"`           // 类型: 0=被控端, 1=中转机
	LastLink  string    `json:"last_link" gorm:"column:last_link"` // 上次部署生成的链接
	CreatedAt time.Time `json:"CreatedAt" gorm:"autoCreateTime"`
	UpdatedAt time.Time `json:"UpdatedAt" gorm:"autoUpdateTime"`
}

// TableName 指定表名
func (RemoteServer) TableName() string {
	return "remote_servers"
}
