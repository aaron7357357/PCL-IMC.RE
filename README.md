# PCL IMC.RE

给 [IMC.RE](https://imc.re) 服务器玩家做的 PCL2 主页方案：一个本地小服务 + 一个注入补丁，让 PCL 的主页变成服务器的实时状态面板，并且可以直接在主页里驱动陶瓦（Terracotta）联机。

![主页效果](logo.png)

---

## 它做了什么

PCL2 的自定义主页是静态 XAML，数字不会自己变。这个项目把它变成"活的"：

- **服务器状态卡**：离线/在线状态、MOTD、版本号、在线人数、延迟，每秒刷新
- **联机 · 陶瓦**：创建房间 / 复制联机码 / 关闭联机 / 输入联机码加入房间，全程不弹网页
- **按版本进服**：`1.8 ~ 1.21.11` 每个版本一行，「正版」走 `on.imc.cab`，「离线」走 `off.imc.cab`
- **毛玻璃**：把 PCL 卡片自带的白色底去掉

实现上是两层：

```
┌─────────────────────────────┐
│ PCL2 (改版)                 │
│  ├ 内置预设「你知道吗」= 本页  │
│  └ PCLPatch.dll（注入）      │
│       · 每 500ms 拉 live.json │
│       · 只改 Tag="live:*" 控件 │
│       · Tag="act:*" 接管点击    │
└──────────────┬──────────────┘
               │ HTTP 127.0.0.1:8848
┌──────────────┴──────────────┐
│ PCL主页服务.exe (本包)       │
│  · /motd.xaml  主页 XAML      │
│  · /live.json  实时数据        │
│  · /tc?do=...  驱动陶瓦        │
│  · 查询 MC 服务器 MOTD/在线人数 │
└─────────────────────────────┘
```

页面 XAML 本身**恒定不变**，只有带 `Tag="live:xxx"` 的控件被改文字，所以折叠状态、滚动位置、毛玻璃都不会被刷新重置。

---

## 快速开始

1. 解压本仓库
2. 双击 **`启动主页服务（无窗口）.vbs`**（启动 `PCL主页服务.exe`，占 8848 端口）
3. 打开 `改版PCL\Plain Craft Launcher 2.exe`

想让它开机自启：双击 `安装开机自启.bat`。

如果你的 PCL **不是**这个改版：先运行一次 `PCL2-主页补丁安装器.exe`，它会备份你的 PCL、注入补丁、把主页地址写好。

### 关闭 / 卸载

| 操作 | 文件 |
|---|---|
| 停止主页服务 | `停止主页服务.bat` |
| 关闭开机自启 | `取消开机自启.bat` |
| 重新开毛玻璃 | `开启毛玻璃.bat` / `关闭毛玻璃.bat` |

---

## 下载

- **蓝奏云**：<https://wwbrg.lanzouv.com/ikExC4f4b0hc> 密码 `imc.re`
  （被微信/QQ 拦截时把域名换成 `lanzoup.com` 或 `lanzoux.com`）

## 文件说明

| 文件 | 说明 |
|---|---|
| `PCL主页服务.exe` | 主页服务（PyInstaller 打包，源码在 `src/motd_local_server.py`） |
| `启动主页服务（无窗口）.vbs` | 静默启动，不弹控制台 |
| `守护主页服务.vbs` | 端口挂了自动拉起（配合计划任务每分钟检查） |
| `PCL2-主页补丁安装器.exe` | 给已有 PCL 注入补丁（源码 `src/Installer.cs`） |
| `改版PCL\` | 已注入补丁 + 内置主页预设的 PCL |
| `src\motd_local_server.py` | 主页服务源码 |
| `src\PCLPatch.cs` | 注入补丁源码（C# / WPF） |
| `logo.png` / `style.txt` | 主页素材与样式开关 |

---

## 原理细节

**为什么要注入 DLL？**
PCL 的自定义主页只是把 XAML 塞进 `_PanCustom`，没有任何刷新机制。补丁在 PCL 启动时挂一个 `DispatcherTimer`，每 500ms 拉一次 `http://127.0.0.1:8848/live.json`，解析出扁平 JSON，然后遍历可视树，找到 `Tag="live:key"` 的控件把文字换掉。

**按钮怎么工作？**
按钮标 `Tag="act:tc-host"`，补丁给它挂 `MouseLeftButtonUp`，通过 `RunAction` 请求 `http://127.0.0.1:8848/tc?do=tc-host`。输入框标 `Tag="in:code"`，补丁把 TextBox 实例记下来，"加入房间"时直接读它的内容。

**毛玻璃**
`MyCard` 的 `MainBorder.Background` 置空、`HasMouseAnimation` 关掉、`_MainChrome` 折叠。因为按 F5 会让 PCL 重建控件、白底又回来，所以每个 tick 都过一遍。

**陶瓦怎么被驱动**
陶瓦（Terracotta）是 PCL 附带的联机工具，本身是个本地 Web 服务。它会打印
`Rocket has launched from http://127.0.0.1:<端口>`，服务端把这行解析出来当端口，
然后用 `/state`、`/state/scanning`、`/state/guesting?room=`、`/state/ide` 控制它。

> 注意：`/state/scanning` 和 `/state/guesting` 会一直挂到状态机变化才返回，
> 所以服务端把它们丢到后台线程里发，接口本身立刻返回。

---

## 已知限制

- 陶瓦联机依赖 EasyTier 公共中继，中继不通时会显示「联机出错（公共节点连不上）」
- `host-scanning` 状态是**在等你进单人存档开局域网**，不是卡死
- 服务端用 PyInstaller 打包，部分杀软会误报，加信任即可

---

## 授权

本仓库里**我们自己写的部分**（主页服务、注入补丁、页面 XAML、安装器、脚本）使用 MIT，见 `LICENSE`。

`改版PCL\` 里的 `Plain Craft Launcher 2.exe` **不是我们的作品**，是 PCL2 的修改版，版权属于 PCL 作者。详见 `NOTICE.md`。
