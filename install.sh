#!/bin/bash

# ==========================================================
# X-Panel 安装脚本
# 作者: X-Panel
# ==========================================================

red='\033[0;31m'
green='\033[0;32m'
blue='\033[0;34m'
yellow='\033[0;33m'
plain='\033[0m'

# check root
[[ $EUID -ne 0 ]] && echo -e "${red}致命错误: ${plain} 请使用 root 权限运行此脚本\n" && exit 1


# ----------------------------------------------------------
# 函数：免费基础版安装逻辑 (install_free_version) 
# ----------------------------------------------------------
install_free_version() {
    echo ""
    echo -e "${green}您选择了安装 【X-Panel 标准版】${plain}"
    echo ""
    echo -e "${green}即将开始执行标准安装流程...${plain}"
    sleep 2

    cur_dir=$(pwd)

    # Check OS and set release variable
    if [[ -f /etc/os-release ]]; then
        source /etc/os-release
        release=$ID
    elif [[ -f /usr/lib/os-release ]]; then
        source /usr/lib/os-release
        release=$ID
    else
        echo ""
        echo -e "${red}检查服务器操作系统失败，请联系作者!${plain}" >&2
        exit 1
    fi
    echo ""
    echo -e "${green}---------->>>>>目前服务器的操作系统为: $release${plain}"

    arch() {
        case "$(uname -m)" in
            x86_64 | x64 | amd64 ) echo 'amd64' ;;
            i*86 | x86 ) echo '386' ;;
            armv8* | armv8 | arm64 | aarch64 ) echo 'arm64' ;;
            armv7* | armv7 | arm ) echo 'armv7' ;;
            armv6* | armv6 ) echo 'armv6' ;;
            armv5* | armv5 ) echo 'armv5' ;;
            s390x) echo 's390x' ;;
            *) echo -e "${green}不支持的CPU架构! ${plain}" && rm -f install.sh && exit 1 ;;
        esac
    }

    echo ""
    echo -e "${yellow}---------->>>>>当前系统的架构为: $(arch)${plain}"
    echo ""
    
    # Check current version if installed
    xui_version=$(/usr/local/x-ui/x-ui -v 2>/dev/null)
    if [[ -z "$xui_version" ]]; then
        echo ""
        echo -e "${red}------>>>当前服务器没有安装任何 x-ui 系列代理面板${plain}"
    else
        echo -e "${green}---------->>>>>当前代理面板的版本为: ${red}〔X-Panel面板〕v${xui_version}${plain}"
    fi
    echo ""
    
    # We will get latest version in install_x-ui via API
    sleep 2

    os_version=$(grep -i version_id /etc/os-release | cut -d \" -f2 | cut -d . -f1)

    # Simple OS version check (omitted detailed checks for brevity, assuming standard OS)
    if [[ "${release}" == "centos" ]] && [[ ${os_version} -lt 8 ]]; then
        echo -e "${red} 请使用 CentOS 8 或更高版本 ${plain}\n" && exit 1
    elif [[ "${release}" == "ubuntu" ]] && [[ ${os_version} -lt 20 ]]; then
        echo -e "${red} 请使用 Ubuntu 20 或更高版本!${plain}\n" && exit 1
    elif [[ "${release}" == "debian" ]] && [[ ${os_version} -lt 11 ]]; then
        echo -e "${red} 请使用 Debian 11 或更高版本 ${plain}\n" && exit 1
    fi

    install_base() {
        case "${release}" in
        ubuntu | debian | armbian)
            apt-get update && apt-get install -y -q wget curl sudo tar tzdata
            ;;
        centos | rhel | almalinux | rocky | ol)
            yum -y --exclude=kernel* update && yum install -y -q wget curl sudo tar tzdata
            ;;
        fedora | amzn | virtuozzo)
            dnf -y --exclude=kernel* update && dnf install -y -q wget curl sudo tar tzdata
            ;;
        arch | manjaro | parch)
            pacman -Sy && pacman -S --noconfirm wget curl sudo tar tzdata
            ;;
        alpine)
            apk update && apk add --no-cache wget curl sudo tar tzdata
            ;;
        opensuse-tumbleweed)
            zypper refresh && zypper -q install -y wget curl sudo tar timezone
            ;;
        *)
            apt-get update && apt-get install -y -q wget curl sudo tar tzdata
            ;;
        esac
    }

    gen_random_string() {
        local length="$1"
        local random_string=$(LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | fold -w "$length" | head -n 1)
        echo "$random_string"
    }

    config_after_install() {
        echo -e "${yellow}安装/更新完成！ 为了您的面板安全，建议修改面板设置 ${plain}"
        echo ""
        read -p "$(echo -e "${green}想继续修改吗？${red}选择“n”以保留旧设置${plain} [y/n]？--->>请输入：")" config_confirm
        if [[ "${config_confirm}" == "y" || "${config_confirm}" == "Y" ]]; then
            read -p "请设置您的用户名: " config_account
            echo -e "${yellow}您的用户名将是: ${config_account}${plain}"
            read -p "请设置您的密码: " config_password
            echo -e "${yellow}您的密码将是: ${config_password}${plain}"
            read -p "请设置面板端口: " config_port
            echo -e "${yellow}您的面板端口号为: ${config_port}${plain}"
            read -p "请设置面板登录访问路径: " config_webBasePath
            echo -e "${yellow}您的面板访问路径为: ${config_webBasePath}${plain}"
            echo -e "${yellow}正在初始化，请稍候...${plain}"
            /usr/local/x-ui/x-ui setting -username ${config_account} -password ${config_password}
            echo -e "${yellow}用户名和密码设置成功!${plain}"
            /usr/local/x-ui/x-ui setting -port ${config_port}
            echo -e "${yellow}面板端口号设置成功!${plain}"
            /usr/local/x-ui/x-ui setting -webBasePath ${config_webBasePath}
            echo -e "${yellow}面板登录访问路径设置成功!${plain}"
            echo ""
        else
            echo ""
            sleep 1
            echo -e "${red}--------------->>>>Cancel...--------------->>>>>>>取消修改...${plain}"
            echo ""
            if [[ ! -f "/etc/x-ui/x-ui.db" ]]; then
                local usernameTemp=$(head -c 10 /dev/urandom | base64)
                local passwordTemp=$(head -c 10 /dev/urandom | base64)
                local webBasePathTemp=$(gen_random_string 15)
                /usr/local/x-ui/x-ui setting -username ${usernameTemp} -password ${passwordTemp} -webBasePath ${webBasePathTemp}
                echo ""
                echo -e "${yellow}检测到为全新安装，出于安全考虑将生成随机登录信息:${plain}"
                echo -e "###############################################"
                echo -e "${green}用户名: ${usernameTemp}${plain}"
                echo -e "${green}密  码: ${passwordTemp}${plain}"
                echo -e "${green}访问路径: ${webBasePathTemp}${plain}"
                echo -e "###############################################"
                echo -e "${green}如果您忘记了登录信息，可以在安装后通过 x-ui 命令然后输入${red}数字 10 选项${green}进行查看${plain}"
            else
                echo -e "${green}此次操作属于版本升级，保留之前旧设置项，登录方式保持不变${plain}"
                echo ""
                echo -e "${green}如果您忘记了登录信息，您可以通过 x-ui 命令然后输入${red}数字 10 选项${green}进行查看${plain}"
                echo ""
                echo ""
            fi
        fi
        sleep 1
        echo -e ">>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>"
        echo ""
        /usr/local/x-ui/x-ui migrate
    }

    install_x-ui() {
        cd /usr/local/

        if [[ -z "$GITHUB_TOKEN" ]]; then
            echo -e "${red}错误: 检测到私有仓库安装，未找到 GITHUB_TOKEN 环境变量。${plain}"
            echo -e "${yellow}请在运行脚本前设置: export GITHUB_TOKEN=your_token${plain}"
            exit 1
        fi
        
        # Helper for authenticated API calls
        api_req() {
            curl -s -H "Authorization: token $GITHUB_TOKEN" "$@"
        }

        # Download resources
        if [ $# == 0 ]; then
            release_json=$(api_req "https://api.github.com/repos/875706361/X-Panel-Linux/releases/latest")
            last_version=$(echo "$release_json" | grep '"tag_name":' | sed -E 's/.*"([^"]+)".*/\1/')
            
            if [[ ! -n "$last_version" ]]; then
                echo -e "${red}获取 X-Panel 版本失败，请检查 GITHUB_TOKEN 是否有效${plain}"
                exit 1
            fi
            echo ""
            echo -e "-----------------------------------------------------"
            echo -e "${green}--------->>获取 X-Panel 最新版本：${yellow}${last_version}${plain}${green}，开始安装...${plain}"
            echo -e "-----------------------------------------------------"
        else
            last_version=$1
            # For specific tag, get that release
            release_json=$(api_req "https://api.github.com/repos/875706361/X-Panel-Linux/releases/tags/${last_version}")
            if [[ $(echo "$release_json" | grep "Not Found") ]]; then
                 echo -e "${red}未找到版本 $1${plain}"
                 exit 1
            fi
            echo ""
            echo -e "--------------------------------------------"
            echo -e "${green}---------------->>>>开始安装 X-Panel 免费基础版$1${plain}"
            echo -e "--------------------------------------------"
        fi

        sleep 2
        echo -e "${green}---------------->>>>>>>>>安装进度50%${plain}"
        
        # Parse Asset ID for current architecture
        target_arch=$(arch)
        target_file="x-ui-linux-${target_arch}.tar.gz"
        
        asset_id=""
        if command -v python3 &>/dev/null; then
             asset_id=$(echo "$release_json" | python3 -c "import sys, json; data = json.load(sys.stdin); print(next((a['id'] for a in data.get('assets', []) if a['name'] == '${target_file}'), ''))")
        else
             echo -e "${yellow}警告: 未检测到 python3，使用文本匹配模式解析资源 ID${plain}"
             asset_id=$(echo "$release_json" | grep -C 10 "${target_file}" | grep '"id":' | head -n 1 | awk '{print $2}' | tr -d ',')
        fi
        
        if [[ ! -n "$asset_id" ]]; then
            echo -e "${red}无法找到架构 ${target_arch} 的下载资源 (Asset ID Not Found)${plain}"
            exit 1
        fi

        echo -e "${green}---------------->>>>>>>>>>>>>>>>>>>>>下载资源 ID: ${asset_id}${plain}"
        
        # Download Asset
        curl -L -H "Authorization: token $GITHUB_TOKEN" -H "Accept: application/octet-stream" \
             "https://api.github.com/repos/875706361/X-Panel-Linux/releases/assets/$asset_id" \
             -o "/usr/local/x-ui-linux-${target_arch}.tar.gz"

        if [[ $? -ne 0 ]]; then
            echo -e "${red}下载 X-Panel 失败${plain}"
            exit 1
        fi

        # Download x-ui.sh script (using Raw content API)
        api_req -H "Accept: application/vnd.github.v3.raw" \
            "https://api.github.com/repos/875706361/X-Panel-Linux/contents/x-ui.sh" \
            -o /usr/bin/x-ui-temp

        # Stop x-ui service and remove old resources
        if [[ -e /usr/local/x-ui/ ]]; then
            systemctl stop x-ui
            rm /usr/local/x-ui/ -rf
        fi
        
        sleep 3
        echo -e "${green}------->>>>>>>>>>>检查并保存安装目录${plain}"
        echo ""
        tar zxvf x-ui-linux-$(arch).tar.gz
        rm x-ui-linux-$(arch).tar.gz -f
        
        cd x-ui
        chmod +x x-ui
        chmod +x x-ui.sh

        # Check the system's architecture and rename the file accordingly
        if [[ $(arch) == "armv5" || $(arch) == "armv6" || $(arch) == "armv7" ]]; then
            mv bin/xray-linux-$(arch) bin/xray-linux-arm
            chmod +x bin/xray-linux-arm
        fi
        chmod +x x-ui bin/xray-linux-$(arch)

        # Update x-ui cli and se set permission
        mv -f /usr/bin/x-ui-temp /usr/bin/x-ui
        chmod +x /usr/bin/x-ui
        sleep 2
        echo -e "${green}------->>>>>>>>>>>保存成功${plain}"
        sleep 2
        echo ""
        config_after_install
        
        # Inner function for show access info
        show_access_info() {
            # 获取 IPv4 和 IPv6 地址
            v4=$(curl -s4m8 http://ip.sb -k)
            v6=$(curl -s6m8 http://ip.sb -k)
            local existing_webBasePath=$(/usr/local/x-ui/x-ui setting -show true | grep -Eo 'webBasePath（访问路径）: .+' | awk '{print $2}') 
            local existing_port=$(/usr/local/x-ui/x-ui setting -show true | grep -Eo 'port（端口号）: .+' | awk '{print $2}') 
            local existing_cert=$(/usr/local/x-ui/x-ui setting -getCert true | grep -Eo 'cert: .+' | awk '{print $2}')
            local existing_key=$(/usr/local/x-ui/x-ui setting -getCert true | grep -Eo 'key: .+' | awk '{print $2}')

            echo ""
            if [[ -n "$existing_cert" && -n "$existing_key" ]]; then
                domain=$(basename "$(dirname "$existing_cert")")
                echo -e "${green}面板已开启 SSL 保护${plain}"
                echo -e "${green}登录地址: https://${domain}:${existing_port}${existing_webBasePath}${plain}"
            else
                echo -e "${yellow}面板当前处于 HTTP 模式（未配置证书）${plain}"
                if [[ -n $v4 ]]; then
                    echo -e "${green}登录地址: http://$v4:${existing_port}${existing_webBasePath}${plain}"
                fi
                if [[ -n $v6 ]]; then
                    echo -e "${green}IPv6 登录地址: http://[$v6]:${existing_port}${existing_webBasePath}${plain}"
                fi
            fi
            echo ""
        }
        
        show_access_info

        cp -f x-ui.service /etc/systemd/system/
        systemctl daemon-reload
        systemctl enable x-ui
        systemctl start x-ui
        systemctl stop warp-go >/dev/null 2>&1
        wg-quick down wgcf >/dev/null 2>&1
        systemctl start warp-go >/dev/null 2>&1
        wg-quick up wgcf >/dev/null 2A>&1

        echo ""
        echo -e "------->>>>${green}X-Panel 免费基础版 ${last_version}${plain}<<<<安装成功，正在启动..."
        sleep 1
        echo ""
        echo -e "         ---------------------"
        echo -e "         |${green}X-Panel 控制菜单用法 ${plain}|${plain}"
        echo -e "         |  ${yellow}一个更好的面板   ${plain}|${plain}"   
        echo -e "         | ${yellow}基于Xray Core构建 ${plain}|${plain}"  
        echo -e "--------------------------------------------"
        echo -e "x-ui              - 进入管理脚本"
        echo -e "x-ui start        - 启动 X-Panel 面板"
        echo -e "x-ui stop         - 关闭 X-Panel 面板"
        echo -e "x-ui restart      - 重启 X-Panel 面板"
        echo -e "x-ui status       - 查看 X-Panel 状态"
        echo -e "x-ui settings     - 查看当前设置信息"
        echo -e "x-ui enable       - 启用 X-Panel 开机启动"
        echo -e "x-ui disable      - 禁用 X-Panel 开机启动"
        echo -e "x-ui log          - 查看 X-Panel 运行日志"
        echo -e "x-ui banlog       - 检查 Fail2ban 禁止日志"
        echo -e "x-ui update       - 更新 X-Panel 面板"
        echo -e "x-ui custom       - 自定义 X-Panel 版本"
        echo -e "x-ui install      - 安装 X-Panel 面板"
        echo -e "x-ui uninstall    - 卸载 X-Panel 面板"
        echo -e "--------------------------------------------"
        echo ""
        sleep 3
        echo -e ">>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>"
        echo ""
        echo -e "${yellow}----->>>X-Panel面板和Xray启动成功<<<-----${plain}"
    }

    # 设置VPS中的时区/时间为【上海时间】
    sudo timedatectl set-timezone Asia/Shanghai

    install_base
    install_x-ui $1
    
    echo ""
    echo -e "----------------------------------------------"
    sleep 4
    info=$(/usr/local/x-ui/x-ui setting -show true)
    echo -e "${info}${plain}"
    echo ""
    echo -e "若您忘记了上述面板信息，后期可通过x-ui命令进入脚本${red}输入数字〔10〕选项获取${plain}"
    echo ""
    echo -e "----------------------------------------------"
    echo ""
    sleep 2
    echo -e "${green}安装/更新完成！${plain}"
    echo ""
    echo -e "----------------------------------------------"
    echo ""
    echo -e "${green}〔X-Panel面板〕项目地址：${yellow}https://github.com/875706361/X-Panel-Linux${plain}" 
    echo ""
    echo -e "----------------------------------------------"
    echo ""
}

# ----------------------------------------------------------
# 脚本执行入口
# ----------------------------------------------------------
clear
install_free_version
