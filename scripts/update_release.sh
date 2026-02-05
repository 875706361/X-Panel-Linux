#!/bin/bash
TOKEN="${GITHUB_TOKEN:-YOUR_GITHUB_TOKEN}"
REPO="875706361/X-Panel-Linux"
TAG="v26.2.6"

# 获取 Release ID
RELEASE_ID=$(curl -s -H "Authorization: token $TOKEN" "https://api.github.com/repos/$REPO/releases/tags/$TAG" | grep -m 1 '"id":' | awk '{print $2}' | sed 's/,//')

echo "Release ID: $RELEASE_ID"

# 获取并删除旧资产
ASSETS=$(curl -s -H "Authorization: token $TOKEN" "https://api.github.com/repos/$REPO/releases/$RELEASE_ID/assets" | grep '"id":' | awk '{print $2}' | sed 's/,//')

for ASSET_ID in $ASSETS; do
    echo "Deleting asset $ASSET_ID..."
    curl -s -X DELETE -H "Authorization: token $TOKEN" "https://api.github.com/repos/$REPO/releases/assets/$ASSET_ID"
done

# 上传新资产
for FILE in ./releases/*.tar.gz; do
    NAME=$(basename "$FILE")
    echo "Uploading $NAME..."
    curl -s -X POST -H "Authorization: token $TOKEN" \
        -H "Content-Type: application/octet-stream" \
        --data-binary @"$FILE" \
        "https://uploads.github.com/repos/$REPO/releases/$RELEASE_ID/assets?name=$NAME"
done

echo "All assets updated!"
