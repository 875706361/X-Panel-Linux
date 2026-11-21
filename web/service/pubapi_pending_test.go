package service

import (
	"encoding/json"
	"testing"
	"time"

	"x-ui/database"
	"x-ui/database/model"
)

// 负值到期 = 「首连后 N 天」的时长。主流版本写毫秒，个别写入路径写秒；
// 服务端必须归一到毫秒再换算成未来时间点，否则 -2592000（30 天写成秒）
// 只会得到 43 分钟后的「到期」，客户端显示「未激活 (首连起1天)」。
func TestPubClientViewNegativeDurationUnits(t *testing.T) {
	cases := []struct {
		name     string
		raw      int64 // 客户端 settings 里的 expiryTime
		wantDays float64
	}{
		{"毫秒 -30天", -30 * 86400000, 30},
		{"秒 -30天", -30 * 86400, 30},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if err := database.InitDB(t.TempDir() + "/x-ui.db"); err != nil {
				t.Fatalf("InitDB: %v", err)
			}
			defer database.CloseDB()

			id := "11111111-2222-3333-4444-555555555555"
			settings, _ := json.Marshal(map[string]any{"clients": []map[string]any{{
				"id": id, "email": "u1", "enable": true, "expiryTime": tc.raw,
			}}})
			ib := model.Inbound{Port: 443, Protocol: "vless", Enable: true, Settings: string(settings)}
			if err := database.GetDB().Create(&ib).Error; err != nil {
				t.Fatalf("插入入站: %v", err)
			}

			view, ok := (&InboundService{}).GetPubClientView(id)
			if !ok {
				t.Fatal("未查到客户端视图")
			}
			if pending, _ := view["expiryPending"].(bool); !pending {
				t.Fatal("负值到期应标记 expiryPending")
			}
			exp, _ := view["expiryTime"].(int64)
			left := time.Until(time.UnixMilli(exp)).Hours()
			if left < (tc.wantDays-1)*24 || left > (tc.wantDays+1)*24 {
				t.Fatalf("负值单位换算错误: 期望约 %.0f 天，实际剩余 %.1f 小时", tc.wantDays, left)
			}
		})
	}
}
