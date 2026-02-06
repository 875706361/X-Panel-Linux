package common

import (
	"encoding/base64"
	"golang.org/x/crypto/curve25519"
)

// DeriveRealityPublicKey 从 X25519 私钥推导出公钥
func DeriveRealityPublicKey(privateKey string) string {
	if privateKey == "" {
		return ""
	}
	// Xray 使用的是 RawURLEncoding (无填充，URL 安全)
	priv, err := base64.RawURLEncoding.DecodeString(privateKey)
	if err != nil {
		// 备选方案: 尝试标准 Base64 解码
		priv, err = base64.StdEncoding.DecodeString(privateKey)
		if err != nil {
			return ""
		}
	}
	if len(priv) != 32 {
		return ""
	}
	pub, err := curve25519.X25519(priv, curve25519.Basepoint)
	if err != nil {
		return ""
	}
	return base64.RawURLEncoding.EncodeToString(pub)
}
