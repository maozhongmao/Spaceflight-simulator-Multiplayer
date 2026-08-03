# Spaceflight-simulator-Multiplayer

Spaceflight Simulator 1.6 的实时多人联机项目。

本项目通过客户端模组和独立服务端，让多个玩家进入同一个 SFS 世界，实时同步玩家火箭及其状态，并提供多人联机所需的基础网络功能。

## 项目组成

- `Client/`：SFS 1.6 客户端模组源码。
- `Server/`：独立联机服务端源码。
- `LICENCE.txt`：本项目的 MIT License。

客户端模组安装在 SFS 1.6 的 `Mods` 目录中，服务端作为独立程序运行。客户端与服务端需要使用相互匹配的版本。

## 主要功能

- 多人进入同一个 SFS 世界。
- 玩家和火箭状态同步。
- 火箭创建、销毁和部件状态同步。
- 玩家控制权同步。
- 火箭对接与解除对接同步。
- 世界时间同步。
- TCP 与 UDP 网络传输。
- 服务端权威处理关键多人事件。
- UDP 不可用时使用 TCP 进行必要的状态同步。

## 使用前提

- Steam 版 Spaceflight Simulator 1.6。
- 支持 SFS 1.6 的 Mod Loader。
- `UITools` 前置模组。
- 与服务端匹配的客户端模组和服务端程序。

## 基本使用方式

1. 安装 SFS 1.6、Mod Loader 和 `UITools`。
2. 将与服务端匹配的客户端模组放入游戏的 `Mods` 目录。
3. 启动对应版本的独立服务端。
4. 在游戏的 Multiplayer 菜单中填写服务器地址和端口。
5. 输入用户名并加入服务器。

本项目是独立开发的非官方多人联机工具，与 Spaceflight Simulator 官方团队及其权利人没有隶属、授权或合作关系。

## 开源协议

本项目使用 MIT License。详细条款请查看 [LICENCE.txt](LICENCE.txt)。

## 源码仓库

https://github.com/maozhongmao/Spaceflight-simulator-Multiplayer

## 联系方式

- QQ Group: 679991439
- Email: maozhongmao@qq.com

Copyright (c) 2026 STCH Studio
Developer: maozhongmao / yangchengtong
