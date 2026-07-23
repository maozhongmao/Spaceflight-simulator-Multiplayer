# Spaceflight-simulator-Multiplayer

## SFS 1.6 实时联机版

本仓库现在提供适用于 **Steam Spaceflight Simulator 1.6.00.16** 的客户端模组与独立联机服务端。

> 客户端与服务端必须使用同一代网络协议，V1 与 V2 不能混用。

### v0.2.0 — TCP Net V2（推荐）

Net V2 使用 TCP 通信，并加入心跳、火箭同步流量控制和网络调试信息。

**更新内容：**

- 网络传输从 UDP/Lidgren 切换为 TCP；
- 增加连接握手、心跳和 Ping/Pong；
- 关键事件使用 FIFO 队列，火箭状态只保留最新值，降低网络阻塞；
- 受控火箭按 20 Hz、移动火箭按 5 Hz、静止火箭按 3 秒快照同步；
- 增加 F8 客户端网络调试窗口和服务端调试模式；
- 增加世界和单枚火箭重新同步能力。

- [下载 Net V2 客户端](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases/download/net-v2/SFS-Multiplayer-1.6-NetV2.zip)
- [下载 Net V2 服务端](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases/download/net-v2/SFS-Multiplayer-Server.exe)
- [查看 Net V2 Release](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases/tag/net-v2)

### v0.1.2 — Net V1（旧版）

Net V1 是早期 SFS 1.6 实时联机版本，保留用于兼容和归档。

**更新内容：**

- 首个适配 SFS 1.6.00.16 的实时联机发行版；
- 提供独立 UDP 服务端；
- 支持世界、火箭、部件状态、时间、聊天和玩家控制同步；
- 修正客户端与服务端的协议编号、字符串和聊天数据格式；
- 修正集合反序列化导致多变量火箭无法加入的问题；
- 修正部件销毁路由、离散事件时序、重复时间戳插值和聊天等待问题。

- [下载 Net V1 客户端](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases/download/net-v1/MultiplayerSFS-1.6.dll)
- [下载 Net V1 服务端](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases/download/net-v1/SFS-Multiplayer-Server.exe)
- [查看 Net V1 Release](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases/tag/net-v1)

### 安装与启动

1. 安装支持 SFS 1.6 的 Mod Loader 和 `UITools`。
2. 将对应版本的客户端文件放入游戏的 `Mods` 目录。
3. 启动对应版本的 `SFS-Multiplayer-Server.exe`。
4. 在客户端输入服务端地址和端口加入游戏。
5. 所有玩家必须使用与服务端一致的版本。

V3 及后续开发版本暂未开源。下一个正式版本计划使用 **v1.0.0**。服务器和客户端发行文件请从 [Releases](https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer/releases) 下载。

---

## 原始 Python 项目

以下内容为 L4z41 创建的原始项目说明，予以保留。

Welcome to spaceflight simulator multiplayer made by L4z41

# HOW DOES IT WORK?
This app uses mega's api to upload quick-saves to a cloud, then another client can download the same file


# IS IT SAFE?
YES. It is 100% safe to use, operator of the server can encrypt the data for another security layer


# HOW TO SETUP:

**1.** download python **from their official site**: https://www.python.org/downloads/

**2.** install all the nececary modules:

```pip install mega.py```

```pip install kivy```

*In some cases mega.py wont work, then do ```pip uninstall mega.py``` and then ```pip install mega```*

**3.** download the files:
```git clone https://github.com/L4z4r1/Spaceflight-simulator-Multiplayer.git```

**4.** go to https://mega.nz/ and create a new account

**5.** open the cloned folder and you will see all the scripts, go to ```Network.py``` and find
```python
#Your e-mail and password for MEGA
email = "example@example.com"
password = "xxxxxxxxxxxxx"
```
edit the ```email``` and ```password``` to your email and password for mega.nz

**5.** open the terminal or cmd and type python Main.py

*your working directory must be in the sfs multiplayer folder*

**6.** run ```pyinstaller "~/SFS Multiplayer/Main.py" --noconfirm --onefile --console --name "SFS Multiplayer" --key "[your key]"```
in the working directory

**7.** share the files with some people and enjoy!

# IMAGES:
![mulEx1](https://user-images.githubusercontent.com/107078837/203858883-5b6e576f-cc63-4e5a-99db-fbf84cca435b.png)

# PLANS:

**-** make a way to ban users and operate the "server"

**-** make the app compatible with mods

**-** enhance security

**-** make a way for it to work with android

**-** make it automaticaly sync 

**-** make it automaticaly back-up worlds

**-** fix some known bugs

