package service

import (
	"encoding/json"
	"os"
	"testing"

	"x-ui/database"
)

// TestPubClientViewRealDB 用线上面板数据库快照回放 /panel/pub/traffic/:id 的取值逻辑。
// 线上 47/48 个入站把到期写在 inbounds.expiry_time，客户端层与 client_traffics 全为 0；
// 旧代码只读客户端/统计行，于是 App 收到 expiryTime=0 显示「长期有效」。
// 设 PUBAPI_TEST_DB=/path/x-ui.db 运行：会复制到临时目录，避免动到快照。
func TestPubClientViewRealDB(t *testing.T) {
	src := os.Getenv("PUBAPI_TEST_DB")
	if src == "" {
		t.Skip("PUBAPI_TEST_DB 未设置，跳过真实库回放")
	}
	raw, err := os.ReadFile(src)
	if err != nil {
		t.Fatalf("读取快照失败: %v", err)
	}
	tmp := t.TempDir() + "/x-ui.db"
	if err := os.WriteFile(tmp, raw, 0o600); err != nil {
		t.Fatalf("写入临时库失败: %v", err)
	}
	if err := database.InitDB(tmp); err != nil {
		t.Fatalf("InitDB: %v", err)
	}
	t.Cleanup(func() { _ = database.CloseDB() })

	db := database.GetDB()
	type row struct {
		Port       int
		ExpiryTime int64
		Total      int64
		Settings   string
	}
	var rows []row
	if err := db.Raw("SELECT port, expiry_time, total, settings FROM inbounds ORDER BY id").Scan(&rows).Error; err != nil {
		t.Fatalf("查询入站: %v", err)
	}

	svc := &InboundService{}
	checked := 0
	for _, r := range rows {
		id := firstClientID(r.Settings)
		if id == "" {
			continue
		}
		view, ok := svc.GetPubClientView(id)
		if !ok {
			t.Errorf("port %d uuid %s 查询失败", r.Port, id)
			continue
		}
		exp, _ := view["expiryTime"].(int64)
		if r.ExpiryTime > 0 && exp != r.ExpiryTime {
			t.Errorf("port %d: 期望回落到入站到期 %d，实际 %d（客户端会显示长期有效）", r.Port, r.ExpiryTime, exp)
		}
		if r.ExpiryTime > 0 && exp < 1e11 {
			t.Errorf("port %d: 到期未归一为毫秒: %d", r.Port, exp)
		}
		if r.Total > 0 {
			if got, _ := view["total"].(int64); got != r.Total {
				t.Errorf("port %d: 期望 total=%d，实际 %d", r.Port, r.Total, got)
			}
		}
		if pending, _ := view["expiryPending"].(bool); pending {
			t.Errorf("port %d: 正数到期被误标为未激活", r.Port)
		}
		checked++
	}
	if checked == 0 {
		t.Fatal("没有任何入站被验证")
	}
	t.Logf("已回放验证 %d 个入站", checked)
}

// firstClientID 从入站 settings JSON 里取第一个客户端 id。
func firstClientID(settings string) string {
	var v struct {
		Clients []struct {
			ID string `json:"id"`
		} `json:"clients"`
	}
	if err := json.Unmarshal([]byte(settings), &v); err != nil {
		return ""
	}
	if len(v.Clients) == 0 {
		return ""
	}
	return v.Clients[0].ID
}
