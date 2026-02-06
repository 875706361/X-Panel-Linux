package common

import (
	"crypto/rand"
	"math/big"
)

// RandomInt 返回一个 0 .. max-1 之间的随机整数（使用 crypto/rand）
func RandomInt(max int) int {
	if max <= 0 {
		return 0
	}
	n, err := rand.Int(rand.Reader, big.NewInt(int64(max)))
	if err != nil {
		return 0
	}
	return int(n.Int64())
}

// RandomLowerAndNum 返回指定长度的随机小写字母+数字字符串
func RandomLowerAndNum(length int) string {
	const charset = "abcdefghijklmnopqrstuvwxyz0123456789"
	b := make([]byte, length)
	for i := range b {
		b[i] = charset[RandomInt(len(charset))]
	}
	return string(b)
}

// GetRandomString 返回指定长度的随机字符串 (别名方法)
func GetRandomString(length int) string {
	return RandomLowerAndNum(length)
}
