# PCL IMC.RE

给 [IMC.RE](https://imc.re) 服务器玩家做的 PCL2 改版：主页换成服务器实时状态面板，顶部多一个「联机」页直接驱动陶瓦（Terracotta）联机，**不需要装 Python、不需要跑本地服务**。

![主页效果](logo.png)

---

## v1.2 重磅更新

这一版把原来那套「本地 Python 服务 + HTTP 轮询」整个搬进了补丁 DLL，**只留一个启动器**：

| 以前 | 现在 |
|---|---|
| 要跑 `PCL主页服务.exe`（PyInstaller 8 MB） | 不需要，直接开 PCL |
| 主页 XAML 从 `127.0.0.1:8848` 拉 | 内嵌在 `PCLPatch.dll` 里 |
| 状态数据靠 Python 查 mcstatus | C# 直连 Minecraft Server List Ping |
| 内存优化调 Python + ctypes | C# 直接 `NtSetSystemInformation` |
| 陶瓦驱动靠本地 HTTP 中转 | C# 直接起 `terracotta.exe` 并调它的 `/state` |
| 版本列表写死在 XAML 里 | 实时扫描，增删自动跟随 |

搜索结果：轻量版只有 **2 个文件** —— `Plain Craft Launcher 2.exe` + `PCLPatch.dll`。

---

## 它做了什么

### 主页（启动页）

- **服务器状态卡**：在线/离线、MOTD、服务端版本、在线人数、延迟，每 0.5 秒刷新
- **内存优化**：清工作集 / 刷已修改页 / 清待命列表，需要管理员权限才清得干净（实测能释放 1~3 GB）
- **按版本进服**：自动扫描本机版本（读 `clientVersion`，整合包也认），只列 1.8 ~ 1.21.11，「正版」走 `on.imc.cab`，「离线」走 `off.imc.cab`
- **排序**：数字版本（1.21.11 → 1.12.2）→ 英文整合包 → 中文整合包
- **实时跟随**：用 `FileSystemWatcher` 盯着 `versions` 目录，装/删版本 1 秒内自动增删行，**没变化就不重建**（不会闪）
- 在线玩家、QQ 群、赞赏、关于

### 联机页（陶瓦）

顶部导航的「联机」，左侧三个分栏：

| 分栏 | 内容 |
|---|---|
| 创建房间 | 填昵称 → 创建房间 → 游戏里 ESC → 对局域网开放 → 生成邀请码 |
| 加入房间 | 粘贴联机码 → 加入 → 游戏里自动多出服务器条目 |
| 设置 | 陶瓦路径（浏览/自动检测）、启动/停止组件、四个自动化开关 |

自动化：

- 启动启动器时自动启动陶瓦
- 联机成功后自动把服务器地址写进各版本 `servers.dat`（NBT 读写，保留原有条目）
- 退出启动器时停止陶瓦
- 首次使用自动从陶瓦官方 GitHub 下载（tar.gz + 系统 `tar.exe` 解压）

---

## 快速开始

1. 解压到单独文件夹
2. 双击 `Plain Craft Launcher 2.exe`
3. 第一次启动会自动把主页预设切成 **imc.re主页**（写 `PCL\imc-preset-once.txt` 标记，只做一次；之后你自己换预设不会被覆盖）
4. 想让它在别的机器上也自动切：删掉 `PCL\imc-preset-once.txt` 再启动一次

> `PCLPatch.dll` 必须和 exe 放在同一个文件夹，改名会失效。

### 可选：完整版

仓库根目录那套老文件（`PCL主页服务.exe`、各种 `.bat` / `.vbs`、`src\motd_local_server.py`）是 v1.1 的实现，**v1.2 已经不需要**，留着只是给想改 Python 逻辑的人参考。

---

## 实现结构

```
┌──────────────────────────────────────────┐
│ Plain Craft Launcher 2.exe（官方 + 注入） │
│  ├ 内置预设页 = IMC 主页（启动器里）        │
│  └ PCLPatch.dll                          │
│       ├ ImcNative.cs  服务器状态 / 内存优化 │
│       ├ TcNative.cs   陶瓦启动 / 状态 / NBT │
│       ├ IMCHome.xaml  内嵌主页             │
│       └ TCHome.xaml   内嵌联机页           │
└──────────────────────────────────────────┘
        │                    │
        ▼                    ▼
  play.imc.re:25565    terracotta.exe
  (Server List Ping)   (/state, /state/*)
```

页面 XAML 只加载一次，之后补丁只改带 `Tag="live:xxx"` 的控件文字，所以折叠状态、滚动位置都不会被刷新重置。

---

## 常见问题

**主页没变成 IMC 那套？**
设置 → 个性化 → 主页 → 预设，选「imc.re主页」。或者删掉 `PCL\imc-preset-once.txt` 重启。

**主页空白？**
说明预设选中了但内容没建起来，一般是 `PCLPatch.dll` 没和 exe 放一起。

**陶瓦按钮没反应？**
联机 → 设置 → 点「自动检测」看能不能找到 `terracotta.exe`；找不到就点「浏览」手动选，或直接点「启动组件」触发自动下载。

**内存优化提示需要管理员权限？**
PCL 用管理员身份运行，清理才会彻底（普通权限只能清自己进程的）。

---

## 说明

- 基于 PCL2 官方发行版注入，保留正版登录、百宝箱等原功能
- 陶瓦联机来自 [burningtnt/Terracotta](https://github.com/burningtnt/Terracotta)，本仓库只做调用与界面
- 遇到问题带截图和 `PCL\PCLPatch.log` 反馈
