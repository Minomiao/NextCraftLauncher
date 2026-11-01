# Next Craft Launcher

一个面向 Windows 的 Minecraft 启动器，操作习惯参考 PCL2。

## 功能

- 扫描 `.minecraft` 中的游戏版本，可切换或添加游戏目录，也能导入 PCL2 的启动设置
- 自动识别已安装的 Java，并按版本需要下载对应的 Java 运行时
- 下载任意 Minecraft 版本（正式版 / 快照 / 远古版本），可选择是否开启版本隔离
- 安装 Fabric / Forge / NeoForge，缺原版文件时一并补齐
- 下载源可在自动 / 官方 / BMCLAPI 之间切换
- 启动游戏并实时显示运行日志

## 使用

默认扫描 `%APPDATA%\.minecraft`，可在界面中修改路径。

配置与日志保存在启动器所在目录的 `NCL` 文件夹中：

- `NCL\settings.json`：游戏目录、内存、Java、下载源等设置
- `NCL\log\`：运行日志，按天分文件，保留 7 天
