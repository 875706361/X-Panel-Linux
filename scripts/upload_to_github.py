import requests
import os
import sys

# 请在运行前设置环境变量: export GITHUB_TOKEN=你的令牌
token = os.environ.get("GITHUB_TOKEN")
if not token:
    print("错误: 请设置 GITHUB_TOKEN 环境变量")
    sys.exit(1)

owner = "875706361"
repo = "X-Panel-Linux"
tag = "v26.2.6"

headers = {
    "Authorization": f"token {token}",
    "Accept": "application/vnd.github.v3+json"
}

# 1. Create Release
release_data = {
    "tag_name": tag,
    "name": f"X-Panel {tag} - Linux Version",
    "body": "This is a Linux-only version of X-Panel with all Windows support removed.\n\n### Changes:\n- Completely removed Windows support.\n- Cleaned all Windows-specific code.\n- Optimized for Linux environments.\n- Updated all project links to point to this repository.",
    "draft": False,
    "prerelease": False
}

print(f"Creating release for tag {tag}...")
url = f"https://api.github.com/repos/{owner}/{repo}/releases"
response = requests.post(url, headers=headers, json=release_data)

if response.status_code == 201:
    release = response.json()
    print(f"Successfully created release: {release['html_url']}")
elif response.status_code == 422:
    print("Release already exists, fetching it...")
    url = f"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}"
    response = requests.get(url, headers=headers)
    release = response.json()
    print(f"Found existing release: {release['html_url']}")
else:
    print(f"Failed to create/fetch release: {response.status_code}")
    print(response.text)
    sys.exit(1)

release_id = release['id']
upload_url = release['upload_url'].split('{')[0]

# 2. Upload Assets
assets_dir = "/clay/11/X-Panel/releases"
if not os.path.exists(assets_dir):
     # 尝试相对路径
     assets_dir = "./releases"

assets = [f for f in os.listdir(assets_dir) if f.endswith(".tar.gz")]

print(f"Uploading {len(assets)} assets to release {release_id}...")

for asset_name in assets:
    asset_path = os.path.join(assets_dir, asset_name)
    print(f"Uploading {asset_name}...")
    
    with open(asset_path, 'rb') as f:
        upload_headers = headers.copy()
        upload_headers["Content-Type"] = "application/octet-stream"
        
        # Check if asset already exists
        for existing_asset in release.get('assets', []):
            if existing_asset['name'] == asset_name:
                print(f"Asset {asset_name} already exists, deleting it first...")
                requests.delete(existing_asset['url'], headers=headers)
                break
        
        upload_response = requests.post(
            f"{upload_url}?name={asset_name}",
            headers=upload_headers,
            data=f
        )
        
        if upload_response.status_code == 201:
            print(f"Successfully uploaded {asset_name}")
        else:
            print(f"Failed to upload {asset_name}: {upload_response.status_code}")
            print(upload_response.text)

print("All tasks completed!")
